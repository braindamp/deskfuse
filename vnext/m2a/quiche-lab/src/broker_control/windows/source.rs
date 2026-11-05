use crate::audio_authority::{AudioAuthority, ControlGrant};
use crate::audio_control::{DeliveryBinding, SourceScope};
use crate::audio_transport::ControlConnection;
use crate::committed_source::{Block, Source};
use crate::fresh_anchor::CommitFence;
use crate::plaintext_expiry::{PcmTiming, PlaintextExpiry};
use crate::source_age;
use crate::{Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use std::io;
use std::net::UdpSocket;
use std::time::Instant;
use windows_sys::Win32::Foundation::HANDLE;

fn denied() -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, "Native source delivery is not authorized")
}

pub(super) trait Input: Send {
    fn next_block(&mut self) -> io::Result<Option<Block>>;
    fn arm(&self) -> io::Result<()>;
    fn event(&self) -> HANDLE;
    fn period(&self) -> HANDLE;
    fn service_period(&self) -> io::Result<()>;
    fn discard_partial(&mut self, qpc: u64) -> io::Result<CommitFence>;
}
impl Input for Source {
    fn next_block(&mut self) -> io::Result<Option<Block>> { Source::next_block(self) }
    fn arm(&self) -> io::Result<()> { Source::arm(self) }
    fn event(&self) -> HANDLE { self.event }
    fn period(&self) -> HANDLE { self.period_event() }
    fn service_period(&self) -> io::Result<()> { Source::service_period(self) }
    fn discard_partial(&mut self, qpc: u64) -> io::Result<CommitFence> {
        self.fence(qpc)
    }
}

struct Delivery {
    connection: ControlConnection,
    binding: DeliveryBinding,
    ledger: PlaintextExpiry,
    generation: u64,
    digest: [u8; 32],
}

pub(super) struct SourceOwner {
    scope: SourceScope,
    input: Box<dyn Input>,
    delivery: Option<Delivery>,
    sequence: u64,
    fence: Option<CommitFence>,
}
impl SourceOwner {
    pub(super) fn new(scope: SourceScope, input: Box<dyn Input>) -> Self {
        Self { scope, input, delivery: None, sequence: 0, fence: None }
    }
    pub(super) fn source_event(&self) -> HANDLE { self.input.event() }
    pub(super) fn period_event(&self) -> HANDLE { self.input.period() }
    pub(super) fn service_period(&self) -> io::Result<()> { self.input.service_period() }
    pub(super) fn has_delivery(&self) -> bool { self.delivery.is_some() }
    pub(super) fn sealed_pending(&self) -> bool {
        self.delivery.as_ref().is_some_and(|d| d.connection.sealed_pending())
    }
    pub(super) fn install(
        &mut self, mut connection: ControlConnection, binding: DeliveryBinding,
        grant: &ControlGrant, now: u64,
    ) -> io::Result<()> {
        if self.delivery.is_some() || !self.scope.matches(grant)
            || !connection.approved_for(grant, &binding)
        {
            connection.retire();
            return Err(denied());
        }
        self.delivery = Some(Delivery {
            connection, binding, ledger: PlaintextExpiry::default(),
            generation: grant.delivery_generation, digest: grant.delivery_digest,
        });
        if let Err(error) = self.recheck(grant, now) {
            self.fence_delivery()?;
            return Err(error);
        }
        Ok(())
    }
    pub(super) fn recheck(&mut self, grant: &ControlGrant, now: u64) -> io::Result<()> {
        if !self.scope.matches(grant) || grant.closed || now >= grant.until {
            self.fence_delivery()?;
            return Err(denied());
        }
        if let Some(delivery) = self.delivery.as_ref() {
            if delivery.generation != grant.delivery_generation
                || delivery.digest != grant.delivery_digest
            {
                self.fence_delivery()?;
            } else if !delivery.connection.approved_for(grant, &delivery.binding) {
                let delivery = self.delivery.as_mut().unwrap();
                if delivery.connection.install_native(
                    &self.scope, grant.boot, delivery.binding.clone(), grant, now,
                ).is_err() {
                    self.fence_delivery()?;
                    return Err(denied());
                }
            }
        }
        Ok(())
    }
    pub(super) fn fence_delivery(&mut self) -> io::Result<()> {
        if let Some(mut delivery) = self.delivery.take() {
            delivery.connection.retire();
            delivery.ledger.clear();
            self.fence = Some(self.input.discard_partial(source_age::local_qpc_100ns()?)?);
        }
        Ok(())
    }
    pub(super) fn routes(&self, cid: &[u8], info: quiche::RecvInfo) -> bool {
        self.delivery.as_ref().is_some_and(|d| d.connection.routes(cid, info))
    }
    pub(super) fn receive(
        &mut self, ciphertext: &mut [u8], info: quiche::RecvInfo, now: u64,
    ) -> io::Result<()> {
        if let Some(delivery) = self.delivery.as_mut() {
            delivery.connection.receive(ciphertext, info, now)?;
        }
        Ok(())
    }
    pub(super) fn pump(
        &mut self, authority: &mut AudioAuthority,
        mut boot: impl FnMut() -> u64, mut terminal: impl FnMut() -> bool,
    ) -> io::Result<bool> {
        let allowed = !terminal() && authority.media_allowed(boot());
        let mut consumed = false;
        for _ in 0..8 {
            let Some(block) = self.input.next_block()? else { break };
            consumed = true;
            if self.fence.is_some_and(|fence| !fence.accepts(
                block.generation, block.min_commit,
                block.timing.commit_qpc, block.first_frame,
            )) { continue; }
            if !allowed || self.delivery.is_none() { continue; }
            if terminal() || !authority.media_allowed(boot()) {
                self.fence_delivery()?;
                return Err(denied());
            }
            if self.sequence > u32::MAX as u64 {
                self.fence_delivery()?;
                return Err(denied());
            }
            let header = Header {
                version: VERSION, kind: KIND_AUDIO, flags: 0,
                header_bytes: HEADER_BYTES as u16, frame_count: 240,
                media_epoch: self.scope.wire[0], source_stream_id: self.scope.wire[1],
                format_config_id: self.scope.wire[2],
                packet_sequence: self.sequence as u32,
                first_source_frame: block.first_frame, payload_bytes: 960, clock_anchor_id: 0,
            };
            self.sequence += 1;
            let delivery = self.delivery.as_mut().unwrap();
            if delivery.connection.queue_media(
                &mut delivery.ledger, &header, &block.pcm,
                PcmTiming::Committed(block.timing), boot(),
            ).is_err() {
                self.fence_delivery()?;
                return Err(denied());
            }
        }
        self.input.arm()?;
        Ok(consumed)
    }
    pub(super) fn service_egress(
        &mut self, authority: &mut AudioAuthority,
        socket: &UdpSocket, writable: bool,
        mut boot: impl FnMut() -> u64, mut terminal: impl FnMut() -> bool,
    ) -> io::Result<()> {
        if terminal() || !authority.media_allowed(boot()) {
            if self.delivery.as_ref().is_some_and(|d|
                d.connection.sealed_pending() || d.ledger.len() != 0
            ) {
                self.fence_delivery()?;
            }
            return Ok(());
        }
        if let Some(delivery) = self.delivery.as_mut() {
            let now = boot();
            if delivery.connection.service_media_egress(
                &mut delivery.ledger, socket, writable, now,
                || !terminal() && authority.media_allowed(boot()),
            ).is_err() {
                self.fence_delivery()?;
                return Err(denied());
            }
        }
        Ok(())
    }
    pub(super) fn needs_write(&self) -> bool {
        self.delivery.as_ref().is_some_and(|d| d.connection.needs_write())
    }
    pub(super) fn deadline(&self) -> Option<Instant> {
        self.delivery.as_ref().and_then(|d| {
            let next = d.connection.network_deadline();
            let ledger = d.ledger.next_expiry_wait(source_age::local_qpc_100ns().ok()?)
                .and_then(|wait| Instant::now().checked_add(wait));
            match (next, ledger) {
                (Some(a), Some(b)) => Some(a.min(b)),
                (Some(a), None) | (None, Some(a)) => Some(a),
                _ => None,
            }
        })
    }
    #[cfg(test)]
    pub(super) fn block_egress(&mut self, now: u64) -> io::Result<()> {
        if let Some(d) = self.delivery.as_mut() {
            d.connection.service_media_with_io(
                &mut d.ledger, true, now,
                |_, _| Err(io::Error::from(io::ErrorKind::WouldBlock)),
            )?;
        }
        Ok(())
    }
    #[cfg(test)]
    pub(super) fn outstanding(&self) -> usize {
        self.delivery.as_ref().map_or(0, |d| d.ledger.len())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audio_transport::tests::pair;
    use crate::audio_control::Observation;
    use crate::source_age::CommitTiming;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};

    struct Fake {
        chunks: VecDeque<Block>,
        fences: Arc<Mutex<usize>>,
    }
    impl Input for Fake {
        fn next_block(&mut self) -> io::Result<Option<Block>> { Ok(self.chunks.pop_front()) }
        fn arm(&self) -> io::Result<()> { Ok(()) }
        fn event(&self) -> HANDLE { std::ptr::null_mut() }
        fn period(&self) -> HANDLE { std::ptr::null_mut() }
        fn service_period(&self) -> io::Result<()> { Ok(()) }
        fn discard_partial(&mut self, qpc: u64) -> io::Result<CommitFence> {
            *self.fences.lock().unwrap() += 1;
            self.chunks.clear();
            Ok(CommitFence {
                generation: 1, accepted: 1, end_frame: 480, request_qpc: qpc,
            })
        }
    }

    fn bytes(value: &serde_json::Value, key: &str) -> Vec<u8> {
        let hex = value[key].as_str().unwrap();
        (0..hex.len()).step_by(2).map(|at|
            u8::from_str_radix(&hex[at..at + 2], 16).unwrap()).collect()
    }

    #[test]
    fn approved_sender_fences_blocked_ciphertext_without_releasing_its_source() {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../../../tools/protocol3/audio-transport-v1.json"
        )).unwrap();
        let scope = SourceScope::parse(&bytes(&f, "sourceHex")).unwrap();
        let mut grant = ControlGrant::parse(&bytes(&f, "controlHex")).unwrap();
        let (_, mut native) = pair();
        let observation: Observation = native.observation(100).unwrap().unwrap();
        let mut wire = bytes(&f, "deliveryHex");
        for (old, new) in [(15, observation.native), (16, observation.phone), (17, observation.nonce)] {
            let offset = wire.windows(32).position(|b| b == [old; 32]).unwrap();
            wire[offset..offset + 32].copy_from_slice(&new);
        }
        let binding = DeliveryBinding::parse(&wire).unwrap();
        grant.delivery_digest = binding.digest;
        grant.until = 100_000_000;
        grant.ready = true;
        grant.output_generation = 13;
        native.install_native(&scope, grant.boot, binding.clone(), &grant, 100).unwrap();
        let mut authority = AudioAuthority::new(grant.boot, grant.worker).unwrap();
        authority.apply(grant.clone(), 100).unwrap();
        authority.source_observed(grant.binding, 100).unwrap();
        authority.delivery_observed(grant.delivery_generation, grant.delivery_digest, 100).unwrap();
        authority.output_observed(grant.delivery_generation, 13, 100).unwrap();

        let qpc = source_age::local_qpc_100ns().unwrap();
        let block = Block {
            first_frame: 240, generation: 1, min_commit: 1,
            timing: CommitTiming { commit_qpc: qpc.saturating_sub(1_000), presentation_qpc: qpc },
            pcm: [7; 960],
        };
        let fences = Arc::new(Mutex::new(0));
        let fake = Fake { chunks: VecDeque::from([block]), fences: Arc::clone(&fences) };
        let mut source = SourceOwner::new(scope.clone(), Box::new(fake));
        source.install(native, binding, &grant, 101).unwrap();
        source.pump(&mut authority, || 102, || false).unwrap();
        assert_eq!(source.outstanding(), 1);
        source.block_egress(103).unwrap();
        assert!(source.sealed_pending());

        let mut next = grant.clone();
        next.revision += 1;
        next.delivery_generation += 1;
        next.delivery_digest = [33; 32];
        next.output_generation = 0;
        next.ready = false;
        authority.apply(next.clone(), 104).unwrap();
        source.recheck(&next, 104).unwrap();
        assert!(!source.has_delivery());
        assert_eq!(source.outstanding(), 0);
        assert!(!source.sealed_pending());
        assert_eq!(*fences.lock().unwrap(), 1);
        assert_eq!(source.sequence, 1);
        source.pump(&mut authority, || 105, || false).unwrap();
        assert_eq!(*fences.lock().unwrap(), 1);
    }
}

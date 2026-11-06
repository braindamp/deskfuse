use crate::audio_authority::{AudioAuthority, ControlGrant};
use crate::audio_control::{self as ac, DeliveryBinding, Record, SourceScope};
use crate::audio_rpc::OutputSignal;
use crate::audio_transport::ControlConnection;
use crate::committed_source::{Block, Source};
use crate::fresh_anchor::CommitFence;
use crate::plaintext_expiry::{PcmTiming, PlaintextExpiry};
use crate::source_age;
use crate::{Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use std::io;
use std::net::{SocketAddr, UdpSocket};
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
    ready_output: Option<u64>,
    submitted_output: u64,
    withdrawn_output: u64,
}

struct RetiredDelivery {
    route: [u8; 16],
    generation: u64,
    digest: [u8; 32],
}

pub(super) struct SourceOwner {
    scope: SourceScope,
    input: Box<dyn Input>,
    delivery: Option<Delivery>,
    sequence: u64,
    fence: Option<CommitFence>,
    closed: Option<[u8; 16]>,
    lost_delivery: Option<RetiredDelivery>,
    reconnect_until: Option<u64>,
    authority_until: u64,
    #[cfg(test)]
    block_io: bool,
}
impl SourceOwner {
    pub(super) fn new(scope: SourceScope, input: Box<dyn Input>) -> Self {
        Self {
            scope, input, delivery: None, sequence: 0, fence: None,
            closed: None, lost_delivery: None, reconnect_until: None, authority_until: 0,
            #[cfg(test)]
            block_io: false,
        }
    }
    pub(super) fn source_event(&self) -> HANDLE { self.input.event() }
    pub(super) fn period_event(&self) -> HANDLE { self.input.period() }
    pub(super) fn service_period(&self) -> io::Result<()> { self.input.service_period() }
    pub(super) fn has_delivery(&self) -> bool { self.delivery.is_some() }
    pub(super) fn replacement_origin(&self, grant: &ControlGrant, now: u64) -> Option<[u8; 16]> {
        if !self.scope.matches(grant) || self.grace_expired(now) { return None; }
        if let Some(delivery) = &self.delivery {
            if delivery.generation != grant.delivery_generation || delivery.digest != grant.delivery_digest {
                return None;
            }
            return delivery.connection.observed_id();
        }
        let lost = self.lost_delivery.as_ref()?;
        (lost.generation == grant.delivery_generation && lost.digest == grant.delivery_digest)
            .then_some(lost.route)
    }
    pub(super) fn sealed_pending(&self) -> bool {
        self.delivery.as_ref().is_some_and(|d| d.connection.sealed_pending())
    }
    pub(super) fn connection_id(&self) -> Option<[u8; 16]> {
        self.delivery.as_ref().and_then(|d| d.connection.observed_id())
    }
    pub(super) fn pending_close(&self) -> Option<[u8; 16]> { self.closed }
    pub(super) fn close_submitted(&mut self, id: [u8; 16]) -> io::Result<()> {
        if self.closed != Some(id) { return Err(denied()); }
        self.closed = None;
        Ok(())
    }
    pub(super) fn grace_deadline(&self) -> Option<u64> { self.reconnect_until }
    pub(super) fn grace_expired(&self, now: u64) -> bool {
        self.delivery.is_none() && self.reconnect_until.is_some_and(|until| now >= until)
    }
    pub(super) fn read_control(&mut self, now: u64) -> io::Result<Vec<Record>> {
        let Some(delivery) = self.delivery.as_mut() else { return Ok(Vec::new()); };
        match delivery.connection.read_control_limited(now, ac::MAX_PENDING) {
            Ok(records) => Ok(records),
            Err(_) => { self.fence_delivery_at(now)?; Ok(Vec::new()) }
        }
    }
    pub(super) fn reply(&mut self, record: Record, now: u64) -> io::Result<bool> {
        let Some(delivery) = self.delivery.as_mut() else { return Ok(false); };
        match delivery.connection.queue_control(record, now) {
            Ok(()) => Ok(true),
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => Ok(false),
            Err(_) => { self.fence_delivery_at(now)?; Ok(false) }
        }
    }
    pub(super) fn install(
        &mut self, mut connection: ControlConnection, binding: DeliveryBinding,
        grant: &ControlGrant, local: SocketAddr, now: u64,
    ) -> io::Result<()> {
        if self.delivery.is_some() || self.closed.is_some()
            || self.grace_expired(now) || !self.scope.matches(grant)
            || connection.local_endpoint() != local
            || connection.observed_id().is_none()
            || !connection.approved_for(grant, &binding)
        {
            connection.retire();
            return Err(denied());
        }
        self.delivery = Some(Delivery {
            connection, binding, ledger: PlaintextExpiry::default(),
            generation: grant.delivery_generation, digest: grant.delivery_digest,
            ready_output: grant.ready.then_some(grant.output_generation),
            submitted_output: 0,
            withdrawn_output: 0,
        });
        self.authority_until = grant.until;
        self.reconnect_until = None;
        self.lost_delivery = None;
        if let Err(error) = self.recheck(grant, now) {
            self.fence_delivery_at(now)?;
            return Err(error);
        }
        Ok(())
    }
    pub(super) fn recheck(&mut self, grant: &ControlGrant, now: u64) -> io::Result<()> {
        if !self.scope.matches(grant) || grant.closed || now >= grant.until {
            self.fence_delivery_at(now)?;
            return Err(denied());
        }
        self.authority_until = grant.until;
        if let Some(delivery) = self.delivery.as_ref() {
            if delivery.generation != grant.delivery_generation
                || delivery.digest != grant.delivery_digest
            {
                self.fence_delivery_at(now)?;
                if grant.delivery_generation != 0 { return Err(denied()); }
            } else if !delivery.connection.approved_for(grant, &delivery.binding) {
                let delivery = self.delivery.as_mut().unwrap();
                if delivery.connection.install_native(
                    &self.scope, grant.boot, delivery.binding.clone(), grant, now,
                ).is_err() {
                    self.fence_delivery_at(now)?;
                    return Err(denied());
                }
            }
        }
        if let Some(delivery) = self.delivery.as_mut() {
            let ready = grant.ready.then_some(grant.output_generation);
            let withdrawn = delivery.ready_output.is_some() && delivery.ready_output != ready;
            delivery.ready_output = ready;
            if withdrawn { self.pause_media(now)?; }
        }
        Ok(())
    }
    fn pause_media(&mut self, now: u64) -> io::Result<()> {
        if let Some(delivery) = self.delivery.as_mut() {
            if delivery.connection.sealed_media_pending() { return self.fence_delivery_at(now); }
            delivery.connection.discard_plaintext_media(&mut delivery.ledger);
            self.fence = Some(self.input.discard_partial(source_age::local_qpc_100ns()?)?);
        }
        Ok(())
    }
    pub(super) fn check_output(&self, id: [u8; 16], signal: OutputSignal) -> bool {
        self.delivery.as_ref().is_some_and(|delivery| {
            delivery.connection.observed_id() == Some(id) && delivery.digest == signal.digest
                && delivery.generation == signal.delivery && signal.output >= delivery.submitted_output
                && (!signal.ready || signal.output > delivery.withdrawn_output)
        })
    }
    pub(super) fn submit_ready(&mut self, output: u64) {
        if let Some(delivery) = self.delivery.as_mut() { delivery.submitted_output = output; }
    }
    pub(super) fn withdraw_output(&mut self, output: u64, authority: &mut AudioAuthority, now: u64) -> io::Result<()> {
        if let Some(delivery) = self.delivery.as_mut() {
            delivery.withdrawn_output = delivery.withdrawn_output.max(output);
        }
        authority.clear_output();
        self.pause_media(now)
    }
    pub(super) fn confirm_output(&mut self, grant: &ControlGrant, authority: &mut AudioAuthority, now: u64) -> io::Result<()> {
        if !grant.ready { return Ok(()); }
        let Some(delivery) = self.delivery.as_ref() else { authority.clear_output(); return Ok(()); };
        if grant.output_generation > delivery.submitted_output { return Err(denied()); }
        if grant.output_generation != delivery.submitted_output || grant.output_generation <= delivery.withdrawn_output {
            authority.clear_output();
        } else if !authority.media_allowed(now) {
            self.fence = Some(self.input.discard_partial(source_age::local_qpc_100ns()?)?);
            authority.output_observed(grant.delivery_generation, grant.output_generation, now)?;
        }
        Ok(())
    }
    pub(super) fn fence_delivery(&mut self) -> io::Result<()> {
        self.fence_delivery_at(super::runtime::now())
    }
    pub(super) fn fence_delivery_at(&mut self, now: u64) -> io::Result<()> {
        if let Some(retired) = self.retire_delivery()? {
            if self.closed.replace(retired.route).is_some() { return Err(denied()); }
            self.lost_delivery = Some(retired);
            self.reconnect_until = Some(
                now.saturating_add(self.scope.grace100ns).min(self.authority_until)
            );
        }
        Ok(())
    }
    /// Replacement has already committed. Its old close notice is control
    /// bookkeeping, independent of the source's reconnect grace.
    pub(super) fn retire_for_replacement(&mut self, previous: &ControlGrant, now: u64) -> io::Result<Option<[u8; 16]>> {
        let route = self.replacement_origin(previous, now).ok_or_else(denied)?;
        if let Some(retired) = self.retire_delivery()? {
            return Ok(Some(retired.route));
        }
        // The same incumbent was lost locally while its exact committed
        // replacement projection was in flight. Its send owner is gone.
        if self.closed.is_some_and(|closed| closed != route) { return Err(denied()); }
        self.lost_delivery = None;
        self.reconnect_until = None;
        Ok(self.closed.take())
    }
    fn retire_delivery(&mut self) -> io::Result<Option<RetiredDelivery>> {
        let Some(delivery) = self.delivery.take() else { return Ok(None); };
        let retired = RetiredDelivery {
            route: delivery.connection.observed_id().ok_or_else(denied)?,
            generation: delivery.generation,
            digest: delivery.digest,
        };
        // The actor is the only sender. Dropping this owner discards QUIC,
        // its sealed packet and the plaintext ledger without draining them.
        drop(delivery);
        self.fence = Some(self.input.discard_partial(source_age::local_qpc_100ns()?)?);
        Ok(Some(retired))
    }
    pub(super) fn routes(&self, cid: &[u8], info: quiche::RecvInfo) -> bool {
        self.delivery.as_ref().is_some_and(|d| d.connection.routes(cid, info))
    }
    pub(super) fn receive(
        &mut self, ciphertext: &mut [u8], info: quiche::RecvInfo, now: u64,
    ) -> io::Result<bool> {
        if let Some(delivery) = self.delivery.as_mut() {
            match delivery.connection.receive(ciphertext, info, now) {
                Ok(received) => return Ok(received),
                Err(_) => { self.fence_delivery_at(now)?; return Ok(true); }
            }
        }
        Ok(false)
    }
    pub(super) fn pump(
        &mut self, authority: &mut AudioAuthority,
        mut boot: impl FnMut() -> u64, mut terminal: impl FnMut() -> bool,
        socket: Option<&UdpSocket>,
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
                self.fence_delivery_at(boot())?;
                return Ok(consumed);
            }
            if self.sequence > u32::MAX as u64 {
                self.fence_delivery_at(boot())?;
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
                self.fence_delivery_at(boot())?;
                break;
            }
            if let Some(socket) = socket {
                self.service_egress(authority, socket, false, &mut boot, &mut terminal)?;
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
        let now = boot();
        if terminal() || !authority.source_allowed(now) {
            self.fence_delivery_at(now)?;
            return Ok(());
        }
        let media = authority.media_allowed(now);
        if !media && self.delivery.as_ref().is_some_and(|delivery|
            delivery.connection.sealed_media_pending() || delivery.ledger.len() != 0)
        {
            self.pause_media(now)?;
        }
        if let Some(delivery) = self.delivery.as_mut() {
            #[cfg(test)]
            let blocked = self.block_io;
            let result = {
                #[cfg(test)]
                if blocked {
                    delivery.connection.service_media_with_io(
                        &mut delivery.ledger, writable, now,
                        |_, _| Err(io::Error::from(io::ErrorKind::WouldBlock)),
                    )
                } else {
                    delivery.connection.service_media_egress(
                        &mut delivery.ledger, socket, writable, now,
                        || !terminal() && authority.source_allowed(boot())
                            && (!media || authority.media_allowed(boot())),
                    )
                }
                #[cfg(not(test))]
                delivery.connection.service_media_egress(
                    &mut delivery.ledger, socket, writable, now,
                    || !terminal() && authority.source_allowed(boot())
                        && (!media || authority.media_allowed(boot())),
                )
            };
            if result.is_err() { self.fence_delivery_at(boot())?; }
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
    pub(super) fn block_sends(&mut self) { self.block_io = true; }
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
    fn output_withdrawal_retires_blocked_media_and_preserves_a_drained_delivery() {
        for blocked in [true, false] { check_output_withdrawal(blocked); }
    }

    fn check_output_withdrawal(blocked: bool) {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../../../tools/protocol3/audio-transport-v1.json"
        )).unwrap();
        let scope = SourceScope::parse(&bytes(&f, "sourceHex")).unwrap();
        let mut grant = ControlGrant::parse(&bytes(&f, "controlHex")).unwrap();
        let (_, mut native) = pair();
        let observed = native.observation(100).unwrap().unwrap();
        let mut delivery = bytes(&f, "deliveryHex");
        for (old, new) in [
            (15, observed.native), (16, observed.phone), (17, observed.nonce),
        ] {
            let offset = delivery.windows(32).position(|bytes| bytes == [old; 32]).unwrap();
            delivery[offset..offset + 32].copy_from_slice(&new);
        }
        let binding = DeliveryBinding::parse(&delivery).unwrap();
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
            timing: CommitTiming {
                commit_qpc: qpc.saturating_sub(1_000), presentation_qpc: qpc,
            },
            pcm: [7; 960],
        };
        let fences = Arc::new(Mutex::new(0));
        let input = Fake {
            chunks: VecDeque::from([block]), fences: Arc::clone(&fences),
        };
        let mut source = SourceOwner::new(scope, Box::new(input));
        source.install(
            native, binding, &grant, "127.0.0.1:44002".parse().unwrap(), 101,
        ).unwrap();
        source.pump(&mut authority, || 102, || false, None).unwrap();
        source.block_egress(103).unwrap();
        assert!(source.delivery.as_ref().unwrap().connection.sealed_media_pending());
        if !blocked {
            let incumbent = source.delivery.as_mut().unwrap();
            incumbent.connection.service_media_with_io(
                &mut incumbent.ledger, true, 103, |bytes, _| Ok(bytes.len()),
            ).unwrap();
            assert!(!incumbent.connection.sealed_pending());
            assert_eq!(incumbent.ledger.len(), 0);
        }

        let mut withdrawn = grant;
        withdrawn.revision += 1;
        withdrawn.ready = false;
        authority.apply(withdrawn.clone(), 103).unwrap();
        source.recheck(&withdrawn, 103).unwrap();
        assert_eq!(source.has_delivery(), !blocked);
        assert_eq!(source.pending_close().is_some(), blocked);
        assert_eq!(*fences.lock().unwrap(), 1);
    }

    #[test]
    fn approved_sender_fences_blocked_ciphertext_without_releasing_its_source() {
        for lost in [false, true] { check_replacement_fence(lost); }
    }
    fn check_replacement_fence(lost: bool) {
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
        source.install(native, binding, &grant, "127.0.0.1:44002".parse().unwrap(), 101).unwrap();
        source.pump(&mut authority, || 102, || false, None).unwrap();
        assert_eq!(source.outstanding(), 1);
        source.block_egress(103).unwrap();
        assert!(source.sealed_pending());

        let mut next = grant.clone();
        next.revision += 1;
        next.delivery_generation += 1;
        next.delivery_digest = [33; 32];
        next.output_generation = 0;
        next.ready = false;
        if lost {
            source.fence_delivery_at(103).unwrap();
            source.close_submitted(observation.connection).unwrap();
        }
        authority.apply(next.clone(), 104).unwrap();
        assert_eq!(source.retire_for_replacement(&grant, 104).unwrap(), (!lost).then_some(observation.connection));
        source.recheck(&next, 104).unwrap();
        assert!(!source.has_delivery());
        assert!(source.grace_deadline().is_none());
        assert!(source.pending_close().is_none());
        assert_eq!(source.outstanding(), 0);
        assert!(!source.sealed_pending());
        assert_eq!(*fences.lock().unwrap(), 1);
        assert_eq!(source.sequence, 1);
        source.pump(&mut authority, || 105, || false, None).unwrap();
        assert_eq!(*fences.lock().unwrap(), 1);
    }
}

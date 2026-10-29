//! Bounded connection-owner-only audio plaintext tracking. Hooked before
//! EVERY quiche Connection::send packet construction. No seal callback assumed.
use crate::source_age::{classify_wasapi, AgeDecision, MAX_SOURCE_AGE_100NS};
use quiche::Connection;
use std::time::Duration;

const CAPACITY: usize = 4;
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Identity {
    pub epoch: u32, pub stream: u32, pub format: u32,
    pub sequence: u32, pub source_frame: u64,
}
#[derive(Debug, Clone, Copy)]
struct Pending { id: Identity, source_qpc: u64, original_packet_offset_frames: u32,
    committed:Option<crate::source_age::CommitTiming> }
impl Pending {
    fn decision(&self,now:u64)->AgeDecision {
        self.committed.map_or_else(||classify_wasapi(now,self.source_qpc,self.original_packet_offset_frames),|c|c.classify(now))
    }
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum QueueAdmission { Queued, Expired, Full }
#[derive(Clone, Copy)]
pub enum PcmTiming {
    Wasapi { source_qpc: u64, original_packet_offset_frames: u32 },
    Committed(crate::source_age::CommitTiming),
}
pub struct PlaintextExpiry {
    entries: Vec<Pending>,
    pub expired: u64, pub orphaned: u64, pub absent: u64,
    pub high_water: usize,
    pub pcm_built: u64,
    pub build_max_ns: u64,
    pub last_offer_qpc: u64,
}
impl Default for PlaintextExpiry {
    fn default() -> Self {
        Self { entries: Vec::with_capacity(CAPACITY), expired: 0,
               orphaned: 0, absent: 0, high_water: 0, pcm_built: 0, build_max_ns: 0, last_offer_qpc: 0 }
    }
}
impl Identity {
    pub fn from_wire(w: &[u8]) -> Option<Self> {
        if w.len() != 1000 || w[0] != 1 || w[1] != 1 { return None; }
        let u32_at = |o: usize| u32::from_be_bytes(w[o..o+4].try_into().unwrap());
        Some(Self {
            epoch:u32_at(8), stream:u32_at(12),format:u32_at(16),
            sequence:u32_at(20),
            source_frame:u64::from_be_bytes(w[24..32].try_into().unwrap()),
        })
    }
}
impl PlaintextExpiry {
    /// Move one final PCM allocation into quiche, retaining original source timing.
    /// All non-Done enqueue errors are terminal: quiche can fail AFTER enqueue.
    pub fn admit_pcm(
        &mut self, conn: &mut Connection, header: &crate::Header,
        pcm: &[u8; 960], timing: PcmTiming,
    ) -> Result<QueueAdmission, Box<dyn std::error::Error>> {
        self.admit_pcm_with_clock(conn, header, pcm, timing, crate::source_age::local_qpc_100ns)
    }

    fn admit_pcm_with_clock(
        &mut self, conn: &mut Connection, header: &crate::Header,
        pcm: &[u8; 960], timing: PcmTiming,
        mut clock: impl FnMut() -> std::io::Result<u64>,
    ) -> Result<QueueAdmission, Box<dyn std::error::Error>> {
        if header.version != crate::VERSION || header.kind != crate::KIND_AUDIO ||
            header.header_bytes as usize != crate::HEADER_BYTES || header.frame_count != 240 ||
            header.payload_bytes != 960 {
            return Err(quiche::Error::InvalidState.into());
        }
        let id = Identity { epoch: header.media_epoch, stream: header.source_stream_id,
            format: header.format_config_id, sequence: header.packet_sequence,
            source_frame: header.first_source_frame };
        let offer = match timing {
            PcmTiming::Wasapi { source_qpc, original_packet_offset_frames } => Pending {
                id, source_qpc, original_packet_offset_frames, committed: None },
            PcmTiming::Committed(timing) => Pending { id, source_qpc: timing.presentation_qpc,
                original_packet_offset_frames: 0, committed: Some(timing) },
        };
        let now = clock()?;
        self.last_offer_qpc = now;
        self.purge(conn, now);
        if !matches!(offer.decision(now), AgeDecision::Fresh { .. }) {
            return Ok(QueueAdmission::Expired);
        }
        if self.entries.iter().any(|entry| entry.id == id) {
            return Err(quiche::Error::InvalidState.into());
        }
        if self.entries.len() >= CAPACITY || conn.is_dgram_send_queue_full() {
            return Ok(QueueAdmission::Full);
        }
        let started = std::time::Instant::now();
        let mut wire = Vec::with_capacity(crate::HEADER_BYTES + pcm.len());
        wire.extend_from_slice(&crate::encode(header));
        wire.extend_from_slice(pcm);
        self.pcm_built += 1;
        self.build_max_ns = self.build_max_ns.max(started.elapsed().as_nanos().min(u64::MAX as u128) as u64);
        // Never cache admission age across allocation/packet construction.
        let final_now = clock()?;
        self.last_offer_qpc = final_now;
        if !matches!(offer.decision(final_now), AgeDecision::Fresh { .. }) {
            return Ok(QueueAdmission::Expired);
        }
        self.entries.push(offer);
        self.high_water = self.high_water.max(self.entries.len());
        match conn.dgram_send_buf(wire) {
            Ok(()) => Ok(QueueAdmission::Queued),
            Err(error) => {
                self.entries.retain(|entry| entry.id != id);
                if error == quiche::Error::Done { Ok(QueueAdmission::Full) }
                else { Err(error.into()) } // caller MUST retire without another conn.send
            }
        }
    }

    /// Reconcile even when ciphertext is pending, then reserve metadata BEFORE
    /// quiche takes a plaintext copy. Queue pressure is a controlled source
    /// drop; a failed enqueue never leaves a reservation behind.
    pub fn admit(
        &mut self, conn: &mut Connection, wire: &[u8],
        source_qpc: u64, original_packet_offset_frames: u32, now_qpc: u64,
    ) -> Result<QueueAdmission, quiche::Error> {
        self.purge(conn, now_qpc);
        if !matches!(
            classify_wasapi(now_qpc, source_qpc, original_packet_offset_frames),
            AgeDecision::Fresh { .. }
        ) {
            return Ok(QueueAdmission::Expired);
        }
        let id = Identity::from_wire(wire).ok_or(quiche::Error::InvalidState)?;
        if self.entries.iter().any(|entry| entry.id == id) {
            return Err(quiche::Error::InvalidState);
        }
        if self.accept(id, source_qpc, original_packet_offset_frames).is_err() {
            return Ok(QueueAdmission::Full);
        }
        match conn.dgram_send(wire) {
            Ok(()) => Ok(QueueAdmission::Queued),
            Err(error) => {
                self.entries.retain(|entry| entry.id != id);
                if error == quiche::Error::Done { Ok(QueueAdmission::Full) }
                else { Err(error) }
            }
        }
    }

    pub fn admit_committed(&mut self,conn:&mut Connection,wire:&[u8],timing:crate::source_age::CommitTiming,now:u64)
        ->Result<QueueAdmission,quiche::Error> {
        self.purge(conn,now);
        if !matches!(timing.classify(now),AgeDecision::Fresh{..}) {return Ok(QueueAdmission::Expired)}
        let id=Identity::from_wire(wire).ok_or(quiche::Error::InvalidState)?;
        if self.entries.iter().any(|p|p.id==id){return Err(quiche::Error::InvalidState)}
        if self.accept(id,timing.presentation_qpc,0).is_err(){return Ok(QueueAdmission::Full)}
        self.entries.last_mut().unwrap().committed=Some(timing);
        match conn.dgram_send(wire) {
            Ok(())=>Ok(QueueAdmission::Queued),
            Err(error)=> {self.entries.retain(|p|p.id!=id);if error==quiche::Error::Done{Ok(QueueAdmission::Full)}else{Err(error)}}
        }
    }

    fn accept(&mut self, id: Identity, source_qpc: u64,
              original_packet_offset_frames: u32) -> Result<(), &'static str> {
        if self.entries.len() >= CAPACITY { return Err("plaintext metadata bound"); }
        if self.entries.iter().any(|p| p.id == id) { return Err("duplicate plaintext identity"); }
        self.entries.push(Pending{id, source_qpc, original_packet_offset_frames,committed:None});
        self.high_water=self.high_water.max(self.entries.len());
        Ok(())
    }
    // This sees ONLY still-queued DATAGRAM plaintext. No access to already
    // sealed packet contents or QUIC rollback. Missing entries are reclaimed.
    pub fn purge(&mut self, conn: &mut Connection, now_qpc: u64) {
        let present: [std::cell::Cell<bool>; CAPACITY] =
            std::array::from_fn(|_| std::cell::Cell::new(false));
        let expired: [std::cell::Cell<bool>; CAPACITY] =
            std::array::from_fn(|_| std::cell::Cell::new(false));
        let unknown = std::cell::Cell::new(0u64);
        conn.dgram_purge_outgoing(|bytes| {
            match Identity::from_wire(bytes) {
                Some(id) => {
                    if let Some((index, entry)) = self.entries.iter().enumerate()
                        .find(|(_, item)| item.id == id)
                    {
                        present[index].set(true);
                        expired[index].set(!matches!(
                            entry.decision(now_qpc),
                            AgeDecision::Fresh { .. }
                        ));
                        expired[index].get()
                    } else {
                        unknown.set(unknown.get() + 1);
                        true // Unknown/retired media is never newly sealed
                    }
                }
                None => false, // non-media DATAGRAMs do not count as PCM
            }
        });
        self.orphaned += unknown.get();
        // Compact in place: cleanup must not allocate a new metadata Vec on
        // every send opportunity. The ciphertext needs no plaintext entry.
        let mut index = 0;
        let mut absent_count = 0;
        let mut expired_count = 0;
        self.entries.retain(|_| {
            let i = index;
            index += 1;
            if !present[i].get() { absent_count += 1; false }
            else if expired[i].get() { expired_count += 1; false }
            else { true }
        });
        self.absent += absent_count;
        self.expired += expired_count;
    }

    /// Relative one-shot obligation in the host QPC domain. Freshness includes
    /// the exact 60-ms boundary, so cleanup becomes due one 100-ns tick later.
    /// The caller pairs this QPC observation with its local Instant; no raw
    /// QPC/Instant values are compared and no periodic media tick is created.
    pub fn next_expiry_wait(&self, now_qpc: u64) -> Option<Duration> {
        self.entries.iter().filter_map(|entry| {
            if !matches!(
                entry.decision(now_qpc),
                AgeDecision::Fresh { .. }
            ) {
                return Some(Duration::ZERO);
            }
            if let Some(timing)=entry.committed {
                return timing.expiry().map(|expiry|Duration::from_nanos(expiry.saturating_sub(now_qpc)*100));
            }
            entry.source_qpc.checked_add(MAX_SOURCE_AGE_100NS)?
                .checked_add(1)
                .map(|expiry| Duration::from_nanos(expiry.saturating_sub(now_qpc) * 100))
        }).min()
    }
    pub fn len(&self) -> usize { self.entries.len() }
    pub fn clear(&mut self) { self.entries.clear(); }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn owned_pcm_checks_age_before_allocation_and_again_before_transfer() {
        let local = "127.0.0.1:49001".parse().unwrap();
        let remote = "127.0.0.1:49002".parse().unwrap();
        let mut config = quiche::Config::new(quiche::PROTOCOL_VERSION).unwrap();
        config.set_application_protos(&[b"df-owned-pcm-test"]).unwrap();
        config.enable_dgram(true, 4, 4);
        let id = quiche::ConnectionId::from_ref(&[0x53; 16]);
        let mut conn = quiche::connect(None, &id, local, remote, &mut config).unwrap();
        let header = crate::Header { version: 1, kind: 1, flags: 0, header_bytes: 40,
            frame_count: 240, media_epoch: 1, source_stream_id: 1, format_config_id: 1,
            packet_sequence: 7, first_source_frame: 48000, payload_bytes: 960, clock_anchor_id: 17 };
        let pcm = [0x5a; 960];
        let timing = PcmTiming::Committed(crate::source_age::CommitTiming {
            commit_qpc: 1_000_000, presentation_qpc: 1_100_000 });
        let mut ledger = PlaintextExpiry::default();
        assert_eq!(ledger.admit_pcm_with_clock(&mut conn, &header, &pcm, timing,
            || Ok(1_600_001)).unwrap(), QueueAdmission::Expired);
        assert_eq!(ledger.pcm_built, 0, "expired commit must not allocate, even with fresh presentation");
        let mut observations = [1_600_000, 1_600_001].into_iter();
        assert_eq!(ledger.admit_pcm_with_clock(&mut conn, &header, &pcm, timing,
            || Ok(observations.next().unwrap())).unwrap(), QueueAdmission::Expired);
        assert_eq!(ledger.pcm_built, 1);
        assert_eq!(ledger.len(), 0);
        assert_eq!(conn.dgram_send_queue_byte_size(), 0);
        // Exactly 60 ms passes age admission, reaching the real unestablished-peer error.
        assert!(ledger.admit_pcm_with_clock(&mut conn, &header, &pcm, timing,
            || Ok(1_600_000)).is_err());
        assert_eq!(ledger.pcm_built, 2);
        assert_eq!(ledger.len(), 0, "fatal enqueue must remove its reservation");
        // No conn.send follows the fatal error: the fixture drops the connection.
    }

    #[test]
    fn exact_identity_and_bound() {
        let mut w=[0u8;1000];w[0]=1;w[1]=1;
        w[8..12].copy_from_slice(&9u32.to_be_bytes());
        w[12..16].copy_from_slice(&5u32.to_be_bytes());
        w[16..20].copy_from_slice(&3u32.to_be_bytes());
        w[20..24].copy_from_slice(&11u32.to_be_bytes());
        w[24..32].copy_from_slice(&48000u64.to_be_bytes());
        let i=Identity::from_wire(&w).unwrap();
        assert_eq!(i.epoch,9);assert_eq!(i.stream,5);assert_eq!(i.format,3);
        assert_eq!(i.sequence,11);assert_eq!(i.source_frame,48000);
        let mut ledger=PlaintextExpiry::default();
        for n in 0..4 {ledger.accept(Identity{sequence:n,..i},100,0).unwrap();}
        assert!(ledger.accept(i,100,0).is_err());
        assert!(ledger.accept(Identity{sequence:9,..i},100,0).is_err());
        assert_eq!(ledger.high_water,4);
        ledger.clear();assert_eq!(ledger.len(),0);
    }
}

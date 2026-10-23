//! Bounded connection-owner-only audio plaintext tracking. Hooked before
//! EVERY quiche Connection::send packet construction. No seal callback assumed.
use crate::source_age::{classify, AgeDecision};
use quiche::Connection;

const CAPACITY: usize = 4;
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Identity {
    pub epoch: u32, pub stream: u32,
    pub sequence: u32, pub source_frame: u64,
}
#[derive(Debug, Clone, Copy)]
struct Pending { id: Identity, source_qpc: u64 }
#[derive(Default)]
pub struct PlaintextExpiry {
    entries: Vec<Pending>,
    pub expired: u64, pub orphaned: u64, pub absent: u64,
    pub high_water: usize,
}
impl Identity {
    pub fn from_wire(w: &[u8]) -> Option<Self> {
        if w.len() != 1000 || w[0] != 1 || w[1] != 1 { return None; }
        let u32_at = |o: usize| u32::from_be_bytes(w[o..o+4].try_into().unwrap());
        Some(Self {
            epoch:u32_at(8), stream:u32_at(12),sequence:u32_at(20),
            source_frame:u64::from_be_bytes(w[24..32].try_into().unwrap()),
        })
    }
}
impl PlaintextExpiry {
    pub fn accept(&mut self, id: Identity, source_qpc: u64) -> Result<(), &'static str> {
        if self.entries.len() >= CAPACITY { return Err("plaintext metadata bound"); }
        if self.entries.iter().any(|p| p.id == id) { return Err("duplicate plaintext identity"); }
        self.entries.push(Pending{id, source_qpc});
        self.high_water=self.high_water.max(self.entries.len());
        Ok(())
    }
    // This sees ONLY still-queued DATAGRAM plaintext. No access to already
    // sealed packet contents or QUIC rollback. Missing entries are reclaimed.
    pub fn purge(&mut self, conn: &mut Connection, now_qpc: u64) {
        let mut present = [false; CAPACITY];
        let mut expired = [false; CAPACITY];
        conn.dgram_purge_outgoing(|bytes| {
            match Identity::from_wire(bytes) {
                Some(id) => {
                    if let Some((index, entry)) = self.entries.iter().enumerate()
                        .find(|(_, item)| item.id == id)
                    {
                        present[index]=true;
                        expired[index]=!matches!(classify(now_qpc,entry.source_qpc),
                                             AgeDecision::Fresh{..});
                        expired[index]
                    } else {
                        self.orphaned += 1;
                        true // Unknown/retired media is never newly sealed
                    }
                }
                None => false, // non-media DATAGRAMs do not count as PCM
            }
        });
        let old = std::mem::take(&mut self.entries);
        for (index, entry) in old.into_iter().enumerate() {
            if !present[index] { self.absent += 1; }
            else if expired[index] { self.expired += 1; }
            else { self.entries.push(entry); }
        }
    }
    pub fn len(&self) -> usize { self.entries.len() }
    pub fn clear(&mut self) { self.entries.clear(); }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exact_identity_and_bound() {
        let mut w=[0u8;1000];w[0]=1;w[1]=1;
        w[8..12].copy_from_slice(&9u32.to_be_bytes());
        w[12..16].copy_from_slice(&5u32.to_be_bytes());
        w[20..24].copy_from_slice(&11u32.to_be_bytes());
        w[24..32].copy_from_slice(&48000u64.to_be_bytes());
        let i=Identity::from_wire(&w).unwrap();
        assert_eq!(i.epoch,9);assert_eq!(i.stream,5);
        assert_eq!(i.sequence,11);assert_eq!(i.source_frame,48000);
        let mut ledger=PlaintextExpiry::default();
        for n in 0..4 {ledger.accept(Identity{sequence:n,..i},100).unwrap();}
        assert!(ledger.accept(i,100).is_err());
        assert!(ledger.accept(Identity{sequence:9,..i},100).is_err());
        assert_eq!(ledger.high_water,4);
        ledger.clear();assert_eq!(ledger.len(),0);
    }
}

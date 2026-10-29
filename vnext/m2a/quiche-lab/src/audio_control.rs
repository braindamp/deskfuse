//! Bounded control only. No certificate/private-key paths, PCM, credentials or
//! peer-supplied process facts are accepted by the SYSTEM-side command codec.
use crate::{
    audio_authority::ControlGrant,
    audio_tls::{ALPN, MAX_CONTROL},
};
use std::io;
pub const INFO: u8 = 1;
pub const PREPARE: u8 = 2;
pub const ISSUE: u8 = 3;
pub const PROOF: u8 = 4;
pub const STATUS: u8 = 5;
pub const OUTPUT: u8 = 6;
pub const DETACH: u8 = 7;
pub const STOP: u8 = 8;
pub const SIGNED_SOURCE_STOP: u8 = 9;
pub const SOURCE_STOP_STATUS: u8 = 10;
pub const APPLIED_RESULT: u8 = 11;
pub const NOTICE: u8 = 128;
pub const ERROR: u8 = 255;
pub const MAX_PENDING: usize = 8;
/// Setup-only selection. Canonical Source.v1 always denotes Game.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum DeliveryMode {
    Game = 1,
    Interactive = 2,
    Streaming = 3,
}
impl DeliveryMode {
    pub fn require_supported(mode: u8) -> io::Result<Self> {
        if mode != Self::Game as u8 {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Unsupported audio delivery mode",
            ));
        }
        Ok(Self::Game)
    }
}
pub(crate) fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid audio control record")
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Record {
    pub kind: u8,
    pub request: u64,
    pub body: Vec<u8>,
}
impl Record {
    pub fn encode(&self) -> io::Result<Vec<u8>> {
        if !valid_kind(self.kind)
            || self.body.len() > MAX_CONTROL - 9
            || (self.request == 0) != (self.kind == NOTICE)
        {
            return Err(invalid());
        }
        let mut out = Vec::with_capacity(13 + self.body.len());
        out.extend_from_slice(&((9 + self.body.len()) as u32).to_be_bytes());
        out.push(self.kind);
        out.extend_from_slice(&self.request.to_be_bytes());
        out.extend_from_slice(&self.body);
        Ok(out)
    }
}
fn valid_kind(k: u8) -> bool {
    (INFO..=APPLIED_RESULT).contains(&k) || (129..=139).contains(&k) || k == NOTICE || k == ERROR
}
pub(crate) fn negative(k: u8) -> bool {
    matches!(k, STOP | SIGNED_SOURCE_STOP | 136 | 137)
}
/// One partial record, not another transport/PCM queue. The caller gives each
/// complete record a bounded work budget and checks control fences between turns.
pub struct Decoder {
    bytes: Vec<u8>,
    expected: Option<usize>,
}
impl Default for Decoder {
    fn default() -> Self {
        Self {
            bytes: Vec::with_capacity(MAX_CONTROL + 4),
            expected: None,
        }
    }
}
impl Decoder {
    pub fn push(&mut self, input: &[u8]) -> io::Result<(usize, Option<Record>)> {
        let target = self.expected.unwrap_or(4);
        let take = (target - self.bytes.len()).min(input.len());
        self.bytes.extend_from_slice(&input[..take]);
        if self.bytes.len() == 4 && self.expected.is_none() {
            let n = u32::from_be_bytes(self.bytes[..4].try_into().unwrap()) as usize;
            if !(9..=MAX_CONTROL).contains(&n) {
                return Err(invalid());
            }
            self.expected = Some(n + 4);
        }
        if self.expected != Some(self.bytes.len()) {
            return Ok((take, None));
        }
        let record = Record {
            kind: self.bytes[4],
            request: u64::from_be_bytes(self.bytes[5..13].try_into().unwrap()),
            body: self.bytes[13..].to_vec(),
        };
        let _ = record.encode()?;
        self.bytes.clear();
        self.expected = None;
        Ok((take, Some(record)))
    }
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Observation {
    pub connection: [u8; 16],
    pub native: [u8; 32],
    pub phone: [u8; 32],
    pub nonce: [u8; 32],
}
impl Observation {
    pub fn encode(&self) -> io::Result<Vec<u8>> {
        if self.connection == [0; 16]
            || self.native == [0; 32]
            || self.phone == [0; 32]
            || self.nonce == [0; 32]
        {
            return Err(invalid());
        }
        let mut b = Vec::with_capacity(112);
        b.extend_from_slice(&self.connection);
        b.extend_from_slice(&self.native);
        b.extend_from_slice(&self.phone);
        b.extend_from_slice(&self.nonce);
        Ok(b)
    }
    pub fn parse(b: &[u8]) -> io::Result<Self> {
        let mut r = Reader(b);
        let value = Self {
            connection: r.take()?,
            native: r.take()?,
            phone: r.take()?,
            nonce: r.take()?,
        };
        r.end()?;
        value.encode()?;
        Ok(value)
    }
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SourceScope {
    pub host: [u8; 32],
    pub namespace: [u8; 16],
    pub enrollment: [u8; 16],
    pub worker: [u8; 16],
    pub source: [u8; 16],
    pub generation: u64,
    pub device_key: [u8; 16],
    pub digest: [u8; 32],
    pub wire: [u32; 3],
    pub grace100ns: u64,
}
impl SourceScope {
    pub fn parse(b: &[u8]) -> io::Result<Self> {
        if b.len() > 2048 {
            return Err(invalid());
        }
        let mut r = Reader(b);
        r.domain(b"DeskFuse.Audio.Source.v1\0")?;
        let host = r.nonzero()?;
        let namespace = r.nonzero()?;
        let enrollment = r.nonzero()?;
        r.nonzero::<32>()?;
        r.sid(false)?;
        let worker = r.nonzero()?;
        r.sid(true)?;
        r.u64()?;
        r.nonzero_u32()?;
        r.nonzero_u32()?;
        r.nonzero_u64()?;
        let source = r.nonzero()?;
        let generation = r.nonzero_u64()?;
        let device_key = r.nonzero::<16>()?;
        if !matches!(r.byte()?, 1 | 2) {
            return Err(invalid());
        }
        r.nonzero::<32>()?;
        if r.u32()? != 48000 || r.u16()? != 2 || r.u16()? != 16 || r.u16()? != 240 || r.byte()? > 1
        {
            return Err(invalid());
        }
        let duration = r.nonzero_u32()?;
        let grace = r.u32()?;
        if grace > duration {
            return Err(invalid());
        }
        let wire = [r.nonzero_u32()?, r.nonzero_u32()?, r.nonzero_u32()?];
        r.end()?;
        Ok(Self {
            host,
            namespace,
            enrollment,
            worker,
            source,
            generation,
            device_key,
            digest: boring::sha::sha256(b),
            wire,
            grace100ns: u64::from(grace) * 10000,
        })
    }
    pub fn matches(&self, g: &ControlGrant) -> bool {
        g.host == self.host
            && g.namespace == self.namespace
            && g.enrollment == self.enrollment
            && g.worker == self.worker
            && g.source == self.source
            && g.source_generation == self.generation
            && g.source_digest == self.digest
    }
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeliveryBinding {
    pub source: [u8; 16],
    pub source_digest: [u8; 32],
    pub source_generation: u64,
    pub binding: [u8; 32],
    pub worker: [u8; 16],
    pub generation: u64,
    pub wire: [u32; 3],
    pub native: [u8; 32],
    pub phone: [u8; 32],
    pub request: [u8; 32],
    pub nonce: [u8; 32],
    pub digest: [u8; 32],
}
impl DeliveryBinding {
    pub fn parse(b: &[u8]) -> io::Result<Self> {
        if b.len() > 2048 {
            return Err(invalid());
        }
        let mut r = Reader(b);
        r.domain(b"DeskFuse.Audio.Delivery.v1\0")?;
        let mut v = Self {
            source: r.nonzero()?,
            source_digest: r.nonzero()?,
            source_generation: r.nonzero_u64()?,
            binding: r.nonzero()?,
            worker: r.nonzero()?,
            generation: r.nonzero_u64()?,
            wire: [r.nonzero_u32()?, r.nonzero_u32()?, r.nonzero_u32()?],
            native: r.nonzero()?,
            phone: r.nonzero()?,
            request: r.nonzero()?,
            nonce: r.nonzero()?,
            digest: [0; 32],
        };
        let n = r.u16()? as usize;
        if r.bytes(n)? != ALPN || r.byte()? != 1 {
            return Err(invalid());
        }
        r.end()?;
        if u64::from_be_bytes(v.request[..8].try_into().unwrap()) == 0
            || v.request[8..].iter().all(|b| *b == 0)
        {
            return Err(invalid());
        }
        v.digest = boring::sha::sha256(b);
        Ok(v)
    }
    pub fn matches(
        &self,
        scope: &SourceScope,
        observation: &Observation,
        g: &ControlGrant,
    ) -> bool {
        scope.matches(g)
            && self.source == scope.source
            && self.source_digest == scope.digest
            && self.source_generation == scope.generation
            && self.worker == scope.worker
            && self.wire == scope.wire
            && self.binding == g.binding
            && self.generation == g.delivery_generation
            && self.digest == g.delivery_digest
            && self.native == observation.native
            && self.phone == observation.phone
            && self.nonce == observation.nonce
    }
}

pub(crate) struct Reader<'a>(pub(crate) &'a [u8]);
impl<'a> Reader<'a> {
    pub(crate) fn bytes(&mut self, n: usize) -> io::Result<&'a [u8]> {
        if self.0.len() < n {
            return Err(invalid());
        }
        let (a, b) = self.0.split_at(n);
        self.0 = b;
        Ok(a)
    }
    pub(crate) fn take<const N: usize>(&mut self) -> io::Result<[u8; N]> {
        Ok(self.bytes(N)?.try_into().unwrap())
    }
    pub(crate) fn nonzero<const N: usize>(&mut self) -> io::Result<[u8; N]> {
        let b = self.take()?;
        if b == [0; N] {
            Err(invalid())
        } else {
            Ok(b)
        }
    }
    pub(crate) fn byte(&mut self) -> io::Result<u8> {
        Ok(self.take::<1>()?[0])
    }
    pub(crate) fn u16(&mut self) -> io::Result<u16> {
        Ok(u16::from_be_bytes(self.take()?))
    }
    pub(crate) fn u32(&mut self) -> io::Result<u32> {
        Ok(u32::from_be_bytes(self.take()?))
    }
    pub(crate) fn u64(&mut self) -> io::Result<u64> {
        Ok(u64::from_be_bytes(self.take()?))
    }
    fn nonzero_u32(&mut self) -> io::Result<u32> {
        let n = self.u32()?;
        if n == 0 {
            Err(invalid())
        } else {
            Ok(n)
        }
    }
    fn nonzero_u64(&mut self) -> io::Result<u64> {
        let n = self.u64()?;
        if n == 0 {
            Err(invalid())
        } else {
            Ok(n)
        }
    }
    fn sid(&mut self, logon: bool) -> io::Result<()> {
        let n = self.byte()? as usize;
        let b = self.bytes(n)?;
        if !(8..=68).contains(&n) || b[0] != 1 || b[1] > 15 || n != 8 + usize::from(b[1]) * 4 {
            return Err(invalid());
        }
        if logon && (n != 20 || b[..8] != [1, 3, 0, 0, 0, 0, 0, 5] || b[8..12] != [5, 0, 0, 0]) {
            return Err(invalid());
        }
        Ok(())
    }
    fn domain(&mut self, d: &[u8]) -> io::Result<()> {
        if self.bytes(d.len())? != d {
            Err(invalid())
        } else {
            Ok(())
        }
    }
    pub(crate) fn end(self) -> io::Result<()> {
        if self.0.is_empty() {
            Ok(())
        } else {
            Err(invalid())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fragmented_control_is_bounded_and_not_confused_with_a_grant() {
        let record = Record {
            kind: ISSUE,
            request: u64::MAX,
            body: vec![0x37; 512],
        };
        let bytes = record.encode().unwrap();
        for split in 0..bytes.len() {
            let mut decoder = Decoder::default();
            let mut completed = None;
            for part in [&bytes[..split], &bytes[split..]] {
                let mut at = 0;
                while at < part.len() {
                    let (n, r) = decoder.push(&part[at..]).unwrap();
                    at += n;
                    if r.is_some() {
                        assert!(completed.is_none());
                        completed = r;
                    }
                }
            }
            assert_eq!(completed.as_ref(), Some(&record));
            assert!(decoder.is_empty());
        }
        for length in [0u32, 8, (MAX_CONTROL + 1) as u32, u32::MAX] {
            assert!(Decoder::default().push(&length.to_be_bytes()).is_err());
        }
        assert!(Record {
            kind: ISSUE,
            request: 0,
            body: vec![]
        }
        .encode()
        .is_err());
        assert!(Record {
            kind: NOTICE,
            request: 1,
            body: vec![]
        }
        .encode()
        .is_err());
    }
}

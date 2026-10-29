//! Negative-only control codecs. Parsing is never source/media authorization.
use crate::audio_control::{invalid, Observation, Reader, SourceScope};
use std::io;
fn hash<const N: usize>(r: &mut Reader<'_>) -> io::Result<[u8; N]> {
    let b = r.take()?;
    if b == [0; N] {
        Err(invalid())
    } else {
        Ok(b)
    }
}
fn domain(r: &mut Reader<'_>, name: &[u8]) -> io::Result<()> {
    if r.bytes(name.len())? != name {
        Err(invalid())
    } else {
        Ok(())
    }
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StopTarget {
    pub host: [u8; 32],
    pub namespace: [u8; 16],
    pub enrollment: [u8; 16],
    pub boot: [u8; 32],
    pub worker: [u8; 16],
    pub source: [u8; 16],
    pub generation: u64,
    pub digest: [u8; 32],
    pub device_key: [u8; 16],
}
impl StopTarget {
    fn read(r: &mut Reader<'_>) -> io::Result<Self> {
        let host = hash(r)?;
        let namespace = hash(r)?;
        let enrollment = hash(r)?;
        if r.byte()? != 1 {
            return Err(invalid());
        }
        let value = Self {
            host,
            namespace,
            enrollment,
            boot: hash(r)?,
            worker: hash(r)?,
            source: hash(r)?,
            generation: r.u64()?,
            digest: hash(r)?,
            device_key: hash(r)?,
        };
        if value.generation == 0 {
            return Err(invalid());
        }
        Ok(value)
    }
    pub fn matches(&self, scope: &SourceScope, boot: [u8; 32]) -> bool {
        self.host == scope.host
            && self.namespace == scope.namespace
            && self.enrollment == scope.enrollment
            && self.boot == boot
            && self.worker == scope.worker
            && self.source == scope.source
            && self.generation == scope.generation
            && self.digest == scope.digest
            && self.device_key == scope.device_key
    }
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SourceStop {
    pub target: StopTarget,
    pub intent: [u8; 32],
    pub connection: [u8; 16],
    pub native: [u8; 32],
    pub phone: [u8; 32],
    pub nonce: [u8; 32],
}
impl SourceStop {
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        if bytes.len() > 1024 {
            return Err(invalid());
        }
        let mut r = Reader(bytes);
        domain(&mut r, b"DeskFuse.Audio.SourceStop.v1\0")?;
        let value = Self {
            target: StopTarget::read(&mut r)?,
            intent: hash(&mut r)?,
            connection: hash(&mut r)?,
            native: hash(&mut r)?,
            phone: hash(&mut r)?,
            nonce: hash(&mut r)?,
        };
        r.end()?;
        Ok(value)
    }
    pub fn matches_observation(&self, o: &Observation) -> bool {
        self.connection == o.connection
            && self.native == o.native
            && self.phone == o.phone
            && self.nonce == o.nonce
    }
}
pub struct SignedSourceStop<'a> {
    pub proof: SourceStop,
    pub transcript: &'a [u8],
    pub signature: &'a [u8],
}
impl<'a> SignedSourceStop<'a> {
    pub fn parse(bytes: &'a [u8]) -> io::Result<Self> {
        if bytes.len() > 1099 {
            return Err(invalid());
        }
        let mut r = Reader(bytes);
        let n = r.u16()? as usize;
        if n > 1024 {
            return Err(invalid());
        }
        let transcript = r.bytes(n)?;
        let proof = SourceStop::parse(transcript)?;
        let n = r.byte()? as usize;
        if !(8..=72).contains(&n) {
            return Err(invalid());
        }
        let signature = r.bytes(n)?;
        r.end()?;
        Ok(Self {
            proof,
            transcript,
            signature,
        })
    }
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum StopState {
    Unknown = 0,
    Denied = 1,
    RundownComplete = 2,
    Quarantined = 3,
}
pub struct StopStatus {
    pub query: [u8; 32],
    pub target: StopTarget,
    pub revision: u64,
    pub state: StopState,
}
pub struct StopStatusQuery {
    pub query: [u8; 32],
    pub target: StopTarget,
}
impl StopStatusQuery {
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        let mut r = Reader(bytes);
        let value = Self {
            query: hash(&mut r)?,
            target: StopTarget::read(&mut r)?,
        };
        r.end()?;
        Ok(value)
    }
}
impl StopStatus {
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        if bytes.len() > 1024 {
            return Err(invalid());
        }
        let mut r = Reader(bytes);
        domain(&mut r, b"DeskFuse.Audio.SourceStopStatus.v1\0")?;
        let query = hash(&mut r)?;
        let target = StopTarget::read(&mut r)?;
        let revision = r.u64()?;
        let state = match r.byte()? {
            0 => StopState::Unknown,
            1 => StopState::Denied,
            2 => StopState::RundownComplete,
            3 => StopState::Quarantined,
            _ => return Err(invalid()),
        };
        r.end()?;
        if (revision == 0) != (state == StopState::Unknown) {
            return Err(invalid());
        }
        Ok(Self {
            query,
            target,
            revision,
            state,
        })
    }
}
pub struct AppliedResultQuery<'a> {
    pub request: [u8; 32],
    pub broker_nonce: [u8; 32],
    pub signature: &'a [u8],
}
impl<'a> AppliedResultQuery<'a> {
    pub fn parse(bytes: &'a [u8]) -> io::Result<Self> {
        let mut r = Reader(bytes);
        let request = hash(&mut r)?;
        if u64::from_be_bytes(request[..8].try_into().unwrap()) == 0
            || request[8..].iter().all(|b| *b == 0)
        {
            return Err(invalid());
        }
        let broker_nonce = hash(&mut r)?;
        let n = r.byte()? as usize;
        if !(8..=72).contains(&n) {
            return Err(invalid());
        }
        let signature = r.bytes(n)?;
        r.end()?;
        Ok(Self {
            request,
            broker_nonce,
            signature,
        })
    }
}
pub struct FastSourceStop {
    pub source: [u8; 16],
    pub generation: u64,
    pub digest: [u8; 32],
    pub intent: [u8; 32],
}
impl FastSourceStop {
    pub fn parse(bytes: &[u8]) -> io::Result<Self> {
        let mut r = Reader(bytes);
        let value = Self {
            source: hash(&mut r)?,
            generation: r.u64()?,
            digest: hash(&mut r)?,
            intent: hash(&mut r)?,
        };
        r.end()?;
        if value.generation == 0 {
            return Err(invalid());
        }
        Ok(value)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn canonical_negative_records_are_bounded_and_match_source_facts() {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        ))
        .unwrap();
        let bytes = |key: &str| {
            let s = f[key].as_str().unwrap();
            (0..s.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
                .collect::<Vec<_>>()
        };
        let source: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-transport-v1.json"
        ))
        .unwrap();
        let s = source["sourceHex"].as_str().unwrap();
        let source_bytes = (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect::<Vec<_>>();
        let scope = SourceScope::parse(&source_bytes).unwrap();
        let stop = bytes("stopHex");
        let proof = SourceStop::parse(&stop).unwrap();
        assert!(proof.target.matches(&scope, [18; 32]));
        assert!(!proof.target.matches(&scope, [19; 32]));
        let o = Observation {
            connection: [19; 16],
            native: [15; 32],
            phone: [16; 32],
            nonce: [17; 32],
        };
        assert!(proof.matches_observation(&o));
        assert!(!proof.matches_observation(&Observation {
            nonce: [20; 32],
            ..o
        }));
        let status = StopStatus::parse(&bytes("statusHex")).unwrap();
        assert_eq!(status.state, StopState::Denied);
        assert_eq!(status.revision, 1);
        assert_eq!(
            StopStatusQuery::parse(&bytes("statusQueryHex"))
                .unwrap()
                .target,
            proof.target
        );
        assert_eq!(
            SignedSourceStop::parse(&bytes("signedStopHex"))
                .unwrap()
                .proof,
            proof
        );
        assert_eq!(
            AppliedResultQuery::parse(&bytes("appliedResultHex"))
                .unwrap()
                .request[..8],
            1u64.to_be_bytes()
        );
        assert_eq!(
            FastSourceStop::parse(&bytes("fastStopHex"))
                .unwrap()
                .generation,
            7
        );
        for end in 0..stop.len() {
            assert!(SourceStop::parse(&stop[..end]).is_err());
        }
        let mut extra = stop;
        extra.push(0);
        assert!(SourceStop::parse(&extra).is_err());
        for mode in [0, 2, 3, 255] {
            assert!(crate::audio_control::DeliveryMode::require_supported(mode).is_err());
        }
    }
}

//! Strict setup/control body and native pipe correlation codecs. No PCM path.
use crate::{audio_control as c, audio_source_stop as s};
use std::io;
pub const HEADER: usize = 25;
pub const MAX_BODY: usize = crate::broker_control::MAX_FRAME - 1 - HEADER;
fn nonzero(b: &[u8]) -> bool {
    b.iter().any(|v| *v != 0)
}
fn u64_at(b: &[u8], offset: usize) -> u64 {
    u64::from_be_bytes(b[offset..offset + 8].try_into().unwrap())
}
fn request_id(b: &[u8]) -> bool {
    b.len() == 32 && u64_at(b, 0) != 0 && nonzero(&b[8..])
}
fn proof(b: &[u8]) -> bool {
    (41..=105).contains(&b.len())
        && nonzero(&b[..32])
        && (8..=72).contains(&b[32])
        && b.len() == 33 + b[32] as usize
}
pub fn validate_request(kind: u8, b: &[u8]) -> io::Result<()> {
    let ok = match kind {
        c::INFO => b.is_empty() || b == [1],
        c::PREPARE => {
            b.len() == 56 && u64_at(b, 0) != 0 && request_id(&b[8..40]) && nonzero(&b[40..])
        }
        c::ISSUE => {
            b.len() == 53
                && (1..=4).contains(&b[0])
                && request_id(&b[1..33])
                && (1..=120000).contains(&u32::from_be_bytes(b[33..37].try_into().unwrap()))
                && nonzero(&b[37..]) == matches!(b[0], 2 | 4)
        }
        c::PROOF => proof(b),
        c::STATUS => b.len() == 32 && nonzero(b),
        c::OUTPUT => {
            b.len() == 49
                && nonzero(&b[..32])
                && u64_at(b, 32) != 0
                && u64_at(b, 40) != 0
                && b[48] <= 1
        }
        c::DETACH => b.len() == 40 && nonzero(&b[..32]) && u64_at(b, 32) != 0,
        c::STOP => s::FastSourceStop::parse(b).is_ok(),
        c::SIGNED_SOURCE_STOP => s::SignedSourceStop::parse(b).is_ok(),
        c::SOURCE_STOP_STATUS => s::StopStatusQuery::parse(b).is_ok(),
        c::APPLIED_RESULT => s::AppliedResultQuery::parse(b).is_ok(),
        _ => false,
    };
    if ok && b.len() <= MAX_BODY {
        Ok(())
    } else {
        Err(c::invalid())
    }
}
#[derive(Debug, Eq, PartialEq)]
pub struct Message {
    pub request: u64,
    pub connection: [u8; 16],
    pub kind: u8,
    pub body: Vec<u8>,
}
impl Message {
    pub fn encode(&self, reply: bool) -> io::Result<Vec<u8>> {
        if self.request == 0
            || !nonzero(&self.connection)
            || self.body.len() > MAX_BODY
            || if reply {
                !(129..=139).contains(&self.kind) && self.kind != c::ERROR
            } else {
                !(c::INFO..=c::APPLIED_RESULT).contains(&self.kind)
            }
        {
            return Err(c::invalid());
        }
        if !reply {
            validate_request(self.kind, &self.body)?;
        }
        let mut out = Vec::with_capacity(HEADER + self.body.len());
        out.extend_from_slice(&self.request.to_be_bytes());
        out.extend_from_slice(&self.connection);
        out.push(self.kind);
        out.extend_from_slice(&self.body);
        Ok(out)
    }
    pub fn parse(bytes: &[u8], reply: bool) -> io::Result<Self> {
        if bytes.len() < HEADER || bytes.len() >= crate::broker_control::MAX_FRAME {
            return Err(c::invalid());
        }
        let value = Self {
            request: u64_at(bytes, 0),
            connection: bytes[8..24].try_into().unwrap(),
            kind: bytes[24],
            body: bytes[25..].to_vec(),
        };
        value.encode(reply)?;
        Ok(value)
    }
}

/// Worker-wide forwarding credits, in addition to each TLS connection's bounded
/// stream queues. Candidate overload returns Busy without flooding the broker
/// pipe or disturbing the active source. Closed routes keep outstanding credit
/// until the broker's actual job completion arrives.
#[derive(Default)]
pub struct Forwarding {
    pending: std::collections::BTreeMap<([u8; 16], u64), u8>,
}
impl Forwarding {
    pub fn admit(&mut self, message: &Message) -> io::Result<bool> {
        message.encode(false)?;
        let key = (message.connection, message.request);
        if self.pending.contains_key(&key) {
            return Err(c::invalid());
        }
        let negative = c::negative(message.kind);
        if self
            .pending
            .values()
            .filter(|k| c::negative(**k) == negative)
            .count()
            >= if negative { 1 } else { c::MAX_PENDING }
        {
            return Ok(false);
        }
        self.pending.insert(key, message.kind);
        Ok(true)
    }
    /// Validate before touching the live/quarantined TLS object. A missing TLS
    /// object does not prevent releasing this completed pipe operation.
    pub fn complete(&mut self, message: &Message) -> io::Result<()> {
        message.encode(true)?;
        let key = (message.connection, message.request);
        let kind = *self.pending.get(&key).ok_or_else(c::invalid)?;
        if message.kind != c::ERROR && message.kind != kind + 128 {
            return Err(c::invalid());
        }
        self.pending.remove(&key);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn bytes(f: &serde_json::Value, key: &str) -> Vec<u8> {
        let s = f[key].as_str().unwrap();
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }
    #[test]
    fn pipe_rpc_agrees_with_broker_vectors_and_rejects_context_and_length_substitution() {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-worker-network-v1.json"
        ))
        .unwrap();
        let o = c::Observation::parse(&bytes(&f, "observationHex")).unwrap();
        assert_eq!(o.connection, [19; 16]);
        for key in ["infoHex", "prepareHex", "issueHex", "replyHex"] {
            let b = bytes(&f, key);
            let reply = key == "replyHex";
            let m = Message::parse(&b, reply).unwrap();
            assert_eq!(m.encode(reply).unwrap(), b);
            for end in 0..b.len() {
                if reply && end >= HEADER {
                    continue;
                }
                assert!(Message::parse(&b[..end], reply).is_err(), "{key} {end}");
            }
        }
        let mut m = Message::parse(&bytes(&f, "issueHex"), false).unwrap();
        m.body[37..].fill(0);
        assert!(m.encode(false).is_err());
        m.kind = c::ERROR;
        m.body = vec![0; MAX_BODY];
        assert!(m.encode(true).is_ok());
        m.body.push(0);
        assert!(m.encode(true).is_err());
        let negative: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        ))
        .unwrap();
        for (kind, key) in [
            (c::STOP, "fastStopHex"),
            (c::SIGNED_SOURCE_STOP, "signedStopHex"),
            (c::SOURCE_STOP_STATUS, "statusQueryHex"),
            (c::APPLIED_RESULT, "appliedResultHex"),
        ] {
            let b = bytes(&negative, key);
            validate_request(kind, &b).unwrap();
            for end in 0..b.len() {
                assert!(validate_request(kind, &b[..end]).is_err());
            }
        }
    }
    #[test]
    fn forwarding_capacity_is_global_and_a_closed_route_keeps_its_negative_credit_until_actual_reply(
    ) {
        let mut credits = Forwarding::default();
        for request in 1..=8 {
            assert!(credits
                .admit(&Message {
                    request,
                    connection: [request as u8; 16],
                    kind: c::INFO,
                    body: vec![]
                })
                .unwrap());
        }
        assert!(!credits
            .admit(&Message {
                request: 9,
                connection: [9; 16],
                kind: c::INFO,
                body: vec![]
            })
            .unwrap());
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        ))
        .unwrap();
        let mut stop = Message {
            request: 9,
            connection: [9; 16],
            kind: c::SIGNED_SOURCE_STOP,
            body: bytes(&f, "signedStopHex"),
        };
        assert!(credits.admit(&stop).unwrap());
        stop.request = 10;
        assert!(!credits.admit(&stop).unwrap());
        // No timeout/connection-close method removes an outstanding operation.
        let mut reply = Message {
            request: 9,
            connection: [9; 16],
            kind: 129,
            body: vec![],
        };
        assert!(credits.complete(&reply).is_err());
        assert!(!credits.admit(&stop).unwrap());
        reply.kind = c::ERROR;
        credits.complete(&reply).unwrap();
        assert!(credits.admit(&stop).unwrap());
        assert!(credits.complete(&reply).is_err());
    }
}

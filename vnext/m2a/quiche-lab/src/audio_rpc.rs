//! Strict setup/control body and native pipe correlation codecs. No PCM path.
use crate::{audio_control as c, audio_source_stop as s};
use std::io;
pub const HEADER: usize = 25;
pub const MAX_BODY: usize = crate::broker_control::MAX_FRAME - 1 - HEADER;
pub const PINNED_ISSUE_BYTES: usize = HEADER + 53 + 8 + 32;
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

pub fn pinned_issue(bytes: &[u8]) -> io::Result<(Message, u64, [u8; 32])> {
    if bytes.len() != PINNED_ISSUE_BYTES { return Err(c::invalid()); }
    let message = Message::parse(&bytes[..HEADER + 53], false)?;
    let revision = u64_at(bytes, HEADER + 53);
    let digest: [u8; 32] = bytes[HEADER + 61..].try_into().unwrap();
    if message.kind != c::ISSUE || message.body[0] != 2 || revision == 0 || digest == [0; 32] {
        return Err(c::invalid());
    }
    Ok((message, revision, digest))
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
    pub fn available(&self, message: &Message) -> io::Result<bool> {
        message.encode(false)?;
        let key = (message.connection, message.request);
        if self.pending.contains_key(&key) {
            return Err(c::invalid());
        }
        let negative = c::negative(message.kind);
        Ok(self.pending.values().filter(|kind| c::negative(**kind) == negative).count()
            < if negative { 1 } else { c::MAX_PENDING })
    }
    pub fn admit(&mut self, message: &Message) -> io::Result<bool> {
        if !self.available(message)? { return Ok(false); }
        self.pending.insert((message.connection, message.request), message.kind);
        Ok(true)
    }
    pub(crate) fn abort_unpublished(&mut self, message: &Message) -> io::Result<()> {
        message.encode(false)?;
        let key = (message.connection, message.request);
        if self.pending.get(&key) != Some(&message.kind) {
            return Err(c::invalid());
        }
        self.pending.remove(&key);
        Ok(())
    }
    pub fn validate_reply(&self, message: &Message) -> io::Result<()> {
        message.encode(true)?;
        let key = (message.connection, message.request);
        let kind = *self.pending.get(&key).ok_or_else(c::invalid)?;
        if message.kind != c::ERROR && message.kind != kind + 128 {
            return Err(c::invalid());
        }
        Ok(())
    }
    pub fn complete(&mut self, message: &Message) -> io::Result<()> {
        self.validate_reply(message)?;
        self.pending.remove(&(message.connection, message.request));
        Ok(())
    }
}


#[derive(Default)]
pub struct PipeRequests {
    forwarding: Forwarding,
    last: std::collections::BTreeMap<[u8; 16], u64>,
    mapped: std::collections::BTreeMap<([u8; 16], u64), (u64, u8)>,
}
impl PipeRequests {
    pub fn propose(&self, tls: &Message) -> io::Result<Option<Message>> {
        if !self.forwarding.available(tls)? { return Ok(None); }
        let request = self.last.get(&tls.connection).copied().unwrap_or(0)
            .checked_add(1).ok_or_else(c::invalid)?;
        let pipe = Message { request, connection: tls.connection, kind: tls.kind, body: tls.body.clone() };
        pipe.encode(false)?;
        Ok(Some(pipe))
    }
    pub fn submitted(&mut self, tls: &Message, pipe: &Message) -> io::Result<()> {
        if self.propose(tls)?.as_ref() != Some(pipe) { return Err(c::invalid()); }
        if !self.forwarding.admit(tls)? { return Err(c::invalid()); }
        let key = (pipe.connection, pipe.request);
        if self.mapped.insert(key, (tls.request, tls.kind)).is_some() { return Err(c::invalid()); }
        self.last.insert(pipe.connection, pipe.request);
        Ok(())
    }
    pub fn reply(&mut self, pipe: Message) -> io::Result<Message> {
        let key = (pipe.connection, pipe.request);
        let (tls_id, kind) = self.mapped.get(&key).copied().ok_or_else(c::invalid)?;
        if pipe.kind != c::ERROR && pipe.kind != kind + 128 {
            return Err(c::invalid());
        }
        let tls = Message { request: tls_id, ..pipe };
        self.forwarding.validate_reply(&tls)?;
        self.mapped.remove(&key);
        Ok(tls)
    }
    pub fn disposition(&mut self, tls: &Message) -> io::Result<()> {
        self.forwarding.complete(tls)
    }
    pub fn retire_route(&mut self, connection: [u8; 16]) {
        self.last.remove(&connection);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pinned_issue_requires_exact_regular_issue_and_frozen_parent_evidence() {
        let mut request = [0x72; 32];
        request[..8].copy_from_slice(&2u64.to_be_bytes());
        let mut body = vec![2];
        body.extend_from_slice(&request);
        body.extend_from_slice(&1000u32.to_be_bytes());
        body.extend_from_slice(&[19; 16]);
        let issue = Message { request: 3, connection: [19; 16], kind: c::ISSUE, body };
        let mut frame = issue.encode(false).unwrap();
        frame.extend_from_slice(&12u64.to_be_bytes());
        frame.extend_from_slice(&[0xa5; 32]);
        let (parsed, revision, digest) = pinned_issue(&frame).unwrap();
        assert_eq!(parsed, issue);
        assert_eq!(revision, 12);
        assert_eq!(digest, [0xa5; 32]);
        for end in 0..frame.len() {
            assert!(pinned_issue(&frame[..end]).is_err(), "truncated at {end}");
        }
        let mut bad = frame.clone();
        bad.push(0);
        assert!(pinned_issue(&bad).is_err());
        let mut bad = frame.clone();
        bad[HEADER] = 1;
        assert!(pinned_issue(&bad).is_err());
        let mut bad = frame.clone();
        bad[HEADER + 53..HEADER + 61].fill(0);
        assert!(pinned_issue(&bad).is_err());
        let mut bad = frame.clone();
        bad[HEADER + 61..].fill(0);
        assert!(pinned_issue(&bad).is_err());
        let mut bad = frame;
        bad[8..24].fill(0);
        assert!(pinned_issue(&bad).is_err());
    }

    #[test]
    fn pipe_admission_numbers_only_submitted_requests_and_retains_reply_credit() {
        let mut routes = PipeRequests::default();
        let connection = [17; 16];
        let make = |request, kind, body: Vec<u8>| Message { request, connection, kind, body };
        let first = make(1, c::INFO, vec![]);
        let pipe_first = routes.propose(&first).unwrap().unwrap();
        assert_eq!(pipe_first.request, 1);
        routes.submitted(&first, &pipe_first).unwrap();

        let rejected = make(2, c::ISSUE, vec![0; 53]);
        assert!(routes.propose(&rejected).is_err());
        let next = make(3, c::INFO, vec![]);
        let pipe_next = routes.propose(&next).unwrap().unwrap();
        assert_eq!(pipe_next.request, 2);
        routes.submitted(&next, &pipe_next).unwrap();

        let reply = routes.reply(Message { kind: 129, ..pipe_first }).unwrap();
        assert_eq!(reply.request, 1);
        assert!(routes.forwarding.pending.contains_key(&(connection, 1)));
        routes.disposition(&reply).unwrap();
        let reply = routes.reply(Message { kind: c::ERROR, body: vec![1], ..pipe_next }).unwrap();
        assert_eq!(reply.request, 3);
        routes.disposition(&reply).unwrap();

        for request in 4..=11 {
            let tls = make(request, c::INFO, vec![]);
            let pipe = routes.propose(&tls).unwrap().unwrap();
            assert_eq!(pipe.request, request - 1);
            routes.submitted(&tls, &pipe).unwrap();
        }
        let next = make(12, c::INFO, vec![]);
        assert!(routes.propose(&next).unwrap().is_none());
        let fixture: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        )).unwrap();
        let hex = fixture["signedStopHex"].as_str().unwrap();
        let body = (0..hex.len()).step_by(2).map(|i|
            u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect();
        let stop = make(13, c::SIGNED_SOURCE_STOP, body);
        let pipe_stop = routes.propose(&stop).unwrap().unwrap();
        assert_eq!(pipe_stop.request, 11);
        assert_eq!(pipe_stop.body, stop.body);
        routes.submitted(&stop, &pipe_stop).unwrap();
        assert!(routes.propose(&make(14, c::INFO, vec![])).unwrap().is_none());

        let closed_reply = routes.reply(Message {
            kind: c::ERROR, body: vec![1], ..pipe_stop
        }).unwrap();
        routes.retire_route(connection);
        assert_eq!(closed_reply.request, stop.request);
        routes.disposition(&closed_reply).unwrap();
    }

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

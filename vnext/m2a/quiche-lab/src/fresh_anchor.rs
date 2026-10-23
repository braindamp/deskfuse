//! Minimal QUIC-authenticated recovery challenge for first source frame that
//! enters Windows WASAPI after Android has requested a fresh playback anchor.
//! QUIC DATAGRAMs can reorder or be lost: Android accepts only matching
//! challenge replies and may retry the SAME request nonce. Stale replies and
//! retired context are rejected; there is no shared wall clock assumption.
use crate::receive_frontier::Context;

pub const LEN: usize = 36;
pub const MAGIC: &[u8; 4] = b"DFA1";
pub const REQUEST: u8 = 1;
pub const ANCHOR: u8 = 2;
pub const RETRY_MS: u64 = 120;
pub const TIMEOUT_MS: u64 = 1_200;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RetryAction { Send, Wait, Expired }
pub fn retry_action(
    started: std::time::Instant, retry_at: std::time::Instant,
    now: std::time::Instant, confirmed: bool,
) -> RetryAction {
    if now.saturating_duration_since(started) >=
        std::time::Duration::from_millis(TIMEOUT_MS) {
        RetryAction::Expired
    } else if !confirmed && now >= retry_at {
        RetryAction::Send
    } else {
        RetryAction::Wait
    }
}

// A source block already buffered before receiving the challenge does
// not establish a fresh anchor. The QPC source observation must occur
// strictly after receipt plus the allowed WASAPI future-timestamp skew.
pub fn after_request_capture_qpc(
    request_received_qpc_100ns: u64,
    captured_source_qpc_100ns: u64,
) -> bool {
    request_received_qpc_100ns != 0 &&
    captured_source_qpc_100ns >
        request_received_qpc_100ns.saturating_add(
            crate::source_age::MAX_FUTURE_SKEW_100NS
        )
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Message {
    pub kind: u8,
    pub nonce: u64,
    /// Request: rejection frontier; response: first fresh captured source frame.
    pub frame: u64,
    pub context: Context,
}
impl Message {
    pub fn confirms(&self, challenge: &Self) -> bool {
        self.kind == ANCHOR && challenge.kind == REQUEST &&
        self.nonce == challenge.nonce && self.context == challenge.context &&
        self.frame >= challenge.frame
    }
    pub fn encode(self) -> [u8; LEN] {
        let mut out = [0; LEN];
        out[..4].copy_from_slice(MAGIC);
        out[4] = self.kind;
        out[8..16].copy_from_slice(&self.nonce.to_be_bytes());
        out[16..24].copy_from_slice(&self.frame.to_be_bytes());
        out[24..28].copy_from_slice(&self.context.epoch.to_be_bytes());
        out[28..32].copy_from_slice(&self.context.stream.to_be_bytes());
        out[32..36].copy_from_slice(&self.context.format.to_be_bytes());
        out
    }
    pub fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() != LEN || &bytes[..4] != MAGIC ||
            !matches!(bytes[4], REQUEST | ANCHOR) ||
            bytes[5..8].iter().any(|x| *x != 0)
        { return None; }
        let get64 = |o: usize| u64::from_be_bytes(bytes[o..o+8].try_into().unwrap());
        let get32 = |o: usize| u32::from_be_bytes(bytes[o..o+4].try_into().unwrap());
        Some(Self {
            kind: bytes[4], nonce: get64(8), frame: get64(16),
            context: Context { epoch: get32(24), stream: get32(28), format: get32(32) },
        })
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn buffered_old_capture_cannot_satisfy_fresh_request() {
        let receipt = 1_000_000u64;
        assert!(!after_request_capture_qpc(receipt, 999_999));
        assert!(!after_request_capture_qpc(receipt, receipt + 100_000));
        assert!(after_request_capture_qpc(receipt, receipt + 100_001));
        assert!(!after_request_capture_qpc(0, 1_000_000));
        assert!(!after_request_capture_qpc(u64::MAX - 1, u64::MAX));
    }
    #[test]
    fn retry_and_hard_timeout_are_bounded_and_loss_tolerant() {
        use std::time::{Duration, Instant};
        let start=Instant::now();
        assert_eq!(retry_action(start,start,start,false),RetryAction::Send);
        assert_eq!(retry_action(start,start+Duration::from_millis(RETRY_MS),
            start+Duration::from_millis(RETRY_MS-1),false),RetryAction::Wait);
        assert_eq!(retry_action(start,start+Duration::from_millis(RETRY_MS),
            start+Duration::from_millis(RETRY_MS),false),RetryAction::Send);
        assert_eq!(retry_action(start,start+Duration::from_millis(RETRY_MS),
            start+Duration::from_millis(RETRY_MS),true),RetryAction::Wait);
        assert_eq!(retry_action(start,start,
            start+Duration::from_millis(TIMEOUT_MS),true),RetryAction::Expired);
        assert_eq!(retry_action(start,start,
            start+Duration::from_millis(TIMEOUT_MS+1),false),RetryAction::Expired);
    }
    #[test]
    fn challenge_binding_and_canonical_wire() {
        let v = Message {kind: REQUEST, nonce: 0x1102,frame:48000,
            context:Context{epoch:1,stream:7,format:1}};
        let bytes = v.encode();
        assert_eq!(Message::decode(&bytes),Some(v));
        let mut bad=bytes;bad[5]=1;assert_eq!(Message::decode(&bad),None);
        bad=bytes;bad[4]=9;assert_eq!(Message::decode(&bad),None);
        bad=bytes;bad[7]=3;assert_eq!(Message::decode(&bad),None);
        assert_eq!(Message::decode(&bytes[..35]),None);
        let response = Message{kind:ANCHOR,frame:48240,..v};
        assert_eq!(Message::decode(&response.encode()),Some(response));
    }
    #[test]
    fn stale_nonce_and_backward_floor_are_not_valid_anchors() {
        let request = Message { kind: REQUEST,nonce:9,frame:48000,
            context:Context{epoch:1,stream:1,format:1}};
        assert!(!Message{kind:ANCHOR,nonce:8,..request}.confirms(&request));
        assert!(!Message{kind:ANCHOR,frame:47999,..request}.confirms(&request));
        assert!(!Message{kind:ANCHOR,context:Context{epoch:2,..request.context},..request}.confirms(&request));
        assert!(Message{kind:ANCHOR,frame:48240,..request}.confirms(&request));
    }
}

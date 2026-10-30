#![recursion_limit="256"]
//! DeskFuse M2a protocol wire layout. The input **must already be authenticated
//! by the surrounding QUIC connection**; this parser does not authenticate it.
//! No heap allocation is needed in encode/decode.
pub mod deferred_trace;
pub mod broker_control;
pub mod audio_authority;
pub mod audio_tls;
pub mod audio_control;
pub mod audio_source_stop;
pub mod audio_rpc;
pub mod audio_candidate;
pub mod audio_transport;
pub mod reactor;
pub mod wasapi_pipe;
pub mod endpoint_lease;
pub mod session_control;
pub mod lab_observation;
#[cfg(windows)]
pub mod committed_source;
#[cfg(windows)]
pub mod native_loopback;
#[cfg(target_os="android")]
extern crate self as deskfuse_quiche_lab;
#[cfg(target_os="android")]
#[path="bin/continuous-quic-lab.rs"]
mod native_session;

/// JNI adapter calls this on its dedicated owner thread. JSON contains only
/// private-app paths/control, never PCM. No panic unwinds across C/JNI.
#[cfg(target_os="android")]
#[no_mangle]
pub unsafe extern "C" fn deskfuse_native_audio_run(json:*const std::ffi::c_char)->i32 {
    std::panic::catch_unwind(|| {
        if json.is_null() {return -1}
        let bytes=std::ffi::CStr::from_ptr(json).to_bytes();
        if bytes.len()>16384 {return -1}
        let Ok(value)=serde_json::from_slice(bytes) else {return -1};
        match native_session::run_authorized(value) {Ok(())=>0,Err(_)=>-1}
    }).unwrap_or(-1)
}
#[cfg(target_os="android")]
#[no_mangle]
pub extern "C" fn deskfuse_native_audio_stop() {session_control::stop();}
#[cfg(target_os="android")]
#[no_mangle]
pub extern "C" fn deskfuse_native_audio_prepare()->bool {session_control::prepare()}
pub mod android_audio;
pub mod source_age;
pub mod plaintext_expiry;
pub mod receive_frontier;
pub mod fresh_anchor;
pub mod reorder;

pub const HEADER_BYTES: usize = 40;
pub const VERSION: u8 = 1;
pub const KIND_AUDIO: u8 = 1;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Header {
    pub version: u8,
    pub kind: u8,
    pub flags: u16,
    pub header_bytes: u16,
    pub frame_count: u16,
    pub media_epoch: u32,
    pub source_stream_id: u32,
    pub format_config_id: u32,
    pub packet_sequence: u32,
    pub first_source_frame: u64,
    pub payload_bytes: u32,
    pub clock_anchor_id: u32,
}

#[derive(Clone, Copy, Debug)]
pub struct Configuration {
    pub media_epoch: u32,
    pub source_stream_id: u32,
    pub format_config_id: u32,
    pub channels: u16,
    pub container_bytes: u16,
    pub maximum_frames: u16,
    pub datagram_limit_bytes: usize,
}

impl Default for Configuration {
    fn default() -> Self {
        Self {
            media_epoch: 1,
            source_stream_id: 1,
            format_config_id: 1,
            channels: 2,
            container_bytes: 2,
            maximum_frames: 480,
            datagram_limit_bytes: 1200,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ParseError {
    TooShort,
    TooLarge,
    UnsupportedVersion,
    UnsupportedKind,
    UnsupportedFlags,
    BadHeaderLength,
    InvalidFrameRange,
    WrongGeneration,
    WrongStream,
    WrongFormat,
    PayloadLength,
    BadConfiguration,
}

pub fn encode(h: &Header) -> [u8; HEADER_BYTES] {
    let mut b = [0u8; HEADER_BYTES];
    b[0] = h.version;
    b[1] = h.kind;
    b[2..4].copy_from_slice(&h.flags.to_be_bytes());
    b[4..6].copy_from_slice(&h.header_bytes.to_be_bytes());
    b[6..8].copy_from_slice(&h.frame_count.to_be_bytes());
    b[8..12].copy_from_slice(&h.media_epoch.to_be_bytes());
    b[12..16].copy_from_slice(&h.source_stream_id.to_be_bytes());
    b[16..20].copy_from_slice(&h.format_config_id.to_be_bytes());
    b[20..24].copy_from_slice(&h.packet_sequence.to_be_bytes());
    b[24..32].copy_from_slice(&h.first_source_frame.to_be_bytes());
    b[32..36].copy_from_slice(&h.payload_bytes.to_be_bytes());
    b[36..40].copy_from_slice(&h.clock_anchor_id.to_be_bytes());
    b
}

pub fn parse<'a>(
    b: &'a [u8],
    cfg: &Configuration,
) -> Result<(Header, &'a [u8]), ParseError> {
    if cfg.channels == 0 || cfg.container_bytes == 0 ||
       cfg.maximum_frames == 0 || cfg.datagram_limit_bytes < HEADER_BYTES {
        return Err(ParseError::BadConfiguration);
    }
    if b.len() < HEADER_BYTES {
        return Err(ParseError::TooShort);
    }
    if b.len() > cfg.datagram_limit_bytes {
        return Err(ParseError::TooLarge);
    }

    let h = Header {
        version: b[0],
        kind: b[1],
        flags: u16::from_be_bytes([b[2], b[3]]),
        header_bytes: u16::from_be_bytes([b[4], b[5]]),
        frame_count: u16::from_be_bytes([b[6], b[7]]),
        media_epoch: u32::from_be_bytes(b[8..12].try_into().expect("fixed slice")),
        source_stream_id: u32::from_be_bytes(b[12..16].try_into().expect("fixed slice")),
        format_config_id: u32::from_be_bytes(b[16..20].try_into().expect("fixed slice")),
        packet_sequence: u32::from_be_bytes(b[20..24].try_into().expect("fixed slice")),
        first_source_frame: u64::from_be_bytes(b[24..32].try_into().expect("fixed slice")),
        payload_bytes: u32::from_be_bytes(b[32..36].try_into().expect("fixed slice")),
        clock_anchor_id: u32::from_be_bytes(b[36..40].try_into().expect("fixed slice")),
    };

    if h.version != VERSION { return Err(ParseError::UnsupportedVersion); }
    if h.kind != KIND_AUDIO { return Err(ParseError::UnsupportedKind); }
    if h.flags != 0 { return Err(ParseError::UnsupportedFlags); }
    if h.header_bytes as usize != HEADER_BYTES { return Err(ParseError::BadHeaderLength); }
    if h.frame_count == 0 || h.frame_count > cfg.maximum_frames ||
       h.first_source_frame.checked_add(u64::from(h.frame_count)).is_none() {
        return Err(ParseError::InvalidFrameRange);
    }
    if h.media_epoch != cfg.media_epoch { return Err(ParseError::WrongGeneration); }
    if h.source_stream_id != cfg.source_stream_id { return Err(ParseError::WrongStream); }
    if h.format_config_id != cfg.format_config_id { return Err(ParseError::WrongFormat); }
    let expected = u64::from(h.frame_count)
        .checked_mul(u64::from(cfg.channels))
        .and_then(|x| x.checked_mul(u64::from(cfg.container_bytes)))
        .ok_or(ParseError::PayloadLength)?;
    if expected != u64::from(h.payload_bytes) ||
       expected != (b.len() - HEADER_BYTES) as u64 {
        return Err(ParseError::PayloadLength);
    }
    Ok((h, &b[HEADER_BYTES..]))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn packet() -> Vec<u8> {
        let h = Header {
            version: VERSION,
            kind: KIND_AUDIO,
            flags: 0,
            header_bytes: HEADER_BYTES as u16,
            frame_count: 240,
            media_epoch: 1,
            source_stream_id: 1,
            format_config_id: 1,
            packet_sequence: 0x1234_5678,
            first_source_frame: 0x0102_0304_0506_0708,
            payload_bytes: 960,
            clock_anchor_id: 0xaabb_ccdd,
        };
        let mut bytes = encode(&h).to_vec();
        bytes.extend_from_slice(&[0x42; 960]);
        bytes
    }

    #[test]
    fn roundtrip_matches_cxx_network_layout() {
        let bytes = packet();
        assert_eq!(bytes.len(), 1000);
        assert_eq!(&bytes[20..24], &[0x12, 0x34, 0x56, 0x78]);
        assert_eq!(&bytes[24..32], &[1, 2, 3, 4, 5, 6, 7, 8]);
        let (h, payload) = parse(&bytes, &Configuration::default()).unwrap();
        assert_eq!(h.packet_sequence, 0x1234_5678);
        assert_eq!(h.first_source_frame, 0x0102_0304_0506_0708);
        assert_eq!(h.clock_anchor_id, 0xaabb_ccdd);
        assert_eq!(payload.len(), 960);
    }

    #[test]
    fn rejects_truncation_and_oversize() {
        let b = packet();
        for n in 0..40 {
            assert_eq!(parse(&b[..n], &Configuration::default()), Err(ParseError::TooShort));
        }
        let mut cfg = Configuration::default();
        cfg.datagram_limit_bytes = 999;
        assert_eq!(parse(&b, &cfg), Err(ParseError::TooLarge));
    }

    #[test]
    fn rejects_wrong_epoch_and_bad_length() {
        let mut b = packet();
        b[11] = 2;
        assert_eq!(parse(&b, &Configuration::default()), Err(ParseError::WrongGeneration));
        b = packet(); b.pop();
        assert_eq!(parse(&b, &Configuration::default()), Err(ParseError::PayloadLength));
    }

    #[test]
    fn rejects_overflow_frames_and_mandatory_flags() {
        let mut b = packet();
        b[24..32].copy_from_slice(&(u64::MAX - 10).to_be_bytes());
        assert_eq!(parse(&b, &Configuration::default()), Err(ParseError::InvalidFrameRange));
        b = packet();
        b[3] = 1;
        assert_eq!(parse(&b, &Configuration::default()), Err(ParseError::UnsupportedFlags));
    }
}

/// Read-only copy on JNI/control thread. No renderer queue is touched here.
#[cfg(target_os="android")]
#[no_mangle]
pub unsafe extern "C" fn deskfuse_native_audio_observation_v1(owner:*const std::ffi::c_char,out:*mut u8,capacity:u32)->i32 {
    std::panic::catch_unwind(|| {
        if owner.is_null() || out.is_null() || capacity<2 || capacity>8192{return -1}
        let Ok(id)=std::ffi::CStr::from_ptr(owner).to_str() else{return -1};
        let Some(bytes)=lab_observation::json(id) else{return 0};
        if bytes.len()+1>capacity as usize{return -1}
        std::ptr::copy_nonoverlapping(bytes.as_ptr(),out,bytes.len());*out.add(bytes.len())=0;bytes.len() as i32
    }).unwrap_or(-1)
}

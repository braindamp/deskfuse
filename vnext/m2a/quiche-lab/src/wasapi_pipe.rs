//! Bounded IPC adapter for the native C++20 WASAPI source.
//!
//! Only the Windows process owns WASAPI/COM. Rust owns QUIC. Every source
//! chunk includes device sample index and WASAPI QPC anchor. The binary
//! pipe is local only, never a trust or authentication boundary.
use std::io::{self, Read};
use std::net::{SocketAddr, UdpSocket};
use std::process::{Command, Stdio};
use std::sync::mpsc::{self, Receiver, SyncSender, TrySendError};
use std::thread;
use std::time::{Duration, Instant};

const HEADER: usize = 40;
const FRAMES: usize = 240;
const PCM_BYTES: usize = 960;
const QUEUE_SLOTS: usize = 8;
const DATA_DISCONTINUITY: u32 = 0x1;
// v2 local DFWC header remains 40 bytes; 200ms native capture buffer.
const MAX_PACKET_OFFSET_FRAMES: u32 = 9600;

pub struct CaptureChunk {
    pub device_frame: u64,
    pub qpc_100ns: u64,
    pub flags: u32,
    pub original_packet_offset_frames: u32,
    pub frames: usize,
    pub pcm: [u8; PCM_BYTES],
    pub enqueued_at: Instant,
}

// Completion travels through the same bounded channel as PCM, after all
// preceding chunks. A malformed pipe or failed native capture must never
// masquerade as an ordinary end-of-stream.
pub enum CaptureEvent {
    Chunk(CaptureChunk),
    Finished(Result<(), String>),
}

pub struct CompleteBlock {
    pub first_device_frame: u64,
    pub first_qpc_100ns: u64,
    pub original_packet_offset_frames: u32,
    pub pcm: [u8; PCM_BYTES],
}

/// The only mechanism that assembles arbitrary engine packet boundaries
/// into exact 240-frame QUIC blocks. On a discontinuity we discard incomplete
/// material without fabricating samples, preserving genuine source positions.
pub struct Assembler {
    next_frame: Option<u64>,
    have: usize,
    first_frame: u64,
    first_qpc: u64,
    first_packet_offset_frames: u32,
    pcm: [u8; PCM_BYTES],
    pub discontinuities: u64,
}

impl Default for Assembler {
    fn default() -> Self {
        Self { next_frame: None, have: 0, first_frame: 0,
               first_qpc: 0, first_packet_offset_frames: 0,
               pcm: [0; PCM_BYTES], discontinuities: 0 }
    }
}

impl Assembler {
    pub fn push(&mut self, chunk: &CaptureChunk) -> Option<CompleteBlock> {
        if chunk.frames == 0 || chunk.frames > FRAMES { return None; }
        if self.next_frame.is_some_and(|n| n != chunk.device_frame)
            || chunk.flags & DATA_DISCONTINUITY != 0
        {
            self.next_frame = None;
            self.have = 0;
            self.discontinuities += 1;
        }

        let mut at = 0usize;
        let mut assembled = None;
        while at < chunk.frames {
            if self.have == 0 {
                self.first_frame = chunk.device_frame + at as u64;
                self.first_qpc = if chunk.qpc_100ns == 0 { 0 }
                    else { chunk.qpc_100ns + (at as u64 * 10_000_000 / 48_000) };
                self.first_packet_offset_frames =
                    chunk.original_packet_offset_frames + at as u32;
            }
            let n = (FRAMES - self.have).min(chunk.frames - at);
            self.pcm[self.have * 4..(self.have + n) * 4]
                .copy_from_slice(&chunk.pcm[at * 4..(at + n) * 4]);
            self.have += n;
            at += n;
            self.next_frame = Some(chunk.device_frame + at as u64);
            if self.have == FRAMES {
                assembled = Some(CompleteBlock {
                    first_device_frame: self.first_frame,
                    first_qpc_100ns: self.first_qpc,
                    original_packet_offset_frames: self.first_packet_offset_frames,
                    pcm: self.pcm,
                });
                self.have = 0;
            }
        }
        assembled
    }
}

fn le32(bytes: &[u8]) -> u32 {
    u32::from_le_bytes(bytes.try_into().expect("fixed 4-byte slice"))
}
fn le64(bytes: &[u8]) -> u64 {
    u64::from_le_bytes(bytes.try_into().expect("fixed 8-byte slice"))
}

fn read_chunk(input: &mut impl Read) -> io::Result<Option<CaptureChunk>> {
    let mut h = [0u8; HEADER];
    // A clean EOF is possible only *between* headers. read_exact on the
    // entire header would incorrectly accept a truncated header as EOF.
    let first_byte = loop {
        match input.read(&mut h[..1]) {
            Ok(count) => break count,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        }
    };
    if first_byte == 0 { return Ok(None); }
    input.read_exact(&mut h[1..])?;
    let count = le32(&h[24..28]) as usize;
    let size = le32(&h[32..36]) as usize;
    let offset = le32(&h[36..40]);
    let device_frame = le64(&h[8..16]);
    let qpc = le64(&h[16..24]);
    let offset_100ns = u64::from(offset) * 10_000_000 / 48_000;
    if &h[..4] != b"DFWC" || le32(&h[4..8]) != 2
        || count == 0 || count > FRAMES || size != count * 4
        || offset > MAX_PACKET_OFFSET_FRAMES || offset % FRAMES as u32 != 0
        || device_frame < u64::from(offset)
        || (qpc != 0 && qpc < offset_100ns)
    {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid WASAPI pipe frame"));
    }
    let mut pcm = [0u8; PCM_BYTES];
    input.read_exact(&mut pcm[..size])?;
    Ok(Some(CaptureChunk {
        device_frame,
        qpc_100ns: qpc,
        original_packet_offset_frames: offset,
        frames: count,
        flags: le32(&h[28..32]),
        pcm,
        enqueued_at: Instant::now(),
    }))
}

/// Starts the Windows-only capture executable and feeds a capacity-eight
/// channel. Dropped source blocks retain source-index gaps. A local one-byte
/// wake datagram wakes the QUIC owner's OS readiness wait; its source IP is
/// *not* the authenticated Android peer and the server ignores it.
// Reader-side queue drops remain bounded and source-index visible. Unlike
// the capture callback this thread may report a *terminal* status only after
// the stream has ended. No error event is silently dropped on a full queue.
fn forward_chunks(
    input: &mut impl Read, sender: &SyncSender<CaptureEvent>,
    notifier: Option<&UdpSocket>, wake_addr: SocketAddr
) -> (u64, Result<(), String>) {
    let mut reader_queue_drops = 0u64;
    loop {
        match read_chunk(input) {
            Ok(Some(chunk)) => match sender.try_send(CaptureEvent::Chunk(chunk)) {
                Ok(()) => {
                    if let Some(wake) = notifier {
                        if let Err(e) = wake.send_to(&[0xA1], wake_addr) {
                            return (reader_queue_drops, Err(format!("WASAPI source wake failed: {e}")));
                        }
                    }
                }
                Err(TrySendError::Full(event)) => {
                    reader_queue_drops += 1;
                    if reader_queue_drops <= 8 || reader_queue_drops % 128 == 0 {
                        if let CaptureEvent::Chunk(dropped) = event {
                            eprintln!(
                                "TRACE Windows WASAPI_READER_QUEUE_DROP source_frame={} frames={} reader_queue_drops={}",
                                dropped.device_frame, dropped.frames, reader_queue_drops
                            );
                        }
                    }
                } // bounded drop; never wait to send PCM from reader thread
                Err(TrySendError::Disconnected(_)) => {
                    return (reader_queue_drops, Err("WASAPI consumer disconnected".into()));
                }
            },
            Ok(None) => return (reader_queue_drops, Ok(())),
            Err(e) => return (reader_queue_drops, Err(format!("WASAPI pipe error: {e}"))),
        }
    }
}

pub fn spawn(
    exe: &str, device_id: &str, seconds: u64, wake_addr: SocketAddr
) -> io::Result<Receiver<CaptureEvent>> {
    if !cfg!(windows) {
        return Err(io::Error::new(io::ErrorKind::Unsupported, "WASAPI is Windows-only"));
    }
    // Readiness notification is required for event-driven PCM delivery.
    // Qualify it BEFORE starting native capture; no silent polling fallback.
    let notifier = UdpSocket::bind(if wake_addr.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" })?;
    notifier.connect(wake_addr)?;
    notifier.set_nonblocking(true)?;
    let mut command = Command::new(exe);
    command.arg("--out").arg("-").arg("--seconds").arg(seconds.to_string());
    if device_id != "default" {
        command.arg("--device-id").arg(device_id);
    }
    let mut child = command.stdin(Stdio::null()).stdout(Stdio::piped())
        .stderr(Stdio::inherit()).spawn()?;
    let mut stdout = child.stdout.take().ok_or_else(||
        io::Error::new(io::ErrorKind::Other, "capture pipe unavailable"))?;

    let (sender, receiver) = mpsc::sync_channel(QUEUE_SLOTS);
    thread::spawn(move || {
        let (reader_queue_drops, mut outcome) =
            forward_chunks(&mut stdout, &sender, Some(&notifier), wake_addr);
        // Do not kill a normally finishing process before its exit status is
        // available; terminate only when the pipe was invalid or owner exited.
        if outcome.is_err() && child.try_wait().ok().flatten().is_none() {
            let _ = child.kill();
        }
        match child.wait() {
            Ok(status) if !status.success() => {
                let reason = format!("WASAPI capture process exited with {status}");
                if outcome.is_ok() { outcome = Err(reason); }
            }
            Err(e) if outcome.is_ok() => {
                outcome = Err(format!("WASAPI capture wait failed: {e}"));
            }
            _ => {}
        }
        eprintln!("DONE Windows WASAPI pipe reader_queue_drops={reader_queue_drops}");
        if let Err(ref reason) = outcome {
            eprintln!("FAIL Windows WASAPI capture source: {reason}");
        }
        // A terminal event is ordered after any queued chunks. Blocking here
        // is safe: this is no longer the WASAPI callback or PCM read hot path.
        // If the QUIC owner has exited, send() returns Disconnected.
        let _ = sender.send(CaptureEvent::Finished(outcome));
        let _ = notifier.send_to(&[0xA1], wake_addr);
    });
    Ok(receiver)
}

/// The read-thread timestamp is a conservative *queue residency* bound only.
/// Actual WASAPI QPC/source timestamp remains independently available.
pub fn queue_too_old(chunk: &CaptureChunk) -> bool {
    chunk.enqueued_at.elapsed() > Duration::from_millis(60)
}


#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn record(version: u32, first: u64, first_qpc: u64, offset: u32) -> Vec<u8> {
        let mut data = vec![0u8; HEADER + PCM_BYTES];
        data[..4].copy_from_slice(b"DFWC");
        data[4..8].copy_from_slice(&version.to_le_bytes());
        data[8..16].copy_from_slice(&first.to_le_bytes());
        data[16..24].copy_from_slice(&first_qpc.to_le_bytes());
        data[24..28].copy_from_slice(&240u32.to_le_bytes());
        data[32..36].copy_from_slice(&960u32.to_le_bytes());
        data[36..40].copy_from_slice(&offset.to_le_bytes());
        data
    }

    #[test]
    fn original_packet_provenance_survives_local_wire_and_assembler() {
        let frame = 27_523_200;
        let packet_qpc = 2_097_401u64;
        let a = read_chunk(&mut Cursor::new(record(2, frame, packet_qpc, 0)))
            .unwrap().unwrap();
        let b = read_chunk(&mut Cursor::new(record(2, frame + 240, packet_qpc + 50_000, 240)))
            .unwrap().unwrap();
        let mut assembler = Assembler::default();
        let first = assembler.push(&a).unwrap();
        let second = assembler.push(&b).unwrap();
        assert_eq!(first.first_qpc_100ns, packet_qpc);
        assert_eq!(first.original_packet_offset_frames, 0);
        assert_eq!(second.first_qpc_100ns, packet_qpc + 50_000);
        assert_eq!(second.original_packet_offset_frames, 240);
        assert_eq!(second.first_device_frame, frame + 240);
    }

    #[test]
    fn partial_header_or_pcm_is_error_not_successful_eof() {
        assert!(read_chunk(&mut Cursor::new(Vec::<u8>::new())).unwrap().is_none());
        assert!(read_chunk(&mut Cursor::new(vec![b'D', b'F', b'W'])).is_err());
        let mut short_payload = record(2, 240, 2_000_000, 0);
        short_payload.truncate(HEADER + 30);
        assert!(read_chunk(&mut Cursor::new(short_payload)).is_err());
    }

    #[test]
    fn mixed_live_versions_stop_forwarding_after_valid_pcm() {
        let (tx, rx) = mpsc::sync_channel(QUEUE_SLOTS);
        let mut wire = record(2, 240, 2_000_000, 0);
        wire.extend(record(1, 480, 2_050_000, 240));
        let addr: SocketAddr = "127.0.0.1:9".parse().unwrap();
        let (drops, outcome) = forward_chunks(&mut Cursor::new(wire), &tx, None, addr);
        assert_eq!(drops, 0);
        assert!(outcome.unwrap_err().contains("invalid WASAPI pipe frame"));
        match rx.try_recv().unwrap() {
            CaptureEvent::Chunk(chunk) => assert_eq!(chunk.device_frame, 240),
            CaptureEvent::Finished(_) => panic!("terminal before accepted PCM"),
        }
        assert!(rx.try_recv().is_err()); // incompatible PCM never forwarded
    }

    #[test]
    fn old_or_malformed_local_capture_header_fails_closed() {
        assert!(read_chunk(&mut Cursor::new(record(1, 240, 2_000_000, 240)))
            .is_err()); // Mixed native binaries not silently accepted.
        assert!(read_chunk(&mut Cursor::new(record(2, 240, 2_000_000, 480)))
            .is_err()); // Original packet cannot predate stream origin.
        assert!(read_chunk(&mut Cursor::new(record(2, 240, 2_000_000, 239)))
            .is_err()); // Native fragments must be on exact 240 frame steps.
        assert!(read_chunk(&mut Cursor::new(record(2, 9_840, 2_000_000, 9_840)))
            .is_err()); // Offset beyond 200ms capture buffer.
        assert!(read_chunk(&mut Cursor::new(record(2, 240, 100, 240)))
            .is_err()); // Origin QPC underflow not allowed.
    }
}

//! Bounded IPC adapter for the native C++20 WASAPI source.
//!
//! Only the Windows process owns WASAPI/COM. Rust owns QUIC. Every source
//! chunk includes device sample index and WASAPI QPC anchor. The binary
//! pipe is local only, never a trust or authentication boundary.
use std::io::{self, Read};
use std::net::{SocketAddr, UdpSocket};
use std::process::{Command, Stdio};
use std::sync::mpsc::{self, Receiver, TrySendError};
use std::thread;
use std::time::{Duration, Instant};

const HEADER: usize = 40;
const FRAMES: usize = 240;
const PCM_BYTES: usize = 960;
const QUEUE_SLOTS: usize = 8;
const DATA_DISCONTINUITY: u32 = 0x1;

pub struct CaptureChunk {
    pub device_frame: u64,
    pub qpc_100ns: u64,
    pub flags: u32,
    pub frames: usize,
    pub pcm: [u8; PCM_BYTES],
    pub enqueued_at: Instant,
}

pub struct CompleteBlock {
    pub first_device_frame: u64,
    pub first_qpc_100ns: u64,
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
    pcm: [u8; PCM_BYTES],
    pub discontinuities: u64,
}

impl Default for Assembler {
    fn default() -> Self {
        Self { next_frame: None, have: 0, first_frame: 0,
               first_qpc: 0, pcm: [0; PCM_BYTES], discontinuities: 0 }
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
    match input.read_exact(&mut h) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e),
    }
    let count = le32(&h[24..28]) as usize;
    let size = le32(&h[32..36]) as usize;
    if &h[..4] != b"DFWC" || le32(&h[4..8]) != 1
        || count == 0 || count > FRAMES || size != count * 4 || le32(&h[36..40]) != 0
    {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid WASAPI pipe frame"));
    }
    let mut pcm = [0u8; PCM_BYTES];
    input.read_exact(&mut pcm[..size])?;
    Ok(Some(CaptureChunk {
        device_frame: le64(&h[8..16]),
        qpc_100ns: le64(&h[16..24]),
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
pub fn spawn(
    exe: &str, device_id: &str, seconds: u64, wake_addr: SocketAddr
) -> io::Result<Receiver<CaptureChunk>> {
    if !cfg!(windows) {
        return Err(io::Error::new(io::ErrorKind::Unsupported, "WASAPI is Windows-only"));
    }
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
        let notifier = UdpSocket::bind("0.0.0.0:0").ok();
        loop {
            match read_chunk(&mut stdout) {
                Ok(Some(chunk)) => match sender.try_send(chunk) {
                    Ok(()) => {
                        if let Some(ref wake) = notifier {
                            let _ = wake.send_to(&[0xA1], wake_addr);
                        }
                    }
                    Err(TrySendError::Full(_)) => {} // bounded drop, not a capture-thread block
                    Err(TrySendError::Disconnected(_)) => break,
                },
                Ok(None) => break,
                Err(e) => {
                    eprintln!("WASAPI pipe error: {e}");
                    break;
                }
            }
        }
        // If the QUIC owner exits while the capture process is still alive,
        // don't orphan the native child process or leak a pipe handle.
        if child.try_wait().ok().flatten().is_none() { let _ = child.kill(); }
        let _ = child.wait();
    });
    Ok(receiver)
}

/// The read-thread timestamp is a conservative *queue residency* bound only.
/// Actual WASAPI QPC/source timestamp remains independently available.
pub fn queue_too_old(chunk: &CaptureChunk) -> bool {
    chunk.enqueued_at.elapsed() > Duration::from_millis(60)
}

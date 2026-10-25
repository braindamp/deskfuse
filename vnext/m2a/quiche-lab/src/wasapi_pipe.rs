//! Bounded IPC adapter for the native C++20 WASAPI source.
//!
//! Only the Windows process owns WASAPI/COM. Rust owns QUIC. Every source
//! chunk includes device sample index and WASAPI QPC anchor. The binary
//! pipe is local only, never a trust or authentication boundary.
use std::io::{self, Read, Write};
use crate::deferred_trace::{Event, Ledger};
use std::net::{SocketAddr, UdpSocket};
use std::process::{Command, Stdio};
use std::sync::mpsc::{self, Receiver, SyncSender, TrySendError};
use std::thread;
use std::time::{Duration, Instant};
use crate::endpoint_lease::EndpointLease;
use std::sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}};

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
    Started { at: Instant },
    Chunk(CaptureChunk),
    Finished { outcome: Result<(), String>, summary: Box<ReaderSummary> },
}

/// Explicit cancellation keeps a broker endpoint lease alive until its native
/// capture process has exited. No thread/pipe queue outlives endpoint teardown.
pub struct CaptureSession {
    receiver: Option<Receiver<CaptureEvent>>,
    _lease: Arc<Mutex<Option<EndpointLease>>>,
    process: Arc<Mutex<Option<ProcessControl>>>,
    cancel: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl CaptureSession {
    pub fn try_recv(&self) -> Result<CaptureEvent, mpsc::TryRecvError> {
        self.receiver.as_ref().ok_or(mpsc::TryRecvError::Disconnected)?.try_recv()
    }
    pub fn stop(&mut self) -> io::Result<()> {
        // Unblock even a terminal sender waiting behind a full bounded channel.
        drop(self.receiver.take());
        self.cancel.store(true, Ordering::Release);
        if let Some(process) = self.process.lock().map_err(|_| io::Error::new(
            io::ErrorKind::Other,"capture process control poisoned"))?.as_ref() {
            process.stop()?;
        }
        if let Some(worker) = self.worker.take() {
            worker.join().map_err(|_| io::Error::new(io::ErrorKind::Other, "capture reader panicked during cleanup"))?;
        }
        Ok(())
    }
}
impl Drop for CaptureSession {
    fn drop(&mut self) { let _ = self.stop(); }
}

struct ProcessControl {
    #[cfg(windows)]
    handle: *mut std::ffi::c_void,
}
// An owned duplicated Windows process HANDLE is usable from any thread. It
// points to an OS object, never Rust memory; the mutex serializes cancellation.
unsafe impl Send for ProcessControl {}
impl ProcessControl {
    #[cfg(windows)]
    fn duplicate(child: &std::process::Child) -> io::Result<Self> {
        use std::os::windows::io::AsRawHandle;
        #[link(name="Kernel32")]
        unsafe extern "system" {
            fn GetCurrentProcess() -> *mut std::ffi::c_void;
            fn DuplicateHandle(source_process:*mut std::ffi::c_void, source:*mut std::ffi::c_void,
                target_process:*mut std::ffi::c_void, target:*mut *mut std::ffi::c_void,
                access:u32, inherit:i32, options:u32) -> i32;
        }
        let mut handle = std::ptr::null_mut();
        let ok = unsafe { let process=GetCurrentProcess();
            DuplicateHandle(process,child.as_raw_handle(),process,&mut handle,0,0,2) };
        if ok == 0 { return Err(io::Error::last_os_error()); }
        Ok(Self { handle })
    }
    #[cfg(not(windows))]
    fn duplicate(_: &std::process::Child) -> io::Result<Self> {
        Err(io::Error::new(io::ErrorKind::Unsupported,"Windows capture process required"))
    }
    #[cfg(windows)]
    fn stop(&self) -> io::Result<()> {
        #[link(name="Kernel32")]
        unsafe extern "system" {
            fn WaitForSingleObject(handle:*mut std::ffi::c_void, milliseconds:u32) -> u32;
            fn TerminateProcess(handle:*mut std::ffi::c_void, code:u32) -> i32;
        }
        match unsafe { WaitForSingleObject(self.handle,0) } {
            0 => return Ok(()),
            258 => {},
            _ => return Err(io::Error::last_os_error()),
        }
        if unsafe { TerminateProcess(self.handle,1) } == 0 {
            // Process may have exited between the zero-time query and kill.
            if unsafe { WaitForSingleObject(self.handle,0) } != 0 {
                return Err(io::Error::last_os_error());
            }
        }
        if unsafe { WaitForSingleObject(self.handle,5000) } != 0 {
            return Err(io::Error::new(io::ErrorKind::TimedOut,"native capture cleanup not confirmed"));
        }
        Ok(())
    }
    #[cfg(not(windows))]
    fn stop(&self) -> io::Result<()> { Ok(()) }
}
impl Drop for ProcessControl {
    fn drop(&mut self) {
        #[cfg(windows)] {
            #[link(name="Kernel32")]
            unsafe extern "system" { fn CloseHandle(handle:*mut std::ffi::c_void) -> i32; }
            unsafe { CloseHandle(self.handle); }
        }
    }
}

pub struct ReaderSummary { pub drops:u64, trace:Ledger }
impl Default for ReaderSummary {
    fn default()->Self {Self {drops:0,trace:Ledger::new(8)}}
}
impl ReaderSummary {
    pub fn flush(&self,out:&mut impl Write)->io::Result<()> {
        self.trace.flush(out,17)?;
        writeln!(out,"DONE Windows WASAPI pipe reader_queue_drops={}",self.drops)
    }
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
) -> (ReaderSummary, Result<(), String>) {
    let mut summary = ReaderSummary::default();
    loop {
        match read_chunk(input) {
            Ok(Some(chunk)) => match sender.try_send(CaptureEvent::Chunk(chunk)) {
                Ok(()) => {
                    if let Some(wake) = notifier {
                        if let Err(e) = wake.send_to(&[0xA1], wake_addr) {
                            return (summary, Err(format!("WASAPI source wake failed: {e}")));
                        }
                    }
                }
                Err(TrySendError::Full(event)) => {
                    summary.drops += 1;
                    if let CaptureEvent::Chunk(dropped) = event {
                        summary.trace.record(Event::ReaderDrop {frame:dropped.device_frame,
                            frames:dropped.frames as u64,count:summary.drops});
                    }
                } // bounded drop; never wait to send PCM from reader thread
                Err(TrySendError::Disconnected(_)) => {
                    return (summary, Err("WASAPI consumer disconnected".into()));
                }
            },
            Ok(None) => return (summary, Ok(())),
            Err(e) => return (summary, Err(format!("WASAPI pipe error: {e}"))),
        }
    }
}

pub fn spawn(
    exe: &str, device_id: &str, seconds: u64, wake_addr: SocketAddr
) -> io::Result<CaptureSession> {
    spawn_source(exe, device_id, seconds, wake_addr, None)
}

pub fn spawn_deskfuse(
    exe: &str, peer_id: &str, seconds: u64, wake_addr: SocketAddr,
) -> io::Result<CaptureSession> {
    let peer_id = crate::endpoint_lease::validate_peer_id(peer_id)?;
    spawn_source(exe, "", seconds, wake_addr, Some(peer_id))
}

fn spawn_source(
    exe: &str, device_id: &str, seconds: u64, wake_addr: SocketAddr,
    peer_id: Option<String>,
) -> io::Result<CaptureSession> {
    if !cfg!(windows) {
        return Err(io::Error::new(io::ErrorKind::Unsupported, "WASAPI is Windows-only"));
    }
    // Readiness notification is required for event-driven PCM delivery.
    // Qualify it BEFORE starting native capture; no silent polling fallback.
    let notifier = UdpSocket::bind(if wake_addr.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" })?;
    notifier.connect(wake_addr)?;
    notifier.set_nonblocking(true)?;
    let exe = exe.to_string();
    let device_id = device_id.to_string();
    let process = Arc::new(Mutex::new(None));
    let cancel = Arc::new(AtomicBool::new(false));
    let worker_process = Arc::clone(&process);
    let worker_cancel = Arc::clone(&cancel);
    let lease_owner=Arc::new(Mutex::new(None));
    let worker_lease=Arc::clone(&lease_owner);
    let (sender, receiver) = mpsc::sync_channel(QUEUE_SLOTS);
    let worker = thread::spawn(move || {
        // Device setup may take seconds. It must never block the QUIC owner.
        let setup = (|| -> io::Result<_> {
            let lease = peer_id.as_ref().map(|peer| EndpointLease::provision(peer, &worker_cancel)).transpose()?;
            let endpoint = lease.as_ref().map_or(device_id.clone(), |x| x.endpoint_id.clone());
            if let Some(ref lease) = lease {
                eprintln!("DESKFUSE_SOURCE endpoint_id={} default_routing_changed=false", lease.endpoint_id);
            }
            let is_dedicated=lease.is_some();
            *worker_lease.lock().map_err(|_|io::Error::new(io::ErrorKind::Other,"lease ownership poisoned"))?=lease;
            let mut control = worker_process.lock().map_err(|_| io::Error::new(
                io::ErrorKind::Other,"capture process control poisoned"))?;
            if worker_cancel.load(Ordering::Acquire) {
                return Err(io::Error::new(io::ErrorKind::Interrupted,"capture startup cancelled"));
            }
            let mut command = Command::new(&exe);
            #[cfg(windows)] if is_dedicated {
                use std::os::windows::process::CommandExt;
                command.creation_flags(0x00000004); // CREATE_SUSPENDED
            }
            command.arg("--out").arg("-").arg("--seconds").arg(seconds.to_string());
            if endpoint != "default" { command.arg("--device-id").arg(&endpoint); }
            let mut child = command.stdin(Stdio::null()).stdout(Stdio::piped())
                .stderr(Stdio::inherit()).spawn()?;
            *control = match ProcessControl::duplicate(&child) {
                Ok(process) => Some(process),
                Err(e) => { let _=child.kill(); let _=child.wait(); return Err(e); }
            };
            #[cfg(windows)] if let Some(ref lease)=*worker_lease.lock().map_err(|_|io::Error::new(io::ErrorKind::Other,"lease ownership poisoned"))? {
                if let Err(e)=lease.register_capture(child.id()).and_then(|_|resume_capture(child.id())) {
                    let _=child.kill(); let _=child.wait(); return Err(e);
                }
            }
            drop(control);
            let Some(stdout) = child.stdout.take() else {
                let _=child.kill(); let _=child.wait();
                return Err(io::Error::new(io::ErrorKind::Other,"capture pipe unavailable"));
            };
            Ok((child,stdout))
        })();
        let (mut child,mut stdout) = match setup {
            Ok(setup) => setup,
            Err(e) => {
                deliver_terminal(&sender,ReaderSummary::default(),Err(e.to_string()),Some(&notifier),wake_addr);
                return;
            }
        };
        if sender.send(CaptureEvent::Started { at:Instant::now() }).is_err() {
            let _=child.kill(); let _=child.wait(); return;
        }
        let _=notifier.send(&[0xA1]);
        let (summary, mut outcome) =
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
        // Acquisition is joined, but the CaptureSession retains its broker
        // lease until the owner has retired QUIC and drops the session.
        // Ordered terminal delivery happens after child cleanup and BEFORE
        // any diagnostic output. No writer exists on this reader path.
        deliver_terminal(&sender, summary, outcome, Some(&notifier), wake_addr);
    });
    Ok(CaptureSession { receiver:Some(receiver), _lease:lease_owner, process, cancel, worker:Some(worker) })
}

#[cfg(windows)]
fn resume_capture(pid:u32)->io::Result<()> {
    use windows_sys::Win32::{Foundation::{CloseHandle,INVALID_HANDLE_VALUE},
        System::{Diagnostics::ToolHelp::*,Threading::{OpenThread,ResumeThread,THREAD_SUSPEND_RESUME}}};
    unsafe {
        let snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
        if snapshot==INVALID_HANDLE_VALUE {return Err(io::Error::last_os_error())}
        let mut entry:THREADENTRY32=std::mem::zeroed();entry.dwSize=std::mem::size_of::<THREADENTRY32>() as u32;
        let mut found=None;
        let mut ok=Thread32First(snapshot,&mut entry);
        while ok!=0 {
            if entry.th32OwnerProcessID==pid {
                let thread=OpenThread(THREAD_SUSPEND_RESUME,0,entry.th32ThreadID);
                if !thread.is_null() {
                    let result=ResumeThread(thread);CloseHandle(thread);
                    found=Some(result!=u32::MAX);break;
                }
            }
            ok=Thread32Next(snapshot,&mut entry);
        }
        CloseHandle(snapshot);
        if found==Some(true) {Ok(())}
        else {Err(io::Error::new(io::ErrorKind::Other,"suspended capture could not be resumed after job assignment"))}
    }
}

fn deliver_terminal(sender:&SyncSender<CaptureEvent>, summary:ReaderSummary,
                    outcome:Result<(),String>, notifier:Option<&UdpSocket>,
                    wake_addr:SocketAddr) {
    // Allocation and a potentially blocking terminal send are permitted only
    // after PCM forwarding/child cleanup. No diagnostic sink is involved.
    let _ = sender.send(CaptureEvent::Finished { outcome, summary:Box::new(summary) });
    if let Some(notifier)=notifier {let _=notifier.send_to(&[0xA1],wake_addr);}
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
        assert_eq!(drops.drops, 0);
        assert!(outcome.unwrap_err().contains("invalid WASAPI pipe frame"));
        match rx.try_recv().unwrap() {
            CaptureEvent::Chunk(chunk) => assert_eq!(chunk.device_frame, 240),
            CaptureEvent::Finished { .. } => panic!("terminal before accepted PCM"),
            CaptureEvent::Started { .. } => panic!("unexpected startup event from raw forwarding test"),
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

    #[test]
    fn full_reader_queue_accounts_source_ranges_and_delivers_terminal_after_pcm() {
        let mut wire=Vec::new();
        for n in 0..25u64 {wire.extend(record(2,48_000+n*240,2_000_000+n*50_000,0));}
        let (tx,rx)=mpsc::sync_channel(QUEUE_SLOTS);
        let addr="127.0.0.1:9".parse().unwrap();
        // No output sink exists in forwarding, even while the queue is full.
        let (summary,outcome)=forward_chunks(&mut Cursor::new(wire),&tx,None,addr);
        assert_eq!(summary.drops,17);
        assert!(outcome.is_ok());
        let terminal=thread::spawn(move||deliver_terminal(&tx,summary,outcome,None,addr));
        for n in 0..8u64 {
            match rx.recv().unwrap() {
                CaptureEvent::Chunk(c)=>assert_eq!(c.device_frame,48_000+n*240),
                _=>panic!("terminal overtook PCM"),
            }
        }
        let CaptureEvent::Finished{outcome,summary}=rx.recv().unwrap() else {panic!("terminal missing")};
        assert!(outcome.is_ok());
        terminal.join().unwrap();
        // Formatting happens only here, after actual terminal delivery.
        let mut text=Vec::new();summary.flush(&mut text).unwrap();
        let text=String::from_utf8(text).unwrap();
        assert!(text.contains("source_frame=49920 frames=240 reader_queue_drops=1"));
        assert!(text.contains("source_frame=53760 frames=240 reader_queue_drops=17"));
        assert!(text.contains("total=17 retained=16 omitted=1"));
        assert!(!text.contains("source_frame=51840 frames=240 reader_queue_drops=9"));
        assert!(text.contains("DONE Windows WASAPI pipe reader_queue_drops=17"));
    }

    #[test]
    fn malformed_source_error_is_delivered_with_summary_after_prior_pcm() {
        let mut wire=record(2,48_000,2_000_000,0);
        wire.extend(record(1,48_240,2_050_000,0));
        let (tx,rx)=mpsc::sync_channel(QUEUE_SLOTS);
        let addr="127.0.0.1:9".parse().unwrap();
        let (summary,outcome)=forward_chunks(&mut Cursor::new(wire),&tx,None,addr);
        deliver_terminal(&tx,summary,outcome,None,addr);
        assert!(matches!(rx.recv().unwrap(),CaptureEvent::Chunk(_)));
        let CaptureEvent::Finished{outcome,summary}=rx.recv().unwrap() else {panic!("terminal missing")};
        assert_eq!(summary.drops,0);
        assert!(outcome.unwrap_err().contains("invalid WASAPI pipe frame"));
    }
}

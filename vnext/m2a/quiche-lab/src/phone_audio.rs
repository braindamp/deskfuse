//! Gated first-delivery JNI owner. The Kotlin enrollment owner verifies the
//! receipt, while this owner retains the actual connection and renderer.
use crate::android_audio::{AndroidAudio, OutputConfigV8};
use crate::audio_control::{
    self as wire, DeliveryBinding, Observation, Reader, Record, SourceScope,
};
use crate::audio_tls::Role;
use crate::audio_transport::{ControlConnection, DeliveryTls};
use crate::native_session::AuthorityMedia;
use crate::Configuration;
use std::ffi::{c_char, c_void, CStr};
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc::{self, Receiver, SyncSender},
    Mutex,
};
use std::time::{Duration, Instant};

// Failure-only Android log; no per-packet logging or control-path changes.
#[link(name = "log")]
extern "C" {
    fn __android_log_write(priority: i32, tag: *const c_char, text: *const c_char) -> i32;
}
fn log_play_failure(operation: &str, error: &io::Error, stream_error: i32) {
    let text = format!("Native PLAY failure operation={operation} kind={:?} native={:?} stream_error={stream_error} detail={error}", error.kind(), error.raw_os_error());
    let bounded: String = text.chars().take(1536).collect::<String>().replace('\0', "\\0");
    if let Ok(text) = std::ffi::CString::new(bounded) {
        unsafe { __android_log_write(6, b"DeskFuseAudio\0".as_ptr().cast(), text.as_ptr()); }
    }
}

// One record per playback owner, only when the experiment path is exercised.
pub(crate) fn log_native_capacity_probe(text: &str) {
    let bounded:String=text.chars().take(1536).collect::<String>().replace('\0',"\\0");
    if let Ok(text)=std::ffi::CString::new(bounded) {
        unsafe { __android_log_write(4,b"DeskFuseAudio\0".as_ptr().cast(),text.as_ptr()); }
    }
}

const RENEW_ISSUE: u8 = 131;
const RENEW_PROOF: u8 = 132;
const RENEW_RESULT: u8 = 133;
const RENEW_INSTALL: u8 = 134;
const WAIT_WARM: u8 = 135;

fn denied() -> io::Error {
    io::Error::from(io::ErrorKind::PermissionDenied)
}

#[link(name = "android")]
extern "C" {
    fn android_setsocknetwork(network: u64, fd: libc::c_int) -> libc::c_int;
}

type Ready = extern "C" fn(*mut c_void);
fn boot() -> io::Result<u64> {
    let mut time = libc::timespec {
        tv_sec: 0,
        tv_nsec: 0,
    };
    if unsafe { libc::clock_gettime(libc::CLOCK_BOOTTIME, &mut time) } != 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(time.tv_sec as u64 * 10_000_000 + time.tv_nsec as u64 / 100)
}

struct Owner {
    run: Mutex<AudioRun>,
    commands: Mutex<Option<SyncSender<Command>>>,
    command_pending: AtomicBool,
    wake: OwnedFd,
    timer: OwnedFd,
    armed_until: AtomicU64,
    stopped: AtomicBool,
    playing: AtomicBool,
    deadline: AtomicU64,
}
struct AudioRun {
    delivery: Delivery,
    source: Option<SourceScope>,
    playback: Option<AuthorityMedia>,
}
struct Delivery {
    socket: UdpSocket,
    connection: ControlConnection,
    request: u64,
    receipt: Option<Vec<u8>>,
    generation: Option<u32>,
}
struct Command {
    kind: u8,
    body: Vec<u8>,
    until: u64,
    complete: SyncSender<io::Result<Vec<u8>>>,
}
struct PendingReply {
    request: u64,
    expected: u8,
    until: u64,
    complete: SyncSender<io::Result<Vec<u8>>>,
}

impl Owner {
    fn new(peer: SocketAddr, until: u64, idle_ms: u32, network: u64) -> io::Result<Self> {
        if network == 0 || boot()? >= until
            || peer.port() == 0
            || peer.ip().is_unspecified()
            || peer.ip().is_multicast()
        {
            return Err(denied());
        }
        let socket = UdpSocket::bind(if peer.is_ipv4() {
            "0.0.0.0:0"
        } else {
            "[::]:0"
        })?;
        if unsafe { android_setsocknetwork(network, socket.as_raw_fd()) } != 0 {
            return Err(io::Error::last_os_error());
        }
        socket.connect(peer)?;
        socket.set_nonblocking(true)?;
        let connection = DeliveryTls::generate(Role::Phone)?.connect(
            socket.local_addr()?,
            peer,
            until,
            idle_ms,
        )?;
        let fd = unsafe { libc::eventfd(0, libc::EFD_CLOEXEC | libc::EFD_NONBLOCK) };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }
        let wake = unsafe { OwnedFd::from_raw_fd(fd) };
        let fd = unsafe {
            libc::timerfd_create(libc::CLOCK_BOOTTIME, libc::TFD_CLOEXEC | libc::TFD_NONBLOCK)
        };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Self {
            run: Mutex::new(AudioRun {
                delivery: Delivery { socket, connection, request: 0, receipt: None, generation: None },
                source: None,
                playback: None,
            }),
            commands: Mutex::new(None),
            command_pending: AtomicBool::new(false),
            wake,
            timer: unsafe { OwnedFd::from_raw_fd(fd) },
            armed_until: AtomicU64::new(0),
            stopped: AtomicBool::new(false),
            playing: AtomicBool::new(false),
            deadline: AtomicU64::new(until),
        })
    }
    fn live(&self) -> io::Result<u64> {
        self.control_time(false)
    }
    fn control_time(&self, negative: bool) -> io::Result<u64> {
        let now = boot()?;
        if (!negative && self.stopped.load(Ordering::Acquire))
            || now >= self.deadline.load(Ordering::Acquire)
        {
            return Err(denied());
        }
        Ok(now)
    }
    fn signal(&self) {
        let value = 1u64;
        unsafe {
            libc::write(self.wake.as_raw_fd(), (&value as *const u64).cast(), 8);
        }
    }
    fn stop(&self) {
        self.stopped.store(true, Ordering::Release);
        self.signal();
    }
    fn submit(&self, kind: u8, body: &[u8], until: u64) -> io::Result<Vec<u8>> {
        let remaining = until.checked_sub(self.live()?).filter(|ticks| *ticks > 0)
            .ok_or(io::ErrorKind::TimedOut)?;
        let (complete, reply) = mpsc::sync_channel(1);
        self.commands.lock().map_err(|_| denied())?.as_ref().ok_or_else(denied)?
            .try_send(Command { kind, body: body.to_vec(), until, complete })
            .map_err(|_| denied())?;
        self.command_pending.store(true, Ordering::Release);
        self.signal();
        reply.recv_timeout(Duration::from_nanos(remaining.saturating_mul(100)))
            .map_err(|e| match e {
                mpsc::RecvTimeoutError::Timeout => io::ErrorKind::TimedOut,
                mpsc::RecvTimeoutError::Disconnected => io::ErrorKind::BrokenPipe,
            })?
    }
    fn wait(&self, delivery: &Delivery, until: u64, negative: bool) -> io::Result<bool> {
        self.control_time(negative)?;
        let deadline = until.min(self.deadline.load(Ordering::Acquire));
        if self.armed_until.load(Ordering::Relaxed) != deadline {
            let timer = libc::itimerspec {
                it_interval: libc::timespec {
                    tv_sec: 0,
                    tv_nsec: 0,
                },
                it_value: libc::timespec {
                    tv_sec: (deadline / 10_000_000) as _,
                    tv_nsec: ((deadline % 10_000_000) * 100) as _,
                },
            };
            if unsafe {
                libc::timerfd_settime(
                    self.timer.as_raw_fd(),
                    libc::TFD_TIMER_ABSTIME,
                    &timer,
                    std::ptr::null_mut(),
                )
            } != 0
            {
                return Err(io::Error::last_os_error());
            }
            self.armed_until.store(deadline, Ordering::Relaxed);
        }
        let timeout = delivery.connection.network_deadline().map_or(-1, |network| {
            network
                .saturating_duration_since(Instant::now())
                .as_nanos()
                .div_ceil(1_000_000)
                .min(i32::MAX as u128) as i32
        });
        let mut fds = [
            libc::pollfd {
                fd: delivery.socket.as_raw_fd(),
                events: libc::POLLIN
                    | if delivery.connection.needs_write() {
                        libc::POLLOUT
                    } else {
                        0
                    },
                revents: 0,
            },
            libc::pollfd {
                fd: self.wake.as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            },
            libc::pollfd {
                fd: self.timer.as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            },
        ];
        if unsafe { libc::poll(fds.as_mut_ptr(), fds.len() as libc::nfds_t, timeout) } < 0 {
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::Interrupted {
                return Err(error);
            }
        }
        if fds[0].revents & (libc::POLLERR | libc::POLLHUP | libc::POLLNVAL) != 0 {
            return Err(denied());
        }
        let signaled = fds[1].revents & libc::POLLIN != 0;
        if signaled {
            let mut value = 0u64;
            unsafe {
                libc::read(self.wake.as_raw_fd(), (&mut value as *mut u64).cast(), 8);
            }
        }
        self.control_time(negative)?;
        Ok(signaled)
    }
}

impl Delivery {
    fn wait_warm(&mut self, owner: &Owner, until: u64) -> io::Result<()> {
        if self.connection.is_approved() || self.connection.observed_id().is_none() {
            return Err(denied());
        }
        let until = until.min(owner.deadline.load(Ordering::Acquire));
        while boot()? < until {
            self.service(owner, false)?;
            if self.discard()? {
                if let Err(error) = owner.wait(self, until, false) {
                    if boot()? >= until { return Ok(()); }
                    return Err(error);
                }
            }
        }
        Ok(())
    }

    fn service(&mut self, owner: &Owner, negative: bool) -> io::Result<Vec<Record>> {
        self.connection
            .service_timeout(owner.control_time(negative)?)?;
        let local = self.socket.local_addr()?;
        let mut packet = [0; 1351];
        for _ in 0..16 {
            match self.socket.recv_from(&mut packet) {
                Ok((n, from)) => {
                    self.connection.receive(
                        &mut packet[..n],
                        quiche::RecvInfo { from, to: local },
                        owner.control_time(negative)?,
                    )?;
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(error) => return Err(error),
            }
        }
        let records = self
            .connection
            .read_control(owner.control_time(negative)?)?;
        self.connection
            .service_egress(&self.socket, true, owner.control_time(negative)?)?;
        Ok(records)
    }
    fn discard(&mut self) -> io::Result<bool> {
        let mut packet = [0; 1350];
        for _ in 0..128 {
            if self.connection.phone_datagram(&mut packet)?.is_none() {
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn exchange(
        &mut self,
        owner: &Owner,
        kind: u8,
        body: &[u8],
        until: u64,
    ) -> io::Result<Vec<u8>> {
        let negative = kind == wire::SIGNED_SOURCE_STOP;
        while self
            .connection
            .facts(owner.control_time(negative)?)?
            .is_none()
        {
            self.service(owner, negative)?;
            if owner.control_time(negative)? >= until {
                return Err(io::ErrorKind::TimedOut.into());
            }
            if self
                .connection
                .facts(owner.control_time(negative)?)?
                .is_none()
            {
                owner.wait(self, until, negative)?;
            }
        }
        let request = self.queue(kind, body.to_vec(), owner.control_time(negative)?)?;
        let reply = self.reply(owner, request, kind, until)?;
        if matches!(kind, wire::PROOF | wire::APPLIED_RESULT) {
            self.receipt = Some(reply.clone());
        }
        Ok(reply)
    }
    fn queue(&mut self, kind: u8, body: Vec<u8>, now: u64) -> io::Result<u64> {
        let request = self.request.checked_add(1).ok_or_else(denied)?;
        self.connection.queue_control(Record { kind, request, body }, now)?;
        self.request = request;
        Ok(request)
    }
    fn reply(&mut self, owner: &Owner, request: u64, kind: u8, until: u64) -> io::Result<Vec<u8>> {
        let negative = kind == wire::SIGNED_SOURCE_STOP;
        loop {
            if owner.control_time(negative)? >= until {
                return Err(io::ErrorKind::TimedOut.into());
            }
            let records = self.service(owner, negative)?;
            let drained = self.discard()?;
            if let Some(record) = records.into_iter().find(|record| record.request == request) {
                if record.kind != kind + 128 {
                    return Err(denied());
                }
                return Ok(record.body);
            }
            if drained {
                owner.wait(self, until, negative)?;
            }
        }
    }
}

impl AudioRun {
    fn service_command(&mut self, owner: &Owner, commands: &Receiver<Command>, pending: &mut Option<PendingReply>) {
        owner.command_pending.store(false, Ordering::Release);
        let Ok(Command { kind, body, until, complete }) = commands.try_recv() else { return; };
        let result = (|| {
            let now = owner.live()?;
            if now >= until { return Err(io::ErrorKind::TimedOut.into()); }
            if pending.as_ref().is_some_and(|reply| now >= reply.until) {
                let _ = pending.take().unwrap().complete.try_send(Err(io::ErrorKind::TimedOut.into()));
            }
            if pending.is_some() { return Err(denied()); }
            let wire = match kind {
                RENEW_ISSUE if body.first() == Some(&3) => wire::ISSUE,
                RENEW_PROOF => wire::PROOF,
                RENEW_RESULT => wire::APPLIED_RESULT,
                RENEW_INSTALL => {
                    self.install(owner, &body, true)?;
                    return Ok(None);
                }
                _ => return Err(denied()),
            };
            let request = self.delivery.queue(wire, body, now)?;
            Ok(Some((request, wire)))
        })();
        match result {
            Ok(Some((request, expected))) => *pending = Some(PendingReply { request, expected, until, complete }),
            Ok(None) => { let _ = complete.try_send(Ok(Vec::new())); }
            Err(error) => { let _ = complete.try_send(Err(error)); }
        }
    }

    fn service_reply(&mut self, records: Vec<Record>, pending: &mut Option<PendingReply>, owner: &Owner) -> io::Result<()> {
        let Some(waiting) = pending.as_ref() else { return Ok(()); };
        let now = owner.live()?;
        let result = if now >= waiting.until {
            Err(io::ErrorKind::TimedOut.into())
        } else if let Some(record) = records.into_iter().find(|record| record.request == waiting.request) {
            if record.kind == waiting.expected + 128 {
                if matches!(waiting.expected, wire::PROOF | wire::APPLIED_RESULT) {
                    self.delivery.receipt = Some(record.body.clone());
                }
                Ok(record.body)
            } else {
                Err(denied())
            }
        } else { return Ok(()); };
        let _ = pending.take().unwrap().complete.try_send(result);
        Ok(())
    }

    fn install(&mut self, owner: &Owner, bytes: &[u8], renew: bool) -> io::Result<()> {
        let mut reader = Reader(bytes);
        let boot_id = reader.nonzero()?;
        let revision = u64::from_be_bytes(reader.nonzero()?);
        let deadline = u64::from_be_bytes(reader.nonzero()?) / 100;
        let observation = Observation::parse(reader.bytes(112)?)?;
        let size = reader.u16()? as usize;
        let source = SourceScope::parse(reader.bytes(size)?)?;
        let size = reader.u16()? as usize;
        let delivery = DeliveryBinding::parse(reader.bytes(size)?)?;
        let generation = u32::try_from(delivery.generation).map_err(io::Error::other)?;
        let size = reader.u16()? as usize;
        let receipt = reader.bytes(size)?;
        reader.end()?;
        if self.delivery.receipt.take().as_deref() != Some(receipt) {
            return Err(denied());
        }
        let now = owner.live()?;
        if renew {
            if self.source.as_ref() != Some(&source) || self.delivery.generation != Some(generation) {
                return Err(denied());
            }
            self.delivery.connection.renew_phone(&delivery, &observation, boot_id, revision, deadline, now)?;
        } else {
            if self.source.is_some() { return Err(denied()); }
            self.delivery.connection.install_phone(&source, delivery, &observation, boot_id, revision, deadline, now)?;
            self.source = Some(source);
            self.delivery.generation = Some(generation);
        }
        owner.deadline.store(deadline, Ordering::Release);
        Ok(())
    }
    fn play(&mut self, owner: &Owner, bytes: &[u8], until: u64, on_ready: impl FnOnce()) -> io::Result<()> {
        if self.playback.is_some() { return Err(denied()); }
        let mut startup=crate::startup_observation::Timeline::start();
        let source = self.source.as_ref().ok_or_else(denied)?;
        let generation = self.delivery.generation.ok_or_else(denied)?;
        let [epoch, stream, format] = source.wire;
        let mut reader = Reader(bytes);
        let config =
            OutputConfigV8::new(reader.u32()?, reader.u32()?, reader.u32()?, reader.u32()?)
                .map_err(io::Error::other)?;
        let path = std::str::from_utf8(reader.0).map_err(io::Error::other)?;
        let mut renderer =
            AndroidAudio::open_with_config(path, config).map_err(io::Error::other)?;
        if !unsafe {
            renderer.bind_gate((owner as *const Owner).cast(), render_permitted, wake_owner)
        } {
            return Err(denied());
        }
        startup.mark("gate_bound_closed",None);
        let output = loop {
            let now = owner.live()?;
            if now >= until || renderer.snapshot().map_err(io::Error::other)?.stream_error != 0 {
                return Err(denied());
            }
            let sample = renderer.timing_sample().map_err(io::Error::other)?;
            // AAudio reports STARTED only after asynchronous requestStart completes.
            if sample.state_before == 4 && sample.state_after == 4 && sample.output_generation != 0
            {
                break sample.output_generation;
            }
            self.delivery.service(owner, false)?;
            self.delivery.discard()?;
            owner.wait(&self.delivery, until.min(now + 100_000), false)?;
        };
        if renderer.snapshot().map_err(io::Error::other)?.stream_error != 0 {
            return Err(denied());
        }
        startup.mark("aaudio_started_observed",None);
        let ready = self.delivery.connection.queue_phone_ready(output, owner.live()?)?;
        startup.mark("ready_request_queued",None);
        self.delivery.request = ready;
        if !self.delivery.reply(owner, ready, wire::OUTPUT, until)?.is_empty() {
            return Err(denied());
        }
        startup.mark("ready_reply_received",None);
        while !self.delivery.discard()? {
            owner.live()?;
        }
        startup.mark("decoded_media_discarded",None);
        let sample = renderer.timing_sample().map_err(io::Error::other)?;
        if sample.state_after != 4 || sample.output_generation != output {return Err(denied());}
        let gate_stats=renderer.snapshot().map_err(io::Error::other)?;
        if gate_stats.stream_error != 0 {return Err(denied());}
        owner.live()?;
        let (submit, commands) = mpsc::sync_channel(1);
        *owner.commands.lock().map_err(|_| denied())? = Some(submit);
        startup.opening_gate(gate_stats);
        owner.playing.store(true, Ordering::Release);
        self.playback = Some(AuthorityMedia::new(
            renderer,
            Configuration {
                media_epoch: epoch,
                source_stream_id: stream,
                format_config_id: format,
                maximum_frames: 240,
                ..Configuration::default()
            },
            generation, startup,
        ));
        on_ready();
        let mut packet = [0; 1350];
        let mut pending = None;
        let mut operation = "delivery.service";
        let mut stream_error = 0;
        let result = (|| -> io::Result<()> {
            loop {
                operation = "delivery.service";
                let records = self.delivery.service(owner, false)?;
                if pending.is_some() { operation = "service_reply"; self.service_reply(records, &mut pending, owner)?; }
                let media = self.playback.as_mut().unwrap();
                operation = "renderer.snapshot";
                stream_error = media
                    .renderer()
                    .snapshot()
                    .map_err(io::Error::other)?
                    .stream_error;
                if stream_error != 0 {
                    operation = "renderer.stream_error";
                    return Err(denied());
                }
                let mut drained = false;
                for _ in 0..128 {
                    operation = "owner.live.receive";
                    owner.live()?;
                    operation = "connection.phone_datagram";
                    let Some(size) = self.delivery.connection.phone_datagram(&mut packet)? else {
                        drained = true;
                        break;
                    };
                    operation = "media.receive";
                    media
                        .receive(&packet[..size], path)
                        .map_err(|error| io::Error::other(error.to_string()))?;
                }
                operation = "owner.live.finish_batch";
                owner.live()?;
                operation = "media.finish_batch";
                media
                    .finish_batch(path)
                    .map_err(|error| io::Error::other(error.to_string()))?;
                let signaled = if drained {
                    let until = pending.as_ref().map_or(u64::MAX, |reply: &PendingReply| reply.until);
                    operation = "owner.wait";
                    owner.wait(&self.delivery, until, false)?
                } else {
                    owner.command_pending.load(Ordering::Acquire)
                };
                if signaled {
                    self.service_command(owner, &commands, &mut pending);
                }
            }
        })();
        if let Err(error) = &result { log_play_failure(operation, error, stream_error); }
        owner.playing.store(false, Ordering::Release);
        owner.commands.lock().map_err(|_| denied())?.take();
        self.playback = None;
        if owner.stopped.load(Ordering::Acquire) {
            Ok(())
        } else {
            result
        }
    }
}

extern "C" fn render_permitted(context: *const c_void) -> bool {
    let owner = unsafe { &*context.cast::<Owner>() };
    owner.playing.load(Ordering::Acquire) && owner.live().is_ok()
}
extern "C" fn wake_owner(context: *const c_void) {
    unsafe { &*context.cast::<Owner>() }.signal();
}

#[no_mangle]
unsafe extern "C" fn deskfuse_phone_audio_open_bound_v1(
    address: *const c_char,
    deadline: u64,
    idle_ms: u32,
    network: u64,
) -> *mut c_void {
    std::panic::catch_unwind(|| {
        let address = unsafe { CStr::from_ptr(address) }
            .to_str()
            .ok()?
            .parse()
            .ok()?;
        Owner::new(address, deadline / 100, idle_ms, network)
            .ok()
            .map(|owner| Box::into_raw(Box::new(owner)).cast())
    })
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}
#[no_mangle]
unsafe extern "C" fn deskfuse_phone_audio_call_v3(
    handle: *const c_void,
    op: u8,
    input: *const u8,
    size: usize,
    deadline: u64,
    output: *mut u8,
    capacity: usize,
    ready: Option<Ready>,
    context: *mut c_void,
) -> i32 {
    std::panic::catch_unwind(|| -> io::Result<i32> {
        let owner = unsafe { &*handle.cast::<Owner>() };
        let bytes = unsafe { std::slice::from_raw_parts(input, size) };
        let result = if (RENEW_ISSUE..=RENEW_INSTALL).contains(&op) {
            owner.submit(op, bytes, deadline / 100)?
        } else {
            let mut run = owner.run.lock().map_err(|_| denied())?;
            match op {
                wire::INFO..=wire::APPLIED_RESULT if !matches!(op, wire::OUTPUT | wire::DETACH) => {
                    run.delivery.exchange(owner, op, bytes, deadline / 100)?
                }
                128 => run.delivery
                    .connection
                    .observation(owner.live()?)?
                    .ok_or_else(denied)?
                    .encode()?,
                129 => {
                    run.install(owner, bytes, false)?;
                    Vec::new()
                }
                130 => {
                    run.play(owner, bytes, deadline / 100, || {
                        if let Some(ready) = ready { ready(context); }
                    })?;
                    Vec::new()
                }
                WAIT_WARM if bytes.is_empty() && run.source.is_none() => {
                    run.delivery.wait_warm(owner, deadline / 100)?;
                    Vec::new()
                }
                _ => return Err(denied()),
            }
        };
        if result.len() > capacity {
            return Err(denied());
        }
        unsafe {
            std::ptr::copy_nonoverlapping(result.as_ptr(), output, result.len());
        }
        Ok(result.len() as i32)
    })
    .map_or(-1, |result| match result {
        Ok(value) => value,
        Err(error) if error.kind() == io::ErrorKind::TimedOut => -2,
        Err(_) => -1,
    })
}
#[no_mangle]
unsafe extern "C" fn deskfuse_phone_audio_stop(handle: *const c_void) {
    unsafe { &*handle.cast::<Owner>() }.stop();
}
#[no_mangle]
unsafe extern "C" fn deskfuse_phone_audio_close(handle: *mut c_void) {
    let mut owner = unsafe { Box::from_raw(handle.cast::<Owner>()) };
    owner.stop();
    if let Ok(run) = owner.run.get_mut() {
        run.delivery.connection.close_phone(&run.delivery.socket);
    }
}

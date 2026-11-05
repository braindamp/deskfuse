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
    Mutex,
};
use std::time::Instant;

fn denied() -> io::Error {
    io::Error::from(io::ErrorKind::PermissionDenied)
}
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
    session: Mutex<Session>,
    wake: OwnedFd,
    timer: OwnedFd,
    armed_until: AtomicU64,
    stopped: AtomicBool,
    playing: AtomicBool,
    deadline: AtomicU64,
}
struct Session {
    socket: UdpSocket,
    connection: ControlConnection,
    request: u64,
    receipt: Option<Vec<u8>>,
    source: Option<(SourceScope, u32)>,
}

impl Owner {
    fn new(peer: SocketAddr, until: u64, idle_ms: u32) -> io::Result<Self> {
        if boot()? >= until
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
            session: Mutex::new(Session {
                socket,
                connection,
                request: 0,
                receipt: None,
                source: None,
            }),
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
    fn wait(&self, session: &Session, until: u64, negative: bool) -> io::Result<()> {
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
        let timeout = session.connection.network_deadline().map_or(-1, |network| {
            network
                .saturating_duration_since(Instant::now())
                .as_nanos()
                .div_ceil(1_000_000)
                .min(i32::MAX as u128) as i32
        });
        let mut fds = [
            libc::pollfd {
                fd: session.socket.as_raw_fd(),
                events: libc::POLLIN
                    | if session.connection.needs_write() {
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
        if fds[1].revents & libc::POLLIN != 0 {
            let mut value = 0u64;
            unsafe {
                libc::read(self.wake.as_raw_fd(), (&mut value as *mut u64).cast(), 8);
            }
        }
        self.control_time(negative)?;
        Ok(())
    }
}

impl Session {
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
        let request = self.request.checked_add(1).ok_or_else(denied)?;
        self.connection.queue_control(
            Record {
                kind,
                request,
                body: body.to_vec(),
            },
            owner.control_time(negative)?,
        )?;
        self.request = request;
        let reply = self.reply(owner, request, kind, until)?;
        if matches!(kind, wire::PROOF | wire::APPLIED_RESULT) {
            self.receipt = Some(reply.clone());
        }
        Ok(reply)
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
    fn install(&mut self, owner: &Owner, bytes: &[u8]) -> io::Result<()> {
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
        if self.receipt.as_deref() != Some(receipt) || self.source.is_some() {
            return Err(denied());
        }
        self.connection.install_phone(
            &source,
            delivery,
            &observation,
            boot_id,
            revision,
            deadline,
            owner.live()?,
        )?;
        owner.deadline.store(deadline, Ordering::Release);
        self.source = Some((source, generation));
        self.receipt = None;
        Ok(())
    }
    fn play(&mut self, owner: &Owner, bytes: &[u8], until: u64) -> io::Result<()> {
        let (source, generation) = self.source.as_ref().ok_or_else(denied)?;
        let generation = *generation;
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
            self.service(owner, false)?;
            self.discard()?;
            owner.wait(self, until.min(now + 100_000), false)?;
        };
        if renderer.snapshot().map_err(io::Error::other)?.stream_error != 0 {
            return Err(denied());
        }
        let ready = self.connection.queue_phone_ready(output, owner.live()?)?;
        self.request = ready;
        if !self.reply(owner, ready, wire::OUTPUT, until)?.is_empty() {
            return Err(denied());
        }
        while !self.discard()? {
            owner.live()?;
        }
        let sample = renderer.timing_sample().map_err(io::Error::other)?;
        if sample.state_after != 4
            || sample.output_generation != output
            || renderer.snapshot().map_err(io::Error::other)?.stream_error != 0
        {
            return Err(denied());
        }
        owner.live()?;
        owner.playing.store(true, Ordering::Release);
        let mut media = AuthorityMedia::new(
            renderer,
            Configuration {
                media_epoch: epoch,
                source_stream_id: stream,
                format_config_id: format,
                maximum_frames: 240,
                ..Configuration::default()
            },
            generation,
        );
        let mut packet = [0; 1350];
        let result = (|| -> io::Result<()> {
            loop {
                self.service(owner, false)?;
                if media
                    .renderer()
                    .snapshot()
                    .map_err(io::Error::other)?
                    .stream_error
                    != 0
                {
                    return Err(denied());
                }
                let mut drained = false;
                for _ in 0..128 {
                    owner.live()?;
                    let Some(size) = self.connection.phone_datagram(&mut packet)? else {
                        drained = true;
                        break;
                    };
                    media
                        .receive(&packet[..size], path)
                        .map_err(|error| io::Error::other(error.to_string()))?;
                }
                owner.live()?;
                media
                    .finish_batch(path)
                    .map_err(|error| io::Error::other(error.to_string()))?;
                if drained {
                    owner.wait(self, u64::MAX, false)?;
                }
            }
        })();
        owner.playing.store(false, Ordering::Release);
        drop(media);
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
unsafe extern "C" fn deskfuse_phone_audio_open(
    address: *const c_char,
    deadline: u64,
    idle_ms: u32,
) -> *mut c_void {
    std::panic::catch_unwind(|| {
        let address = unsafe { CStr::from_ptr(address) }
            .to_str()
            .ok()?
            .parse()
            .ok()?;
        Owner::new(address, deadline / 100, idle_ms)
            .ok()
            .map(|owner| Box::into_raw(Box::new(owner)).cast())
    })
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}
#[no_mangle]
unsafe extern "C" fn deskfuse_phone_audio_call(
    handle: *const c_void,
    op: u8,
    input: *const u8,
    size: usize,
    deadline: u64,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    std::panic::catch_unwind(|| -> io::Result<i32> {
        let owner = unsafe { &*handle.cast::<Owner>() };
        let mut session = owner.session.lock().map_err(|_| denied())?;
        let bytes = unsafe { std::slice::from_raw_parts(input, size) };
        let result = match op {
            wire::INFO..=wire::APPLIED_RESULT if !matches!(op, wire::OUTPUT | wire::DETACH) => {
                session.exchange(owner, op, bytes, deadline / 100)?
            }
            128 => session
                .connection
                .observation(owner.live()?)?
                .ok_or_else(denied)?
                .encode()?,
            129 => {
                session.install(owner, bytes)?;
                Vec::new()
            }
            130 => {
                session.play(owner, bytes, deadline / 100)?;
                Vec::new()
            }
            _ => return Err(denied()),
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
    if let Ok(session) = owner.session.get_mut() {
        session.connection.close_phone(&session.socket);
    }
}

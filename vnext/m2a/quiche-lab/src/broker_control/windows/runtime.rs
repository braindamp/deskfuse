use super::*;
use super::channel::{BrokerControlClient, Frame};
use super::resume::ResumeWake;
use crate::audio_authority::{AudioAuthority, ControlGrant};
use crate::audio_listener_config::ListenerConfig;
use std::collections::VecDeque;
use std::net::UdpSocket;
use std::sync::Arc;

struct BoundListener {
    events: crate::reactor::WindowsEvents,
    bytes: Vec<u8>,
    config: ListenerConfig,
    _socket: Arc<UdpSocket>,
    acknowledgement: Vec<u8>,
}

struct NativeRuntime {
    client: BrokerControlClient,
    authority: AudioAuthority,
    listener: Option<BoundListener>,
    resume: ResumeWake,
    #[cfg(test)]
    test_clock: Option<Arc<std::sync::atomic::AtomicU64>>,
    #[cfg(test)]
    test_idle: Option<std::sync::mpsc::Sender<()>>,
    #[cfg(test)]
    test_datagram: Option<std::sync::mpsc::Sender<usize>>,
}

fn now() -> u64 {
    let mut value = 0;
    unsafe { QueryInterruptTimePrecise(&mut value); }
    value
}
fn retired() -> io::Error {
    io::Error::new(io::ErrorKind::BrokenPipe, "Native authority retired")
}
fn expired() -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, "Native authority cutoff")
}

pub(super) fn run(registration: [u8; 16]) -> io::Result<()> {
    NativeRuntime::new(BrokerControlClient::connect(registration)?)?.run()
}

impl NativeRuntime {
    fn new(client: BrokerControlClient) -> io::Result<Self> {
        let authority = AudioAuthority::new(client.boot(), client.worker())?;
        let resume = ResumeWake::register()?;
        Ok(Self {
            client, authority, listener: None, resume,
            #[cfg(test)]
            test_clock: None,
            #[cfg(test)]
            test_idle: None,
            #[cfg(test)]
            test_datagram: None,
        })
    }

    fn clock(&self) -> u64 {
        #[cfg(test)]
        if let Some(clock) = &self.test_clock {
            return clock.load(std::sync::atomic::Ordering::SeqCst);
        }
        now()
    }

    fn run(mut self) -> io::Result<()> {
        let outcome = self.serve();
        self.authority.close();
        self.listener = None;
        let joined = self.client.stop_and_join();
        let unregistered = self.resume.unregister();
        outcome.and(joined).and(unregistered)
    }

    fn live(&mut self) -> io::Result<()> {
        if let Some(error) = self.client.failure() { return Err(error); }
        if self.client.terminal() { return Err(retired()); }
        if self.authority.deadline().is_some() && !self.authority.source_allowed(self.clock()) {
            return Err(expired());
        }
        Ok(())
    }

    fn process(&mut self, frame: Frame) -> io::Result<Option<(u8, Vec<u8>)>> {
        match frame.kind {
            AUTHORITY => {
                let grant = ControlGrant::parse(&frame.body)?;
                if self.listener.as_ref().is_some_and(|owned| !owned.config.scope.matches(&grant)) {
                    return Err(invalid());
                }
                let tick = self.clock();
                self.authority.apply(grant, tick)?;
                Ok(Some((AUTHORITY_OBSERVED, frame.body)))
            }
            CONFIGURE_LISTENER => {
                let config = ListenerConfig::parse(&frame.body, self.client.worker())?;
                if let Some(owned) = &self.listener {
                    if owned.bytes != frame.body { return Err(invalid()); }
                    return Ok(Some((LISTENER_CONFIGURED, owned.acknowledgement.clone())));
                }
                if self.authority.grant().is_some_and(|grant| !config.scope.matches(grant)) {
                    return Err(invalid());
                }
                self.live()?;
                let (socket, acknowledgement) = config.bind()?;
                let events = crate::reactor::WindowsEvents::new(&socket)?;
                self.live()?;
                self.listener = Some(BoundListener {
                    events,
                    bytes: frame.body,
                    config,
                    _socket: socket,
                    acknowledgement: acknowledgement.clone(),
                });
                Ok(Some((LISTENER_CONFIGURED, acknowledgement)))
            }
            STOP if frame.body.is_empty() => {
                self.authority.close();
                Ok(None)
            }
            _ => Err(invalid()),
        }
    }

    fn handle_terminal(&mut self, mut already: VecDeque<Frame>) -> io::Result<()> {
        if let Some(error) = self.client.failure() { return Err(error); }
        already.extend(self.client.drain(16)?);
        if already.iter().any(|frame| frame.kind == STOP) {
            self.authority.close();
            self.listener = None;
            return Ok(());
        }
        // A closed projection fences before any queued configuration is
        // processed. Validate preceding grants in order without their positive
        // acknowledgements; none can resurrect an already terminal source.
        let mut closed = None;
        for frame in already {
            if frame.kind != AUTHORITY { continue; }
            let grant = ControlGrant::parse(&frame.body)?;
            if self.listener.as_ref().is_some_and(|owned| !owned.config.scope.matches(&grant)) {
                return Err(invalid());
            }
            let terminal = grant.closed;
            let tick = self.clock();
            self.authority.apply(grant, tick)?;
            if terminal {
                closed = Some(frame.body);
                break;
            }
        }
        self.authority.close();
        self.listener = None;
        if let Some(body) = closed {
            // This is a control acknowledgement, not a rundown observation.
            self.acknowledge(AUTHORITY_OBSERVED, &body, true)?;
            Ok(())
        } else {
            Err(retired())
        }
    }

    fn serve(&mut self) -> io::Result<()> {
        let mut pending = VecDeque::new();
        let outcome = self.serve_live(&mut pending);
        // Terminal may interrupt any ordinary turn, including an ACK waiting
        // for real write completion. Preserve frames already drained locally
        // and dispatch the restrictive frame before cancelling the pipe.
        if outcome.is_err() && self.client.terminal() {
            return self.handle_terminal(pending);
        }
        outcome
    }

    fn serve_live(&mut self, pending: &mut VecDeque<Frame>) -> io::Result<()> {
        loop {
            if self.client.terminal() {
                return Err(retired());
            }
            self.live()?;
            let frames = self.client.drain(8)?;
            if frames.is_empty() {
                if self.listener.is_some() {
                    if self.drain_listener()? { continue; }
                    self.live()?;
                    let timeout = self.timeout(None)?;
                    #[cfg(test)]
                    if let Some(sender) = &self.test_idle { let _ = sender.send(()); }
                    self.listener.as_ref().unwrap().events.wait_listener(
                        self.client.control_event().as_raw_handle(),
                        self.client.terminal_event().as_raw_handle(), self.resume.event(), timeout)?;
                } else {
                    self.wait(self.client.control_event().as_raw_handle(), None, false)?;
                }
                continue;
            }
            pending.extend(frames);
            while let Some(frame) = pending.pop_front() {
                if self.client.terminal() {
                    pending.push_front(frame);
                    return Err(retired());
                }
                self.live()?;
                if let Some((kind, body)) = self.process(frame)? {
                    self.acknowledge(kind, &body, false)?;
                }
            }
        }
    }

    fn drain_listener(&mut self) -> io::Result<bool> {
        let limit = self.listener.as_ref().unwrap().config.policy.packet_work as usize;
        let mut bytes = [0u8; crate::reactor::MAX_QUIC_UDP_BYTES + 1];
        for _ in 0..limit {
            self.live()?;
            let received = self.listener.as_ref().unwrap()._socket.recv_from(&mut bytes);
            match received {
                Ok((size, _)) => {
                    #[cfg(test)]
                    if let Some(sender) = &self.test_datagram { let _ = sender.send(size); }
                    #[cfg(not(test))]
                    let _ = size;
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => return Ok(false),
                Err(error) if error.raw_os_error() == Some(10040) => {}
                Err(error) => return Err(error),
            }
        }
        // No candidate/active route is authorized by this ingress slice.
        // Every packet is discarded without source, delivery or readiness side effects.
        Ok(true)
    }

    fn timeout(&self, until: Option<Instant>) -> io::Result<u32> {
        let mut remaining = until.map(|deadline| deadline.saturating_duration_since(Instant::now()).as_millis());
        if let Some(cutoff) = self.authority.deadline() {
            let ticks = cutoff.checked_sub(self.clock()).ok_or_else(expired)?;
            let ms = u128::from(ticks / 10000) + u128::from(ticks % 10000 != 0);
            remaining = Some(remaining.map_or(ms, |other| other.min(ms)));
        }
        match remaining {
            Some(0) => Err(io::Error::new(io::ErrorKind::TimedOut, "Native control wait deadline")),
            Some(ms) => Ok(ms.min(u128::from(u32::MAX - 1)).max(1) as u32),
            None => Ok(INFINITE),
        }
    }

    fn wait(&self, event: HANDLE, until: Option<Instant>, after_terminal: bool) -> io::Result<()> {
        let ms = self.timeout(until)?;
        let handles = [self.client.terminal_event().as_raw_handle(), event, self.resume.event()];
        let handles = if after_terminal { &handles[1..] } else { &handles[..] };
        let result = unsafe { WaitForMultipleObjects(handles.len() as u32, handles.as_ptr(), 0, ms) };
        if result == WAIT_TIMEOUT { return Err(io::Error::new(io::ErrorKind::TimedOut, "Native control wait deadline")); }
        if result == WAIT_FAILED { return Err(io::Error::last_os_error()); }
        if result < WAIT_OBJECT_0 + handles.len() as u32 {
            return Ok(());
        }
        Err(invalid())
    }

    fn acknowledge(&mut self, kind: u8, body: &[u8], after_terminal: bool) -> io::Result<()> {
        let deadline = Instant::now() + Duration::from_secs(3);
        let receipt = loop {
            if !after_terminal { self.live()?; }
            else if let Some(error) = self.client.failure() { return Err(error); }
            if Instant::now() >= deadline {
                return Err(io::Error::new(io::ErrorKind::TimedOut, "Native acknowledgement deadline"));
            }
            match self.client.try_write_confirmed(kind, body)? {
                Some(receipt) => break receipt,
                None => self.wait(self.client.capacity_event().as_raw_handle(), Some(deadline), after_terminal)?,
            }
        };
        loop {
            if !after_terminal { self.live()?; }
            else if let Some(error) = self.client.failure() { return Err(error); }
            if let Some(result) = receipt.take_result() { return result; }
            self.wait(receipt.event().as_raw_handle(), Some(deadline), after_terminal)?;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use super::super::channel::tests::{fixture, pair};
    fn listener_bytes() -> Vec<u8> {
        let f: serde_json::Value = serde_json::from_str(include_str!("../../../../../../tools/protocol3/audio-listener-v1.json")).unwrap();
        let hex = f["configuration4Hex"].as_str().unwrap();
        (0..hex.len()).step_by(2).map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect()
    }
    fn grant_for(config: &ListenerConfig, revision: u64, closed: bool) -> Vec<u8> {
        let mut bytes = fixture("control", "controlHex");
        let start = b"DeskFuse.Audio.Control.v1\0".len() + 32 + 16 + 16 + 32 + 16 + 16 + 8;
        bytes[start..start + 32].copy_from_slice(&config.scope.digest);
        let revision_at = start + 32 + 32 + 8 + 32;
        bytes[revision_at..revision_at + 8].copy_from_slice(&revision.to_be_bytes());
        bytes[revision_at + 8..revision_at + 16].copy_from_slice(&(now() + 600_000_000).to_be_bytes());
        if closed { let n = bytes.len(); bytes[n - 2] = 0; bytes[n - 1] = 1; }
        bytes
    }
    fn runtime() -> (NativeRuntime, Control) {
        let (local, remote) = pair();
        let grant = ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let client = BrokerControlClient::start_synthetic(local, grant.boot, grant.worker).unwrap();
        (NativeRuntime::new(client).unwrap(), remote)
    }
    fn spawn_runtime(runtime: NativeRuntime) -> std::thread::JoinHandle<io::Result<()>> {
        assert!(runtime.listener.is_none());
        let client = runtime.client;
        std::thread::spawn(move || NativeRuntime::new(client).and_then(NativeRuntime::run))
    }
    fn monitored_runtime(runtime: NativeRuntime, clock: Arc<std::sync::atomic::AtomicU64>) -> (
        std::thread::JoinHandle<io::Result<()>>,
        std::sync::mpsc::Receiver<usize>,
        std::sync::mpsc::Receiver<()>,
        std::sync::mpsc::Receiver<usize>,
        std::sync::mpsc::Receiver<Option<io::ErrorKind>>,
    ) {
        assert!(runtime.listener.is_none());
        let client = runtime.client;
        let (cookie_tx, cookie_rx) = std::sync::mpsc::sync_channel(1);
        let (idle_tx, idle_rx) = std::sync::mpsc::channel();
        let (datagram_tx, datagram_rx) = std::sync::mpsc::channel();
        let (done_tx, done_rx) = std::sync::mpsc::sync_channel(1);
        let thread = std::thread::spawn(move || {
            let result = (|| -> io::Result<()> {
                let mut runtime = NativeRuntime::new(client)?;
                runtime.test_clock = Some(clock);
                runtime.test_idle = Some(idle_tx);
                runtime.test_datagram = Some(datagram_tx);
                let _ = cookie_tx.send(runtime.resume.cookie());
                runtime.run()
            })();
            let _ = done_tx.send(result.as_ref().err().map(|error| error.kind()));
            result
        });
        (thread, cookie_rx, idle_rx, datagram_rx, done_rx)
    }

    #[test]
    fn sole_client_reader_binds_once_and_retries_exact_endpoint_ack() {
        let (runtime, remote) = runtime();
        let worker = runtime.client.worker();
        let bytes = listener_bytes();
        let config = ListenerConfig::parse(&bytes, worker).unwrap();
        let thread = spawn_runtime(runtime);
        remote.write(CONFIGURE_LISTENER, &bytes).unwrap();
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let address = match (ack[33], ack.len()) {
            (4, 40) => std::net::SocketAddr::new(std::net::IpAddr::V4(std::net::Ipv4Addr::new(ack[34], ack[35], ack[36], ack[37])), u16::from_be_bytes([ack[38], ack[39]])),
            _ => panic!("unexpected listener ACK"),
        };
        assert_ne!(address.port(), 0);
        assert_eq!(ack, config.acknowledgement(address).unwrap());
        remote.write(CONFIGURE_LISTENER, &bytes).unwrap();
        assert_eq!(remote.read(3000).unwrap().1, ack);
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
        assert!(UdpSocket::bind(address).is_ok());
    }
    #[test]
    fn resume_rechecks_expired_boot_cutoff_and_preserves_preexpiry_listener() {
        use std::sync::atomic::{AtomicU64, Ordering};
        let (runtime, remote) = runtime();
        let bytes = listener_bytes();
        let config = ListenerConfig::parse(&bytes, runtime.client.worker()).unwrap();
        let grant = grant_for(&config, 12, false);
        let at = grant.len() - 18;
        let cutoff = u64::from_be_bytes(grant[at..at + 8].try_into().unwrap());
        let clock = Arc::new(AtomicU64::new(now()));
        let (thread, cookie_rx, idle_rx, _datagram_rx, done_rx) =
            monitored_runtime(runtime, Arc::clone(&clock));
        let cookie = cookie_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        remote.write(AUTHORITY, &grant).unwrap();
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, grant));
        remote.write(CONFIGURE_LISTENER, &bytes).unwrap();
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let addr = std::net::SocketAddr::from((
            [ack[34], ack[35], ack[36], ack[37]],
            u16::from_be_bytes([ack[38], ack[39]]),
        ));
        idle_rx.recv_timeout(Duration::from_secs(3)).unwrap();

        clock.store(cutoff - 10_000_000, Ordering::SeqCst);
        super::super::resume::simulate_resume(cookie);
        idle_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        assert!(!thread.is_finished());
        assert!(UdpSocket::bind(addr).is_err());

        clock.store(cutoff + 1, Ordering::SeqCst);
        super::super::resume::simulate_resume(cookie);
        let result = done_rx.recv_timeout(Duration::from_secs(3));
        if result.is_err() { let _ = remote.write(STOP, &[]); }
        assert_eq!(result.unwrap(), Some(io::ErrorKind::PermissionDenied));
        assert!(thread.join().unwrap().is_err());
        assert!(UdpSocket::bind(addr).is_ok());
    }

    #[test]
    fn idle_udp_event_drains_loopback_without_another_broker_frame() {
        use std::sync::atomic::AtomicU64;
        let (runtime, remote) = runtime();
        let clock = Arc::new(AtomicU64::new(now()));
        let (thread, _cookie_rx, idle_rx, datagram_rx, done_rx) =
            monitored_runtime(runtime, clock);
        remote.write(CONFIGURE_LISTENER, &listener_bytes()).unwrap();
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let addr = std::net::SocketAddr::from((
            [ack[34], ack[35], ack[36], ack[37]],
            u16::from_be_bytes([ack[38], ack[39]]),
        ));
        idle_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
        assert_eq!(sender.send_to(&[42u8; 1200], addr).unwrap(), 1200);
        let received = datagram_rx.recv_timeout(Duration::from_secs(3));
        if received.is_err() { let _ = remote.write(STOP, &[]); }
        assert_eq!(received.unwrap(), 1200);
        assert!(remote.read(100).is_err());
        remote.write(STOP, &[]).unwrap();
        assert_eq!(done_rx.recv_timeout(Duration::from_secs(3)).unwrap(), None);
        assert!(thread.join().unwrap().is_ok());
    }

    #[test]
    fn closed_projection_during_positive_ack_completion_is_still_acknowledged() {
        let (runtime, remote) = runtime();
        let config = ListenerConfig::parse(&listener_bytes(), runtime.client.worker()).unwrap();
        let grant = grant_for(&config, 12, false);
        let closed = grant_for(&config, 13, true);
        let terminal = runtime.client.terminal_event().try_clone_to_owned().unwrap();
        let (entered, resume) = runtime.client.delay_next_write_completion();
        let thread = spawn_runtime(runtime);
        remote.write(AUTHORITY, &grant).unwrap();
        entered.recv_timeout(Duration::from_secs(3)).unwrap();
        remote.write(AUTHORITY, &closed).unwrap();
        let observed = unsafe { WaitForSingleObject(terminal.as_raw_handle(), 3000) };
        resume.send(()).unwrap();
        assert_eq!(observed, WAIT_OBJECT_0);
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, grant));
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, closed));
        assert!(thread.join().unwrap().is_ok());
    }
    #[test]
    fn stop_during_listener_ack_completion_retires_the_socket_and_joins() {
        let (runtime, remote) = runtime();
        let terminal = runtime.client.terminal_event().try_clone_to_owned().unwrap();
        let (entered, resume) = runtime.client.delay_next_write_completion();
        let thread = spawn_runtime(runtime);
        remote.write(CONFIGURE_LISTENER, &listener_bytes()).unwrap();
        entered.recv_timeout(Duration::from_secs(3)).unwrap();
        remote.write(STOP, &[]).unwrap();
        let observed = unsafe { WaitForSingleObject(terminal.as_raw_handle(), 3000) };
        resume.send(()).unwrap();
        assert_eq!(observed, WAIT_OBJECT_0);
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let address = std::net::SocketAddr::from(([ack[34], ack[35], ack[36], ack[37]], u16::from_be_bytes([ack[38], ack[39]])));
        assert!(thread.join().unwrap().is_ok());
        assert!(UdpSocket::bind(address).is_ok());
    }
}

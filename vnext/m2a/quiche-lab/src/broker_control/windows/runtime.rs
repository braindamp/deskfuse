use super::*;
use super::channel::{BrokerControlClient, Frame};
use super::resume::ResumeWake;
use super::source::SourceOwner;
use crate::audio_authority::{AudioAuthority, ControlGrant};
use crate::audio_candidate::{CandidateOutcome, CandidateSetup, SetupEvent};
use crate::audio_control::{self as ac, Record};
use crate::audio_listener_config::ListenerConfig;
use crate::audio_rpc::{Message, PipeRequests};
use std::collections::{BTreeMap, VecDeque};
use std::net::{SocketAddr, UdpSocket};
use std::sync::Arc;

struct BoundListener {
    events: crate::reactor::WindowsEvents,
    bytes: Vec<u8>,
    config: ListenerConfig,
    _socket: Arc<UdpSocket>,
    acknowledgement: Vec<u8>,
    setup_wake: Arc<OwnedHandle>,
    candidate: Option<CandidateSetup>,
    observed: Option<[u8; 16]>,
    pending_regular: VecDeque<Record>,
    pending_negative: Option<Record>,
    pending_local: VecDeque<(Record, bool)>,
    joined: Option<CandidateOutcome>,
    cooldown_until: u64,
    candidate_until: u64,
}

struct NativeRuntime {
    client: BrokerControlClient,
    authority: AudioAuthority,
    listener: Option<BoundListener>,
    source: Option<SourceOwner>,
    resume: ResumeWake,
    pipe_requests: PipeRequests,
    pending_replies: VecDeque<Message>,
    reply_custody: BTreeMap<([u8; 16], u64), Message>,
    active_regular: VecDeque<([u8; 16], Record)>,
    active_negative: Option<([u8; 16], Record)>,
    active_local: VecDeque<([u8; 16], Record, bool)>,
    #[cfg(test)]
    test_clock: Option<Arc<std::sync::atomic::AtomicU64>>,
    #[cfg(test)]
    test_idle: Option<std::sync::mpsc::Sender<()>>,
    #[cfg(test)]
    test_datagram: Option<std::sync::mpsc::Sender<usize>>,
    #[cfg(test)]
    test_source: Option<std::sync::mpsc::SyncSender<(bool, usize, bool)>>,
}

pub(super) fn now() -> u64 {
    let mut value = 0;
    unsafe { QueryInterruptTimePrecise(&mut value); }
    value
}
fn setup_clock() -> io::Result<u64> { Ok(now()) }
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
            client, authority, listener: None, source: None, resume,
            pipe_requests: PipeRequests::default(),
            pending_replies: VecDeque::new(),
            reply_custody: BTreeMap::new(),
            active_regular: VecDeque::new(),
            active_negative: None,
            active_local: VecDeque::new(),
            #[cfg(test)]
            test_clock: None,
            #[cfg(test)]
            test_idle: None,
            #[cfg(test)]
            test_datagram: None,
            #[cfg(test)]
            test_source: None,
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
        if let Some(source) = self.source.as_mut() { let _ = source.fence_delivery(); }
        self.source = None;
        self.listener = None;
        let joined = self.client.stop_and_join();
        let unregistered = self.resume.unregister();
        outcome.and(joined).and(unregistered)
    }

    fn live(&mut self) -> io::Result<()> {
        if let Some(error) = self.client.failure() { return Err(error); }
        if self.client.terminal() { return Err(retired()); }
        let now = self.clock();
        if self.authority.deadline().is_some() && !self.authority.source_allowed(now) {
            return Err(expired());
        }
        if self.source.as_ref().is_some_and(|source| source.grace_expired(now)) {
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
                self.authority.apply(grant.clone(), tick)?;
                if let Some(source) = self.source.as_mut() {
                    if grant.closed { source.fence_delivery()?; }
                    else { source.recheck(&grant, tick)?; }
                }
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
                let setup_wake = Arc::new(handle(unsafe { CreateEventW(null(), 0, 0, null()) })?);
                self.live()?;
                self.listener = Some(BoundListener {
                    events,
                    bytes: frame.body,
                    config,
                    _socket: socket,
                    acknowledgement: acknowledgement.clone(),
                    setup_wake,
                    candidate: None,
                    observed: None,
                    pending_regular: VecDeque::new(),
                    pending_negative: None,
                    pending_local: VecDeque::new(),
                    joined: None,
                    cooldown_until: 0,
                    candidate_until: 0,
                });
                Ok(Some((LISTENER_CONFIGURED, acknowledgement)))
            }
            NETWORK_REPLY => {
                let message = self.pipe_requests.reply(Message::parse(&frame.body, true)?)?;
                if self.pending_replies.len() >= ac::MAX_PENDING + 1 {
                    return Err(invalid());
                }
                self.pending_replies.push_back(message);
                Ok(None)
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
        let retiring = self.listener.as_mut().and_then(|owned| owned.candidate.take());
        if let Some(setup) = &retiring { setup.cancel(); }
        self.listener = None;
        let published = if let Some(body) = closed {
            self.acknowledge(AUTHORITY_OBSERVED, &body, true)
        } else {
            Err(retired())
        };
        drop(retiring);
        published
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
            self.live()?;
            pending.extend(self.client.drain(8)?);
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
            let mut work = self.dispatch_replies()?;
            work |= self.service_candidate()?;
            work |= self.service_active()?;
            let mut source_work = false;
            #[cfg(test)]
            let synthetic_clock = self.test_clock.clone();
            let boot = || {
                #[cfg(test)]
                if let Some(clock) = &synthetic_clock {
                    return clock.load(std::sync::atomic::Ordering::SeqCst);
                }
                now()
            };
            if let Some(source) = self.source.as_mut() {
                let client = &self.client;
                source_work = source.pump(
                    &mut self.authority, &boot, || client.terminal(),
                    self.listener.as_ref().map(|owned| owned._socket.as_ref()),
                )?;
                if let Some(listener) = &self.listener {
                    source.service_egress(
                        &mut self.authority, &listener._socket, false,
                        &boot, || client.terminal(),
                    )?;
                }
            }
            #[cfg(test)]
            if let (Some(source), Some(sender)) = (self.source.as_ref(), self.test_source.as_ref()) {
                let _ = sender.try_send((source.has_delivery(), source.outstanding(), source.sealed_pending()));
            }
            if self.listener.is_some() {
                let drained = self.drain_listener()?;
                let ready = self.listener.as_ref().unwrap().events.poll_listener(
                    self.source.as_ref().map(SourceOwner::period_event),
                )?;
                if ready.period {
                    if let Some(source) = self.source.as_ref() { source.service_period()?; }
                }
                if ready.writable {
                    let owned = self.listener.as_ref().unwrap();
                    if let Some(source) = self.source.as_mut() {
                        let client = &self.client;
                        source.service_egress(
                            &mut self.authority, &owned._socket, true,
                            &boot, || client.terminal(),
                        )?;
                    }
                    if let Some(setup) = self.listener.as_ref().and_then(|owned| owned.candidate.as_ref()) {
                        setup.writable();
                    }
                }
                if drained || ready.readable || work || source_work { continue; }
                self.live()?;
                let timeout = match self.timeout(self.source.as_ref().and_then(SourceOwner::deadline)) {
                    Ok(ms) => ms,
                    Err(error) if error.kind() == io::ErrorKind::TimedOut => {
                        self.live()?;
                        continue;
                    }
                    Err(error) => return Err(error),
                };
                #[cfg(test)]
                if let Some(sender) = &self.test_idle { let _ = sender.send(()); }
                let owned = self.listener.as_ref().unwrap();
                let socket = Arc::clone(&owned._socket);
                let completed = owned.candidate.as_ref().and_then(CandidateSetup::completion_handle);
                let source_event = self.source.as_ref().map(SourceOwner::source_event);
                let period_event = self.source.as_ref().map(SourceOwner::period_event);
                let ready = match owned.events.wait_listener(
                    self.client.control_event().as_raw_handle(),
                    self.client.terminal_event().as_raw_handle(),
                    self.client.capacity_event().as_raw_handle(),
                    self.resume.event(),
                    owned.setup_wake.as_raw_handle(),
                    source_event, period_event,
                    completed, timeout,
                ) {
                    Ok(ready) => ready,
                    Err(error) if error.kind() == io::ErrorKind::TimedOut => {
                        self.live()?;
                        continue;
                    }
                    Err(error) => return Err(error),
                };
                self.live()?;
                if ready.period {
                    if let Some(source) = self.source.as_ref() { source.service_period()?; }
                }
                if ready.writable {
                    if let Some(source) = self.source.as_mut() {
                        let client = &self.client;
                        source.service_egress(
                            &mut self.authority, &socket, true,
                            &boot, || client.terminal(),
                        )?;
                    }
                    if let Some(setup) = self.listener.as_ref().and_then(|owned| owned.candidate.as_ref()) {
                        setup.writable();
                    }
                }
                if ready.readable { continue; }
            } else {
                self.wait(self.client.control_event().as_raw_handle(), None, false)?;
            }
        }
    }

    fn active_local_reply(&mut self, id: [u8; 16], record: Record, reason: u8) -> io::Result<()> {
        if self.active_local.len() >= ac::MAX_PENDING + 1 { return Err(invalid()); }
        self.active_local.push_back((
            id, Record { kind: ac::ERROR, request: record.request, body: vec![reason] },
            ac::negative(record.kind),
        ));
        Ok(())
    }

    fn forward_active(&mut self, id: [u8; 16], record: Record) -> io::Result<bool> {
        let negative = ac::negative(record.kind);
        if matches!(record.kind, ac::PREPARE | ac::ISSUE | ac::PROOF) {
            self.active_local_reply(id, record, 4)?;
            return Ok(true);
        }
        let tls = Message { request: record.request, connection: id, kind: record.kind, body: record.body };
        if let Some(pipe) = self.pipe_requests.propose(&tls)? {
            if self.client.try_write(NETWORK_REQUEST, &pipe.encode(false)?)? {
                self.pipe_requests.submitted(&tls, &pipe)?;
                return Ok(true);
            }
        }
        let record = Record { kind: tls.kind, request: tls.request, body: tls.body };
        if negative {
            self.active_negative = Some((id, record));
            return Ok(false);
        }
        self.active_local_reply(id, record, 2)?;
        Ok(true)
    }

    fn service_active(&mut self) -> io::Result<bool> {
        if self.source.is_none() { return Ok(false); }
        let now = self.clock();
        let observed = self.source.as_ref().and_then(SourceOwner::connection_id);
        let events = self.source.as_mut().unwrap().read_control(now)?;
        let mut work = !events.is_empty();
        if let Some(id) = observed {
            for record in events {
                if ac::negative(record.kind) {
                    if self.active_negative.is_some() { return Err(invalid()); }
                    self.active_negative = Some((id, record));
                } else if self.active_regular.len() < ac::MAX_PENDING {
                    self.active_regular.push_back((id, record));
                } else {
                    self.active_local_reply(id, record, 2)?;
                }
            }
        }
        if let Some((id, record)) = self.active_negative.take() {
            work |= self.forward_active(id, record)?;
        }
        if self.active_negative.is_none() {
            for _ in 0..ac::MAX_PENDING {
                let Some((id, record)) = self.active_regular.pop_front() else { break; };
                if self.source.as_ref().and_then(SourceOwner::connection_id) == Some(id) {
                    work |= self.forward_active(id, record)?;
                }
            }
        }
        let count = self.active_local.len();
        for _ in 0..count {
            let (id, record, negative) = self.active_local.pop_front().unwrap();
            if self.source.as_ref().and_then(SourceOwner::connection_id) != Some(id) {
                work = true;
                continue;
            }
            match self.source.as_mut().unwrap().reply(record.clone(), now)? {
                true => work = true,
                false => self.active_local.push_back((id, record, negative)),
            }
        }
        if let Some(id) = self.source.as_ref().and_then(SourceOwner::pending_close) {
            if self.active_negative.as_ref().is_none_or(|(pending, _)| *pending != id) {
                if self.client.try_write(CONNECTION_CLOSED, &id)? {
                    self.source.as_mut().unwrap().close_submitted(id)?;
                    self.pipe_requests.retire_route(id);
                    self.active_regular.retain(|(connection, _)| *connection != id);
                    self.active_local.retain(|(connection, _, _)| *connection != id);
                    work = true;
                }
            }
        }
        Ok(work)
    }

    fn dispatch_replies(&mut self) -> io::Result<bool> {
        let count = self.pending_replies.len();
        let mut dispatched = false;
        for _ in 0..count {
            self.live()?;
            let message = self.pending_replies.pop_front().unwrap();
            message.encode(true)?;
            let active = self.listener.as_ref().is_some_and(|owned| {
                owned.observed == Some(message.connection)
                    && owned.candidate.as_ref().is_some_and(|setup| !setup.finished())
            });
            if !active && self.source.as_ref().and_then(SourceOwner::connection_id) == Some(message.connection) {
                let reply = Record {
                    kind: message.kind, request: message.request, body: message.body.clone(),
                };
                let tick = self.clock();
                if self.source.as_mut().unwrap().reply(reply, tick)? {
                    self.pipe_requests.disposition(&message)?;
                    dispatched = true;
                } else if self.source.as_ref().and_then(SourceOwner::connection_id) == Some(message.connection) {
                    self.pending_replies.push_back(message);
                } else {
                    self.pipe_requests.disposition(&message)?;
                    dispatched = true;
                }
                continue;
            }
            if active {
                let reply = Record {
                    kind: message.kind,
                    request: message.request,
                    body: message.body.clone(),
                };
                let result = self.listener.as_ref().unwrap().candidate.as_ref().unwrap().try_reply(&reply);
                match result {
                    Ok(true) => {
                        let key = (message.connection, message.request);
                        if self.reply_custody.insert(key, message).is_some() {
                            return Err(invalid());
                        }
                        dispatched = true;
                    }
                    Ok(false) => self.pending_replies.push_back(message),
                    Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {
                        self.pipe_requests.disposition(&message)?;
                        dispatched = true;
                    }
                    Err(error) => return Err(error),
                }
            } else {
                self.pipe_requests.disposition(&message)?;
                dispatched = true;
            }
        }
        Ok(dispatched)
    }

    fn local_reply(&mut self, record: Record, reason: u8) -> io::Result<()> {
        let owned = self.listener.as_mut().ok_or_else(invalid)?;
        if owned.pending_local.len() >= ac::MAX_PENDING + 1 { return Err(invalid()); }
        owned.pending_local.push_back((Record {
            kind: ac::ERROR, request: record.request, body: vec![reason],
        }, ac::negative(record.kind)));
        Ok(())
    }

    fn forward_request(&mut self, record: Record) -> io::Result<bool> {
        let owned = self.listener.as_ref().ok_or_else(invalid)?;
        let connection = owned.observed.ok_or_else(invalid)?;
        let negative = ac::negative(record.kind);
        if matches!(record.kind, ac::PREPARE | ac::ISSUE | ac::PROOF | ac::OUTPUT | ac::DETACH | ac::STOP) {
            self.local_reply(record, 4)?;
            return Ok(true);
        }
        if !negative && (owned.joined.is_some()
            || owned.candidate.as_ref().is_none_or(|setup| setup.finished())
            || self.clock() >= owned.candidate_until)
        {
            return Ok(true);
        }
        let tls = Message { request: record.request, connection, kind: record.kind, body: record.body };
        let pipe = self.pipe_requests.propose(&tls)?;
        if let Some(pipe) = pipe {
            if self.client.try_write(NETWORK_REQUEST, &pipe.encode(false)?)? {
                self.pipe_requests.submitted(&tls, &pipe)?;
                return Ok(true);
            }
        }
        if negative {
            self.listener.as_mut().unwrap().pending_negative = Some(Record {
                kind: tls.kind, request: tls.request, body: tls.body
            });
            return Ok(false);
        }
        self.local_reply(Record { kind: tls.kind, request: tls.request, body: tls.body }, 2)?;
        Ok(true)
    }

    fn service_candidate(&mut self) -> io::Result<bool> {
        if !self.listener.as_ref().is_some_and(|owned| owned.candidate.is_some() || owned.joined.is_some()) {
            return Ok(false);
        }
        let expired = self.listener.as_ref().is_some_and(|owned|
            owned.candidate.is_some() && self.clock() >= owned.candidate_until);
        if expired {
            self.listener.as_ref().unwrap().candidate.as_ref().unwrap().cancel();
        }
        let events = self.listener.as_ref().unwrap().candidate.as_ref()
            .map(|setup| setup.drain_events(32)).transpose()?.unwrap_or_default();
        let mut progress = !events.is_empty();
        for event in events {
            self.live()?;
            match event {
                SetupEvent::Observed(observation) => {
                    if self.listener.as_ref().unwrap().observed.is_some() { return Err(invalid()); }
                    self.acknowledge(CONNECTION_OBSERVED, &observation.encode()?, false)?;
                    self.listener.as_mut().unwrap().observed = Some(observation.connection);
                }
                SetupEvent::Request(record) => {
                    let owned = self.listener.as_mut().unwrap();
                    if owned.observed.is_none() { return Err(invalid()); }
                    if ac::negative(record.kind) {
                        if owned.pending_negative.is_some() { return Err(invalid()); }
                        owned.pending_negative = Some(record);
                    } else {
                        if owned.pending_regular.len() >= ac::MAX_PENDING { return Err(invalid()); }
                        owned.pending_regular.push_back(record);
                    }
                }
                SetupEvent::ReplyQueued(request) => {
                    let connection = self.listener.as_ref().unwrap().observed.ok_or_else(invalid)?;
                    if let Some(message) = self.reply_custody.remove(&(connection, request)) {
                        self.pipe_requests.disposition(&message)?;
                    }
                }
                SetupEvent::Pinned(_) => return Err(invalid()),
            }
        }

        if let Some(record) = self.listener.as_mut().unwrap().pending_negative.take() {
            progress |= self.forward_request(record)?;
        }
        if self.listener.as_ref().unwrap().pending_negative.is_none() {
            for _ in 0..ac::MAX_PENDING {
                let record = self.listener.as_mut().unwrap().pending_regular.pop_front();
                let Some(record) = record else { break; };
                progress |= self.forward_request(record)?;
            }
        }
        let local_count = self.listener.as_ref().unwrap().pending_local.len();
        for _ in 0..local_count {
            self.live()?;
            let reply = {
                let owned = self.listener.as_mut().unwrap();
                let at = owned.pending_local.iter().position(|(_, negative)| *negative).unwrap_or(0);
                owned.pending_local.remove(at).unwrap()
            };
            let (reply, negative) = reply;
            let accepted = self.listener.as_ref().unwrap().candidate.as_ref()
                .filter(|setup| !setup.finished())
                .map(|setup| setup.try_reply(&reply))
                .transpose();
            match accepted {
                Ok(Some(true)) => progress = true,
                Ok(Some(false)) => self.listener.as_mut().unwrap().pending_local.push_back((reply, negative)),
                Ok(None) => progress = true,
                Err(error) if error.kind() == io::ErrorKind::BrokenPipe => progress = true,
                Err(error) => return Err(error),
            }
        }

        if self.listener.as_ref().unwrap().candidate.as_ref().is_some_and(CandidateSetup::finished) {
            let outcome = self.listener.as_mut().unwrap().candidate.as_mut().unwrap().take_finished()?;
            if let Some(outcome) = outcome {
                if outcome.promoted || outcome.pinned_request.is_some() { return Err(invalid()); }
                let owned = self.listener.as_mut().unwrap();
                owned.candidate = None;
                owned.joined = Some(outcome);
                progress = true;
            }
        }
        if self.listener.as_ref().unwrap().joined.is_some()
            && self.listener.as_ref().unwrap().pending_negative.is_none()
        {
            let observed = self.listener.as_ref().unwrap().observed;
            if let Some(connection) = observed {
                if !self.client.try_write(CONNECTION_CLOSED, &connection)? { return Ok(progress); }
                let keys: Vec<_> = self.reply_custody.keys()
                    .filter(|(id, _)| *id == connection).copied().collect();
                for key in keys {
                    let reply = self.reply_custody.remove(&key).unwrap();
                    self.pipe_requests.disposition(&reply)?;
                }
                self.pipe_requests.retire_route(connection);
            }
            let next_at = self.clock();
            let owned = self.listener.as_mut().unwrap();
            owned.joined = None;
            owned.observed = None;
            owned.candidate_until = 0;
            owned.pending_regular.clear();
            owned.pending_local.clear();
            owned.cooldown_until = next_at.saturating_add(owned.config.policy.cooldown100ns());
            progress = true;
        }
        Ok(progress)
    }

    fn drain_listener(&mut self) -> io::Result<bool> {
        let limit = self.listener.as_ref().unwrap().config.policy.packet_work as usize;
        let to = self.listener.as_ref().unwrap()._socket.local_addr()?;
        let mut bytes = [0u8; crate::reactor::MAX_QUIC_UDP_BYTES + 1];
        for _ in 0..limit {
            self.live()?;
            let received = self.listener.as_ref().unwrap()._socket.recv_from(&mut bytes);
            match received {
                Ok((size, from)) => {
                    #[cfg(test)]
                    if let Some(sender) = &self.test_datagram { let _ = sender.send(size); }
                    if size <= crate::reactor::MAX_QUIC_UDP_BYTES {
                        self.route_packet(&mut bytes[..size], from, to)?;
                    }
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => return Ok(false),
                Err(error) if error.raw_os_error() == Some(10040) => {}
                Err(error) => return Err(error),
            }
        }
        Ok(true)
    }

    fn route_packet(&mut self, bytes: &mut [u8], from: SocketAddr, to: SocketAddr) -> io::Result<()> {
        if bytes.is_empty() { return Ok(()); }
        let header = match quiche::Header::from_slice(bytes, 16) {
            Ok(header) => header,
            Err(_) => return Ok(()),
        };
        let tick = self.clock();
        let info = quiche::RecvInfo { from, to };
        if let Some(source) = self.source.as_mut() {
            if source.routes(header.dcid.as_ref(), info) {
                source.receive(bytes, quiche::RecvInfo { from, to }, tick)?;
                return Ok(());
            }
        }
        let owned = self.listener.as_ref().unwrap();
        if owned.joined.is_some() { return Ok(()); }
        if let Some(setup) = &owned.candidate {
            if setup.routes(header.dcid.as_ref(), info) && !setup.finished() {
                let _ = setup.try_packet(bytes, quiche::RecvInfo { from, to });
            }
            return Ok(());
        }
        if bytes.len() < 1200 || header.ty != quiche::Type::Initial
            || header.version != quiche::PROTOCOL_VERSION || header.dcid.len() != 16
            || self.clock() < owned.cooldown_until
        {
            return Ok(());
        }
        let limits = owned.config.policy.limits(self.clock())?;
        let wake = Arc::clone(&owned.setup_wake);
        let signal: Arc<dyn Fn() + Send + Sync> = Arc::new(move || {
            unsafe { SetEvent(wake.as_raw_handle()); }
        });
        let started = CandidateSetup::start(
            bytes, info, Arc::clone(&owned._socket), owned.config.scope.clone(),
            self.client.boot(), limits, setup_clock, signal,
        );
        let tick = self.clock();
        let owned = self.listener.as_mut().unwrap();
        owned.cooldown_until = tick.saturating_add(owned.config.policy.cooldown100ns());
        if let Ok(setup) = started {
            setup.writable();
            owned.candidate = Some(setup);
            owned.candidate_until = limits.until100ns;
        }
        Ok(())
    }

    fn timeout(&self, until: Option<Instant>) -> io::Result<u32> {
        let mut remaining = until.map(|deadline| deadline.saturating_duration_since(Instant::now()).as_millis());
        if let Some(cutoff) = self.authority.deadline() {
            let ticks = cutoff.checked_sub(self.clock()).ok_or_else(expired)?;
            let ms = u128::from(ticks / 10000) + u128::from(ticks % 10000 != 0);
            remaining = Some(remaining.map_or(ms, |other| other.min(ms)));
        }
        if let Some(cutoff) = self.source.as_ref().and_then(SourceOwner::grace_deadline) {
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
    fn malformed_configuration_and_foreign_source_fail_before_binding() {
        let (mut runtime, _remote) = runtime();
        let bytes = listener_bytes();
        let config = ListenerConfig::parse(&bytes, runtime.client.worker()).unwrap();
        let mut wrong = bytes.clone();
        wrong[1] = 2;
        assert!(runtime.process(Frame {kind: CONFIGURE_LISTENER, body: wrong}).is_err());
        assert!(runtime.listener.is_none());
        let prior = grant_for(&config, 12, false);
        runtime.process(Frame {kind: AUTHORITY, body: prior}).unwrap();
        let mut wrong_source = bytes.clone();
        let source_start = 4 + b"DeskFuse.Audio.Source.v1\0".len();
        wrong_source[source_start] ^= 1;
        assert!(runtime.process(Frame {kind: CONFIGURE_LISTENER, body: wrong_source}).is_err());
        assert!(runtime.listener.is_none());
    }
    #[test]
    fn later_source_substitution_is_terminal_and_exact_authority_bytes_are_acknowledged() {
        let (mut runtime, _remote) = runtime();
        let bytes = listener_bytes();
        let config = ListenerConfig::parse(&bytes, runtime.client.worker()).unwrap();
        let ack = runtime.process(Frame {kind: CONFIGURE_LISTENER, body: bytes.clone()}).unwrap().unwrap();
        assert_eq!(ack.0, LISTENER_CONFIGURED);
        let socket = Arc::clone(&runtime.listener.as_ref().unwrap()._socket);
        assert_eq!(runtime.process(Frame {kind: CONFIGURE_LISTENER, body: bytes.clone()}).unwrap().unwrap().1, ack.1);
        assert!(Arc::ptr_eq(&socket, &runtime.listener.as_ref().unwrap()._socket));
        let mut conflicting = bytes.clone();
        *conflicting.last_mut().unwrap() ^= 1;
        assert!(runtime.process(Frame {kind: CONFIGURE_LISTENER, body: conflicting}).is_err());
        assert!(Arc::ptr_eq(&socket, &runtime.listener.as_ref().unwrap()._socket));
        let correct = grant_for(&config, 12, false);
        assert_eq!(runtime.process(Frame {kind: AUTHORITY, body: correct.clone()}).unwrap().unwrap(), (AUTHORITY_OBSERVED, correct.clone()));
        assert_eq!(runtime.process(Frame {kind: AUTHORITY, body: correct.clone()}).unwrap().unwrap().1, correct);
        let mut wrong = correct;
        let at = b"DeskFuse.Audio.Control.v1\0".len() + 32 + 16 + 16 + 32 + 16 + 16 + 8;
        wrong[at] ^= 1;
        assert!(runtime.process(Frame {kind: AUTHORITY, body: wrong}).is_err());
    }
    #[test]
    fn queued_config_does_not_bind_after_stop_and_idle_wait_wakes_from_client_terminal() {
        let (runtime, remote) = runtime();
        let thread = spawn_runtime(runtime);
        remote.write(STOP, &[]).unwrap();
        remote.write(CONFIGURE_LISTENER, &listener_bytes()).ok();
        assert!(thread.join().unwrap().is_ok());
    }

    #[test]
    fn closed_projection_fences_queued_configuration_and_acks_exact_bytes() {
        let (runtime, remote) = runtime();
        let config = ListenerConfig::parse(&listener_bytes(), runtime.client.worker()).unwrap();
        let closed = grant_for(&config, 12, true);
        let thread = spawn_runtime(runtime);
        remote.write(AUTHORITY, &closed).unwrap();
        let _ = remote.write(CONFIGURE_LISTENER, &listener_bytes());
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, closed));
        assert!(thread.join().unwrap().is_ok());
    }
    #[test]
    fn lost_service_wakes_otherwise_idle_runtime() {
        let (runtime, remote) = runtime();
        let thread = spawn_runtime(runtime);
        drop(remote);
        assert!(thread.join().unwrap().is_err());
    }
    #[test]
    fn idle_projection_expires_without_another_pipe_frame() {
        let (runtime, remote) = runtime();
        let config = ListenerConfig::parse(&listener_bytes(), runtime.client.worker()).unwrap();
        let mut bytes = grant_for(&config, 12, false);
        let offset = bytes.len() - 18;
        bytes[offset..offset + 8].copy_from_slice(&(now() + 20_000_000).to_be_bytes());
        let thread = spawn_runtime(runtime);
        remote.write(AUTHORITY, &bytes).unwrap();
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, bytes));
        assert!(thread.join().unwrap().is_err());
    }
    #[test]
    fn configured_listener_drains_one_explicit_packet_budget_without_admitting_media() {
        let (mut runtime, _remote) = runtime();
        runtime.process(Frame { kind: CONFIGURE_LISTENER, body: listener_bytes() }).unwrap();
        let owner = runtime.listener.as_ref().unwrap();
        let address = owner._socket.local_addr().unwrap();
        let limit = owner.config.policy.packet_work as usize;
        let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
        for _ in 0..limit * 3 {
            sender.send_to(&[2u8; 1200], address).unwrap();
        }
        assert!(runtime.drain_listener().unwrap());
        assert!(runtime.drain_listener().unwrap());
        assert!(runtime.drain_listener().unwrap());
        assert!(!runtime.drain_listener().unwrap());
        assert!(runtime.authority.grant().is_none());
        assert!(!runtime.authority.media_allowed(now()));
    }

    #[test]
    fn configured_listener_wakes_for_stop_during_bounded_udp_ingress() {
        let (runtime, remote) = runtime();
        let config = listener_bytes();
        let thread = spawn_runtime(runtime);
        remote.write(CONFIGURE_LISTENER, &config).unwrap();
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let address = std::net::SocketAddr::new(
            std::net::IpAddr::V4(std::net::Ipv4Addr::new(ack[34], ack[35], ack[36], ack[37])),
            u16::from_be_bytes([ack[38], ack[39]]));
        let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
        for _ in 0..64 { sender.send_to(&[7u8; 1200], address).unwrap(); }
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
        assert!(UdpSocket::bind(address).is_ok());
    }

    #[test]
    fn valid_projection_round_trips_exact_wire_bytes_through_production_style_dispatch() {
        let (runtime, remote) = runtime();
        let config = ListenerConfig::parse(&listener_bytes(), runtime.client.worker()).unwrap();
        let grant = grant_for(&config, 12, false);
        let thread = spawn_runtime(runtime);
        remote.write(AUTHORITY, &grant).unwrap();
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, grant.clone()));
        remote.write(AUTHORITY, &grant).unwrap();
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, grant));
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
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
    fn candidate_observation_precedes_info_and_readonly_applied_lookup() {
        use crate::audio_tls::Role;
        use crate::audio_transport::DeliveryTls;
        let (runtime, remote) = runtime();
        let mut config = listener_bytes();
        let policy_at = config.len() - 26;
        config[policy_at..policy_at + 4].copy_from_slice(&10_000u32.to_be_bytes());
        let thread = spawn_runtime(runtime);
        remote.write(CONFIGURE_LISTENER, &config).unwrap();
        let (kind, ack) = remote.read(3000).unwrap();
        assert_eq!(kind, LISTENER_CONFIGURED);
        let server = SocketAddr::from((
            [ack[34], ack[35], ack[36], ack[37]],
            u16::from_be_bytes([ack[38], ack[39]]),
        ));
        let fixture: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../../../tools/protocol3/audio-source-stop-v1.json"
        )).unwrap();
        let hex = fixture["appliedResultHex"].as_str().unwrap();
        let applied: Vec<u8> = (0..hex.len()).step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect();
        let source: Vec<u8> = config[4..4 + u16::from_be_bytes([config[2], config[3]]) as usize].to_vec();
        let phone_source = source.clone();
        let phone = std::thread::spawn(move || {
            let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
            socket.set_nonblocking(true).unwrap();
            let mut conn = DeliveryTls::generate(Role::Phone).unwrap()
                .connect(socket.local_addr().unwrap(), server, now() + 200_000_000, 10_000).unwrap();
            let mut observed = false;
            let mut info_sent = false;
            let mut applied_sent = false;
            let deadline = Instant::now() + Duration::from_secs(8);
            let mut buf = [0u8; 1350];
            loop {
                assert!(Instant::now() < deadline, "candidate control integration deadline");
                let tick = now();
                conn.service_timeout(tick).unwrap();
                for _ in 0..16 {
                    match socket.recv_from(&mut buf) {
                        Ok((n, from)) => {
                            conn.receive(&mut buf[..n], quiche::RecvInfo {
                                from, to: socket.local_addr().unwrap()
                            }, now()).unwrap();
                        }
                        Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                        Err(error) => panic!("{error}"),
                    }
                }
                if !info_sent && conn.facts(now()).unwrap().is_some() {
                    conn.queue_control(Record { kind: ac::INFO, request: 1, body: Vec::new() }, now()).unwrap();
                    info_sent = true;
                }
                for record in conn.read_control(now()).unwrap() {
                    match record.kind {
                        ac::NOTICE => {
                            assert!(crate::audio_control::Observation::parse(&record.body).is_ok());
                            observed = true;
                        }
                        129 => {
                            assert!(observed);
                            assert_eq!(record.request, 1);
                            assert_eq!(record.body, phone_source);
                            assert!(!applied_sent);
                            conn.queue_control(Record {
                                kind: ac::APPLIED_RESULT, request: 2, body: applied.clone()
                            }, now()).unwrap();
                            applied_sent = true;
                        }
                        ac::ERROR => {
                            assert!(observed && info_sent && applied_sent);
                            assert_eq!(record.request, 2);
                            assert_eq!(record.body, [1]);
                            return;
                        }
                        other => panic!("unexpected candidate reply: {other}"),
                    }
                }
                conn.service_egress(&socket, true, now()).unwrap();
                std::thread::sleep(Duration::from_millis(1));
            }
        });
        let (kind, body) = remote.read(3000).unwrap();
        assert_eq!(kind, CONNECTION_OBSERVED);
        let observation = crate::audio_control::Observation::parse(&body).unwrap();
        let (kind, body) = remote.read(3000).unwrap();
        assert_eq!(kind, NETWORK_REQUEST);
        let info = Message::parse(&body, false).unwrap();
        assert_eq!(info.connection, observation.connection);
        assert_eq!((info.kind, info.request), (ac::INFO, 1));
        let reply = Message { kind: 129, body: source.clone(), ..info };
        remote.write(NETWORK_REPLY, &reply.encode(true).unwrap()).unwrap();
        let (kind, body) = remote.read(3000).unwrap();
        assert_eq!(kind, NETWORK_REQUEST);
        let applied_request = Message::parse(&body, false).unwrap();
        assert_eq!(applied_request.connection, observation.connection);
        assert_eq!((applied_request.kind, applied_request.request), (ac::APPLIED_RESULT, 2));
        let miss = Message { kind: ac::ERROR, body: vec![1], ..applied_request };
        remote.write(NETWORK_REPLY, &miss.encode(true).unwrap()).unwrap();
        phone.join().unwrap();
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
    }

    #[test]
    fn joined_candidate_owns_slot_until_ordered_close_is_queued() {
        use crate::audio_tls::Role;
        use crate::audio_transport::DeliveryTls;
        use std::sync::atomic::{AtomicU64, Ordering};

        let (mut runtime, remote) = runtime();
        let clock = Arc::new(AtomicU64::new(now()));
        runtime.test_clock = Some(Arc::clone(&clock));
        runtime.process(Frame { kind: CONFIGURE_LISTENER, body: listener_bytes() }).unwrap();
        let listener = Arc::clone(&runtime.listener.as_ref().unwrap()._socket);
        let to = listener.local_addr().unwrap();
        let phone = UdpSocket::bind("127.0.0.1:0").unwrap();
        let tick = runtime.clock();
        let mut client = DeliveryTls::generate(Role::Phone).unwrap()
            .connect(phone.local_addr().unwrap(), to, tick + 100_000_000, 10_000).unwrap();
        client.service_egress(&phone, true, tick).unwrap();
        let mut initial = [0; 1350];
        let until = Instant::now() + Duration::from_secs(3);
        let (size, from) = loop {
            match listener.recv_from(&mut initial) {
                Ok(packet) => break packet,
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    assert!(Instant::now() < until, "phone Initial did not arrive");
                    std::thread::yield_now();
                }
                Err(error) => panic!("{error}"),
            }
        };
        let initial = &mut initial[..size];
        runtime.route_packet(initial, from, to).unwrap();
        let connection = [4; 16];
        let owned = runtime.listener.as_mut().unwrap();
        owned.observed = Some(connection);
        let setup = owned.candidate.as_ref().unwrap();
        setup.cancel();
        assert_eq!(unsafe { WaitForSingleObject(setup.completion_handle().unwrap().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        clock.store(owned.cooldown_until, Ordering::SeqCst);

        let (entered, resume) = runtime.client.delay_next_write_completion();
        assert!(runtime.client.try_write(LISTENER_CONFIGURED, &[1]).unwrap());
        entered.recv_timeout(Duration::from_secs(3)).unwrap();
        for _ in 1..8 { assert!(runtime.client.try_write(LISTENER_CONFIGURED, &[1]).unwrap()); }
        assert!(runtime.service_candidate().unwrap());
        assert!(runtime.listener.as_ref().unwrap().joined.is_some());
        runtime.route_packet(initial, from, to).unwrap();
        let owned = runtime.listener.as_ref().unwrap();
        assert!(owned.candidate.is_none());
        assert_eq!(owned.observed, Some(connection));

        resume.send(()).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(runtime.client.capacity_event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        assert!(runtime.service_candidate().unwrap());
        for _ in 0..8 { assert_eq!(remote.read(3000).unwrap(), (LISTENER_CONFIGURED, vec![1])); }
        assert_eq!(remote.read(3000).unwrap(), (CONNECTION_CLOSED, connection.to_vec()));
        assert!(runtime.listener.as_ref().unwrap().joined.is_none());
        assert!(runtime.listener.as_ref().unwrap().observed.is_none());
        runtime.route_packet(initial, from, to).unwrap();
        assert!(runtime.listener.as_ref().unwrap().candidate.is_none());
        clock.store(runtime.listener.as_ref().unwrap().cooldown_until, Ordering::SeqCst);
        runtime.route_packet(initial, from, to).unwrap();
        assert!(runtime.listener.as_ref().unwrap().candidate.is_some());
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

    struct OfflineInput {
        chunks: VecDeque<crate::committed_source::Block>,
        fences: Arc<std::sync::atomic::AtomicUsize>,
        drops: Arc<std::sync::atomic::AtomicUsize>,
    }
    impl Drop for OfflineInput {
        fn drop(&mut self) {
            self.drops.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        }
    }
    impl super::super::source::Input for OfflineInput {
        fn next_block(&mut self) -> io::Result<Option<crate::committed_source::Block>> {
            let mut block = self.chunks.pop_front();
            if let Some(block) = block.as_mut() {
                let qpc = crate::source_age::local_qpc_100ns()?;
                block.timing.commit_qpc = qpc.saturating_sub(1000);
                block.timing.presentation_qpc = qpc;
            }
            Ok(block)
        }
        fn arm(&self) -> io::Result<()> { Ok(()) }
        fn event(&self) -> HANDLE { std::ptr::null_mut() }
        fn period(&self) -> HANDLE { std::ptr::null_mut() }
        fn service_period(&self) -> io::Result<()> { Ok(()) }
        fn discard_partial(&mut self, qpc: u64) -> io::Result<crate::fresh_anchor::CommitFence> {
            self.fences.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            self.chunks.clear();
            Ok(crate::fresh_anchor::CommitFence {
                generation: 1, accepted: 1, end_frame: 480, request_qpc: qpc,
            })
        }
    }
    fn offline_bytes(hex: &str) -> Vec<u8> {
        (0..hex.len()).step_by(2)
            .map(|at| u8::from_str_radix(&hex[at..at + 2], 16).unwrap())
            .collect()
    }
    fn encode_control(grant: &ControlGrant) -> Vec<u8> {
        let mut bytes = b"DeskFuse.Audio.Control.v1\0".to_vec();
        bytes.extend_from_slice(&grant.host);
        bytes.extend_from_slice(&grant.namespace);
        bytes.extend_from_slice(&grant.enrollment);
        bytes.extend_from_slice(&grant.boot);
        bytes.extend_from_slice(&grant.worker);
        bytes.extend_from_slice(&grant.source);
        bytes.extend_from_slice(&grant.source_generation.to_be_bytes());
        bytes.extend_from_slice(&grant.source_digest);
        bytes.extend_from_slice(&grant.binding);
        bytes.extend_from_slice(&grant.delivery_generation.to_be_bytes());
        bytes.extend_from_slice(&grant.delivery_digest);
        bytes.extend_from_slice(&grant.revision.to_be_bytes());
        bytes.extend_from_slice(&grant.until.to_be_bytes());
        bytes.extend_from_slice(&grant.output_generation.to_be_bytes());
        bytes.push(grant.ready as u8);
        bytes.push(grant.closed as u8);
        bytes
    }
    fn offline_incumbent(
        ready: bool, blocked: bool, idle_ms: u32, staged_info: bool,
    ) -> (
        NativeRuntime, Control, crate::audio_transport::ControlConnection, UdpSocket,
        ControlGrant, [u8; 16], Arc<std::sync::atomic::AtomicUsize>,
        Arc<std::sync::atomic::AtomicUsize>,
        Arc<std::sync::atomic::AtomicU64>,
    ) {
        use crate::audio_control::{DeliveryBinding, SourceScope};
        use crate::audio_transport::tests::{pair_at, pump};
        use std::sync::atomic::{AtomicU64, AtomicUsize};
        let fixture: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../../../tools/protocol3/audio-transport-v1.json"
        )).unwrap();
        let scope = SourceScope::parse(&offline_bytes(fixture["sourceHex"].as_str().unwrap())).unwrap();
        let mut grant = ControlGrant::parse(&offline_bytes(fixture["controlHex"].as_str().unwrap())).unwrap();
        grant.until = 100_000_000;
        grant.ready = ready;
        grant.output_generation = if ready { 13 } else { 0 };
        let (local, remote) = pair();
        let client = BrokerControlClient::start_synthetic(local, grant.boot, grant.worker).unwrap();
        let mut runtime = NativeRuntime::new(client).unwrap();
        let bytes = listener_bytes();
        let config = ListenerConfig::parse(&bytes, grant.worker).unwrap();
        let (socket, acknowledgement) = config.bind().unwrap();
        let endpoint = socket.local_addr().unwrap();
        let phone_socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        phone_socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        let (mut phone, mut native) = pair_at(phone_socket.local_addr().unwrap(), endpoint, idle_ms);
        let observed = native.observation(100).unwrap().unwrap();
        let mut delivery = offline_bytes(fixture["deliveryHex"].as_str().unwrap());
        for (old, new) in [
            (15, observed.native), (16, observed.phone), (17, observed.nonce),
        ] {
            let offset = delivery.windows(32).position(|bytes| bytes == [old; 32]).unwrap();
            delivery[offset..offset + 32].copy_from_slice(&new);
        }
        let binding = DeliveryBinding::parse(&delivery).unwrap();
        grant.delivery_digest = binding.digest;
        if staged_info {
            phone.queue_control(Record { kind: ac::INFO, request: 1, body: Vec::new() }, 100).unwrap();
            pump(&mut phone, &mut native);
        }
        native.install_native(&scope, grant.boot, binding.clone(), &grant, 100).unwrap();
        let fences = Arc::new(AtomicUsize::new(0));
        let drops = Arc::new(AtomicUsize::new(0));
        let qpc = crate::source_age::local_qpc_100ns().unwrap();
        let block = crate::committed_source::Block {
            first_frame: 240, generation: 1, min_commit: 1,
            timing: crate::source_age::CommitTiming {
                commit_qpc: qpc.saturating_sub(1_000), presentation_qpc: qpc,
            }, pcm: [7; 960],
        };
        let input = OfflineInput {
            chunks: if ready { VecDeque::from([block]) } else { VecDeque::new() },
            fences: Arc::clone(&fences), drops: Arc::clone(&drops),
        };
        let mut source = SourceOwner::new(scope, Box::new(input));
        source.install(native, binding, &grant, endpoint, 100).unwrap();
        if blocked { source.block_sends(); }
        runtime.authority.apply(grant.clone(), 100).unwrap();
        runtime.authority.source_observed(grant.binding, 100).unwrap();
        runtime.authority.delivery_observed(grant.delivery_generation, grant.delivery_digest, 100).unwrap();
        if ready { runtime.authority.output_observed(grant.delivery_generation, 13, 100).unwrap(); }
        runtime.listener = Some(BoundListener {
            events: crate::reactor::WindowsEvents::new(&socket).unwrap(),
            bytes, config, _socket: socket, acknowledgement,
            setup_wake: Arc::new(handle(unsafe { CreateEventW(null(), 0, 0, null()) }).unwrap()),
            candidate: None, observed: None,
            pending_regular: VecDeque::new(), pending_negative: None,
            pending_local: VecDeque::new(), joined: None,
            cooldown_until: 0, candidate_until: 0,
        });
        runtime.source = Some(source);
        let clock = Arc::new(AtomicU64::new(100));
        runtime.test_clock = Some(Arc::clone(&clock));
        (runtime, remote, phone, phone_socket, grant, observed.connection, fences, drops, clock)
    }
    fn spawn_offline(mut runtime: NativeRuntime) -> std::thread::JoinHandle<io::Result<()>> {
        let listener = runtime.listener.take().unwrap();
        let BoundListener {
            events, bytes, config, _socket, acknowledgement, setup_wake, ..
        } = listener;
        drop(events);
        runtime.resume.unregister().unwrap();
        let client = runtime.client;
        let authority = runtime.authority;
        let source = runtime.source;
        let clock = runtime.test_clock;
        let test_source = runtime.test_source;
        std::thread::spawn(move || {
            let mut runtime = NativeRuntime::new(client)?;
            runtime.authority = authority;
            runtime.source = source;
            runtime.test_clock = clock;
            runtime.test_source = test_source;
            runtime.listener = Some(BoundListener {
                events: crate::reactor::WindowsEvents::new(&_socket)?,
                bytes, config, _socket, acknowledgement, setup_wake,
                candidate: None, observed: None,
                pending_regular: VecDeque::new(), pending_negative: None,
                pending_local: VecDeque::new(), joined: None,
                cooldown_until: 0, candidate_until: 0,
            });
            runtime.run()
        })
    }
    #[test]
    fn real_worker_loop_fences_blocked_incumbent_and_retains_source_until_stop() {
        use std::sync::atomic::Ordering;
        let (mut runtime, remote, _, _, grant, connection, fences, drops, _clock) =
            offline_incumbent(true, true, 10000, false);
        let (tx, rx) = std::sync::mpsc::sync_channel(32);
        runtime.test_source = Some(tx);
        let thread = spawn_offline(runtime);
        let mut pending = false;
        for _ in 0..64 {
            let (active, queued, sealed) = rx.recv_timeout(Duration::from_millis(100)).unwrap();
            if active && sealed { pending = true; break; }
        }
        assert!(pending, "real owner never retained blocked ciphertext");
        let mut next = grant;
        next.revision += 1;
        next.delivery_generation += 1;
        next.delivery_digest = [33; 32];
        next.ready = false;
        next.output_generation = 0;
        let update = encode_control(&next);
        remote.write(AUTHORITY, &update).unwrap();
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, update));
        assert_eq!(remote.read(3000).unwrap(), (CONNECTION_CLOSED, connection.to_vec()));
        let mut retained = false;
        for _ in 0..64 {
            let (active, queued, sealed) = rx.recv_timeout(Duration::from_millis(100)).unwrap();
            if !active && queued == 0 && !sealed { retained = true; break; }
        }
        assert!(retained, "newer grant did not fence old delivery in worker loop");
        assert_eq!(fences.load(Ordering::SeqCst), 1);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
        assert_eq!(drops.load(Ordering::SeqCst), 1);
    }
    #[test]
    fn real_worker_sends_control_before_output_ready_and_retires_idle_delivery_only() {
        use crate::audio_rpc::Message;
        use std::sync::atomic::Ordering;
        let (runtime, remote, mut phone, phone_socket, _grant, connection, fences, drops, _clock) =
            offline_incumbent(false, false, 1000, true);
        let thread = std::thread::spawn(move || runtime.run());
        let (kind, wire) = remote.read(3000).unwrap();
        assert_eq!(kind, NETWORK_REQUEST);
        let request = Message::parse(&wire, false).unwrap();
        assert_eq!((request.kind, request.request, request.connection),
            (ac::INFO, 1, connection));
        let reply = Message {
            request: request.request, connection, kind: 129, body: vec![42],
        };
        remote.write(NETWORK_REPLY, &reply.encode(true).unwrap()).unwrap();
        let mut got = false;
        let mut wire = [0u8; 1350];
        for _ in 0..32 {
            let (size, from) = match phone_socket.recv_from(&mut wire) {
                Ok(value) => value,
                Err(_) => break,
            };
            phone.receive(&mut wire[..size], quiche::RecvInfo {
                from, to: phone_socket.local_addr().unwrap(),
            }, 101).unwrap();
            if phone.read_control(101).unwrap().iter().any(|record|
                record.request == 1 && record.kind == 129 && record.body == [42]
            ) {
                got = true;
                break;
            }
        }
        assert!(got, "native approved control reply stalled before OutputReady");
        assert_eq!(remote.read(3000).unwrap(), (CONNECTION_CLOSED, connection.to_vec()));
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert_eq!(fences.load(Ordering::SeqCst), 1);
        remote.write(STOP, &[]).unwrap();
        assert!(thread.join().unwrap().is_ok());
        assert_eq!(drops.load(Ordering::SeqCst), 1);
    }

}

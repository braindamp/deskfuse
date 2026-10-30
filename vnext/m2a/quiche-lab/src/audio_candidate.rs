//! One cold, quarantined native TLS/setup owner. The listener remains the sole
//! UDP reader; only candidate ciphertext is copied into this bounded mailbox.
//! No PCM, media queues, second socket reader or positive authority is created.
use crate::{
    audio_authority::ControlGrant,
    audio_control::{DeliveryBinding, Observation, Record, SourceScope, MAX_PENDING},
    audio_tls::Role,
    audio_transport::{ControlConnection, DeliveryTls},
};
use std::{
    collections::VecDeque,
    io,
    net::UdpSocket,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

#[derive(Clone, Copy)]
pub struct SetupLimits {
    pub until100ns: u64,
    pub idle_ms: u32,
    pub queued_packets: usize,
    pub max_packets: u32,
    pub max_bytes: u64,
    pub max_control_messages: u16,
    pub packets_per_turn: usize,
    pub controls_per_turn: usize,
}
impl SetupLimits {
    fn validate(self, now: u64) -> io::Result<()> {
        if self.until100ns <= now
            || self.idle_ms == 0
            || !(1..=32).contains(&self.queued_packets)
            || !(1..=4096).contains(&self.max_packets)
            || !(1200..=4 * 1024 * 1024).contains(&self.max_bytes)
            || !(1..=256).contains(&self.max_control_messages)
            || !(1..=self.queued_packets).contains(&self.packets_per_turn)
            || !(1..=MAX_PENDING).contains(&self.controls_per_turn)
        {
            return Err(invalid());
        }
        Ok(())
    }
}
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid candidate setup")
}
fn retired() -> io::Error {
    io::Error::new(io::ErrorKind::BrokenPipe, "Candidate setup retired")
}
#[cfg(windows)]
fn setup_priority() -> io::Result<()> {
    use windows_sys::Win32::System::Threading::{
        GetCurrentThread, SetThreadPriority, THREAD_PRIORITY_BELOW_NORMAL,
    };
    // CPU priority only. Background I/O mode on this shared listener could
    // interfere with the active owner's resource scheduling.
    if unsafe { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}
#[cfg(not(windows))]
fn setup_priority() -> io::Result<()> {
    Ok(())
}

pub struct StagedPacket {
    bytes: [u8; 1350],
    length: usize,
    info: quiche::RecvInfo,
}
impl StagedPacket {
    fn copy(bytes: &[u8], info: quiche::RecvInfo) -> io::Result<Self> {
        if bytes.is_empty() || bytes.len() > 1350 {
            return Err(invalid());
        }
        let mut value = Self {
            bytes: [0; 1350],
            length: bytes.len(),
            info,
        };
        value.bytes[..bytes.len()].copy_from_slice(bytes);
        Ok(value)
    }
    pub fn receive(mut self, connection: &mut ControlConnection, now: u64) -> io::Result<bool> {
        let received = connection.receive(&mut self.bytes[..self.length], self.info, now)?;
        connection.discard_staged_datagrams(now, MAX_PENDING)?;
        Ok(received)
    }
}
enum Lifecycle {
    Pin(ControlGrant, [u8; 32]),
    Promote(ControlGrant, DeliveryBinding),
}
pub enum SetupEvent {
    Observed(Observation),
    Request(Record),
    Pinned([u8; 32]),
    ReplyQueued(u64),
}
pub struct CandidateOutcome {
    /// Returned even on failure: the source owner retains uncertain operation
    /// custody and handles actual closure before releasing its route/lease.
    pub connection: Option<ControlConnection>,
    pub pinned_request: Option<[u8; 32]>,
    pub promoted: bool,
    pub error: Option<io::Error>,
    /// The existing bounded setup mailboxes move, without a new PCM queue/copy.
    /// Adopt these into the source's control turn before enabling new media.
    pub staged_packets: VecDeque<StagedPacket>,
    pub staged_replies: VecDeque<Record>,
}
#[derive(Clone)]
struct PendingReply {
    record: Record,
    negative: bool,
}
#[derive(Default)]
struct Queue {
    packets: VecDeque<StagedPacket>,
    replies: VecDeque<PendingReply>,
    events: VecDeque<SetupEvent>,
    regular_replies: usize,
    negative_replies: usize,
    negative_request: Option<u64>,
    lifecycle: Option<Lifecycle>,
    writable: bool,
}
struct Shared {
    queue: Mutex<Queue>,
    changed: Condvar,
    stopping: AtomicBool,
}
impl Shared {
    fn lock(&self) -> std::sync::MutexGuard<'_, Queue> {
        self.queue.lock().unwrap_or_else(|p| p.into_inner())
    }
}
/// Scoped to the source's setup owner. Drop is source shutdown: it cancels AND
/// joins the actual worker. Warm expiry never silently drops a pinned outcome.
pub struct CandidateSetup {
    shared: Arc<Shared>,
    worker: Option<JoinHandle<CandidateOutcome>>,
    packet_capacity: usize,
    destination: [u8; 16],
    from: std::net::SocketAddr,
    to: std::net::SocketAddr,
}
impl CandidateSetup {
    pub fn start(
        initial: &[u8],
        info: quiche::RecvInfo,
        socket: Arc<UdpSocket>,
        scope: SourceScope,
        expected_boot: [u8; 32],
        limits: SetupLimits,
        clock: fn() -> io::Result<u64>,
        wake_owner: Arc<dyn Fn() + Send + Sync>,
    ) -> io::Result<Self> {
        limits.validate(clock()?)?;
        if expected_boot == [0; 32] {
            return Err(invalid());
        }
        socket.set_nonblocking(true)?;
        let mut initial = StagedPacket::copy(initial, info)?;
        if initial.length as u64 > limits.max_bytes {
            return Err(invalid());
        }
        if initial.length < 1200 {
            return Err(invalid());
        }
        let header = quiche::Header::from_slice(&mut initial.bytes[..initial.length], 16)
            .map_err(|_| invalid())?;
        if header.ty != quiche::Type::Initial
            || header.version != quiche::PROTOCOL_VERSION
            || header.dcid.len() != 16
        {
            return Err(invalid());
        }
        let destination = header.dcid.as_ref().try_into().map_err(|_| invalid())?;
        let from = info.from;
        let to = info.to;
        let shared = Arc::new(Shared {
            queue: Mutex::new(Queue::default()),
            changed: Condvar::new(),
            stopping: AtomicBool::new(false),
        });
        let owned = Arc::clone(&shared);
        let worker = thread::Builder::new()
            .name("audio-candidate".into())
            .spawn(move || {
                let result = run(
                    initial,
                    &socket,
                    &scope,
                    expected_boot,
                    limits,
                    clock,
                    &owned,
                    &wake_owner,
                );
                wake_owner();
                result
            })?;
        Ok(Self {
            shared,
            worker: Some(worker),
            packet_capacity: limits.queued_packets,
            destination,
            from,
            to,
        })
    }
    pub fn try_packet(&self, bytes: &[u8], info: quiche::RecvInfo) -> io::Result<bool> {
        if self.shared.stopping.load(Ordering::Acquire) || self.worker.is_none() {
            return Err(retired());
        }
        if info.from != self.from || info.to != self.to {
            return Ok(false);
        }
        let mut queue = self.shared.lock();
        if queue.packets.len() >= self.packet_capacity {
            return Ok(false);
        } // Candidate-only overload drop.
        let mut packet = StagedPacket::copy(bytes, info)?;
        let matches = quiche::Header::from_slice(&mut packet.bytes[..packet.length], 16)
            .is_ok_and(|h| h.dcid.as_ref() == self.destination);
        if !matches {
            return Ok(false);
        }
        queue.packets.push_back(packet);
        self.shared.changed.notify_one();
        Ok(true)
    }
    /// Dispatch hint, never authentication. Fixed route and current CID are
    /// rechecked before the bounded candidate-only mailbox accepts a packet.
    pub fn routes(&self, destination: &[u8], info: quiche::RecvInfo) -> bool {
        destination == self.destination
            && info.from == self.from
            && info.to == self.to
            && !self.shared.stopping.load(Ordering::Acquire)
    }
    pub fn try_reply(&self, reply: &Record) -> io::Result<bool> {
        if self.shared.stopping.load(Ordering::Acquire) || self.worker.is_none() {
            return Err(retired());
        }
        if !(129..=139).contains(&reply.kind) && reply.kind != 255 {
            return Err(invalid());
        }
        reply.encode()?;
        let mut queue = self.shared.lock();
        let negative = queue.negative_request == Some(reply.request);
        if if negative {
            queue.negative_replies >= 1
        } else {
            queue.regular_replies >= MAX_PENDING
        } {
            return Ok(false);
        }
        if negative {
            queue.negative_replies += 1;
        } else {
            queue.regular_replies += 1;
        }
        queue.replies.push_back(PendingReply {
            record: reply.clone(),
            negative,
        });
        self.shared.changed.notify_one();
        Ok(true)
    }
    pub fn try_pin(&self, parent: ControlGrant, request: [u8; 32]) -> io::Result<bool> {
        self.lifecycle(Lifecycle::Pin(parent, request))
    }
    /// Call only AFTER the source owner's serialized old-egress/assembly/ledger
    /// retirement barrier. The new connection remains off the media owner until
    /// its real thread completion has been joined and its exact value moved.
    pub fn try_promote_after_barrier(
        &self,
        grant: ControlGrant,
        binding: DeliveryBinding,
    ) -> io::Result<bool> {
        self.lifecycle(Lifecycle::Promote(grant, binding))
    }
    fn lifecycle(&self, command: Lifecycle) -> io::Result<bool> {
        if self.shared.stopping.load(Ordering::Acquire) || self.finished() {
            return Err(retired());
        }
        let mut queue = self.shared.lock();
        if queue.lifecycle.is_some() {
            return Ok(false);
        }
        queue.lifecycle = Some(command);
        self.shared.changed.notify_one();
        Ok(true)
    }
    pub fn writable(&self) {
        let mut queue = self.shared.lock();
        queue.writable = true;
        self.shared.changed.notify_one();
    }
    pub fn drain_events(&self, limit: usize) -> io::Result<Vec<SetupEvent>> {
        if limit == 0 || limit > 32 {
            return Err(invalid());
        }
        let mut queue = self.shared.lock();
        let n = limit.min(queue.events.len());
        Ok(queue.events.drain(..n).collect())
    }
    pub fn finished(&self) -> bool {
        self.worker.as_ref().is_none_or(|t| t.is_finished())
    }
    #[cfg(windows)]
    pub fn completion_handle(&self) -> Option<std::os::windows::io::BorrowedHandle<'_>> {
        use std::os::windows::io::AsHandle;
        self.worker.as_ref().map(|thread| thread.as_handle())
    }
    pub fn take_finished(&mut self) -> io::Result<Option<CandidateOutcome>> {
        // Captured observations/requests precede closure/transfer. Do not let
        // a completion race silently abandon the final queued negative RPC.
        if !self.finished() || !self.shared.lock().events.is_empty() {
            return Ok(None);
        }
        match self.worker.take() {
            Some(worker) => {
                let mut result = worker.join().map_err(|_| retired())?;
                self.take_staged(&mut result);
                Ok(Some(result))
            }
            None => Ok(None),
        }
    }
    pub fn cancel_and_join(&mut self) -> io::Result<Option<CandidateOutcome>> {
        self.shared.stopping.store(true, Ordering::Release);
        self.shared.changed.notify_one();
        match self.worker.take() {
            Some(worker) => {
                let mut result = worker.join().map_err(|_| retired())?;
                self.take_staged(&mut result);
                Ok(Some(result))
            }
            None => Ok(None),
        }
    }
    fn take_staged(&self, result: &mut CandidateOutcome) {
        self.shared.stopping.store(true, Ordering::Release);
        let mut queue = self.shared.lock();
        result.staged_packets = std::mem::take(&mut queue.packets);
        result.staged_replies = std::mem::take(&mut queue.replies)
            .into_iter()
            .map(|r| r.record)
            .collect();
    }
}
impl Drop for CandidateSetup {
    fn drop(&mut self) {
        let _ = self.cancel_and_join();
    }
}

fn emit(shared: &Shared, event: SetupEvent, wake: &Arc<dyn Fn() + Send + Sync>) -> io::Result<()> {
    {
        let mut queue = shared.lock();
        if queue.events.len() >= 32 {
            return Err(invalid());
        }
        queue.events.push_back(event);
    }
    wake();
    Ok(())
}
fn run(
    mut initial: StagedPacket,
    socket: &UdpSocket,
    scope: &SourceScope,
    expected_boot: [u8; 32],
    limits: SetupLimits,
    clock: fn() -> io::Result<u64>,
    shared: &Shared,
    wake: &Arc<dyn Fn() + Send + Sync>,
) -> CandidateOutcome {
    let mut connection = None;
    let mut pinned = None;
    let mut promoted = false;
    let result = (|| -> io::Result<()> {
        setup_priority()?;
        if shared.stopping.load(Ordering::Acquire) {
            return Err(retired());
        }
        let tls = DeliveryTls::generate_for_mode(Role::Native, 1)?;
        connection = Some(tls.accept(
            &mut initial.bytes[..initial.length],
            initial.info,
            limits.until100ns,
            limits.idle_ms,
        )?);
        let conn = connection.as_mut().unwrap();
        let mut packets = 1u32;
        let mut bytes = initial.length as u64;
        let mut messages = 0u16;
        let mut observed = false;
        loop {
            if shared.stopping.load(Ordering::Acquire) {
                return Err(retired());
            }
            let now = clock()?;
            let (command, writable) = {
                let mut q = shared.lock();
                (q.lifecycle.take(), std::mem::take(&mut q.writable))
            };
            match command {
                Some(Lifecycle::Pin(parent, request)) => {
                    if parent.boot != expected_boot {
                        return Err(invalid());
                    }
                    conn.pin_candidate(scope, &parent, request, now)?;
                    pinned = Some(request);
                    emit(shared, SetupEvent::Pinned(request), wake)?;
                }
                Some(Lifecycle::Promote(grant, binding)) => {
                    if pinned != Some(binding.request)
                        || grant.ready
                        || grant.output_generation != 0
                    {
                        return Err(invalid());
                    }
                    conn.install_native(scope, expected_boot, binding, &grant, now)?;
                    promoted = true;
                    return Ok(());
                }
                None => {}
            }
            for _ in 0..limits.packets_per_turn {
                let packet = shared.lock().packets.pop_front();
                let Some(mut packet) = packet else { break };
                packets = packets.checked_add(1).ok_or_else(invalid)?;
                bytes = bytes
                    .checked_add(packet.length as u64)
                    .ok_or_else(invalid)?;
                if packets > limits.max_packets || bytes > limits.max_bytes {
                    return Err(invalid());
                }
                conn.receive(&mut packet.bytes[..packet.length], packet.info, now)?;
            }
            conn.service_timeout(now)?;
            if !observed {
                if let Some(value) = conn.observation(now)? {
                    messages = messages.checked_add(1).ok_or_else(invalid)?; // Public NOTICE.
                    if messages > limits.max_control_messages {
                        return Err(invalid());
                    }
                    emit(shared, SetupEvent::Observed(value), wake)?;
                    observed = true;
                }
            }
            conn.discard_quarantined_datagrams(now, MAX_PENDING)?;
            let records = conn.read_control_limited(now, limits.controls_per_turn)?;
            let received_controls = records.len();
            for record in records {
                messages = messages.checked_add(1).ok_or_else(invalid)?;
                if messages > limits.max_control_messages {
                    return Err(invalid());
                }
                if crate::audio_control::negative(record.kind) {
                    shared.lock().negative_request = Some(record.request);
                }
                emit(shared, SetupEvent::Request(record), wake)?;
            }
            for _ in 0..limits.controls_per_turn.saturating_sub(received_controls) {
                let reply = {
                    let mut q = shared.lock();
                    // Prioritize a not-yet-submitted negative reply. Already
                    // queued QUIC stream frames retain their original order.
                    let next = q
                        .replies
                        .iter()
                        .position(|r| r.negative && conn.reply_capacity(true))
                        .or_else(|| {
                            q.replies
                                .iter()
                                .position(|r| !r.negative && conn.reply_capacity(false))
                        });
                    next.and_then(|i| q.replies.remove(i))
                };
                let Some(reply) = reply else { break };
                if messages >= limits.max_control_messages {
                    return Err(invalid());
                }
                match conn.queue_control(reply.record.clone(), now) {
                    Ok(()) => {
                        messages += 1;
                        {
                            let mut q = shared.lock();
                            if reply.negative {
                                q.negative_replies -= 1;
                            } else {
                                q.regular_replies -= 1;
                            }
                            if q.negative_request == Some(reply.record.request) {
                                q.negative_request = None;
                            }
                        }
                        emit(shared, SetupEvent::ReplyQueued(reply.record.request), wake)?;
                    }
                    Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                        shared.lock().replies.push_front(reply);
                        break;
                    }
                    Err(error) => return Err(error),
                }
            }
            let (sent_bytes, sent_packets, yielded) = conn.service_candidate_egress(
                socket,
                writable,
                now,
                limits.max_bytes.saturating_sub(bytes),
                limits.max_packets.saturating_sub(packets),
                limits.packets_per_turn.min(MAX_PENDING),
            )?;
            bytes = bytes.checked_add(sent_bytes).ok_or_else(invalid)?;
            packets = packets.checked_add(sent_packets).ok_or_else(invalid)?;
            // QUIC pacing/timeout or source-bound candidate expiry wakes this
            // cold owner. Writable readiness comes from the single socket owner.
            let until =
                Duration::from_nanos(conn.deadline().saturating_sub(now).saturating_mul(100));
            let network = conn
                .network_deadline()
                .map(|at| at.saturating_duration_since(Instant::now()))
                .unwrap_or(until);
            let mut queue = shared.lock();
            if !yielded
                && !conn.control_work_pending()
                && !shared.stopping.load(Ordering::Acquire)
                && queue.lifecycle.is_none()
                && queue.packets.is_empty()
                && !queue
                    .replies
                    .iter()
                    .any(|r| conn.reply_capacity(r.negative))
                && !queue.writable
            {
                let waited = shared
                    .changed
                    .wait_timeout(queue, until.min(network))
                    .unwrap_or_else(|p| p.into_inner());
                queue = waited.0;
            }
            drop(queue);
        }
    })();
    if result.is_err() {
        if let Some(conn) = connection.as_mut() {
            conn.retire();
        }
    }
    CandidateOutcome {
        connection,
        pinned_request: pinned,
        promoted,
        error: result.err(),
        staged_packets: VecDeque::new(),
        staged_replies: VecDeque::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;
    fn now() -> io::Result<u64> {
        Ok(100)
    }
    fn fixture() -> (Vec<u8>, Vec<u8>, ControlGrant) {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-transport-v1.json"
        ))
        .unwrap();
        let bytes = |key: &str| {
            let s = f[key].as_str().unwrap();
            (0..s.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
                .collect::<Vec<_>>()
        };
        (
            bytes("sourceHex"),
            bytes("deliveryHex"),
            ControlGrant::parse(&bytes("controlHex")).unwrap(),
        )
    }
    struct Harness {
        setup: CandidateSetup,
        phone: ControlConnection,
        phone_socket: UdpSocket,
        listener: Arc<UdpSocket>,
        events: Vec<SetupEvent>,
        replies: Vec<Record>,
        wakes: Arc<AtomicUsize>,
    }
    impl Harness {
        fn start(exhaust_send_budget: bool, messages: u16) -> Self {
            let phone_socket = UdpSocket::bind("127.0.0.1:0").unwrap();
            phone_socket.set_nonblocking(true).unwrap();
            let listener = Arc::new(UdpSocket::bind("127.0.0.1:0").unwrap());
            listener.set_nonblocking(true).unwrap();
            let mut phone = DeliveryTls::generate(Role::Phone)
                .unwrap()
                .connect(
                    phone_socket.local_addr().unwrap(),
                    listener.local_addr().unwrap(),
                    100_000_000,
                    10000,
                )
                .unwrap();
            phone.service_egress(&phone_socket, true, 100).unwrap();
            let mut packet = [0u8; 1350];
            let until = Instant::now() + Duration::from_secs(3);
            let (n, from) = loop {
                match listener.recv_from(&mut packet) {
                    Ok(v) => break v,
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => {}
                    Err(e) => panic!("{e}"),
                };
                assert!(Instant::now() < until);
                thread::yield_now();
            };
            let limits = SetupLimits {
                until100ns: 100_000_000,
                idle_ms: 10000,
                queued_packets: 16,
                max_packets: 1024,
                max_bytes: if exhaust_send_budget {
                    n as u64
                } else {
                    1024 * 1024
                },
                max_control_messages: messages,
                packets_per_turn: 8,
                controls_per_turn: 8,
            };
            let (source, _, grant) = fixture();
            let wakes = Arc::new(AtomicUsize::new(0));
            let counter = Arc::clone(&wakes);
            let wake: Arc<dyn Fn() + Send + Sync> = Arc::new(move || {
                counter.fetch_add(1, Ordering::SeqCst);
            });
            let setup = CandidateSetup::start(
                &packet[..n],
                quiche::RecvInfo {
                    from,
                    to: listener.local_addr().unwrap(),
                },
                Arc::clone(&listener),
                SourceScope::parse(&source).unwrap(),
                grant.boot,
                limits,
                now,
                wake,
            )
            .unwrap();
            Self {
                setup,
                phone,
                phone_socket,
                listener,
                events: vec![],
                replies: vec![],
                wakes,
            }
        }
        fn turn(&mut self) {
            self.phone.service_timeout(100).unwrap();
            self.phone
                .service_egress(&self.phone_socket, true, 100)
                .unwrap();
            let mut bytes = [0u8; 1350];
            for _ in 0..32 {
                match self.listener.recv_from(&mut bytes) {
                    Ok((n, from)) => {
                        if !self.setup.finished() {
                            let _ = self
                                .setup
                                .try_packet(
                                    &bytes[..n],
                                    quiche::RecvInfo {
                                        from,
                                        to: self.listener.local_addr().unwrap(),
                                    },
                                )
                                .unwrap();
                        }
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            for _ in 0..32 {
                match self.phone_socket.recv_from(&mut bytes) {
                    Ok((n, from)) => {
                        self.phone
                            .receive(
                                &mut bytes[..n],
                                quiche::RecvInfo {
                                    from,
                                    to: self.phone_socket.local_addr().unwrap(),
                                },
                                100,
                            )
                            .unwrap();
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            self.replies.extend(self.phone.read_control(100).unwrap());
            self.events.extend(self.setup.drain_events(32).unwrap());
            // Test peer scheduler only; production waits on actual socket/control
            // events. Let the below-normal cold owner run on a one-core runner.
            thread::sleep(Duration::from_millis(1));
        }
        fn until(&mut self, predicate: impl Fn(&Self) -> bool) {
            let deadline = Instant::now() + Duration::from_secs(5);
            while !predicate(self) {
                assert!(Instant::now() < deadline, "candidate test deadline");
                self.turn();
            }
            self.events.extend(self.setup.drain_events(32).unwrap());
        }
    }
    #[test]
    fn actual_candidate_thread_routes_control_pins_and_transfers_one_live_tls_value_without_closing_listener(
    ) {
        let mut h = Harness::start(false, 32);
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e, SetupEvent::Observed(_)))
        });
        let observation = h
            .events
            .iter()
            .find_map(|e| {
                if let SetupEvent::Observed(o) = e {
                    Some(o.clone())
                } else {
                    None
                }
            })
            .unwrap();
        h.phone
            .queue_control(
                Record {
                    kind: 1,
                    request: 1,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e,SetupEvent::Request(r)if r.kind==1&&r.request==1))
        });
        let (source, mut bytes, mut grant) = fixture();
        assert!(h
            .setup
            .try_reply(&Record {
                kind: 129,
                request: 1,
                body: source.clone()
            })
            .unwrap());
        h.until(|h| h.replies.iter().any(|r| r.kind == 129 && r.request == 1));
        assert_eq!(
            h.replies.iter().find(|r| r.kind == 129).unwrap().body,
            source
        );
        assert!(h
            .events
            .iter()
            .any(|e| matches!(e, SetupEvent::ReplyQueued(1))));
        for (old, new) in [
            (15, observation.native),
            (16, observation.phone),
            (17, observation.nonce),
        ] {
            let at = bytes.windows(32).position(|b| b == [old; 32]).unwrap();
            bytes[at..at + 32].copy_from_slice(&new);
        }
        let binding = DeliveryBinding::parse(&bytes).unwrap();
        grant.delivery_digest = binding.digest;
        grant.until = 100_000_000;
        grant.ready = false;
        grant.output_generation = 0;
        assert!(h.setup.try_pin(grant.clone(), binding.request).unwrap());
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e,SetupEvent::Pinned(id)if *id==binding.request))
        });
        h.phone
            .queue_control(
                Record {
                    kind: 1,
                    request: 2,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e,SetupEvent::Request(r)if r.request==2))
        });
        // The source-owner barrier is a separate prerequisite; this test covers
        // the cold owner move and exact connection binding, not physical PCM.
        assert!(h
            .setup
            .try_promote_after_barrier(grant.clone(), binding.clone())
            .unwrap());
        h.until(|h| h.setup.finished());
        assert!(h
            .setup
            .try_reply(&Record {
                kind: 129,
                request: 2,
                body: vec![77]
            })
            .unwrap());
        h.phone
            .queue_control(
                Record {
                    kind: 1,
                    request: 3,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        let mut captured = 0;
        let mut packet = [0; 1350];
        while captured == 0 {
            assert!(Instant::now() < deadline);
            h.phone.service_egress(&h.phone_socket, true, 100).unwrap();
            for _ in 0..32 {
                match h.listener.recv_from(&mut packet) {
                    Ok((n, from)) => {
                        assert!(h
                            .setup
                            .try_packet(
                                &packet[..n],
                                quiche::RecvInfo {
                                    from,
                                    to: h.listener.local_addr().unwrap()
                                }
                            )
                            .unwrap());
                        captured += 1;
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            thread::sleep(Duration::from_millis(1));
        }
        let mut result = h.setup.take_finished().unwrap().unwrap();
        assert!(result.error.is_none());
        assert!(result.promoted);
        assert_eq!(result.pinned_request, Some(binding.request));
        assert!(!result.staged_packets.is_empty());
        assert_eq!(result.staged_replies.len(), 1);
        let mut active = result.connection.take().unwrap();
        for packet in result.staged_packets {
            packet.receive(&mut active, 100).unwrap();
        }
        for reply in result.staged_replies {
            active.queue_control(reply, 100).unwrap();
        }
        assert!(active.is_approved());
        assert_eq!(active.observation(100).unwrap(), Some(observation));
        assert!(h.setup.take_finished().unwrap().is_none());
        assert!(h.wakes.load(Ordering::SeqCst) > 0);
        drop(h.setup); // The moved active value and shared listener remain live.
        assert!(active.is_approved());
        assert!(h.listener.local_addr().is_ok());
        let until = Instant::now() + Duration::from_secs(3);
        let mut got = active
            .read_control(100)
            .unwrap()
            .iter()
            .any(|r| r.request == 3);
        let mut reply_delivered = false;
        let mut packet = [0; 1350];
        while !got || !reply_delivered {
            assert!(Instant::now() < until);
            h.phone.service_egress(&h.phone_socket, true, 100).unwrap();
            for _ in 0..32 {
                match h.listener.recv_from(&mut packet) {
                    Ok((n, from)) => {
                        active
                            .receive(
                                &mut packet[..n],
                                quiche::RecvInfo {
                                    from,
                                    to: h.listener.local_addr().unwrap(),
                                },
                                100,
                            )
                            .unwrap();
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            got |= active
                .read_control(100)
                .unwrap()
                .iter()
                .any(|r| r.request == 3 && r.kind == 1);
            active.service_egress(&h.listener, true, 100).unwrap();
            for _ in 0..32 {
                match h.phone_socket.recv_from(&mut packet) {
                    Ok((n, from)) => {
                        h.phone
                            .receive(
                                &mut packet[..n],
                                quiche::RecvInfo {
                                    from,
                                    to: h.phone_socket.local_addr().unwrap(),
                                },
                                100,
                            )
                            .unwrap();
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            reply_delivered |= h
                .phone
                .read_control(100)
                .unwrap()
                .iter()
                .any(|r| r.kind == 129 && r.request == 2 && r.body == [77]);
            thread::yield_now();
        }
    }
    #[test]
    fn candidate_total_send_budget_fails_before_network_send_and_keeps_listener_custody() {
        let mut h = Harness::start(true, 32);
        h.until(|h| h.setup.finished());
        let result = h.setup.take_finished().unwrap().unwrap();
        assert!(result.error.is_some());
        assert!(!result.promoted);
        assert!(!result.connection.unwrap().is_approved());
        assert!(h.listener.local_addr().is_ok());
        assert!(h.setup.cancel_and_join().unwrap().is_none());
    }
    #[test]
    fn candidate_control_budget_and_explicit_cancellation_retire_only_actual_setup_work() {
        let mut h = Harness::start(false, 2);
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e, SetupEvent::Observed(_)))
        });
        h.phone
            .queue_control(
                Record {
                    kind: 1,
                    request: 1,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        h.phone
            .queue_control(
                Record {
                    kind: 1,
                    request: 2,
                    body: vec![],
                },
                100,
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        let mut bytes = [0u8; 1350];
        while !h.setup.finished() {
            assert!(Instant::now() < deadline);
            h.phone.service_egress(&h.phone_socket, true, 100).unwrap();
            for _ in 0..32 {
                match h.listener.recv_from(&mut bytes) {
                    Ok((n, from)) => {
                        if !h.setup.finished() {
                            let _ = h
                                .setup
                                .try_packet(
                                    &bytes[..n],
                                    quiche::RecvInfo {
                                        from,
                                        to: h.listener.local_addr().unwrap(),
                                    },
                                )
                                .unwrap();
                        }
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(e) => panic!("{e}"),
                }
            }
            thread::sleep(Duration::from_millis(1));
        }
        assert!(h.setup.take_finished().unwrap().is_none()); // Last captured request must precede closure.
        h.events.extend(h.setup.drain_events(32).unwrap());
        let result = h.setup.take_finished().unwrap().unwrap();
        assert!(result.error.is_some());
        assert!(!result.promoted);
        assert!(!result.connection.unwrap().is_approved());
        assert_eq!(
            h.events
                .iter()
                .filter(|e| matches!(e, SetupEvent::Request(_)))
                .count(),
            1
        );
        let mut second = Harness::start(false, 32);
        let outcome = second.setup.cancel_and_join().unwrap().unwrap();
        assert!(outcome.error.is_some());
        assert!(second.setup.finished());
        assert!(second.listener.local_addr().is_ok());
        assert!(second.setup.cancel_and_join().unwrap().is_none());
    }
    #[test]
    fn candidate_pin_rejects_a_boot_other_than_its_actual_broker_bootstrap() {
        let mut h = Harness::start(false, 32);
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e, SetupEvent::Observed(_)))
        });
        let (_, bytes, mut grant) = fixture();
        let binding = DeliveryBinding::parse(&bytes).unwrap();
        grant.boot[0] ^= 1;
        assert!(h.setup.try_pin(grant, binding.request).unwrap());
        h.until(|h| h.setup.finished());
        let outcome = h.setup.take_finished().unwrap().unwrap();
        assert!(outcome.error.is_some());
        assert!(!outcome.promoted);
        assert!(!outcome.connection.unwrap().is_approved());
        assert!(h.listener.local_addr().is_ok());
    }
    #[test]
    fn candidate_routes_a_negative_error_with_eight_regular_requests_still_pending() {
        let mut h = Harness::start(false, 64);
        h.until(|h| {
            h.events
                .iter()
                .any(|e| matches!(e, SetupEvent::Observed(_)))
        });
        // The phone opens control stream 0; the server's NOTICE cannot precede
        // that client-opened stream on the wire.
        for request in 1..=8 {
            h.phone
                .queue_control(
                    Record {
                        kind: 1,
                        request,
                        body: vec![],
                    },
                    100,
                )
                .unwrap();
        }
        let deadline = Instant::now() + Duration::from_secs(3);
        let observation = loop {
            if let Some(value) = h.phone.observation(100).unwrap() {
                break value;
            }
            assert!(Instant::now() < deadline);
            h.turn();
        };
        let actual = quiche::RecvInfo {
            from: h.phone_socket.local_addr().unwrap(),
            to: h.listener.local_addr().unwrap(),
        };
        let mut wrong = [0u8; 25];
        wrong[0] = 0x40;
        wrong[1..17].fill(99);
        assert!(!h.setup.try_packet(&wrong, actual).unwrap());
        assert!(!h
            .setup
            .try_packet(
                &[0],
                quiche::RecvInfo {
                    from: "127.0.0.1:1".parse().unwrap(),
                    to: actual.to
                }
            )
            .unwrap());
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-source-stop-v1.json"
        ))
        .unwrap();
        let hex = f["signedStopHex"].as_str().unwrap();
        let mut stop = (0..hex.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
            .collect::<Vec<_>>();
        let n = u16::from_be_bytes(stop[..2].try_into().unwrap()) as usize;
        stop[2 + n - 112..2 + n].copy_from_slice(&observation.encode().unwrap());
        // DER-shaped fixture tests framing/capacity only. Broker signature
        // verification is exercised separately with actual enrolled signatures.
        h.phone
            .queue_control(
                Record {
                    kind: 9,
                    request: 9,
                    body: stop,
                },
                100,
            )
            .unwrap();
        h.until(|h| {
            h.events
                .iter()
                .filter(|e| matches!(e, SetupEvent::Request(_)))
                .count()
                == 9
        });
        assert!(h
            .setup
            .try_reply(&Record {
                kind: 255,
                request: 9,
                body: vec![1]
            })
            .unwrap());
        h.until(|h| h.replies.iter().any(|r| r.kind == 255 && r.request == 9));
        assert_eq!(
            h.replies
                .iter()
                .filter(|r| r.kind != crate::audio_control::NOTICE)
                .count(),
            1
        );
        assert!(!h.phone.is_approved());
        for request in 1..=8 {
            assert!(h
                .setup
                .try_reply(&Record {
                    kind: 129,
                    request,
                    body: vec![]
                })
                .unwrap());
        }
        h.until(|h| {
            h.replies
                .iter()
                .filter(|r| r.kind != crate::audio_control::NOTICE)
                .count()
                == 9
        });
        let result = h.setup.cancel_and_join().unwrap().unwrap();
        assert!(result.error.is_some());
        assert!(!result.connection.unwrap().is_approved());
        assert!(h.listener.local_addr().is_ok());
    }
}

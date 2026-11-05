//! Actual process-bound pipe custody, off the native source/UDP service turn.
//! One reader, one ordered writer, bounded requests and independent negative
//! capacity. Cancellation joins every real overlapped operation and both threads.
use super::*;
use std::{
    collections::VecDeque,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex,
    },
    thread::{self, JoinHandle},
};

#[derive(Clone, Debug)]
pub struct Frame {
    pub kind: u8,
    pub body: Vec<u8>,
}
#[derive(Clone, Copy)]
enum Lane {
    Regular = 0,
    Negative = 1,
    Internal = 2,
}
struct Outbound {
    frame: Frame,
    lane: Lane,
    closed_ack: bool,
    completion: Option<Arc<Completion>>,
}
struct Completion {
    event: OwnedHandle,
    finished: AtomicBool,
    result: Mutex<Option<io::Result<()>>>,
}
impl Completion {
    fn new() -> io::Result<Self> {
        Ok(Self {
            event: handle(unsafe { CreateEventW(null(), 1, 0, null()) })?,
            finished: AtomicBool::new(false),
            result: Mutex::new(None),
        })
    }
    fn finish(&self, result: io::Result<()>) {
        *self.result.lock().unwrap_or_else(|p| p.into_inner()) = Some(result);
        self.finished.store(true, Ordering::Release);
        unsafe { SetEvent(self.event.as_raw_handle()); }
    }
}
pub struct WriteReceipt(Arc<Completion>);
impl WriteReceipt {
    pub(super) fn new() -> io::Result<Self> { Ok(Self(Arc::new(Completion::new()?))) }
    pub fn event(&self) -> std::os::windows::io::BorrowedHandle<'_> {
        use std::os::windows::io::AsHandle;
        self.0.event.as_handle()
    }
    pub fn take_result(&self) -> Option<io::Result<()>> {
        if !self.0.finished.load(Ordering::Acquire) { return None; }
        self.0.result.lock().unwrap_or_else(|p| p.into_inner()).take()
    }
}
#[derive(Default)]
struct Queue {
    incoming: VecDeque<Frame>,
    outgoing: VecDeque<Outbound>,
    retained: [usize; 3],
    error: Option<String>,
}
struct Shared {
    queue: Mutex<Queue>,
    changed: Condvar,
    terminal: AtomicBool,
    stopping: AtomicBool,
    control_event: OwnedHandle,
    terminal_event: OwnedHandle,
    capacity_event: OwnedHandle,
    #[cfg(test)]
    completion_gate: Mutex<Option<(std::sync::mpsc::SyncSender<()>, std::sync::mpsc::Receiver<()>)>>,
    #[cfg(test)]
    enqueue_gate: Mutex<Option<(std::sync::mpsc::SyncSender<()>, std::sync::mpsc::Receiver<()>)>>,
}
impl Shared {
    fn lock(&self) -> std::sync::MutexGuard<'_, Queue> {
        self.queue.lock().unwrap_or_else(|p| p.into_inner())
    }
    fn terminal(&self) {
        self.terminal.store(true, Ordering::Release);
        unsafe {
            SetEvent(self.terminal_event.as_raw_handle());
        }
    }
    fn fail(&self, control: &Control, error: io::Error) {
        {
            let mut q = self.lock();
            if q.error.is_none() {
                q.error = Some(error.to_string());
            }
            self.terminal();
            self.stopping.store(true, Ordering::Release);
            for outgoing in q.outgoing.drain(..) {
                if let Some(completion) = outgoing.completion {
                    completion.finish(Err(io::Error::new(io::ErrorKind::BrokenPipe, "Control failed")));
                }
            }
        }
        self.changed.notify_all();
        control.cancel();
    }
}
pub struct BrokerControlClient {
    control: Arc<Control>,
    shared: Arc<Shared>,
    reader: Option<JoinHandle<()>>,
    writer: Option<JoinHandle<()>>,
    boot: [u8; 32],
    worker: [u8; 16],
}
impl BrokerControlClient {
    pub fn connect(worker: [u8; 16]) -> io::Result<Self> {
        let control = Control::connect(worker)?;
        let (kind, hello) = control.read(3000)?;
        if kind != HELLO {
            return Err(invalid());
        }
        require_hello(&hello, &worker, control.server_pid, control.server_created)?;
        control.write(READY, &hello)?;
        Self::start(control, hello[17..49].try_into().unwrap(), worker)
    }
    #[cfg(test)]
    pub(super) fn start_synthetic(control: Control, boot: [u8; 32], worker: [u8; 16]) -> io::Result<Self> {
        Self::start(control, boot, worker)
    }
    fn start(control: Control, boot: [u8; 32], worker: [u8; 16]) -> io::Result<Self> {
        if boot == [0; 32] || worker == [0; 16] {
            return Err(invalid());
        }
        let control = Arc::new(control);
        let shared = Arc::new(Shared {
            queue: Mutex::new(Queue::default()),
            changed: Condvar::new(),
            terminal: AtomicBool::new(false),
            stopping: AtomicBool::new(false),
            control_event: handle(unsafe { CreateEventW(null(), 1, 0, null()) })?,
            terminal_event: handle(unsafe { CreateEventW(null(), 1, 0, null()) })?,
            capacity_event: handle(unsafe { CreateEventW(null(), 0, 0, null()) })?,
            #[cfg(test)]
            completion_gate: Mutex::new(None),
            #[cfg(test)]
            enqueue_gate: Mutex::new(None),
        });
        let r = Arc::clone(&shared);
        let rc = Arc::clone(&control);
        let reader = thread::Builder::new()
            .name("audio-broker-read".into())
            .spawn(move || {
                if let Err(error) = read_loop(&rc, &r, boot, worker) {
                    r.fail(&rc, error);
                }
            })?;
        let w = Arc::clone(&shared);
        let wc = Arc::clone(&control);
        let writer = match thread::Builder::new()
            .name("audio-broker-write".into())
            .spawn(move || {
                if let Err(error) = write_loop(&wc, &w) {
                    w.fail(&wc, error);
                }
            }) {
            Ok(writer) => writer,
            Err(error) => {
                shared.stopping.store(true, Ordering::Release);
                control.cancel();
                let _ = reader.join();
                return Err(error);
            }
        };
        Ok(Self {
            control,
            shared,
            reader: Some(reader),
            writer: Some(writer),
            boot,
            worker,
        })
    }
    pub fn boot(&self) -> [u8; 32] {
        self.boot
    }
    pub fn worker(&self) -> [u8; 16] {
        self.worker
    }
    pub fn terminal(&self) -> bool {
        self.shared.terminal.load(Ordering::Acquire)
    }
    pub fn control_event(&self) -> std::os::windows::io::BorrowedHandle<'_> {
        use std::os::windows::io::AsHandle;
        self.shared.control_event.as_handle()
    }
    pub fn terminal_event(&self) -> std::os::windows::io::BorrowedHandle<'_> {
        use std::os::windows::io::AsHandle;
        self.shared.terminal_event.as_handle()
    }
    pub fn capacity_event(&self) -> std::os::windows::io::BorrowedHandle<'_> {
        use std::os::windows::io::AsHandle;
        self.shared.capacity_event.as_handle()
    }
    #[cfg(test)]
    pub(super) fn delay_next_write_completion(&self) -> (std::sync::mpsc::Receiver<()>, std::sync::mpsc::SyncSender<()>) {
        let (entered, observed) = std::sync::mpsc::sync_channel(1);
        let (resume, released) = std::sync::mpsc::sync_channel(1);
        *self.shared.completion_gate.lock().unwrap() = Some((entered, released));
        (observed, resume)
    }
    #[cfg(test)]
    pub(super) fn delay_next_enqueue(&self) -> (std::sync::mpsc::Receiver<()>, std::sync::mpsc::SyncSender<()>) {
        let (entered, observed) = std::sync::mpsc::sync_channel(1);
        let (resume, released) = std::sync::mpsc::sync_channel(1);
        *self.shared.enqueue_gate.lock().unwrap() = Some((entered, released));
        (observed, resume)
    }
    pub fn failure(&self) -> Option<io::Error> {
        self.shared.lock().error.as_ref().map(|message| io::Error::other(message.clone()))
    }
    pub fn drain(&self, limit: usize) -> io::Result<Vec<Frame>> {
        if !(1..=16).contains(&limit) {
            return Err(invalid());
        }
        let mut q = self.shared.lock();
        let n = limit.min(q.incoming.len());
        let frames = q.incoming.drain(..n).collect();
        // Same lock as publication: a reader cannot publish between empty-check
        // and reset. Terminal is a separate persistent, monotone signal.
        if q.incoming.is_empty() {
            unsafe {
                ResetEvent(self.shared.control_event.as_raw_handle());
            }
        }
        Ok(frames)
    }
    pub fn try_write(&self, kind: u8, body: &[u8]) -> io::Result<bool> {
        self.enqueue(kind, body, None)
    }
    pub fn try_write_confirmed(&self, kind: u8, body: &[u8]) -> io::Result<Option<WriteReceipt>> {
        let receipt = WriteReceipt::new()?;
        Ok(self.try_write_with_receipt(kind, body, &receipt)?.then_some(receipt))
    }
    // Keep one receipt across full-queue retries. The owner submits it once.
    pub(super) fn try_write_with_receipt(&self, kind: u8, body: &[u8], receipt: &WriteReceipt) -> io::Result<bool> {
        self.enqueue(kind, body, Some(Arc::clone(&receipt.0)))
    }
    fn enqueue(&self, kind: u8, body: &[u8], completion: Option<Arc<Completion>>) -> io::Result<bool> {
        if body.len() >= MAX_FRAME {
            return Err(invalid());
        }
        let mut closed_ack = false;
        let lane = match kind {
            NETWORK_REQUEST => {
                let message = crate::audio_rpc::Message::parse(body, false)?;
                if crate::audio_control::negative(message.kind) {
                    Lane::Negative
                } else {
                    Lane::Regular
                }
            }
            PINNED_ISSUE => {
                crate::audio_rpc::pinned_issue(body)?;
                Lane::Regular
            }
            CONNECTION_OBSERVED => {
                crate::audio_control::Observation::parse(body)?;
                Lane::Internal
            }
            CONNECTION_CLOSED => {
                if body.len() != 16 || body.iter().all(|b| *b == 0) {
                    return Err(invalid());
                }
                Lane::Internal
            }
            SOURCE_OPENED | SOURCE_MAPPED => Lane::Internal,
            AUTHORITY_OBSERVED => {
                closed_ack = crate::audio_authority::ControlGrant::parse(body)?.closed;
                Lane::Internal
            }
            // Listener observation is public cold metadata; its concrete codec
            // and caller validate it before publication. It is never authority.
            LISTENER_CONFIGURED => {
                if body.is_empty() || body.len() > 256 {
                    return Err(invalid());
                }
                Lane::Internal
            }
            _ => return Err(invalid()),
        };
        #[cfg(test)]
        if let Some((entered, released)) = self.shared.enqueue_gate.lock().unwrap().take() {
            let _ = entered.send(());
            let _ = released.recv_timeout(Duration::from_secs(3));
        }
        let mut q = self.shared.lock();
        if self.shared.stopping.load(Ordering::Acquire) || (self.terminal() && !closed_ack) {
            return Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "Broker control retired",
            ));
        }
        let index = lane as usize;
        let capacity = match lane {
            Lane::Regular => 8,
            Lane::Negative => 1,
            Lane::Internal => 8,
        };
        if q.retained[index] >= capacity {
            return Ok(false);
        }
        q.retained[index] += 1;
        q.outgoing.push_back(Outbound {
            frame: Frame {
                kind,
                body: body.to_vec(),
            },
            lane,
            closed_ack,
            completion,
        });
        self.shared.changed.notify_one();
        Ok(true)
    }
    pub fn stop_and_join(&mut self) -> io::Result<()> {
        {
            let mut q = self.shared.lock();
            self.shared.terminal();
            self.shared.stopping.store(true, Ordering::Release);
            for outgoing in q.outgoing.drain(..) {
                if let Some(completion) = outgoing.completion {
                    completion.finish(Err(io::Error::new(io::ErrorKind::BrokenPipe, "Control cancelled")));
                }
            }
        }
        self.shared.changed.notify_all();
        self.control.cancel();
        let mut panic = false;
        if let Some(reader) = self.reader.take() {
            panic |= reader.join().is_err();
        }
        if let Some(writer) = self.writer.take() {
            panic |= writer.join().is_err();
        }
        if panic {
            return Err(io::Error::other("Broker control thread failed"));
        }
        Ok(())
    }
}
impl Drop for BrokerControlClient {
    fn drop(&mut self) {
        let _ = self.stop_and_join();
    }
}

fn read_loop(
    control: &Control,
    shared: &Shared,
    boot: [u8; 32],
    worker: [u8; 16],
) -> io::Result<()> {
    while !shared.stopping.load(Ordering::Acquire) {
        let (kind, body) = control.read(INFINITE)?;
        let terminal = match kind {
            STOP if body.is_empty() => true,
            AUTHORITY => {
                let grant = crate::audio_authority::ControlGrant::parse(&body)?;
                if grant.boot != boot || grant.worker != worker {
                    return Err(invalid());
                }
                grant.closed
            }
            NETWORK_REPLY => {
                crate::audio_rpc::Message::parse(&body, true)?;
                false
            }
            CONFIGURE_LISTENER => {
                crate::audio_listener_config::ListenerConfig::parse(&body,worker)?;
                false
            }
            OPEN_SOURCE | ATTACH_PCM => {
                let (grant, _, payload) = super::source_attach::envelope(&body)?;
                if grant.boot != boot || grant.worker != worker { return Err(invalid()); }
                if kind == OPEN_SOURCE { super::source_attach::endpoint(payload)?; }
                else { super::source_attach::pcm_handles(payload)?; }
                false
            }
            _ => return Err(invalid()),
        };
        let mut q = shared.lock();
        if q.incoming.len() >= 16 {
            return Err(invalid());
        }
        q.incoming.push_back(Frame { kind, body });
        if terminal { shared.terminal(); }
        unsafe {
            SetEvent(shared.control_event.as_raw_handle());
        }
    }
    Ok(())
}
fn write_loop(control: &Control, shared: &Shared) -> io::Result<()> {
    loop {
        let (value, rejected) = {
            let mut q = shared.lock();
            while q.outgoing.is_empty() && !shared.stopping.load(Ordering::Acquire) {
                q = shared.changed.wait(q).unwrap_or_else(|p| p.into_inner());
            }
            if shared.stopping.load(Ordering::Acquire) {
                return Ok(());
            }
            let value = q.outgoing.pop_front().unwrap();
            // Dequeue admits the write under the same gate that publishes the
            // terminal fence. An admitted operation keeps its actual I/O custody.
            let rejected = shared.terminal.load(Ordering::Acquire) && !value.closed_ack;
            (value, rejected)
        };
        // Class capacity includes this actual write until completed/cancelled.
        // FIFO preserves already admitted request IDs/frame ordering.
        let outcome = if rejected {
            Err(io::Error::new(io::ErrorKind::BrokenPipe, "Broker control retired"))
        } else {
            control.write(value.frame.kind, &value.frame.body)
        };
        #[cfg(test)]
        {
            let gate = shared.completion_gate.lock().unwrap().take();
            if let Some((entered, released)) = gate {
                let _ = entered.send(());
                let _ = released.recv_timeout(Duration::from_secs(3));
            }
        }
        if let Some(completion) = value.completion {
            completion.finish(match &outcome {
                Ok(()) => Ok(()),
                Err(error) => Err(io::Error::new(error.kind(), error.to_string())),
            });
        }
        if !rejected {
            outcome?;
        }
        let mut q = shared.lock();
        q.retained[value.lane as usize] -= 1;
        unsafe { SetEvent(shared.capacity_event.as_raw_handle()); }
        // A source turn holding a bounded, not-yet-submitted frame can retry
        // when capacity actually returns, without a polling/capture timer.
        // This wake is only a hint, never an authority acknowledgement.
        unsafe {
            SetEvent(shared.control_event.as_raw_handle());
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    pub(crate) fn pair() -> (Control, Control) {
        // Synthetic local I/O only. Production connect() always performs the
        // SYSTEM/descriptor/image/incarnation checks; this factory is not built
        // into the product and creates no service, source, endpoint or keys.
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let name = wide(std::ffi::OsStr::new(&format!(
            r"\\.\pipe\DeskFuse.SourceControlIoTest.{}.{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        )));
        let server = handle(unsafe {
            CreateNamedPipeW(
                name.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                512,
                512,
                0,
                null(),
            )
        })
        .unwrap();
        let client = handle(unsafe {
            CreateFileW(
                name.as_ptr(),
                CLIENT_MASK,
                0,
                null(),
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                null_mut(),
            )
        })
        .unwrap();
        let event = handle(unsafe { CreateEventW(null(), 1, 0, null()) }).unwrap();
        let mut operation: OVERLAPPED = unsafe { std::mem::zeroed() };
        operation.hEvent = event.as_raw_handle();
        if unsafe { ConnectNamedPipe(server.as_raw_handle(), &mut operation) } == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_IO_PENDING as i32) {
                let waited = unsafe { WaitForSingleObject(event.as_raw_handle(), 3000) };
                let mut n = 0;
                if waited != WAIT_OBJECT_0 {
                    unsafe {
                        CancelIoEx(server.as_raw_handle(), &operation);
                        GetOverlappedResult(server.as_raw_handle(), &operation, &mut n, 1);
                    }
                    panic!("synthetic pipe connect deadline");
                }
                assert_ne!(
                    unsafe { GetOverlappedResult(server.as_raw_handle(), &operation, &mut n, 1) },
                    0
                );
            } else {
                assert_eq!(error.raw_os_error(), Some(ERROR_PIPE_CONNECTED as i32));
            }
        }
        let wrap = |pipe| Control {
            pipe,
            server: handle(unsafe { OpenProcess(0x0010_1000, 0, std::process::id()) }).unwrap(),
            server_pid: std::process::id(),
            server_created: 1,
            cancelled: handle(unsafe { CreateEventW(null(), 1, 0, null()) }).unwrap(),
        };
        (wrap(client), wrap(server))
    }
    pub(crate) fn fixture(file: &str, key: &str) -> Vec<u8> {
        let text = match file {
            "control" => include_str!("../../../../../../tools/protocol3/audio-authority-v1.json"),
            _ => include_str!("../../../../../../tools/protocol3/audio-source-stop-v1.json"),
        };
        let value: serde_json::Value = serde_json::from_str(text).unwrap();
        let hex = value[key].as_str().unwrap();
        (0..hex.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
            .collect()
    }
    #[test]
    fn pinned_issue_uses_regular_pipe_lane_and_preserves_signed_stop_capacity() {
        let (local, remote) = pair();
        let grant = crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let mut request_id = [0x72; 32];
        request_id[..8].copy_from_slice(&2u64.to_be_bytes());
        let mut body = vec![2];
        body.extend_from_slice(&request_id);
        body.extend_from_slice(&1000u32.to_be_bytes());
        body.extend_from_slice(&[19; 16]);
        let mut issue = crate::audio_rpc::Message {
            request: 1, connection: [19; 16], kind: crate::audio_control::ISSUE, body
        }.encode(false).unwrap();
        issue.extend_from_slice(&12u64.to_be_bytes());
        issue.extend_from_slice(&[0xa5; 32]);
        assert!(client.try_write(PINNED_ISSUE, &issue).unwrap());
        assert_eq!(remote.read(3000).unwrap(), (PINNED_ISSUE, issue.clone()));
        let stop = crate::audio_rpc::Message {
            request: 2, connection: [19; 16],
            kind: crate::audio_control::SIGNED_SOURCE_STOP,
            body: fixture("stop", "signedStopHex"),
        }.encode(false).unwrap();
        assert!(client.try_write(NETWORK_REQUEST, &stop).unwrap());
        assert_eq!(remote.read(3000).unwrap(), (NETWORK_REQUEST, stop));
        let digest_at = issue.len() - 32;
        issue[digest_at..].fill(0);
        assert!(client.try_write(PINNED_ISSUE, &issue).is_err());
        client.stop_and_join().unwrap();
    }

    #[test]
    fn actual_pipe_reader_observes_terminal_while_ordered_writer_and_negative_capacity_are_retained(
    ) {
        let (local, remote) = pair();
        let grant =
            crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let mut sequence = 1;
        let mut full = false;
        for _ in 0..32 {
            let message = crate::audio_rpc::Message {
                request: sequence,
                connection: [19; 16],
                kind: 10,
                body: fixture("stop", "statusQueryHex"),
            };
            if !client
                .try_write(NETWORK_REQUEST, &message.encode(false).unwrap())
                .unwrap()
            {
                full = true;
                break;
            }
            sequence += 1;
        }
        assert!(full); // Server deliberately reads nothing: actual WriteFile is blocked.
        let stop = crate::audio_rpc::Message {
            request: sequence,
            connection: [19; 16],
            kind: 9,
            body: fixture("stop", "signedStopHex"),
        };
        assert!(client
            .try_write(NETWORK_REQUEST, &stop.encode(false).unwrap())
            .unwrap());
        assert!(!client
            .try_write(
                NETWORK_REQUEST,
                &crate::audio_rpc::Message {
                    request: sequence + 1,
                    ..stop
                }
                .encode(false)
                .unwrap()
            )
            .unwrap());
        remote.write(STOP, &[]).unwrap();
        assert_eq!(
            unsafe { WaitForSingleObject(client.terminal_event().as_raw_handle(), 3000) },
            WAIT_OBJECT_0
        );
        assert!(client.terminal());
        // Terminal is set before bounded routing; wait on the actual routing
        // event for the complete captured frame rather than treating the hint as data.
        let until = Instant::now() + Duration::from_secs(3);
        loop {
            assert_eq!(
                unsafe { WaitForSingleObject(client.control_event().as_raw_handle(), 3000) },
                WAIT_OBJECT_0
            );
            if client.drain(16).unwrap().iter().any(|f| f.kind == STOP) {
                break;
            }
            assert!(Instant::now() < until);
        }
        client.stop_and_join().unwrap();
        assert!(client.reader.is_none() && client.writer.is_none());
        assert!(client.try_write(CONNECTION_CLOSED, &[1; 16]).is_err());
    }


    #[test]
    fn persistent_cancellation_joins_a_reader_between_header_and_body_without_reconnect() {
        let (local, remote) = pair();
        let grant =
            crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        remote.exact(&mut 100u32.to_be_bytes(), true, 3000).unwrap(); // Real partial frame; body never arrives.
        client.stop_and_join().unwrap();
        client.stop_and_join().unwrap();
        assert!(client.terminal());
        assert!(client.reader.is_none() && client.writer.is_none());
        assert!(client.try_write(CONNECTION_CLOSED, &[1; 16]).is_err());
    }
    #[test]
    fn actual_write_completion_wakes_capacity_retry_without_fabricating_an_authority_ack() {
        let (local, remote) = pair();
        let grant =
            crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        assert!(client.try_write(CONNECTION_CLOSED, &[19; 16]).unwrap());
        let (kind, body) = remote.read(3000).unwrap();
        assert_eq!(kind, CONNECTION_CLOSED);
        assert_eq!(body, [19; 16]);
        assert_eq!(
            unsafe { WaitForSingleObject(client.control_event().as_raw_handle(), 3000) },
            WAIT_OBJECT_0
        );
        assert!(client.drain(16).unwrap().is_empty());
        assert_eq!(client.shared.lock().retained[Lane::Internal as usize], 0);
        client.stop_and_join().unwrap();
    }

    #[test]
    fn acknowledged_write_requires_real_io_completion_and_cancellation_joins_all_receipts() {
        let (local, remote) = pair();
        let grant = crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let body = vec![17u8; 256];
        let mut receipts = Vec::new();
        let mut reached_capacity = false;
        for _ in 0..64 {
            match client.try_write_confirmed(LISTENER_CONFIGURED, &body).unwrap() {
                Some(receipt) => receipts.push(receipt),
                None => { reached_capacity = true; break; }
            }
        }
        assert!(reached_capacity);
        assert!(receipts.iter().any(|receipt| receipt.0.result.lock().unwrap().is_none()));
        remote.write(STOP, &[]).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(client.terminal_event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        client.stop_and_join().unwrap();
        assert!(receipts.iter().all(|receipt| receipt.0.result.lock().unwrap().is_some()));
        assert!(client.reader.is_none() && client.writer.is_none());
    }

    #[test]
    fn terminal_rejects_raced_enqueue_and_a_queued_unadmitted_ack_without_writing() {
        let grant = crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let (local, remote) = pair();
        let client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let terminal = client.terminal_event().try_clone_to_owned().unwrap();
        let (entered, release) = client.delay_next_enqueue();
        let worker = thread::spawn(move || {
            let attempted = client.try_write_confirmed(LISTENER_CONFIGURED, &[7; 24]);
            (attempted, client)
        });
        entered.recv_timeout(Duration::from_secs(3)).unwrap();
        remote.write(STOP, &[]).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(terminal.as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        release.send(()).unwrap();
        let (attempted, mut client) = worker.join().unwrap();
        assert!(attempted.is_err());
        assert!(remote.read(100).is_err());
        client.stop_and_join().unwrap();

        let (local, remote) = pair();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let (entered, release) = client.delay_next_write_completion();
        let admitted = client.try_write_confirmed(LISTENER_CONFIGURED, &[3; 24]).unwrap().unwrap();
        assert_eq!(remote.read(3000).unwrap(), (LISTENER_CONFIGURED, vec![3; 24]));
        entered.recv_timeout(Duration::from_secs(3)).unwrap();
        let queued = client.try_write_confirmed(LISTENER_CONFIGURED, &[4; 24]).unwrap().unwrap();
        remote.write(STOP, &[]).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(client.terminal_event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        release.send(()).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(admitted.event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        assert!(admitted.take_result().unwrap().is_ok());
        assert_eq!(unsafe { WaitForSingleObject(queued.event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        assert!(queued.take_result().unwrap().is_err());
        assert!(remote.read(100).is_err());
        client.stop_and_join().unwrap();
    }

    #[test]
    fn closed_projection_ack_survives_terminal_after_an_admitted_write() {
        let grant = crate::audio_authority::ControlGrant::parse(&fixture("control", "controlHex")).unwrap();
        let (local, remote) = pair();
        let mut client = BrokerControlClient::start(local, grant.boot, grant.worker).unwrap();
        let (entered, release) = client.delay_next_write_completion();
        let admitted = client.try_write_confirmed(LISTENER_CONFIGURED, &[5; 24]).unwrap().unwrap();
        assert_eq!(remote.read(3000).unwrap(), (LISTENER_CONFIGURED, vec![5; 24]));
        entered.recv_timeout(Duration::from_secs(3)).unwrap();

        let mut closed = fixture("control", "controlHex");
        let n = closed.len();
        closed[n - 2] = 0;
        closed[n - 1] = 1;
        assert!(crate::audio_authority::ControlGrant::parse(&closed).unwrap().closed);
        remote.write(AUTHORITY, &closed).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(client.terminal_event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        let restrictive = client.try_write_confirmed(AUTHORITY_OBSERVED, &closed).unwrap().unwrap();
        assert!(client.try_write_confirmed(LISTENER_CONFIGURED, &[9; 24]).is_err());
        release.send(()).unwrap();
        assert_eq!(unsafe { WaitForSingleObject(admitted.event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        assert!(admitted.take_result().unwrap().is_ok());
        assert_eq!(remote.read(3000).unwrap(), (AUTHORITY_OBSERVED, closed));
        assert_eq!(unsafe { WaitForSingleObject(restrictive.event().as_raw_handle(), 3000) }, WAIT_OBJECT_0);
        assert!(restrictive.take_result().unwrap().is_ok());
        client.stop_and_join().unwrap();
    }

}

//! Cross-platform QUIC UDP egress and socket readiness primitive.
//!
//! One connection owner invokes \`quiche::Connection::send()\` and owns at most
//! ONE already-sealed ciphertext packet. WouldBlock never loses/reconstructs
//! that packet, and no second \`conn.send()\` is allowed behind it.
//! This is not a codec, source clock, or media freshness controller.
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

pub const MAX_QUIC_UDP_BYTES: usize = 1350;
const MAX_BURST: usize = 128;

struct Pending {
    len: usize,
    to: SocketAddr,
    at: Instant,
    created: Instant,
    blocked_since: Option<Instant>,
}

/// A single egress owner. \`blocked\` requires an OS writable notification.
pub struct Egress {
    // quiche seals directly into this one owned slot. While pending, neither
    // pacing nor WouldBlock may overwrite it or construct another packet.
    bytes: [u8; MAX_QUIC_UDP_BYTES],
    pending: Option<Pending>,
    blocked: bool,
    pub udp_packets_sent: u64,
    pub would_block_count: u64,
    pub service_calls: u64,
    pub maintenance_calls: u64,
    pub pending_max_us: u64,
    pub pacing_max_us: u64,
    pub blocked_max_us: u64,
}

impl Default for Egress {
    fn default() -> Self {
        Self { bytes: [0; MAX_QUIC_UDP_BYTES], pending: None, blocked: false,
               udp_packets_sent: 0, would_block_count: 0, service_calls: 0,
               maintenance_calls: 0, pending_max_us: 0, pacing_max_us: 0, blocked_max_us: 0 }
    }
}

impl Egress {
    pub fn needs_write_readiness(&self) -> bool {
        self.blocked
    }

    pub fn has_pending_ciphertext(&self) -> bool {
        self.pending.is_some()
    }

    /// Pacing deadline only; if blocked, wait for OS socket writability.
    pub fn deadline(&self) -> Option<Instant> {
        if self.blocked { None } else { self.pending.as_ref().map(|p| p.at) }
    }

    /// Reuse the *exact same* ciphertext until the kernel accepts it.
    /// Never call \`conn.send()\` while the pending slot is occupied.
    pub fn service(
        &mut self,
        socket: &UdpSocket,
        conn: &mut quiche::Connection,
        writable: bool,
    ) -> Result<(), Box<dyn std::error::Error>> {
        self.service_with_hook(socket, conn, writable, |_| Ok(()))
    }

    /// Run owner maintenance on service entry, INCLUDING while a sealed slot
    /// is paced/blocked, and immediately before every packet construction.
    /// Plaintext may be reconciled/purged without constructing or modifying
    /// the already-sealed packet. No conn.send runs behind that packet.
    pub fn service_with_hook(
        &mut self,
        socket: &UdpSocket,
        conn: &mut quiche::Connection,
        writable: bool,
        before_construct: impl FnMut(&mut quiche::Connection)
            -> Result<(), Box<dyn std::error::Error>>,
    ) -> Result<(), Box<dyn std::error::Error>> {
        self.service_with_hook_at(socket, socket.local_addr()?, conn, writable, before_construct)
    }

    /// The connection/socket owner supplies its bound address, refreshed on socket replacement.
    pub fn service_with_hook_at(
        &mut self, socket: &UdpSocket, local: SocketAddr, conn: &mut quiche::Connection,
        writable: bool, before_construct: impl FnMut(&mut quiche::Connection)
            -> Result<(), Box<dyn std::error::Error>>,
    ) -> Result<(), Box<dyn std::error::Error>> {
        self.service_with_io(local, conn, writable, before_construct,
                             |bytes, to| socket.send_to(bytes, to))
    }

    /// Same reactor path with an explicit nonblocking datagram sender. The
    /// socket adapter above and deterministic WouldBlock tests share packet
    /// construction, maintenance and immutable sealed-slot ownership.
    pub fn service_with_io(
        &mut self, local: SocketAddr, conn: &mut quiche::Connection,
        writable: bool,
        mut maintain: impl FnMut(&mut quiche::Connection)
            -> Result<(), Box<dyn std::error::Error>>,
        mut send: impl FnMut(&[u8], SocketAddr) -> io::Result<usize>,
    ) -> Result<(), Box<dyn std::error::Error>> {
        self.service_calls += 1;
        if self.pending.is_some() { self.maintenance_calls += 1; maintain(conn)?; }
        if self.blocked && !writable {
            return Ok(());
        }
        for _ in 0..MAX_BURST {
            if self.pending.is_none() {
                self.maintenance_calls += 1;
                maintain(conn)?;
                let (len, info) = match conn.send(&mut self.bytes) {
                    Ok(packet) => packet,
                    Err(quiche::Error::Done) => return Ok(()),
                    Err(e) => return Err(format!("QUIC egress send: {e:?}").into()),
                };
                if info.from != local {
                    return Err("QUIC send address does not match the local UDP socket".into());
                }
                if len == 0 || len > MAX_QUIC_UDP_BYTES {
                    return Err("QUIC output exceeded bounded UDP packet slot".into());
                }
                let created = Instant::now();
                self.pacing_max_us = self.pacing_max_us.max(info.at.saturating_duration_since(created).as_micros() as u64);
                self.pending = Some(Pending { len, to: info.to, at: info.at, created, blocked_since: None });
                self.blocked = false;
            }

            if !self.flush_owned_slot(&mut send)? {
                return Ok(()); // paced or blocked; same exact sealed bytes retained
            }
        }
        Ok(())
    }

    // Production and deterministic fault injection share this exact path.
    // The already-sealed bytes are immutable until one successful UDP send.
    fn flush_owned_slot(
        &mut self, mut send: impl FnMut(&[u8], SocketAddr)->io::Result<usize>
    ) -> Result<bool, Box<dyn std::error::Error>> {
        let Some(pending)=self.pending.as_mut() else{return Ok(false)};
        if Instant::now() < pending.at {return Ok(false)}
        match send(&self.bytes[..pending.len],pending.to) {
            Ok(n) if n==pending.len => {
                self.pending_max_us = self.pending_max_us.max(pending.created.elapsed().as_micros() as u64);
                if let Some(started) = pending.blocked_since {
                    self.blocked_max_us = self.blocked_max_us.max(started.elapsed().as_micros() as u64);
                }
                self.pending=None;
                self.blocked=false;
                self.udp_packets_sent+=1;
                Ok(true)
            }
            Ok(_)=>Err("UDP partial send".into()),
            Err(e) if e.kind()==io::ErrorKind::WouldBlock=>{
                pending.blocked_since.get_or_insert_with(Instant::now);
                self.blocked=true;
                self.would_block_count+=1;
                Ok(false)
            }
            Err(e)=>Err(e.into()),
        }
    }
}

#[cfg(test)]
mod ciphertext_slot_tests {
    use super::*;
    #[test]
    fn pending_ciphertext_prevents_any_quiche_construction_until_writable() {
        let udp=UdpSocket::bind("127.0.0.1:0").unwrap();
        udp.set_nonblocking(true).unwrap();
        let local=udp.local_addr().unwrap();
        let remote:SocketAddr="127.0.0.1:4433".parse().unwrap();
        let mut cfg=quiche::Config::new(quiche::PROTOCOL_VERSION).unwrap();
        cfg.set_application_protos(&[b"df-sealed-slot-unit"]).unwrap();
        let cid=quiche::ConnectionId::from_ref(&[0x52;16]);
        let mut conn=quiche::connect(None,&cid,local,remote,&mut cfg).unwrap();
        let sealed=vec![0x64u8;1200];
        let mut e=Egress::default();
        e.bytes[..sealed.len()].copy_from_slice(&sealed);
        e.pending=Some(Pending{len:sealed.len(),to:remote,at:Instant::now(),created:Instant::now(),blocked_since:None});
        e.blocked=true;
        let mut hooks=0;
        let constructed = conn.stats().sent;
        for attempt in 0..5 {
            e.service_with_hook(&udp,&mut conn,false, |_|{
                hooks+=1;Ok(())
            }).unwrap();
            assert_eq!(hooks,1 + attempt,"maintenance runs behind occupied sealed slot");
            assert_eq!(conn.stats().sent,constructed,"no conn.send behind occupied sealed slot");
            assert_eq!(&e.bytes[..e.pending.as_ref().unwrap().len],sealed.as_slice());
        }
        e.service_with_hook(&udp,&mut conn,true, |_|{
            hooks+=1;Ok(())
        }).unwrap();
        assert!(hooks>=1,
            "plaintext freshness hook runs before next construction after sealed UDP acceptance");
        assert!(e.udp_packets_sent>=1);
    }

    #[test]
    fn repeated_wouldblock_preserves_identical_ciphertext_and_one_slot() {
        let target:SocketAddr="127.0.0.1:443".parse().unwrap();
        let original=vec![0x4au8;1200];
        let mut e=Egress::default();
        e.bytes[..original.len()].copy_from_slice(&original);
        e.pending=Some(Pending{len:original.len(),to:target,at:Instant::now(),created:Instant::now(),blocked_since:None});
        for _ in 0..12 {
            let mut called=0;
            assert!(!e.flush_owned_slot(|bytes,to|{
                called+=1;
                assert_eq!(bytes,original.as_slice());
                assert_eq!(to,target);
                Err(io::Error::from(io::ErrorKind::WouldBlock))
            }).unwrap());
            assert_eq!(called,1);
            assert!(e.has_pending_ciphertext());
            assert!(e.needs_write_readiness());
            assert_eq!(&e.bytes[..e.pending.as_ref().unwrap().len],original.as_slice());
            assert_eq!(e.udp_packets_sent,0);
        }
        assert!(e.flush_owned_slot(|bytes,_| {
            assert_eq!(bytes,original.as_slice());Ok(bytes.len())
        }).unwrap());
        assert!(!e.has_pending_ciphertext());
        assert!(!e.needs_write_readiness());
        assert_eq!(e.would_block_count,12);
        assert_eq!(e.udp_packets_sent,1);
    }
}

#[derive(Default, Debug, Clone, Copy)]
pub struct Readiness {
    pub readable: bool,
    pub writable: bool,
}

#[cfg(windows)]
pub struct WindowsEvents {
    socket:usize,event:isize,timer:*mut std::ffi::c_void,
}
#[cfg(windows)]
impl WindowsEvents {
    pub fn new(udp:&UdpSocket)->io::Result<Self> {
        use std::os::windows::io::AsRawSocket;
        use windows_sys::Win32::{Networking::WinSock::*,System::Threading::*};
        unsafe {
            let event=WSACreateEvent();if event==0{return Err(io::Error::last_os_error())}
            let timer=CreateWaitableTimerExW(std::ptr::null(),std::ptr::null(),2,0x1f0003);
            if timer.is_null(){WSACloseEvent(event);return Err(io::Error::last_os_error())}
            let value=Self{socket:udp.as_raw_socket() as usize,event,timer};
            if WSAEventSelect(value.socket,event,(FD_READ|FD_WRITE|FD_CLOSE) as i32)!=0 {
                return Err(io::Error::from_raw_os_error(WSAGetLastError()))
            }
            Ok(value)
        }
    }
    pub fn wait(&self,want_write:bool,source:Option<*mut std::ffi::c_void>,period:Option<*mut std::ffi::c_void>,deadline:Option<Instant>)
        ->io::Result<(Readiness,bool)> {
        use windows_sys::Win32::{Networking::WinSock::*,System::Threading::*};
        unsafe {
            let mut handles=[self.event as *mut std::ffi::c_void,self.timer,std::ptr::null_mut(),std::ptr::null_mut()];
            let mut count=2;
            if let Some(h)=source {if !h.is_null(){handles[count]=h;count+=1;}}
            let period_index=if let Some(h)=period.filter(|h|!h.is_null()){handles[count]=h;count+=1;Some(count-1)}else{None};
            if let Some(t)=deadline {
                let duration=t.saturating_duration_since(Instant::now());
                let hns=((duration.as_nanos()+99)/100).max(1).min(i64::MAX as u128) as i64;
                let due=-hns;
                if SetWaitableTimer(self.timer,&due,0,None,std::ptr::null(),0)==0{return Err(io::Error::last_os_error())}
            } else {CancelWaitableTimer(self.timer);}
            let result=WaitForMultipleObjects(count as u32,handles.as_ptr(),0,u32::MAX);
            if result==u32::MAX{return Err(io::Error::last_os_error())}
            let mut network:WSANETWORKEVENTS=std::mem::zeroed();
            if WSAEnumNetworkEvents(self.socket,self.event,&mut network)!=0 {
                return Err(io::Error::from_raw_os_error(WSAGetLastError()))
            }
            if network.lNetworkEvents & FD_CLOSE as i32!=0 {return Err(io::Error::new(io::ErrorKind::ConnectionAborted,"QUIC socket closed"))}
            for index in [FD_READ_BIT,FD_WRITE_BIT] {
                if network.iErrorCode[index as usize]!=0{return Err(io::Error::from_raw_os_error(network.iErrorCode[index as usize]))}
            }
            // Auto-reset audio event may be signalled concurrently with the
            // socket event, so service it on every owner wake (padding bounds it).
            let period_ready=period_index.is_some_and(|index|result==index as u32 || WaitForSingleObject(handles[index],0)==0);
            Ok((Readiness{readable:network.lNetworkEvents & FD_READ as i32!=0,
                writable:want_write && network.lNetworkEvents & FD_WRITE as i32!=0},period_ready))
        }
    }
}
#[cfg(windows)]
impl Drop for WindowsEvents {
    fn drop(&mut self){unsafe{
        windows_sys::Win32::Networking::WinSock::WSAEventSelect(self.socket,0,0);
        windows_sys::Win32::Networking::WinSock::WSACloseEvent(self.event);
        windows_sys::Win32::Foundation::CloseHandle(self.timer);
    }}
}

/// Wait for actual OS socket readiness or a dynamically armed deadline.
/// The caller must set the socket nonblocking and service QUIC deadlines.
pub fn wait_udp(
    socket: &UdpSocket,
    want_write: bool,
    deadline: Option<Instant>,
) -> io::Result<Readiness> {
    let ms = match deadline {
        None => 1000, // no busy spin; permits external shutdown checks
        Some(t) => {
            let d = t.saturating_duration_since(Instant::now());
            if d.is_zero() { 0 } else {
                d.as_millis().saturating_add(1).min(i32::MAX as u128) as i32
            }
        }
    };
    platform_wait(socket, want_write, ms)
}

#[cfg(unix)]
fn platform_wait(socket: &UdpSocket, want_write: bool, timeout_ms: i32) -> io::Result<Readiness> {
    use std::os::fd::AsRawFd;
    #[repr(C)]
    struct PollFd { fd: i32, events: i16, revents: i16 }
    // poll(2) uses a 32-bit nfds_t on Darwin and Android, and
    // unsigned long on Linux/glibc. Match the platform ABI exactly.
    #[cfg(any(target_vendor = "apple", target_os = "android"))]
    unsafe extern "C" { fn poll(fds: *mut PollFd, nfds: u32, timeout: i32) -> i32; }
    #[cfg(not(any(target_vendor = "apple", target_os = "android")))]
    unsafe extern "C" { fn poll(fds: *mut PollFd, nfds: usize, timeout: i32) -> i32; }
    const POLLIN: i16 = 0x0001;
    const POLLOUT: i16 = 0x0004;
    const POLLERR: i16 = 0x0008;
    const POLLHUP: i16 = 0x0010;
    const POLLNVAL: i16 = 0x0020;
    let mut fd = PollFd {
        fd: socket.as_raw_fd(),
        events: POLLIN | if want_write { POLLOUT } else { 0 },
        revents: 0,
    };
    loop {
        let result = unsafe { poll(&mut fd, 1, timeout_ms) };
        if result < 0 {
            let e = io::Error::last_os_error();
            if e.kind() == io::ErrorKind::Interrupted { continue; }
            return Err(e);
        }
        if fd.revents & (POLLERR | POLLHUP | POLLNVAL) != 0 {
            return Err(io::Error::new(io::ErrorKind::Other, "UDP socket poll error/hangup"));
        }
        return Ok(Readiness {
            readable: fd.revents & POLLIN != 0,
            writable: want_write && fd.revents & POLLOUT != 0,
        });
    }
}

#[cfg(windows)]
fn platform_wait(socket: &UdpSocket, want_write: bool, timeout_ms: i32) -> io::Result<Readiness> {
    use std::os::windows::io::AsRawSocket;
    #[repr(C)]
    struct WsaPollFd { fd: usize, events: i16, revents: i16 }
    #[link(name = "Ws2_32")]
    unsafe extern "system" {
        fn WSAPoll(fd_array: *mut WsaPollFd, fds: u32, timeout: i32) -> i32;
        fn WSAGetLastError() -> i32;
    }
    const POLLRDNORM: i16 = 0x0100;
    const POLLWRNORM: i16 = 0x0010;
    const POLLERR: i16 = 0x0001;
    const POLLHUP: i16 = 0x0002;
    const POLLNVAL: i16 = 0x0004;
    let mut fd = WsaPollFd {
        fd: socket.as_raw_socket() as usize,
        events: POLLRDNORM | if want_write { POLLWRNORM } else { 0 },
        revents: 0,
    };
    let result = unsafe { WSAPoll(&mut fd, 1, timeout_ms) };
    if result < 0 {
        return Err(io::Error::from_raw_os_error(unsafe { WSAGetLastError() }));
    }
    if fd.revents & (POLLERR | POLLHUP | POLLNVAL) != 0 {
        return Err(io::Error::new(io::ErrorKind::Other, "UDP socket WSAPoll error/hangup"));
    }
    Ok(Readiness {
        readable: fd.revents & POLLRDNORM != 0,
        writable: want_write && fd.revents & POLLWRNORM != 0,
    })
}

/// Compute the next QUIC timer / egress pacing / source-release wake.
pub fn next_deadline(
    quic: Option<Instant>,
    egress: Option<Instant>,
    source: Option<Instant>,
) -> Option<Instant> {
    [quic, egress, source].into_iter().flatten().min()
}

/// A single 240-frame block is 5 ms at 48 kHz. Clocking this lab's
/// synthetic producer does *not* establish a production audio send pacer.
pub const SYNTHETIC_BLOCK: Duration = Duration::from_millis(5);

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
    bytes: Vec<u8>,
    to: SocketAddr,
    at: Instant,
}

/// A single egress owner. \`blocked\` requires an OS writable notification.
#[derive(Default)]
pub struct Egress {
    pending: Option<Pending>,
    blocked: bool,
    pub udp_packets_sent: u64,
    pub would_block_count: u64,
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

    /// The hook runs IMMEDIATELY before *every* quiche::Connection::send()
    /// construction attempt. For PCM, it purges expired queued plaintext using
    /// quiche's supported dgram_purge_outgoing. It is never called while
    /// ciphertext is occupying the one sealed slot; subsequent code cannot
    /// construct another packet behind blocked/sealed ciphertext.
    pub fn service_with_hook(
        &mut self,
        socket: &UdpSocket,
        conn: &mut quiche::Connection,
        writable: bool,
        mut before_construct: impl FnMut(&mut quiche::Connection)
            -> Result<(), Box<dyn std::error::Error>>,
    ) -> Result<(), Box<dyn std::error::Error>> {
        if self.blocked && !writable {
            return Ok(());
        }
        for _ in 0..MAX_BURST {
            if self.pending.is_none() {
                before_construct(conn)?;
                let mut buf = [0u8; MAX_QUIC_UDP_BYTES];
                let (len, info) = match conn.send(&mut buf) {
                    Ok(packet) => packet,
                    Err(quiche::Error::Done) => return Ok(()),
                    Err(e) => return Err(format!("QUIC egress send: {e:?}").into()),
                };
                if info.from != socket.local_addr()? {
                    return Err("QUIC send address does not match the local UDP socket".into());
                }
                if len == 0 || len > MAX_QUIC_UDP_BYTES {
                    return Err("QUIC output exceeded bounded UDP packet slot".into());
                }
                self.pending = Some(Pending { bytes: buf[..len].to_vec(), to: info.to, at: info.at });
                self.blocked = false;
            }

            if !self.flush_owned_slot(|bytes, to| socket.send_to(bytes, to))? {
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
        let Some(pending)=self.pending.as_ref() else{return Ok(false)};
        if Instant::now() < pending.at {return Ok(false)}
        match send(&pending.bytes,pending.to) {
            Ok(n) if n==pending.bytes.len() => {
                self.pending=None;
                self.blocked=false;
                self.udp_packets_sent+=1;
                Ok(true)
            }
            Ok(_)=>Err("UDP partial send".into()),
            Err(e) if e.kind()==io::ErrorKind::WouldBlock=>{
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
        e.pending=Some(Pending{bytes:sealed.clone(),to:remote,at:Instant::now()});
        e.blocked=true;
        let mut hooks=0;
        for _ in 0..5 {
            e.service_with_hook(&udp,&mut conn,false, |_|{
                hooks+=1;Ok(())
            }).unwrap();
            assert_eq!(hooks,0,"no conn.send behind occupied sealed slot");
            assert_eq!(e.pending.as_ref().unwrap().bytes,sealed);
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
        e.pending=Some(Pending{bytes:original.clone(),to:target,at:Instant::now()});
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
            assert_eq!(e.pending.as_ref().unwrap().bytes,original);
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

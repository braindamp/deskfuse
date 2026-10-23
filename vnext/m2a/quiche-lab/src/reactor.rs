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
        if self.blocked && !writable {
            return Ok(());
        }
        for _ in 0..MAX_BURST {
            if self.pending.is_none() {
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

            let pending = self.pending.as_ref().expect("pending assigned above");
            if Instant::now() < pending.at {
                return Ok(()); // arm deadline; do NOT sleep after sealing
            }
            match socket.send_to(&pending.bytes, pending.to) {
                Ok(n) if n == pending.bytes.len() => {
                    self.pending = None;
                    self.blocked = false;
                    self.udp_packets_sent += 1;
                }
                Ok(_) => return Err("UDP partial send".into()),
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                    self.blocked = true;
                    self.would_block_count += 1;
                    return Ok(()); // retain ciphertext, arm writable readiness
                }
                Err(e) => return Err(e.into()),
            }
        }
        Ok(())
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

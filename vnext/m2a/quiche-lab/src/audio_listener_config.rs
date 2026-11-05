//! Explicit local listener/setup policy, delivered only on the bound broker
//! pipe. Configuring public TLS does not open a source or grant media authority.
use crate::{
    audio_candidate::SetupLimits,
    audio_control::{DeliveryMode, Reader, SourceScope},
};
use std::{
    io,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV6, UdpSocket},
    sync::Arc,
};
fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "Invalid local audio listener configuration",
    )
}
#[derive(Clone, Copy)]
pub struct CandidatePolicy {
    pub warm_ms: u32,
    pub idle_ms: u32,
    pub queued_packets: u16,
    pub max_packets: u32,
    pub max_bytes: u32,
    pub max_messages: u16,
    pub packet_work: u8,
    pub control_work: u8,
    pub cooldown_ms: u32,
}
impl CandidatePolicy {
    pub fn limits(&self, now: u64) -> io::Result<SetupLimits> {
        if self.warm_ms == 0 || self.idle_ms == 0 || self.cooldown_ms == 0 {
            return Err(invalid());
        }
        let until = now
            .checked_add(u64::from(self.warm_ms) * 10000)
            .ok_or_else(invalid)?;
        let value = SetupLimits {
            until100ns: until,
            idle_ms: self.idle_ms,
            queued_packets: self.queued_packets as usize,
            max_packets: self.max_packets,
            max_bytes: u64::from(self.max_bytes),
            max_control_messages: self.max_messages,
            packets_per_turn: self.packet_work as usize,
            controls_per_turn: self.control_work as usize,
        };
        value.validate(now)?;
        Ok(value)
    }
    pub fn cooldown100ns(&self) -> u64 {
        u64::from(self.cooldown_ms) * 10000
    }
}
pub struct ListenerConfig {
    pub scope: SourceScope,
    pub address: SocketAddr,
    pub policy: CandidatePolicy,
    digest: [u8; 32],
}
impl ListenerConfig {
    pub fn parse(bytes: &[u8], expected_worker: [u8; 16]) -> io::Result<Self> {
        if bytes.len() > 4096 || expected_worker == [0; 16] {
            return Err(invalid());
        }
        let mut r = Reader(bytes);
        if r.byte()? != 1 {
            return Err(invalid());
        }
        DeliveryMode::require_supported(r.byte()?)?;
        let n = r.u16()? as usize;
        if n > 2048 {
            return Err(invalid());
        }
        let scope = SourceScope::parse(r.bytes(n)?)?;
        if scope.worker != expected_worker {
            return Err(invalid());
        }
        let address = read_address(&mut r)?;
        let policy = CandidatePolicy {
            warm_ms: r.u32()?,
            idle_ms: r.u32()?,
            queued_packets: r.u16()?,
            max_packets: r.u32()?,
            max_bytes: r.u32()?,
            max_messages: r.u16()?,
            packet_work: r.byte()?,
            control_work: r.byte()?,
            cooldown_ms: r.u32()?,
        };
        r.end()?;
        policy.limits(0)?;
        Ok(Self {
            scope,
            address,
            policy,
            digest: boring::sha::sha256(bytes),
        })
    }
    pub fn bind(&self) -> io::Result<(Arc<UdpSocket>, Vec<u8>)> {
        let socket = Arc::new(UdpSocket::bind(self.address)?);
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawSocket;
            use windows_sys::Win32::Networking::WinSock::{WSAGetLastError, WSAIoctl, SIO_UDP_CONNRESET};
            // A delayed ICMP error on this shared socket cannot name a QUIC route.
            let enabled = 0u32;
            let mut returned = 0;
            if unsafe { WSAIoctl(
                socket.as_raw_socket() as usize, SIO_UDP_CONNRESET, (&enabled as *const u32).cast(),
                std::mem::size_of_val(&enabled) as u32, std::ptr::null_mut(), 0,
                &mut returned, std::ptr::null_mut(), None,
            ) } != 0 {
                return Err(io::Error::from_raw_os_error(unsafe { WSAGetLastError() }));
            }
        }
        socket.set_nonblocking(true)?;
        let acknowledgement = self.acknowledgement(socket.local_addr()?)?;
        Ok((socket, acknowledgement))
    }
    pub fn acknowledgement(&self, actual: SocketAddr) -> io::Result<Vec<u8>> {
        if actual.port() == 0
            || actual.ip() != self.address.ip()
            || self.address.port() != 0 && actual.port() != self.address.port()
            || match (actual, self.address) {
                (SocketAddr::V6(a), SocketAddr::V6(b)) => a.scope_id() != b.scope_id(),
                _ => false,
            }
        {
            return Err(invalid());
        }
        let mut out = Vec::with_capacity(56);
        out.push(1);
        out.extend_from_slice(&self.digest);
        write_address(&mut out, actual)?;
        Ok(out)
    }
}
fn validate(address: SocketAddr) -> io::Result<()> {
    match address {
        SocketAddr::V4(a)
            if a.ip().is_unspecified()
                || a.ip().is_multicast()
                || *a.ip() == Ipv4Addr::BROADCAST =>
        {
            return Err(invalid())
        }
        SocketAddr::V6(a)
            if a.ip().is_unspecified()
                || a.ip().is_multicast()
                || a.ip().to_ipv4_mapped().is_some()
                || a.ip().is_unicast_link_local() && a.scope_id() == 0
                || a.flowinfo() != 0 =>
        {
            return Err(invalid())
        }
        _ => {}
    }
    Ok(())
}
fn read_address(r: &mut Reader<'_>) -> io::Result<SocketAddr> {
    let value = match r.byte()? {
        4 => {
            let address = Ipv4Addr::from(r.take::<4>()?);
            SocketAddr::new(IpAddr::V4(address), r.u16()?)
        }
        6 => {
            let address = Ipv6Addr::from(r.take::<16>()?);
            let port = r.u16()?;
            SocketAddr::V6(SocketAddrV6::new(address, port, 0, r.u32()?))
        }
        _ => return Err(invalid()),
    };
    validate(value)?;
    Ok(value)
}
fn write_address(out: &mut Vec<u8>, address: SocketAddr) -> io::Result<()> {
    validate(address)?;
    match address {
        SocketAddr::V4(a) => {
            out.push(4);
            out.extend_from_slice(&a.ip().octets());
            out.extend_from_slice(&a.port().to_be_bytes());
        }
        SocketAddr::V6(a) => {
            out.push(6);
            out.extend_from_slice(&a.ip().octets());
            out.extend_from_slice(&a.port().to_be_bytes());
            out.extend_from_slice(&a.scope_id().to_be_bytes());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn bytes(key: &str) -> Vec<u8> {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-listener-v1.json"
        ))
        .unwrap();
        let h = f[key].as_str().unwrap();
        (0..h.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&h[i..i + 2], 16).unwrap())
            .collect()
    }
    fn worker() -> [u8; 16] {
        let f: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../tools/protocol3/audio-transport-v1.json"
        ))
        .unwrap();
        let h = f["sourceHex"].as_str().unwrap();
        let s = (0..h.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&h[i..i + 2], 16).unwrap())
            .collect::<Vec<_>>();
        SourceScope::parse(&s).unwrap().worker
    }
    #[test]
    fn listener_scope_policy_and_ack_match_broker_vectors_before_any_socket_action() {
        for family in [4, 6] {
            let b = bytes(&format!("configuration{family}Hex"));
            let c = ListenerConfig::parse(&b, worker()).unwrap();
            let actual = match family {
                4 => "127.0.0.1:44002",
                _ => "[::1]:44002",
            }
            .parse()
            .unwrap();
            assert_eq!(
                c.acknowledgement(actual).unwrap(),
                bytes(&format!("acknowledgement{family}Hex"))
            );
            assert!(c
                .acknowledgement("127.0.0.2:44002".parse().unwrap())
                .is_err());
            for end in 0..b.len() {
                assert!(ListenerConfig::parse(&b[..end], worker()).is_err());
            }
            let mut bad = b.clone();
            bad.push(0);
            assert!(ListenerConfig::parse(&bad, worker()).is_err());
            let mut foreign = worker();
            foreign[0] ^= 1;
            assert!(ListenerConfig::parse(&b, foreign).is_err());
            bad = b.clone();
            bad[1] = 2;
            assert!(ListenerConfig::parse(&bad, worker()).is_err());
            assert!(c.policy.limits(u64::MAX).is_err());
        }
    }
    #[test]
    fn actual_ephemeral_loopback_bind_reports_the_assigned_endpoint_without_source_authority() {
        let config = ListenerConfig::parse(&bytes("configuration4Hex"), worker()).unwrap();
        let (socket, ack) = config.bind().unwrap();
        assert_ne!(socket.local_addr().unwrap().port(), 0);
        assert_eq!(
            ack,
            config
                .acknowledgement(socket.local_addr().unwrap())
                .unwrap()
        );
        let mut packet = [0; 1];
        assert_eq!(
            socket.recv_from(&mut packet).unwrap_err().kind(),
            io::ErrorKind::WouldBlock
        );
    }
}

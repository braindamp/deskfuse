//! Real OS UDP sockets + authenticated QUIC v0.30.0 DATAGRAMs.
//! Transport test only: NOT a Xiaomi, Wi-Fi, mutual-client-auth, or audio sink test.
use deskfuse_quiche_lab::{encode, parse, Configuration, Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use deskfuse_quiche_lab::reactor::Egress;
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, DnType, ExtendedKeyUsagePurpose,
    IsCa, KeyPair, KeyUsagePurpose,
};
use std::error::Error;
use std::fs;
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::thread;
use std::time::{Duration, Instant};

type TestResult<T> = Result<T, Box<dyn Error>>;
const UDP_MTU: usize = 1350;
const DEADLINE: Duration = Duration::from_secs(12);
const ALPN: &[u8] = b"df-av-m2a-1";

fn config() -> TestResult<quiche::Config> {
    let mut c = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    c.set_application_protos(&[ALPN])?;
    c.set_max_idle_timeout(5_000);
    c.set_max_recv_udp_payload_size(UDP_MTU);
    c.set_max_send_udp_payload_size(UDP_MTU);
    c.set_initial_max_data(64 * 1024);
    c.set_initial_max_stream_data_bidi_local(8192);
    c.set_initial_max_stream_data_bidi_remote(8192);
    c.set_initial_max_streams_bidi(1);
    c.set_disable_active_migration(true);
    c.enable_dgram(true, 4, 4);
    Ok(c)
}

// Reuse the actual bounded reactor's sealed-packet/pacing implementation:
// the previous fixture slept INSIDE a 256-packet QUIC send drain, starving
// peer receive and timeout service on slower Apple Silicon CI runners.
fn receive_udp(socket: &UdpSocket, conn: &mut quiche::Connection) -> TestResult<usize> {
    let mut buf = [0u8; 65535];
    let mut count = 0;
    for _ in 0..256 {
        match socket.recv_from(&mut buf) {
            Ok((size, from)) => {
                let meta = quiche::RecvInfo { from, to: socket.local_addr()? };
                match conn.recv(&mut buf[..size], meta) {
                    Ok(_) | Err(quiche::Error::Done) => {}
                    Err(e) => return Err(format!("QUIC recv error: {e:?}").into()),
                }
                count += 1;
            }
            Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
            Err(e) => return Err(e.into()),
        }
    }
    Ok(count)
}

fn tick_timeout(conn: &mut quiche::Connection) {
    if conn.timeout_instant().is_some_and(|when| when <= Instant::now()) {
        conn.on_timeout();
    }
}

fn drive(
    client_udp: &UdpSocket,
    server_udp: &UdpSocket,
    client: &mut quiche::Connection,
    server: &mut quiche::Connection,
    client_egress: &mut Egress,
    server_egress: &mut Egress,
) -> TestResult<usize> {
    // Service inbound ACKs before constructing new outbound packets.
    // Bounded receive/service/receive alternation avoids a single producer
    // starving the other QUIC state machine while paced output is pending.
    let mut progressed = 0;
    progressed += receive_udp(client_udp, client)?;
    progressed += receive_udp(server_udp, server)?;
    tick_timeout(client);
    tick_timeout(server);
    let emitted_before = client_egress.udp_packets_sent + server_egress.udp_packets_sent;
    // Already-sealed ciphertext remains in Egress until pacing or socket
    // readiness permits its *unchanged* send; never call quiche::send behind it.
    client_egress.service(client_udp, client, true)?;
    server_egress.service(server_udp, server, true)?;
    progressed += (client_egress.udp_packets_sent + server_egress.udp_packets_sent
        - emitted_before) as usize;
    progressed += receive_udp(client_udp, client)?;
    progressed += receive_udp(server_udp, server)?;
    if progressed == 0 {
        // Test-only cooperative yield, NOT a source send clock. The existing
        // 5-second QUIC idle deadline and 12-second test deadline are unchanged.
        thread::sleep(Duration::from_millis(1));
    }
    Ok(progressed)
}


fn connection_status(stage: &str, start: Instant, client: &quiche::Connection, server: &quiche::Connection) -> String {
    // Diagnostic only. Do not extend the 5s QUIC idle timeout or relax
    // verification to turn an unexplained CI failure into a green build.
    let c = client.stats();
    let s = server.stats();
    format!(
        "stage={stage} elapsed_ms={} client(established={},closed={},udp_sent={},udp_recv={},dgram_sent={},dgram_recv={},lost={}) server(established={},closed={},udp_sent={},udp_recv={},dgram_sent={},dgram_recv={},lost={})",
        start.elapsed().as_millis(),
        client.is_established(), client.is_closed(), c.sent, c.recv, c.dgram_sent, c.dgram_recv, c.lost,
        server.is_established(), server.is_closed(), s.sent, s.recv, s.dgram_sent, s.dgram_recv, s.lost
    )
}

#[test]
fn verified_quiche_datagram_over_real_udp_loopback() -> TestResult<()> {
    // Test CA/server identity are ephemeral, locally scoped and auto-deleted.
    let tmp = tempfile::tempdir()?;
    let mut ca_params = CertificateParams::new(Vec::<String>::new())?;
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.distinguished_name.push(DnType::CommonName, "DeskFuse localhost test CA");
    ca_params.key_usages = vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
    let ca = CertifiedIssuer::self_signed(ca_params, KeyPair::generate()?)?;
    let mut leaf_params = CertificateParams::new(vec!["localhost".to_string()])?;
    leaf_params.distinguished_name.push(DnType::CommonName, "localhost");
    leaf_params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    leaf_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let key = KeyPair::generate()?;
    let cert = leaf_params.signed_by(&key, &ca)?;
    let ca_path = tmp.path().join("ca.pem");
    let cert_path = tmp.path().join("server.pem");
    let key_path = tmp.path().join("server.key");
    fs::write(&ca_path, ca.pem())?;
    fs::write(&cert_path, format!("{}\n{}", cert.pem(), ca.pem()))?;
    fs::write(&key_path, key.serialize_pem())?;

    let mut server_config = config()?;
    server_config.load_cert_chain_from_pem_file(cert_path.to_str().ok_or("invalid path")?)?;
    server_config.load_priv_key_from_pem_file(key_path.to_str().ok_or("invalid path")?)?;
    server_config.verify_peer(false); // Only server-auth for now. Client auth is a separate gate.

    let mut client_config = config()?;
    client_config.load_verify_locations_from_file(ca_path.to_str().ok_or("invalid path")?)?;
    client_config.verify_peer(true); // Verify trusted CA and hostname: localhost.

    // Actual kernel UDP sockets on loopback, distinct ephemeral ports. Never
    // touch the existing DeskFuse network service, firewall or Android phone.
    let server_udp = UdpSocket::bind("127.0.0.1:0")?;
    let client_udp = UdpSocket::bind("127.0.0.1:0")?;
    let server_addr: SocketAddr = server_udp.local_addr()?;
    let client_addr: SocketAddr = client_udp.local_addr()?;
    assert_ne!(server_addr, client_addr);

    let client_scid = quiche::ConnectionId::from_ref(&[0x61; 16]);
    let mut client = quiche::connect(
        Some("localhost"), &client_scid, client_addr, server_addr, &mut client_config,
    )?;

    // Initial server acceptance uses the destination CID from client Initial.
    // Dedicated localhost-only one-session fixture; not a public UDP server:
    // no stateless Retry, no CID table, no anti-amplification policy audit yet.
    let mut initial = [0u8; UDP_MTU];
    let (initial_len, initial_meta) = client.send(&mut initial)?;
    assert_eq!(initial_meta.from, client_addr);
    assert_eq!(initial_meta.to, server_addr);
    assert_eq!(client_udp.send_to(&initial[..initial_len], server_addr)?, initial_len);

    server_udp.set_read_timeout(Some(Duration::from_secs(2)))?;
    let mut first = [0u8; UDP_MTU];
    let (n, from) = server_udp.recv_from(&mut first)?;
    assert_eq!(from, client_addr);
    let header = quiche::Header::from_slice(&mut first[..n], quiche::MAX_CONN_ID_LEN)?;
    let mut server = quiche::accept(
        &header.dcid, None, server_addr, client_addr, &mut server_config,
    )?;
    server.recv(&mut first[..n], quiche::RecvInfo { from, to: server_addr })?;

    // Both sockets now participate in an actual nonblocking UDP service loop.
    server_udp.set_nonblocking(true)?;
    client_udp.set_nonblocking(true)?;
    let start = Instant::now();
    let mut client_egress = Egress::default();
    let mut server_egress = Egress::default();
    while !(client.is_established() && server.is_established()) {
        if start.elapsed() > DEADLINE || client.is_closed() || server.is_closed() {
            return Err(format!("real UDP authenticated QUIC handshake did not establish; {}", connection_status("handshake", start, &client, &server)).into());
        }
        drive(&client_udp, &server_udp, &mut client, &mut server,
              &mut client_egress, &mut server_egress)?;
    }

    // Fixed PCM16 block indexed to source frame 48,000, not arrival time.
    let cfg = Configuration::default();
    let h = Header {
        version: VERSION,
        kind: KIND_AUDIO,
        flags: 0,
        header_bytes: HEADER_BYTES as u16,
        frame_count: 240,
        media_epoch: 1,
        source_stream_id: 1,
        format_config_id: 1,
        packet_sequence: 7,
        first_source_frame: 48_000,
        payload_bytes: 960,
        clock_anchor_id: 17,
    };
    let mut wire = encode(&h).to_vec();
    wire.extend((0..960).map(|i| i as u8));
    assert_eq!(wire.len(), 1000);
    assert!(client.dgram_max_writable_len().ok_or("DATAGRAM not negotiated")? >= wire.len());
    client.dgram_send(&wire)?;

    let mut inbound = [0u8; 1200];
    let mut echoed = false;
    while !echoed {
        if start.elapsed() > DEADLINE || client.is_closed() || server.is_closed() {
            return Err(format!("real UDP QUIC DATAGRAM exchange timed out or connection closed; {}", connection_status("forward_datagram", start, &client, &server)).into());
        }
        drive(&client_udp, &server_udp, &mut client, &mut server,
              &mut client_egress, &mut server_egress)?;
        match server.dgram_recv(&mut inbound) {
            Ok(received) => {
                let (parsed, pcm) = parse(&inbound[..received], &cfg)
                    .map_err(|e| format!("server rejected authenticated datagram: {e:?}"))?;
                assert_eq!(parsed, h);
                assert_eq!(pcm, &wire[HEADER_BYTES..]);
                // Echo after authenticated receive; reverse QUIC send also
                // crosses a real OS UDP socket and the client crypto stack.
                server.dgram_send(&inbound[..received])?;
                echoed = true;
            }
            Err(quiche::Error::Done) => {}
            Err(e) => return Err(format!("server DATAGRAM receive failed: {e:?}").into()),
        }
    }

    let mut returned = false;
    while !returned {
        if start.elapsed() > DEADLINE || client.is_closed() || server.is_closed() {
            return Err(format!("return UDP QUIC DATAGRAM timed out or connection closed; {}", connection_status("echo_datagram", start, &client, &server)).into());
        }
        drive(&client_udp, &server_udp, &mut client, &mut server,
              &mut client_egress, &mut server_egress)?;
        match client.dgram_recv(&mut inbound) {
            Ok(n) => {
                let (parsed, pcm) = parse(&inbound[..n], &cfg)
                    .map_err(|e| format!("client rejected authenticated echo: {e:?}"))?;
                assert_eq!(parsed, h);
                assert_eq!(pcm, &wire[HEADER_BYTES..]);
                returned = true;
            }
            Err(quiche::Error::Done) => {}
            Err(e) => return Err(format!("client DATAGRAM receive failed: {e:?}").into()),
        }
    }

    println!("PASS verified QUIC TLS and 1000-byte source-indexed DATAGRAM over two real localhost UDP sockets in both directions");
    Ok(())
}

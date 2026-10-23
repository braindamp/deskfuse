//! Actual quiche TLS 1.3 encryption and server-certificate verification.
//! This first gate passes packets in-process, without UDP/Android, so it is
//! NOT yet a Windows-to-Xiaomi hardware or mutual-authentication result.
use deskfuse_quiche_lab::{encode, parse, Configuration, Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use rcgen::{
    BasicConstraints, CertificateParams, CertifiedIssuer, DnType, ExtendedKeyUsagePurpose,
    IsCa, KeyPair, KeyUsagePurpose,
};
use std::error::Error;
use std::fs;
use std::net::SocketAddr;
use std::thread;
use std::time::{Duration, Instant};

const MAX_UDP_BYTES: usize = 1350;

type TestResult<T> = Result<T, Box<dyn Error>>;

fn config() -> TestResult<quiche::Config> {
    let mut c = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    c.set_application_protos(&[b"df-av-m2a-1"])?;
    c.set_max_idle_timeout(5_000);
    c.set_max_recv_udp_payload_size(MAX_UDP_BYTES);
    c.set_max_send_udp_payload_size(MAX_UDP_BYTES);
    c.set_initial_max_data(64 * 1024);
    c.set_initial_max_stream_data_bidi_local(8192);
    c.set_initial_max_stream_data_bidi_remote(8192);
    c.set_initial_max_streams_bidi(1);
    c.set_disable_active_migration(true);
    // Explicitly bounded DATAGRAM queues; no 0-RTT.
    c.enable_dgram(true, 4, 4);
    Ok(c)
}

// In-process transport shim for one test session. Production must use real
// sockets/readiness, fully scheduled protocol timers and safe send-info pacing.
fn transfer(
    src: &mut quiche::Connection,
    dst: &mut quiche::Connection,
    from: SocketAddr,
    to: SocketAddr,
) -> TestResult<usize> {
    let mut count = 0;
    let mut bytes = [0u8; MAX_UDP_BYTES];
    for _ in 0..256 {
        let (len, send_info) = match src.send(&mut bytes) {
            Ok(x) => x,
            Err(quiche::Error::Done) => break,
            Err(e) => return Err(format!("quiche send failure: {e:?}").into()),
        };
        if send_info.from != from || send_info.to != to {
            return Err("unexpected QUIC send route".into());
        }
        // Rust Instant, not a Windows absolute time conversion. Test-only sleep
        // respects quiche's pacing hint; not the product audio send scheduler.
        let delay = send_info.at.saturating_duration_since(Instant::now());
        if delay > Duration::from_secs(1) {
            return Err("unexpectedly long pacing deadline".into());
        }
        if !delay.is_zero() {
            thread::sleep(delay);
        }
        let recv_info = quiche::RecvInfo { from, to };
        match dst.recv(&mut bytes[..len], recv_info) {
            Ok(_) | Err(quiche::Error::Done) => {}
            Err(e) => return Err(format!("quiche recv failure: {e:?}").into()),
        }
        count += 1;
    }
    Ok(count)
}

#[test]
fn verified_tls_quiche_delivers_source_indexed_datagram() -> TestResult<()> {
    // Ephemeral private test CA, signed localhost server leaf and key.
    // Private key exists only inside the test's auto-deleting temp directory.
    // Never publish or reuse these materials on a network.
    let tmp = tempfile::tempdir()?;
    let mut ca_params = CertificateParams::new(Vec::<String>::new())?;
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.distinguished_name.push(DnType::CommonName, "DeskFuse M2a ephemeral test CA");
    ca_params.key_usages = vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
    let ca = CertifiedIssuer::self_signed(ca_params, KeyPair::generate()?)?;

    let mut leaf_params = CertificateParams::new(vec!["localhost".to_string()])?;
    leaf_params.distinguished_name.push(DnType::CommonName, "localhost");
    leaf_params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    leaf_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let leaf_key = KeyPair::generate()?;
    let leaf = leaf_params.signed_by(&leaf_key, &ca)?;

    let ca_path = tmp.path().join("ca.pem");
    let cert_path = tmp.path().join("server.pem");
    let key_path = tmp.path().join("server.key");
    fs::write(&ca_path, ca.pem())?;
    fs::write(&cert_path, format!("{}\n{}", leaf.pem(), ca.pem()))?;
    fs::write(&key_path, leaf_key.serialize_pem())?;

    let mut server_cfg = config()?;
    server_cfg.load_cert_chain_from_pem_file(cert_path.to_str().ok_or("non-UTF8 cert path")?)?;
    server_cfg.load_priv_key_from_pem_file(key_path.to_str().ok_or("non-UTF8 key path")?)?;
    server_cfg.verify_peer(false); // Server client-identity auth is a later explicit gate.

    let mut client_cfg = config()?;
    client_cfg.load_verify_locations_from_file(ca_path.to_str().ok_or("non-UTF8 CA path")?)?;
    client_cfg.verify_peer(true); // Critical: verify signed chain AND SNI hostname.

    let client_addr: SocketAddr = "127.0.0.1:54001".parse()?;
    let server_addr: SocketAddr = "127.0.0.1:54002".parse()?;
    let client_scid = quiche::ConnectionId::from_ref(&[0x31; 16]);
    let mut client = quiche::connect(
        Some("localhost"), &client_scid, client_addr, server_addr, &mut client_cfg
    )?;

    // First client Initial reveals the destination CID. A single-session
    // in-process server may use that CID without stateless Retry. Production
    // multi-peer UDP servers must implement CID routing and address validation.
    let mut initial = [0u8; MAX_UDP_BYTES];
    let (n, info) = client.send(&mut initial)?;
    assert_eq!(info.to, server_addr);
    let hdr = quiche::Header::from_slice(&mut initial[..n], quiche::MAX_CONN_ID_LEN)?;
    let mut server = quiche::accept(
        &hdr.dcid, None, server_addr, client_addr, &mut server_cfg
    )?;
    server.recv(
        &mut initial[..n],
        quiche::RecvInfo { from: client_addr, to: server_addr },
    )?;

    let start = Instant::now();
    let mut established = false;
    for _ in 0..10_000 {
        let a = transfer(&mut server, &mut client, server_addr, client_addr)?;
        let b = transfer(&mut client, &mut server, client_addr, server_addr)?;
        if client.is_established() && server.is_established() {
            established = true;
            break;
        }
        if client.is_closed() || server.is_closed() || start.elapsed() >= Duration::from_secs(10) {
            break;
        }
        let now = Instant::now();
        if client.timeout_instant().is_some_and(|at| at <= now) { client.on_timeout(); }
        if server.timeout_instant().is_some_and(|at| at <= now) { server.on_timeout(); }
        if a + b == 0 { thread::sleep(Duration::from_millis(1)); }
    }
    assert!(established, "QUIC handshake did not establish with a trusted localhost certificate");

    let cfg = Configuration::default();
    let header = Header {
        version: VERSION,
        kind: KIND_AUDIO,
        flags: 0,
        header_bytes: HEADER_BYTES as u16,
        frame_count: 240,
        media_epoch: cfg.media_epoch,
        source_stream_id: cfg.source_stream_id,
        format_config_id: cfg.format_config_id,
        packet_sequence: 1,
        first_source_frame: 48_000,
        payload_bytes: 960,
        clock_anchor_id: 17,
    };
    let mut application_datagram = encode(&header).to_vec();
    application_datagram.extend((0..960).map(|i| i as u8));
    assert_eq!(application_datagram.len(), 1000);

    let max = client.dgram_max_writable_len().ok_or("DATAGRAM not negotiated")?;
    assert!(max >= application_datagram.len(), "QUIC path DATAGRAM limit is too small");
    client.dgram_send(&application_datagram)?;

    let mut inbound = [0u8; 1200];
    let mut got = false;
    for _ in 0..10_000 {
        transfer(&mut client, &mut server, client_addr, server_addr)?;
        match server.dgram_recv(&mut inbound) {
            Ok(n) => {
                let (received_header, payload) = parse(&inbound[..n], &cfg)
                    .map_err(|e| format!("wire parser rejected authenticated QUIC plaintext: {e:?}"))?;
                assert_eq!(received_header, header);
                assert_eq!(payload, &application_datagram[HEADER_BYTES..]);
                got = true;
                break;
            }
            Err(quiche::Error::Done) => {}
            Err(e) => return Err(format!("QUIC DATAGRAM receive failed: {e:?}").into()),
        }
        if start.elapsed() >= Duration::from_secs(10) { break; }
        let now = Instant::now();
        if client.timeout_instant().is_some_and(|at| at <= now) { client.on_timeout(); }
        if server.timeout_instant().is_some_and(|at| at <= now) { server.on_timeout(); }
        thread::sleep(Duration::from_millis(1));
    }
    assert!(got, "authenticated QUIC DATAGRAM was not delivered");
    println!("PASS server-verified QUIC TLS 1.3 and source-indexed 1000-byte DATAGRAM");
    Ok(())
}

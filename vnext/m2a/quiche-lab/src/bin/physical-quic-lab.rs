//! M2a hardware gate: authenticated Windows listener and Android ARM64 client.
 //! Separate command-line lab, NOT an APK or a production network service.
 //! No audio capture or playback. Source-indexed PCM test data only.
use deskfuse_quiche_lab::{encode, parse, Configuration, Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use std::env;
use std::error::Error;
use std::io;
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::thread;
use std::time::{Duration, Instant};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const ALPN: &[u8] = b"df-physical-m2a-1";
const DATAGRAM_BYTES: usize = 1350;
const PAYLOAD_BYTES: usize = 960;
const PACKETS: u32 = 16;
const LIMIT: Duration = Duration::from_secs(30);
const FINISH_GRACE: Duration = Duration::from_millis(750);

fn config(ca: &str, cert: &str, key: &str) -> Result<quiche::Config> {
    let mut c = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    c.load_verify_locations_from_file(ca)?;
    c.load_cert_chain_from_pem_file(cert)?;
    c.load_priv_key_from_pem_file(key)?;
    c.verify_peer(true); // TLS verification enabled at BOTH endpoints.
    c.set_application_protos(&[ALPN])?;
    c.set_max_idle_timeout(15_000);
    c.set_max_recv_udp_payload_size(DATAGRAM_BYTES);
    c.set_max_send_udp_payload_size(DATAGRAM_BYTES);
    c.set_initial_max_data(64 * 1024);
    c.set_initial_max_stream_data_bidi_local(8192);
    c.set_initial_max_stream_data_bidi_remote(8192);
    c.set_initial_max_streams_bidi(1);
    c.set_disable_active_migration(true);
    c.enable_dgram(true, 4, 4);
    Ok(c)
}

fn test_packet(sequence: u32) -> Vec<u8> {
    let h = Header {
        version: VERSION,
        kind: KIND_AUDIO,
        flags: 0,
        header_bytes: HEADER_BYTES as u16,
        frame_count: 240,
        media_epoch: 1,
        source_stream_id: 1,
        format_config_id: 1,
        packet_sequence: sequence,
        first_source_frame: 48_000 + u64::from(sequence) * 240,
        payload_bytes: PAYLOAD_BYTES as u32,
        clock_anchor_id: 17,
    };
    let mut v = encode(&h).to_vec();
    v.extend((0..PAYLOAD_BYTES).map(|i| (i as u8).wrapping_add(sequence as u8)));
    v
}

fn verify_packet(data: &[u8], expected_sequence: u32) -> Result<()> {
    let (header, _) =
        parse(data, &Configuration::default()).map_err(|e| format!("packet parse failed: {e:?}"))?;
    if header.packet_sequence != expected_sequence || data != test_packet(expected_sequence) {
        return Err(format!("incorrect sequence, sample index or PCM payload; expected {expected_sequence}").into());
    }
    Ok(())
}

fn flush(socket: &UdpSocket, conn: &mut quiche::Connection) -> Result<usize> {
    let mut out = [0u8; DATAGRAM_BYTES];
    let mut emitted = 0;
    for _ in 0..128 {
        let (n, meta) = match conn.send(&mut out) {
            Ok(sent) => sent,
            Err(quiche::Error::Done) => break,
            Err(e) => return Err(format!("quiche send failed: {e:?}").into()),
        };
        let wait = meta.at.saturating_duration_since(Instant::now());
        if wait > Duration::from_secs(1) {
            return Err("abnormally long QUIC pacing deadline".into());
        }
        if !wait.is_zero() { thread::sleep(wait); }
        let n_sent = socket.send_to(&out[..n], meta.to)?;
        if n_sent != n { return Err("UDP short send".into()); }
        emitted += 1;
    }
    Ok(emitted)
}

fn timeout(conn: &mut quiche::Connection) {
    if conn.timeout_instant().is_some_and(|at| at <= Instant::now()) {
        conn.on_timeout();
    }
}

fn tls_peer_verified(conn: &quiche::Connection) -> Result<()> {
    // quiche's server-side verify_peer(true) sets SSL_VERIFY_PEER, which can
    // permit no client certificate in some TLS configurations. Never treat
    // "handshake established" alone as authorization.
    if conn.peer_cert().is_none() {
        return Err("REFUSED: peer did not present a TLS client/server certificate".into());
    }
    Ok(())
}

fn client(server: SocketAddr, ca: &str, cert: &str, key: &str) -> Result<()> {
    let mut cfg = config(ca, cert, key)?;
    let socket = UdpSocket::bind("0.0.0.0:0")?;
    socket.connect(server)?; // Choose routed interface / concrete local IP.
    socket.set_nonblocking(true)?;
    let local = socket.local_addr()?;
    let scid = quiche::ConnectionId::from_ref(&[0x51; 16]); // One-shot test only, not production CID.
    let mut conn = quiche::connect(Some("deskfuse-lab.local"), &scid, local, server, &mut cfg)?;

    let start = Instant::now();
    let mut next_to_send = 0u32;
    let mut next_to_receive = 0u32;
    let mut sent_at = start;
    let mut rtt_sum = Duration::ZERO;
    let mut authenticated = false;
    let mut input = [0u8; 65535];
    let mut datagram = [0u8; 1500];

    println!("CONNECT Android outbound {} -> {}, QUIC TLS client certificate enabled", local, server);
    while start.elapsed() < LIMIT {
        if conn.is_closed() {
            return Err("client QUIC connection closed before exchange completed".into());
        }
        let mut progressed = flush(&socket, &mut conn)?;
        for _ in 0..128 {
            match socket.recv_from(&mut input) {
                Ok((n, from)) if from == server => {
                    let meta = quiche::RecvInfo { from, to: local };
                    match conn.recv(&mut input[..n], meta) {
                        Ok(_) | Err(quiche::Error::Done) => {}
                        Err(e) => return Err(format!("client recv failed: {e:?}").into()),
                    }
                    progressed += 1;
                }
                Ok(_) => return Err("unexpected UDP source".into()),
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }
        if conn.is_established() && !authenticated {
            tls_peer_verified(&conn)?;
            authenticated = true;
            println!("AUTH client: server CA chain and deskfuse-lab.local identity verified");
        }
        if authenticated {
            loop {
                match conn.dgram_recv(&mut datagram) {
                    Ok(n) => {
                        if next_to_receive >= next_to_send {
                            return Err("unsolicited/replayed echo".into());
                        }
                        verify_packet(&datagram[..n], next_to_receive)?;
                        rtt_sum += sent_at.elapsed();
                        println!("RECEIVED seq={} source_frame={} bytes={}",
                            next_to_receive, 48_000+u64::from(next_to_receive)*240, n);
                        next_to_receive += 1;
                        progressed += 1;
                    }
                    Err(quiche::Error::Done) => break,
                    Err(e) => return Err(format!("client DATAGRAM receive failed: {e:?}").into()),
                }
            }
            if next_to_receive == PACKETS {
                println!("PASS physical UDP QUIC mTLS device client: {} source-indexed DATAGRAMs returned, mean sequential roundtrip {:.2} ms",
                    PACKETS, rtt_sum.as_secs_f64()*1000.0/f64::from(PACKETS));
                return Ok(());
            }
            if next_to_send == next_to_receive && next_to_send < PACKETS {
                let packet = test_packet(next_to_send);
                if conn.dgram_max_writable_len().unwrap_or(0) < packet.len() {
                    return Err("QUIC DATAGRAM limit too small".into());
                }
                conn.dgram_send(&packet)?;
                sent_at = Instant::now();
                next_to_send += 1;
                progressed += 1;
            }
        }
        timeout(&mut conn);
        if progressed == 0 { thread::sleep(Duration::from_millis(2)); }
    }
    Err(format!("client timeout: echoed {next_to_receive}/{PACKETS}; no transport-level retransmission of DATAGRAMs").into())
}

fn server(bind: SocketAddr, allow_ip: IpAddr, ca: &str, cert: &str, key: &str) -> Result<()> {
    let mut cfg = config(ca, cert, key)?;
    let socket = UdpSocket::bind(bind)?;
    socket.set_nonblocking(true)?;
    let local = socket.local_addr()?;
    println!("READY Windows UDP listener {} ; only client IP {} ; QUIC TLS client identity required", local, allow_ip);
    let mut conn: Option<quiche::Connection> = None;
    let mut peer: Option<SocketAddr> = None;
    let mut authenticated = false;
    let mut next_sequence = 0u32;
    let mut complete: Option<Instant> = None;
    let start = Instant::now();
    let mut input = [0u8; 65535];
    let mut datagram = [0u8; 1500];

    while start.elapsed() < LIMIT {
        if conn.as_ref().is_some_and(|c| c.is_closed()) {
            return Err("server QUIC connection closed before exchange completed".into());
        }
        let mut progressed = 0;
        for _ in 0..128 {
            match socket.recv_from(&mut input) {
                Ok((n, from)) => {
                    if from.ip() != allow_ip || peer.is_some_and(|p| p != from) {
                        // Restrict expensive handshakes to the authorized phone IP.
                        continue;
                    }
                    if conn.is_none() {
                        let hdr = match quiche::Header::from_slice(
                            &mut input[..n], quiche::MAX_CONN_ID_LEN
                        ) {
                            Ok(h) if h.version == quiche::PROTOCOL_VERSION => h,
                            _ => continue,
                        };
                        let c = quiche::accept(&hdr.dcid, None, local, from, &mut cfg)?;
                        conn = Some(c);
                        peer = Some(from);
                    }
                    let c = conn.as_mut().ok_or("connection missing")?;
                    match c.recv(&mut input[..n], quiche::RecvInfo { from, to: local }) {
                        Ok(_) | Err(quiche::Error::Done) => {}
                        Err(e) => return Err(format!("server QUIC recv failed: {e:?}").into()),
                    }
                    progressed += 1;
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }
        if let Some(c) = conn.as_mut() {
            if c.is_established() && !authenticated {
                tls_peer_verified(c)?; // MANDATORY: server never accepts anonymous client.
                authenticated = true;
                println!("AUTH server: CA-validated client certificate presented");
            }
            if authenticated {
                loop {
                    match c.dgram_recv(&mut datagram) {
                        Ok(n) => {
                            if next_sequence >= PACKETS {
                                return Err("extra/replayed DATAGRAM".into());
                            }
                            verify_packet(&datagram[..n], next_sequence)?;
                            c.dgram_send(&datagram[..n])?;
                            println!("VERIFIED seq={} source_frame={} bytes={}",
                                next_sequence, 48_000+u64::from(next_sequence)*240, n);
                            next_sequence += 1;
                            progressed += 1;
                            if next_sequence == PACKETS { complete = Some(Instant::now()); }
                        }
                        Err(quiche::Error::Done) => break,
                        Err(e) => return Err(format!("server DATAGRAM receive failed: {e:?}").into()),
                    }
                }
            }
            progressed += flush(&socket, c)?;
            timeout(c);
        }
        // Give the last encrypted echoed packet a short opportunity to leave,
        // but do not claim receiver delivery from server-side send completion.
        if complete.is_some_and(|t| t.elapsed() >= FINISH_GRACE) {
            println!("PASS Windows listener: verified {} authenticated source-indexed DATAGRAMs and queued echoes", PACKETS);
            return Ok(());
        }
        if progressed == 0 { thread::sleep(Duration::from_millis(2)); }
    }
    Err(format!("server timeout: verified {next_sequence}/{PACKETS} client DATAGRAMs").into())
}

fn main() {
    if let Err(e) = run() {
        eprintln!("FAIL device QUIC lab: {e}");
        std::process::exit(1);
    }
}
fn run() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    match args.as_slice() {
        [_, mode, bind, allowed_ip, ca, cert, key] if mode == "server" => {
            server(bind.parse()?, allowed_ip.parse()?, ca, cert, key)
        }
        [_, mode, remote, ca, cert, key] if mode == "client" => {
            client(remote.parse()?, ca, cert, key)
        }
        _ => Err("usage: physical-quic-lab server <bind-ip:port> <allowed-client-ip> <ca.pem> <server.pem> <server.key> | client <windows-ip:port> <ca.pem> <client.pem> <client.key>".into()),
    }
}

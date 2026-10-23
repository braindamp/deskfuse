//! First persistent synthetic Windows->Android audio transport adapter.
//!
//! Lab executable only: authenticated QUIC DATAGRAMs, no WASAPI or Android
//! playback, no installed APK/driver modification, no credentials in source.
//! Synthetic source frames are released on a 48-kHz source timeline.
//! One readiness-driven transport owner runs QUIC and UDP. No stop-and-wait
//! echo dependency, periodic 2-ms poll, or QUIC ACK audio clock.
use deskfuse_quiche_lab::reactor::{next_deadline, wait_udp, Egress, Readiness, MAX_QUIC_UDP_BYTES, SYNTHETIC_BLOCK};
use deskfuse_quiche_lab::{encode, parse, Configuration, Header, HEADER_BYTES, KIND_AUDIO, VERSION};
use std::env;
use std::error::Error;
use std::io;
use std::sync::mpsc::TryRecvError;
use deskfuse_quiche_lab::wasapi_pipe::{self, Assembler};
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const ALPN: &[u8] = b"df-continuous-m2a-1";
const AUDIO_FRAMES: u64 = 240;
const PAYLOAD_BYTES: usize = 960;
const APP_BYTES: usize = 1000;
const CONNECT_LIMIT: Duration = Duration::from_secs(30);

fn config(ca: &str, cert: &str, key: &str) -> Result<quiche::Config> {
    let mut c = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    c.load_verify_locations_from_file(ca)?;
    c.load_cert_chain_from_pem_file(cert)?;
    c.load_priv_key_from_pem_file(key)?;
    c.verify_peer(true);
    c.set_application_protos(&[ALPN])?;
    c.set_max_idle_timeout(15_000);
    c.set_max_recv_udp_payload_size(MAX_QUIC_UDP_BYTES);
    c.set_max_send_udp_payload_size(MAX_QUIC_UDP_BYTES);
    c.set_initial_max_data(64 * 1024);
    c.set_initial_max_stream_data_bidi_local(8192);
    c.set_initial_max_stream_data_bidi_remote(8192);
    c.set_initial_max_streams_bidi(1);
    c.set_disable_active_migration(true);
    c.enable_dgram(true, 4, 4);
    Ok(c)
}

fn packet(sequence: u32) -> Vec<u8> {
    let header = Header {
        version: VERSION,
        kind: KIND_AUDIO,
        flags: 0,
        header_bytes: HEADER_BYTES as u16,
        frame_count: AUDIO_FRAMES as u16,
        media_epoch: 1,
        source_stream_id: 1,
        format_config_id: 1,
        packet_sequence: sequence,
        first_source_frame: 48_000 + u64::from(sequence) * AUDIO_FRAMES,
        payload_bytes: PAYLOAD_BYTES as u32,
        clock_anchor_id: 17,
    };
    let mut bytes = encode(&header).to_vec();
    bytes.extend((0..PAYLOAD_BYTES).map(|i| (i as u8).wrapping_add(sequence as u8)));
    bytes
}

fn captured_packet(sequence: u32, device_frame: u64, pcm: &[u8; 960]) -> Vec<u8> {
    let h = Header {
        version: VERSION, kind: KIND_AUDIO, flags: 0,
        header_bytes: HEADER_BYTES as u16, frame_count: AUDIO_FRAMES as u16,
        media_epoch: 1, source_stream_id: 1, format_config_id: 1,
        packet_sequence: sequence, first_source_frame: device_frame,
        payload_bytes: PAYLOAD_BYTES as u32, clock_anchor_id: 17,
    };
    let mut wire = encode(&h).to_vec();
    wire.extend_from_slice(pcm);
    wire
}

fn verified(conn: &quiche::Connection) -> Result<()> {
    if conn.peer_cert().is_none() {
        return Err("REFUSED: mutually authenticated TLS peer certificate absent".into());
    }
    Ok(())
}

fn service_timeout(conn: &mut quiche::Connection) {
    if conn.timeout_instant().is_some_and(|t| t <= Instant::now()) {
        conn.on_timeout();
    }
}

fn socket(bind: SocketAddr) -> Result<UdpSocket> {
    let udp = UdpSocket::bind(bind)?;
    udp.set_nonblocking(true)?;
    Ok(udp)
}

fn deadline(dates: &[Option<Instant>]) -> Option<Instant> {
    dates.iter().copied().flatten().min()
}

fn server(bind: SocketAddr, allow_ip: IpAddr, ca: &str, cert: &str, key: &str, seconds: u64) -> Result<()> {
    let mut cfg = config(ca, cert, key)?;
    let udp = socket(bind)?;
    let local = udp.local_addr()?;
    let end_waiting = Instant::now() + CONNECT_LIMIT;
    let mut conn: Option<quiche::Connection> = None;
    let mut peer: Option<SocketAddr> = None;
    let mut origin: Option<Instant> = None;
    let run_for = Duration::from_secs(seconds);
    let mut next_sequence = 0u64;
    let mut offered = 0u64;
    let mut not_queued = 0u64;
    let mut skipped_source_blocks = 0u64;
    let mut egress = Egress::default();
    let mut bytes = [0u8; 65535];

    println!("READY continuous Windows source {} ; allowed phone IP {} ; mutual TLS mandatory", local, allow_ip);
    loop {
        let now = Instant::now();
        if origin.is_none() && now >= end_waiting { return Err("no authenticated Android client within connection deadline".into()); }
        if let Some(c) = conn.as_mut() {
            service_timeout(c);
            if c.is_closed() { return Err("Windows QUIC connection closed".into()); }
        }

        // Drain incoming kernel UDP packets without busy polling.
        for _ in 0..128 {
            let (n, from) = match udp.recv_from(&mut bytes) {
                Ok(packet) => packet,
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            };
            if from.ip() != allow_ip || peer.is_some_and(|p| p != from) { continue; }
            if conn.is_none() {
                let header = match quiche::Header::from_slice(&mut bytes[..n], quiche::MAX_CONN_ID_LEN) {
                    Ok(h) if h.version == quiche::PROTOCOL_VERSION => h,
                    _ => continue,
                };
                conn = Some(quiche::accept(&header.dcid, None, local, from, &mut cfg)?);
                peer = Some(from);
            }
            let c = conn.as_mut().expect("connection accepted");
            match c.recv(&mut bytes[..n], quiche::RecvInfo { from, to: local }) {
                Ok(_) | Err(quiche::Error::Done) => {}
                Err(e) => return Err(format!("Windows QUIC receive: {e:?}").into()),
            }
        }

        if let Some(c) = conn.as_mut() {
            if c.is_established() && origin.is_none() {
                verified(c)?;
                origin = Some(Instant::now());
                println!("AUTH Windows: Android client CA and certificate verified; starting source-clock blocks");
            }
            if let Some(start) = origin {
                let now = Instant::now();
                if now.duration_since(start) < run_for {
                    // Source timestamps are derived from the origin clock, not
                    // network ACKs. Missed source periods are never replayed.
                    let due = (now.duration_since(start).as_micros() / SYNTHETIC_BLOCK.as_micros()) as u64;
                    if due >= next_sequence {
                        skipped_source_blocks += due - next_sequence;
                        next_sequence = due + 1;
                        if due > u64::from(u32::MAX) { return Err("source sequence exhausted".into()); }
                        let payload = packet(due as u32);
                        if c.dgram_max_writable_len().unwrap_or(0) < APP_BYTES {
                            return Err("DATAGRAM negotiation cannot carry 1000-byte PCM frame".into());
                        }
                        match c.dgram_send(&payload) {
                            Ok(()) => offered += 1,
                            Err(quiche::Error::Done) => not_queued += 1,
                            Err(e) => return Err(format!("DATAGRAM enqueue: {e:?}").into()),
                        }
                    }
                }
            }
            // QUIC and this kernel socket have exactly one egress owner.
            egress.service(&udp, c, false)?;
            if let Some(start) = origin {
                // Stop source production at run_for, but leave the authenticated
                // QUIC socket open long enough for final sealed packets and
                // receiver ACK/control work; this is NOT new PCM pacing.
                if start.elapsed() >= run_for + Duration::from_secs(3) {
                    let stats = c.stats();
                    println!(
                        "DONE Windows source duration={}s offered={} quiche_queue_full={} skipped_source={} udp_packets={} udp_would_block={} quic_dgram_sent={} quic_lost={}",
                        seconds, offered, not_queued, skipped_source_blocks,
                        egress.udp_packets_sent, egress.would_block_count, stats.dgram_sent, stats.lost
                    );
                    return Ok(());
                }
            }
        }

        let source_wake = origin.and_then(|start| {
            if start.elapsed() >= run_for { None }
            else { start.checked_add(SYNTHETIC_BLOCK.saturating_mul(next_sequence.min(u32::MAX as u64) as u32)) }
        });
        let quic_wake = conn.as_ref().and_then(|c| c.timeout_instant());
        let wake = deadline(&[
            next_deadline(quic_wake, egress.deadline(), source_wake),
            if origin.is_none() { Some(end_waiting) } else { origin.and_then(|x| x.checked_add(run_for)) },
        ]);
        let writable = egress.needs_write_readiness();
        let ready = wait_udp(&udp, writable, wake)?;
        if ready.writable {
            if let Some(c) = conn.as_mut() {
                egress.service(&udp, c, true)?;
            }
        }
    }
}

fn client(remote: SocketAddr, ca: &str, cert: &str, key: &str, seconds: u64) -> Result<()> {
    let mut cfg = config(ca, cert, key)?;
    let route = UdpSocket::bind("0.0.0.0:0")?;
    route.connect(remote)?;
    let local_ip = route.local_addr()?.ip();
    drop(route);
    let udp = socket(SocketAddr::new(local_ip, 0))?;
    let local = udp.local_addr()?;
    let scid = quiche::ConnectionId::from_ref(&[0x71; 16]); // isolated one-peer lab only
    let mut conn = quiche::connect(Some("deskfuse-lab.local"), &scid, local, remote, &mut cfg)?;
    let mut egress = Egress::default();
    let started_waiting = Instant::now();
    let mut authenticated_at: Option<Instant> = None;
    let mut first_sequence: Option<u32> = None;
    let mut last_sequence: Option<u32> = None;
    let mut received = 0u64;
    let mut gaps = 0u64;
    let mut late = 0u64;
    let mut input = [0u8; 65535];
    let mut datagram = [0u8; 1500];

    println!("CONNECT continuous Android sink {} -> {} ; mutual TLS mandatory", local, remote);
    loop {
        service_timeout(&mut conn);
        if conn.is_closed() { return Err("Android QUIC connection closed before receiving complete observation window".into()); }

        for _ in 0..128 {
            match udp.recv_from(&mut input) {
                Ok((n, from)) if from == remote => {
                    match conn.recv(&mut input[..n], quiche::RecvInfo { from, to: local }) {
                        Ok(_) | Err(quiche::Error::Done) => {}
                        Err(e) => return Err(format!("Android QUIC receive: {e:?}").into()),
                    }
                }
                Ok(_) => return Err("unexpected unauthorised UDP source on Android".into()),
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }
        if conn.is_established() && authenticated_at.is_none() {
            verified(&conn)?;
            authenticated_at = Some(Instant::now());
            println!("AUTH Android: verified Windows server CA chain and deskfuse-lab.local");
        }
        if authenticated_at.is_some() {
            for _ in 0..128 {
                match conn.dgram_recv(&mut datagram) {
                    Ok(n) => {
                        let (h, _) = parse(&datagram[..n], &Configuration::default())
                            .map_err(|e| format!("Android rejected PCM header: {e:?}"))?;
                        if n != APP_BYTES || datagram[..n] != packet(h.packet_sequence) {
                            return Err("Android rejected PCM source-frame or payload identity".into());
                        }
                        if let Some(last) = last_sequence {
                            if h.packet_sequence <= last { late += 1; continue; }
                            gaps += u64::from(h.packet_sequence - last - 1);
                        }
                        first_sequence.get_or_insert(h.packet_sequence);
                        last_sequence = Some(h.packet_sequence);
                        received += 1;
                        if received % 200 == 0 {
                            println!("ANDROID PCM received={} last_seq={} source_frame={} missing_before={} stale={}",
                                received, h.packet_sequence, h.first_source_frame, gaps, late);
                        }
                    }
                    Err(quiche::Error::Done) => break,
                    Err(e) => return Err(format!("Android DATAGRAM receive: {e:?}").into()),
                }
            }
        }
        egress.service(&udp, &mut conn, false)?;
        if let Some(at) = authenticated_at {
            if at.elapsed() >= Duration::from_secs(seconds + 2) {
                let stats = conn.stats();
                println!(
                    "DONE Android sink received={} first_seq={:?} last_seq={:?} missing_between={} stale={} udp_packets={} quic_lost={} (no playback)",
                    received, first_sequence, last_sequence, gaps, late, egress.udp_packets_sent, stats.lost
                );
                if received == 0 { return Err("no authenticated PCM DATAGRAMs received".into()); }
                return Ok(());
            }
        } else if started_waiting.elapsed() >= CONNECT_LIMIT {
            return Err("Android never authenticated Windows server".into());
        }

        let until = authenticated_at
            .map(|at| at + Duration::from_secs(seconds + 2))
            .unwrap_or(started_waiting + CONNECT_LIMIT);
        let wake = next_deadline(conn.timeout_instant(), egress.deadline(), Some(until));
        let ready: Readiness = wait_udp(&udp, egress.needs_write_readiness(), wake)?;
        if ready.writable {
            egress.service(&udp, &mut conn, true)?;
        }
    }
}

fn positive_seconds(s: &str) -> Result<u64> {
    let n = s.parse::<u64>()?;
    if !(1..=60).contains(&n) { return Err("duration must be 1..60 seconds in the isolated lab".into()); }
    Ok(n)
}

fn run() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    match args.as_slice() {
        [_, mode, bind, allow_ip, ca, cert, key, secs] if mode == "server" => {
            server(bind.parse()?, allow_ip.parse()?, ca, cert, key, positive_seconds(secs)?)
        }
        [_, mode, remote, ca, cert, key, secs] if mode == "client" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?)
        }
        _ => Err("usage: continuous-quic-lab server <bind-ip:port> <allowed-phone-ip> <ca.pem> <server.pem> <server.key> <seconds> | client <windows-ip:port> <ca.pem> <client.pem> <client.key> <seconds>".into()),
    }
}

fn main() {
    if let Err(e) = run() {
        eprintln!("FAIL continuous QUIC lab: {e}");
        std::process::exit(1);
    }
}

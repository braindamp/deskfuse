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
use deskfuse_quiche_lab::android_audio::{Admission, AndroidAudio};
use deskfuse_quiche_lab::source_age::{self, AgeDecision};
use deskfuse_quiche_lab::plaintext_expiry::{PlaintextExpiry, Identity as OutgoingIdentity};
use deskfuse_quiche_lab::receive_frontier::{ReceiveFrontier, Context as SourceContext, Reject as SourceReject};
use deskfuse_quiche_lab::fresh_anchor::{self, Message as AnchorMessage};
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const ALPN: &[u8] = b"df-continuous-m2a-1";
const AUDIO_FRAMES: u64 = 240;
const PAYLOAD_BYTES: usize = 960;
const APP_BYTES: usize = 1000;
// Only short, precisely aligned source gaps are concealed by silence.
// A longer gap is explicitly recorded; never queue hundreds of milliseconds
// of obsolete zeros into the phone's fixed 80-ms output ring.
const MAX_CONCEALMENT_FRAMES: u64 = 480; // two 5-ms DATAGRAMs
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

fn source_egress(
    egress: &mut Egress, udp: &UdpSocket, conn: &mut quiche::Connection,
    writable: bool, outstanding: &mut PlaintextExpiry, physical_capture: bool,
) -> Result<()> {
    egress.service_with_hook(udp, conn, writable, |c| {
        if physical_capture {
            let now = source_age::local_qpc_100ns()?;
            outstanding.purge(c, now);
        }
        Ok(())
    })
}

fn server(bind: SocketAddr, allow_ip: IpAddr, ca: &str, cert: &str, key: &str, seconds: u64, capture: Option<(&str, &str)>) -> Result<()> {
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
    let mut outstanding = PlaintextExpiry::default();
    // Request receipt in the exact same Windows-QPC clock domain as WASAPI.
    let mut pending_capture_anchor: Option<(AnchorMessage, u64)> = None;
    let mut anchor_nonce_frontier = 0u64;
    let mut anchor_replies_sent = 0u64;
    let mut bytes = [0u8; 65535];
    let mut source = None;
    let mut assembler = Assembler::default();
    let mut source_chunks_too_old = 0u64;
    let mut last_capture_qpc = 0u64;
    let mut expired_source_qpc = 0u64;
    let mut invalid_source_qpc = 0u64;
    let mut future_source_qpc = 0u64;
    let mut max_observed_source_age_100ns = 0u64;

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
                if let Some((capture_exe, endpoint)) = capture {
                    source = Some(wasapi_pipe::spawn(capture_exe, endpoint, seconds, local)?);
                    println!("AUTH Windows: Android client verified; native WASAPI capture process started");
                } else {
                    println!("AUTH Windows: Android client CA and certificate verified; starting synthetic source-clock blocks");
                }
            }
            // QUIC-authenticated control DATAGRAMs are never treated as PCM.
            // A repeated nonce is a retry; a smaller nonce cannot revive a
            // retired request. Only a source block captured AFTER the request
            // is observed can satisfy the challenge.
            for _ in 0..16 {
                let n = match c.dgram_recv(&mut bytes) {
                    Ok(n) => n,
                    Err(quiche::Error::Done) => break,
                    Err(e) => return Err(format!("Windows recovery request: {e:?}").into()),
                };
                if capture.is_some() {
                    if let Some(req) = AnchorMessage::decode(&bytes[..n]) {
                        if req.kind != fresh_anchor::REQUEST ||
                            req.context != (SourceContext{epoch:1,stream:1,format:1}) ||
                            req.nonce == 0 {
                            return Err("Windows rejected authenticated anchor request context".into());
                        }
                        if req.nonce >= anchor_nonce_frontier {
                            anchor_nonce_frontier = req.nonce;
                            pending_capture_anchor = Some((
                                req, source_age::local_qpc_100ns()?,
                            ));
                        }
                    } else {
                        return Err("Windows rejected unknown authenticated recovery control".into());
                    }
                }
            }
            if let Some(ref receiver) = source {
                // Real WASAPI events wake this QUIC owner via the local UDP socket.
                // Never synthesize a 5-ms send tick or use ACKs as an audio clock.
                for _ in 0..128 {
                    match receiver.try_recv() {
                        Ok(chunk) => {
                            if wasapi_pipe::queue_too_old(&chunk) {
                                source_chunks_too_old += 1;
                                continue;
                            }
                            // WASAPI capture QPC spans the *whole* native
                            // pipe and reader queues; enqueue residency alone
                            // is not a source-age bound. Fail closed on QPC
                            // timestamp errors or excessive age.
                            let qpc_now = source_age::local_qpc_100ns()?;
                            let age = source_age::classify(qpc_now, chunk.qpc_100ns);
                            match age {
                                AgeDecision::Fresh { age_100ns } => {
                                    max_observed_source_age_100ns =
                                        max_observed_source_age_100ns.max(age_100ns);
                                }
                                AgeDecision::MissingTimestamp => {
                                    invalid_source_qpc += 1;
                                    continue;
                                }
                                AgeDecision::FutureTimestamp => {
                                    future_source_qpc += 1;
                                    continue;
                                }
                                AgeDecision::Expired => {
                                    expired_source_qpc += 1;
                                    continue;
                                }
                            }
                            if let Some(block) = assembler.push(&chunk) {
                                // Re-evaluate the ORIGINAL first-frame capture
                                // QPC immediately before application offer.
                                // quiche may still queue this plaintext before
                                // later sealing; that limitation is tracked.
                                match source_age::classify(
                                    source_age::local_qpc_100ns()?,
                                    block.first_qpc_100ns,
                                ) {
                                    AgeDecision::Fresh { age_100ns } => {
                                        max_observed_source_age_100ns =
                                            max_observed_source_age_100ns.max(age_100ns);
                                    }
                                    AgeDecision::MissingTimestamp => {
                                        invalid_source_qpc += 1;
                                        continue;
                                    }
                                    AgeDecision::FutureTimestamp => {
                                        future_source_qpc += 1;
                                        continue;
                                    }
                                    AgeDecision::Expired => {
                                        expired_source_qpc += 1;
                                        continue;
                                    }
                                }
                                if let Some((req, request_qpc)) = pending_capture_anchor {
                                    if block.first_device_frame >= req.frame &&
                                        block.first_qpc_100ns >
                                            request_qpc.saturating_add(
                                                source_age::MAX_FUTURE_SKEW_100NS) {
                                        // Anchored to actual fresh post-request
                                        // WASAPI capture; the control response
                                        // travels over the same authenticated
                                        // QUIC connection as the media.
                                        let response = AnchorMessage {
                                            kind: fresh_anchor::ANCHOR,
                                            frame: block.first_device_frame,
                                            ..req
                                        };
                                        match c.dgram_send(&response.encode()) {
                                            Ok(()) => {
                                                anchor_replies_sent += 1;
                                                pending_capture_anchor = None;
                                            }
                                            Err(quiche::Error::Done) => {}
                                            Err(e) => return Err(format!("Windows recovery response: {e:?}").into()),
                                        }
                                    }
                                }
                                if next_sequence > u64::from(u32::MAX) {
                                    return Err("captured packet sequence exhausted".into());
                                }
                                let pcm = captured_packet(next_sequence as u32, block.first_device_frame, &block.pcm);
                                next_sequence += 1;
                                last_capture_qpc = block.first_qpc_100ns;
                                if c.dgram_max_writable_len().unwrap_or(0) < pcm.len() {
                                    return Err("QUIC DATAGRAM cannot fit 1000-byte captured PCM".into());
                                }
                                // The 240-frame WASAPI blocks can be delivered
                                // in an OS-callback burst. Service QUIC egress
                                // *between* blocks, rather than filling its
                                // four-packet DATAGRAM queue before the transport
                                // owner gets a chance to seal available packets.
                                // Egress retains a paced/blocked sealed packet,
                                // and we never overwrite it or add a send timer.
                                source_egress(&mut egress, &udp, c, false, &mut outstanding, capture.is_some())?;
                                // A prior packet can remain in quiche's
                                // plaintext queue across a long cwnd/pacing
                                // stall: this original QPC check is the last
                                // admission opportunity before queuing.
                                match source_age::classify(
                                    source_age::local_qpc_100ns()?,
                                    block.first_qpc_100ns,
                                ) {
                                    AgeDecision::Fresh { .. } => {}
                                    _ => {
                                        expired_source_qpc += 1;
                                        continue;
                                    }
                                }
                                match c.dgram_send(&pcm) {
                                    Ok(()) => {
                                        offered += 1;
                                        let id = OutgoingIdentity::from_wire(&pcm)
                                            .ok_or("outgoing PCM header identity missing")?;
                                        outstanding.accept(id, block.first_qpc_100ns)
                                            .map_err(|e| format!("quiche plaintext bound violated: {e}"))?;
                                        source_egress(&mut egress, &udp, c, false, &mut outstanding, capture.is_some())?;
                                    }
                                    Err(quiche::Error::Done) => {
                                        not_queued += 1;
                                        // Source frame is the cross-machine key;
                                        // clocks are NOT assumed synchronized.
                                        if not_queued <= 4 || not_queued % 16 == 0 {
                                            eprintln!("TRACE Windows QUIC_QUEUE_DROP source_frame={} qpc100={} count={} age_max_100ns={}",
                                                block.first_device_frame, block.first_qpc_100ns,
                                                not_queued, max_observed_source_age_100ns);
                                        }
                                    }
                                    Err(e) => return Err(format!("captured PCM QUIC enqueue: {e:?}").into()),
                                }
                            }
                        }
                        Err(TryRecvError::Empty) => break,
                        Err(TryRecvError::Disconnected) => break,
                    }
                }
            } else if capture.is_none() {
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
    
            }
            // QUIC and this kernel socket have exactly one egress owner.
            source_egress(&mut egress, &udp, c, false, &mut outstanding, capture.is_some())?;
            if let Some(start) = origin {
                // Stop source production at run_for, but leave the authenticated
                // QUIC socket open long enough for final sealed packets and
                // receiver ACK/control work; this is NOT new PCM pacing.
                if start.elapsed() >= run_for + Duration::from_secs(3) {
                    let stats = c.stats();
                    if capture.is_some() {
                        println!(
                            "DONE Windows WASAPI source duration={}s real_pcm_blocks_queued={} quiche_queue_full={} source_queue_old={} qpc_expired={} qpc_invalid={} qpc_future={} qpc_max_age_100ns={} quiche_plaintext_expired={} quiche_plaintext_orphans={} quiche_plaintext_absent={} quiche_identity_high_water={} anchor_replies={} capture_discontinuities={} last_wasapi_qpc_100ns={} udp_packets={} udp_would_block={} quic_dgram_sent={} quic_lost={}",
                            seconds, offered, not_queued, source_chunks_too_old,
                            expired_source_qpc, invalid_source_qpc, future_source_qpc,
                            max_observed_source_age_100ns,
                            outstanding.expired, outstanding.orphaned,
                            outstanding.absent, outstanding.high_water,
                            anchor_replies_sent, assembler.discontinuities, last_capture_qpc, egress.udp_packets_sent,
                            egress.would_block_count, stats.dgram_sent, stats.lost
                        );
                    } else {
                    println!(
                        "DONE Windows source duration={}s offered={} quiche_queue_full={} skipped_source={} udp_packets={} udp_would_block={} quic_dgram_sent={} quic_lost={}",
                        seconds, offered, not_queued, skipped_source_blocks,
                        egress.udp_packets_sent, egress.would_block_count, stats.dgram_sent, stats.lost
                    );
                    }
                    return Ok(());
                }
            }
        }

        let source_wake = if capture.is_some() { None } else { origin.and_then(|start| {
            if start.elapsed() >= run_for { None }
            else { start.checked_add(SYNTHETIC_BLOCK.saturating_mul(next_sequence.min(u32::MAX as u64) as u32)) }
        }) };
        let quic_wake = conn.as_ref().and_then(|c| c.timeout_instant());
        let wake = deadline(&[
            next_deadline(quic_wake, egress.deadline(), source_wake),
            if origin.is_none() { Some(end_waiting) } else { origin.and_then(|x| x.checked_add(run_for + Duration::from_secs(3))) },
        ]);
        let writable = egress.needs_write_readiness();
        let ready = wait_udp(&udp, writable, wake)?;
        if ready.writable {
            if let Some(c) = conn.as_mut() {
                source_egress(&mut egress, &udp, c, true, &mut outstanding, capture.is_some())?;
            }
        }
    }
}

#[derive(Copy, Clone)]
struct PlayModes {
    fades: bool,
    conceal: bool,
    linear: bool,
}
impl Default for PlayModes {
    fn default() -> Self {
        Self { fades: true, conceal: true, linear: true }
    }
}
impl PlayModes {
    // Exact, deliberately boring mode specification: f1c0r1
    // f: fade; c: short-gap concealment; r: linear resampling comparator.
    fn parse(flags: &str) -> Result<Self> {
        let x = flags.as_bytes();
        if x.len() != 6 || x[0] != b'f' || x[2] != b'c' || x[4] != b'r'
           || ![x[1], x[3], x[5]].iter().all(|b| *b == b'0' || *b == b'1')
        {
            return Err("diagnostic modes must be exactly f0c0r0 .. f1c1r1".into());
        }
        Ok(Self {
            fades: x[1] == b'1',
            conceal: x[3] == b'1',
            linear: x[5] == b'1',
        })
    }
}

struct PendingAnchor {
    challenge: AnchorMessage,
    recovery_ticket: u64,
    started: Instant,
    retry_at: Instant,
    confirmed: Option<u64>,
}

fn begin_fresh_anchor(
    pending: &mut Option<PendingAnchor>,
    next_nonce: &mut u64,
    renderer: &mut AndroidAudio,
    context: SourceContext,
    rejection_frontier: u64,
) -> Result<()> {
    if pending.is_none() {
        let nonce = *next_nonce;
        *next_nonce = next_nonce.checked_add(1)
            .ok_or("recovery challenge nonce exhausted")?;
        if nonce == 0 { return Err("recovery challenge nonce invalid".into()); }
        let now = Instant::now();
        *pending = Some(PendingAnchor {
            challenge: AnchorMessage {
                kind: fresh_anchor::REQUEST,
                nonce,
                frame: rejection_frontier,
                context,
            },
            recovery_ticket: renderer.request_recovery(),
            started: now,
            retry_at: now,
            confirmed: None,
        });
        eprintln!("TRACE Android ANCHOR_REQUEST nonce={} floor={}",
            nonce, rejection_frontier);
    }
    Ok(())
}

fn client(remote: SocketAddr, ca: &str, cert: &str, key: &str, seconds: u64, synthetic: bool, playback_library: Option<&str>, modes: PlayModes) -> Result<()> {
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
    let mut audio_queue_full = 0u64;
    let mut recover_pending = 0u64;
    let mut recover_started = 0u64;
    let mut native_stale = 0u64;
    let mut denied_reanchor = 0u64;
    let mut ingress_frontier = ReceiveFrontier::default();
    let mut session_context: Option<SourceContext> = None;
    let mut anchor_pending: Option<PendingAnchor> = None;
    let mut fresh_anchor_nonce = 1u64;
    let mut last_handled_invalidation = 0u64;
    let mut confirmed_reanchors = 0u64;
    let mut audio: Option<AndroidAudio> = None;
    let mut latest_render_record: Option<(deskfuse_quiche_lab::android_audio::RenderSnapshot, u64)> = None;
    let mut coherent_records_read = 0u64;
    let mut source_gap_frames = 0u64;
    let mut source_concealed_frames = 0u64;
    let mut source_unconcealed_frames = 0u64;
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
            if let Some(library) = playback_library {
                let mut renderer = AndroidAudio::open(library)
                    .map_err(|e| format!("Android speaker renderer initialization failed: {e}"))?;
                renderer.set_diagnostic_modes(modes.fades, modes.linear)
                    .map_err(|e| format!("Android diagnostic mode configuration failed: {e}"))?;
                audio = Some(renderer);
                println!("AUDIO Android: native AAudio output started, stereo PCM16 48000 Hz; modes fades={} concealment={} linear_asrc={}",
                         modes.fades, modes.conceal, modes.linear);
            }
        }
        if authenticated_at.is_some() {
            for _ in 0..128 {
                match conn.dgram_recv(&mut datagram) {
                    Ok(n) => {
                        if let Some(reply) = AnchorMessage::decode(&datagram[..n]) {
                            if reply.kind != fresh_anchor::ANCHOR {
                                return Err("Android rejected unexpected server recovery request".into());
                            }
                            if let Some(ref mut pending) = anchor_pending {
                                if reply.nonce == pending.challenge.nonce &&
                                    reply.context == pending.challenge.context &&
                                    reply.frame >= pending.challenge.frame {
                                    pending.confirmed = Some(reply.frame);
                                    eprintln!("TRACE Android ANCHOR_CONFIRMED nonce={} source={}",
                                        reply.nonce, reply.frame);
                                } // retired challenge responses are never fresh.
                            }
                            continue;
                        }
                        let (h, _) = parse(&datagram[..n], &Configuration::default())
                            .map_err(|e| format!("Android rejected PCM header: {e:?}"))?;
                        if n != APP_BYTES || (synthetic && datagram[..n] != packet(h.packet_sequence)) {
                            return Err("Android rejected PCM source-frame or synthetic payload identity".into());
                        }
                        if let Some(ref pending) = anchor_pending {
                            let fresh = pending.confirmed.is_some_and(|anchor| {
                                h.first_source_frame >= anchor &&
                                (SourceContext {
                                    epoch:h.media_epoch, stream:h.source_stream_id,
                                    format:h.format_config_id,
                                }) == pending.challenge.context
                            });
                            let applied = audio.as_ref().is_some_and(|speaker|
                                speaker.recovery_applied() >= pending.recovery_ticket);
                            if !fresh || !applied {
                                // No old or merely future-looking packet
                                // becomes fresh without a matching server
                                // post-request capture anchor.
                                continue;
                            }
                            // Advance the irreversible source frontier
                            // to the server's *post-request* fresh observation.
                            // Old dropped playback intervals must never be
                            // synthesized back into the newly primed ring.
                            ingress_frontier.accept_fresh_anchor(
                                pending.confirmed.expect("fresh and applied above")
                            ).map_err(|e|
                                format!("recovery anchor rolled source frontier backward: {e:?}"))?;
                            anchor_pending = None;
                            confirmed_reanchors += 1;
                        }
                        // QUIC peer authentication alone does not revive a
                        // retired stream epoch or a previously committed
                        // source-frame interval. This irreversible frontier
                        // outlives every local AAudio re-prime.
                        let accepted = match ingress_frontier.ingest(
                            SourceContext {
                                epoch: h.media_epoch,
                                stream: h.source_stream_id,
                                format: h.format_config_id,
                            },
                            h.packet_sequence, h.first_source_frame, AUDIO_FRAMES,
                        ) {
                            Ok(v) => v,
                            Err(SourceReject::Late) => { late += 1; continue; }
                            Err(SourceReject::Overlapping) => {
                                native_stale += 1; continue;
                            }
                            Err(SourceReject::RetiredContext) => {
                                denied_reanchor += 1;
                                return Err("retired media identity requires a new authenticated configuration".into());
                            }
                            Err(SourceReject::Overflow) =>
                                return Err("authenticated source-frame range overflow".into()),
                        };
                        session_context.get_or_insert(SourceContext {
                            epoch:h.media_epoch, stream:h.source_stream_id,
                            format:h.format_config_id,
                        });
                        gaps += accepted.missing_sequences;
                        source_gap_frames += accepted.missing_frames;
                        let expected = accepted.expected_source;
                        let missing = accepted.missing_frames;
                        if missing != 0 {
                                eprintln!("TRACE Android SOURCE_GAP epoch={} sequence={} first_source_frame={} gap_frames={} elapsed_ms={}",
                                    h.media_epoch, h.packet_sequence, h.first_source_frame,
                                    missing,
                                    authenticated_at.map(|t| t.elapsed().as_millis()).unwrap_or(0));
                                if let Some(ref mut speaker) = audio {
                                    if modes.conceal && missing <= MAX_CONCEALMENT_FRAMES && missing % AUDIO_FRAMES == 0 {
                                        // A genuine 5- or 10-ms media hole. Preserve
                                        // output position with bounded zero-valued PCM
                                        // BEFORE the next valid source interval.
                                        const ZERO_PCM: [u8; PAYLOAD_BYTES] = [0; PAYLOAD_BYTES];
                                        let mut complete_concealment = true;
                                        for offset in 0..(missing / AUDIO_FRAMES) {
                                            let state = speaker.push_media(
                                                &ZERO_PCM,
                                                expected + offset * AUDIO_FRAMES,
                                                h.media_epoch, h.source_stream_id,
                                                h.format_config_id, 1, true)
                                                .map_err(|e| format!("Android concealment: {e}"))?;
                                            match state {
                                                Admission::Accepted => {
                                                    source_concealed_frames += AUDIO_FRAMES;
                                                }
                                                _ => {
                                                    complete_concealment = false;
                                                    source_unconcealed_frames +=
                                                        missing - offset * AUDIO_FRAMES;
                                                    begin_fresh_anchor(
                                                        &mut anchor_pending,
                                                        &mut fresh_anchor_nonce, speaker,
                                                        SourceContext {
                                                            epoch:h.media_epoch,
                                                            stream:h.source_stream_id,
                                                            format:h.format_config_id,
                                                        },
                                                        ingress_frontier.floor(),
                                                    )?;
                                                    recover_started += 1;
                                                    break;
                                                }
                                            }
                                        }
                                        if !complete_concealment {
                                            // The lost packet still advances
                                            // the authenticated irreversible
                                            // reception frontier. Never
                                            // re-insert this range on retry.
                                            first_sequence.get_or_insert(h.packet_sequence);
                                            last_sequence = Some(h.packet_sequence);
                                            received += 1;
                                            continue;
                                        }
                                    } else {
                                        // A long gap cannot be backfilled by increasing
                                        // latency beyond the fixed phone ring capacity.
                                        // Account for it; later renderer generation and
                                        // drift recovery will handle this explicitly.
                                        source_unconcealed_frames += missing;
                                        begin_fresh_anchor(
                                            &mut anchor_pending, &mut fresh_anchor_nonce,
                                            speaker,
                                            SourceContext {
                                                epoch:h.media_epoch,
                                                stream:h.source_stream_id,
                                                format:h.format_config_id,
                                            },
                                            ingress_frontier.floor(),
                                        )?;
                                        recover_started += 1;
                                        // The current packet establishes a
                                        // forward AUTHENTICATED source cursor,
                                        // but is NOT offered into retired FIFO.
                                        // Only subsequent future packets may
                                        // re-anchor after callback recovery.
                                        first_sequence.get_or_insert(h.packet_sequence);
                                        last_sequence = Some(h.packet_sequence);
                                        received += 1;
                                        continue;
                                    }
                                }
                            }
                        first_sequence.get_or_insert(h.packet_sequence);
                        last_sequence = Some(h.packet_sequence);
                        received += 1;
                        if let Some(ref mut speaker) = audio {
                            // PCM is verified, authenticated, and source-monotonic
                            // BEFORE publication to the AAudio callback ring.
                            let samples = &datagram[HEADER_BYTES..n];
                            match speaker.push_media(
                                samples, h.first_source_frame,
                                h.media_epoch, h.source_stream_id,
                                h.format_config_id, 1, false)
                                .map_err(|e| format!("Android audio renderer: {e}"))? {
                                Admission::Accepted => {}
                                Admission::Overflow |
                                Admission::Discontinuity => {
                                    audio_queue_full += 1;
                                    recover_started += 1;
                                    begin_fresh_anchor(
                                        &mut anchor_pending, &mut fresh_anchor_nonce,
                                        speaker,
                                        SourceContext {
                                            epoch:h.media_epoch, stream:h.source_stream_id,
                                            format:h.format_config_id,
                                        },
                                        ingress_frontier.floor(),
                                    )?;
                                }
                                Admission::RecoverPending => {
                                    recover_pending += 1;
                                    begin_fresh_anchor(
                                        &mut anchor_pending, &mut fresh_anchor_nonce,
                                        speaker,
                                        SourceContext {
                                            epoch:h.media_epoch, stream:h.source_stream_id,
                                            format:h.format_config_id,
                                        },
                                        ingress_frontier.floor(),
                                    )?;
                                }
                                Admission::Stale => { native_stale += 1; }
                            }
                        }
                        if received % 200 == 0 {
                            println!("ANDROID PCM received={} last_seq={} source_frame={} missing_before={} stale={} elapsed_ms={}",
                                received, h.packet_sequence, h.first_source_frame,
                                gaps, late,
                                authenticated_at.map(|t| t.elapsed().as_millis()).unwrap_or(0));
                            if let Some(ref speaker) = audio {
                                let a = speaker.snapshot()
                                    .map_err(|e| format!("AAudio periodic diagnostics: {e}"))?;
                                if let Some((record, dropped)) = latest_render_record {
                                    println!("TRACE Android RENDER committed_source={} support={} phase_q32={} epoch={} stream={} format={} connection={} playout={} released={} discarded={} concealed_output={} callbacks_record={} dropped_callback_records={} queued_frames={} drift_ppm={} underruns={} rebuffer={} silence_frames={} queue_full={}",
                                        record.last_source, record.last_support_source,
                                        record.fraction_q32, record.epoch, record.stream, record.format,
                                        record.connection, record.playout,
                                        record.released, record.discarded, record.concealed_outputs,
                                        record.callback_seq, dropped, a.queued_frames,
                                        record.drift_ppm, record.underruns, record.reprimes,
                                        record.silence, audio_queue_full);
                                }
                            }
                        }
                    }
                    Err(quiche::Error::Done) => break,
                    Err(e) => return Err(format!("Android DATAGRAM receive: {e:?}").into()),
                }
            }
        }
        // Consumer of coherent callback snapshots is the QUIC owner, not
        // the Android real-time thread. Bound draining work and retain only
        // the most recent complete callback record for diagnostics.
        let mut callback_recovery_needed = false;
        if let Some(ref speaker) = audio {
            for _ in 0..16 {
                match speaker.pop_snapshot()
                    .map_err(|e| format!("AAudio off-callback telemetry: {e}"))? {
                    Some((record, dropped)) => {
                        if record.invalidations > last_handled_invalidation {
                            last_handled_invalidation = record.invalidations;
                            callback_recovery_needed = true;
                        }
                        latest_render_record = Some((record, dropped));
                        coherent_records_read += 1;
                    }
                    None => break,
                }
            }
        }
        if callback_recovery_needed && anchor_pending.is_none() &&
            authenticated_at.is_some_and(|at|
                at.elapsed() < Duration::from_secs(seconds.saturating_sub(1))) {
            if let (Some(context), Some(ref mut speaker)) = (session_context, audio.as_mut()) {
                begin_fresh_anchor(
                    &mut anchor_pending, &mut fresh_anchor_nonce, speaker,
                    context, ingress_frontier.floor(),
                )?;
                recover_started += 1;
            }
        }
        if let Some(ref mut pending) = anchor_pending {
            let now = Instant::now();
            if now.duration_since(pending.started) >=
                Duration::from_millis(fresh_anchor::TIMEOUT_MS) {
                return Err(format!(
                    "authenticated post-recovery source anchor timed out nonce={} floor={}",
                    pending.challenge.nonce, pending.challenge.frame
                ).into());
            }
            if pending.confirmed.is_none() && now >= pending.retry_at {
                match conn.dgram_send(&pending.challenge.encode()) {
                    Ok(()) | Err(quiche::Error::Done) => {}
                    Err(e) => return Err(format!("Android source anchor request: {e:?}").into()),
                }
                pending.retry_at = now + Duration::from_millis(fresh_anchor::RETRY_MS);
            }
        }
        egress.service(&udp, &mut conn, false)?;
        if let Some(at) = authenticated_at {
            if at.elapsed() >= Duration::from_secs(seconds + 2) {
                let stats = conn.stats();
                println!(
                    "DONE Android sink received={} first_seq={:?} last_seq={:?} missing_between={} source_missing_frames={} concealed_frames={} unfilled_frames={} stale={} udp_packets={} quic_lost={} mode={}",
                    received, first_sequence, last_sequence, gaps, source_gap_frames,
                    source_concealed_frames, source_unconcealed_frames,
                    late, egress.udp_packets_sent, stats.lost,
                    if audio.is_some() { "native-speaker" } else { "verify-only" }
                );
                if let Some(ref speaker) = audio {
                    let a = speaker.snapshot().map_err(|e| format!("AAudio stats: {e}"))?;
                    println!(
                        "AUDIO Android speaker accepted_frames={} rendered_frames={} silence_frames={} overflow_packets={} queue_full_from_receiver={} callbacks={} queued_frames={} stream_error={} format={}Hz/{}ch drift_ppm={} underrun_events={} reprime_events={}",
                        a.accepted_frames, a.rendered_frames, a.silence_frames,
                        a.overflow_packets, audio_queue_full, a.callback_count,
                        a.queued_frames, a.stream_error, a.actual_sample_rate, a.actual_channels,
                        a.drift_correction_ppm, a.underrun_events, a.reprime_events
                    );
                    if let Some((record, dropped)) = latest_render_record {
                        println!("AUDIO Android committed callback={} source={} support={} fraction_q32={} epoch={} stream={} format={} connection={} playout_generation={} committed_output={} released_source={} discarded_rebuffer={} concealed_output={} coherence_dropped={} coherence_read={}",
                            record.callback_seq, record.last_source,
                            record.last_support_source, record.fraction_q32,
                            record.epoch, record.stream, record.format, record.connection,
                            record.playout, record.committed_output, record.released,
                            record.discarded, record.concealed_outputs,
                            dropped, coherent_records_read);
                    }
                    println!("AUDIO Android authenticated_reanchors={} outstanding_anchor={} last_receiver_frontier={}",
                        confirmed_reanchors, anchor_pending.is_some(),
                        ingress_frontier.floor());
                    if a.stream_error != 0 { return Err(format!("AAudio stream failure code={}", a.stream_error).into()); }
                    if a.rendered_frames == 0 { return Err("AAudio played no authenticated PCM frames".into()); }
                }
                if received == 0 { return Err("no authenticated PCM DATAGRAMs received".into()); }
                return Ok(());
            }
        } else if started_waiting.elapsed() >= CONNECT_LIMIT {
            return Err("Android never authenticated Windows server".into());
        }

        let until = authenticated_at
            .map(|at| at + Duration::from_secs(seconds + 2))
            .unwrap_or(started_waiting + CONNECT_LIMIT);
        let recovery_wake = anchor_pending.as_ref().map(|pending| {
            let timeout = pending.started + Duration::from_millis(fresh_anchor::TIMEOUT_MS);
            if pending.confirmed.is_some() { timeout }
            else { timeout.min(pending.retry_at) }
        });
        let wake = next_deadline(
            next_deadline(conn.timeout_instant(), egress.deadline(), Some(until)),
            recovery_wake, None
        );
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
            server(bind.parse()?, allow_ip.parse()?, ca, cert, key, positive_seconds(secs)?, None)
        }
        [_, mode, remote, ca, cert, key, secs] if mode == "client" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, true, None, PlayModes::default())
        }
        [_, mode, bind, allow_ip, ca, cert, key, exe, device, secs] if mode == "server-wasapi" => {
            server(bind.parse()?, allow_ip.parse()?, ca, cert, key,
                   positive_seconds(secs)?, Some((exe, device)))
        }
        [_, mode, remote, ca, cert, key, secs] if mode == "client-capture" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, false, None, PlayModes::default())
        }
        [_, mode, remote, ca, cert, key, library, secs] if mode == "client-playback" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, false, Some(library), PlayModes::default())
        }
        [_, mode, remote, ca, cert, key, library, flags, secs] if mode == "client-playback-modes" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?,
                   false, Some(library), PlayModes::parse(flags)?)
        }
        _ => Err("usage: continuous-quic-lab server <bind:port> <allowed-ip> <ca> <server-cert> <server-key> <secs> | server-wasapi <bind:port> <allowed-ip> <ca> <server-cert> <server-key> <wasapi-capture.exe> <endpoint-id|default> <secs> | client|client-capture <windows-ip:port> <ca> <client-cert> <client-key> <secs> | client-playback <windows-ip:port> <ca> <client-cert> <client-key> <libdeskfuse_android_audio.so> <secs> | client-playback-modes <windows-ip:port> <ca> <client-cert> <client-key> <libdeskfuse_android_audio.so> <f1c1r1> <secs>".into()),
    }
}

fn main() {
    if let Err(e) = run() {
        eprintln!("FAIL continuous QUIC lab: {e}");
        std::process::exit(1);
    }
}

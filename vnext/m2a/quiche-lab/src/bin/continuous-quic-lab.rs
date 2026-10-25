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
use deskfuse_quiche_lab::wasapi_pipe::{self, Assembler, CaptureEvent};
use deskfuse_quiche_lab::android_audio::{Admission, AndroidAudio};
use deskfuse_quiche_lab::source_age::{self, AgeDecision};
use deskfuse_quiche_lab::plaintext_expiry::{PlaintextExpiry, QueueAdmission};
use deskfuse_quiche_lab::receive_frontier::{ReceiveFrontier, ContinuityWatch, Context as SourceContext, Reject as SourceReject};
use deskfuse_quiche_lab::deferred_trace::{WindowsTrace, Event as TraceEvent, FilterStage, AndroidTrace, AndroidFault, AndroidPeriodic};
use deskfuse_quiche_lab::android_audio::{AudioStats, PlayoutState};
use deskfuse_quiche_lab::fresh_anchor::{self, Message as AnchorMessage};
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
#[cfg(windows)]
#[path="../direct_sender.rs"]
mod direct_sender;
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
    deskfuse_quiche_lab::session_control::verify(conn.peer_cert())?;
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

#[derive(Clone, Copy)]
enum CaptureSource<'a> {
    Endpoint { exe:&'a str, endpoint:&'a str },
    DeskFuse { exe:&'a str, enrolled_peer:&'a str, certificate_der:&'a str },
}

fn require_enrolled_certificate(actual: Option<&[u8]>, expected: &[u8]) -> Result<()> {
    if expected.is_empty() || actual != Some(expected) {
        return Err("authenticated certificate does not match the enrolled endpoint owner".into());
    }
    Ok(())
}

fn server(bind: SocketAddr, allow_ip: IpAddr, ca: &str, cert: &str, key: &str, seconds: u64, capture: Option<CaptureSource<'_>>) -> Result<()> {
    let expected_peer = if let Some(CaptureSource::DeskFuse { enrolled_peer, certificate_der, .. }) = capture {
        use std::io::Read;
        deskfuse_quiche_lab::endpoint_lease::validate_peer_id(enrolled_peer)?;
        let mut bytes=Vec::new();
        std::fs::File::open(certificate_der)?.take(16*1024+1).read_to_end(&mut bytes)?;
        if bytes.is_empty() || bytes.len()>16*1024 || bytes[0]!=0x30 {
            return Err("expected enrolled certificate must be bounded DER, not PEM".into());
        }
        Some(bytes)
    } else { None };
    let mut cfg = config(ca, cert, key)?;
    let udp = socket(bind)?;
    let local = udp.local_addr()?;
    deskfuse_quiche_lab::session_control::register_socket(&udp)?;
    if deskfuse_quiche_lab::session_control::active() {
        use std::io::Write;
        println!("NATIVE_LISTEN {}",local.port());std::io::stdout().flush()?;
    }
    let end_waiting = Instant::now() + CONNECT_LIMIT;
    let mut conn: Option<quiche::Connection> = None;
    let mut peer: Option<SocketAddr> = None;
    let mut origin: Option<Instant> = None;
    let mut capture_started: Option<Instant> = None;
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
    let mut capture_completed=false; // retains acquisition/lease through transport retirement
    let mut assembler = Assembler::default();
    let mut source_chunks_too_old = 0u64;
    let mut last_capture_qpc = 0u64;
    // Observe what actually reaches the QUIC owner, before any new media
    // packet sequence is assigned. Distinguishes pre-sequence source holes
    // from packet loss and immutable ciphertext admission.
    let mut next_capture_frame:Option<u64> = None;
    let mut capture_source_gaps=0u64;
    let mut capture_source_missing_frames=0u64;
    let mut ingress_chunks=0u64;
    let mut assembled_blocks=0u64;
    let mut expired_source_qpc = 0u64;
    let mut invalid_source_qpc = 0u64;
    let mut future_source_qpc = 0u64;
    let mut provenance_saved_fragments = 0u64;
    let mut max_observed_source_age_100ns = 0u64;
    let mut previous_initial_qpc:Option<(u64,u64,u64,AgeDecision)> = None;
    let mut adjacent_240_qpc_pairs=0u64;
    let mut mixed_future_pairs=0u64;
    // One-shot pre-auth network diagnostics, no per-packet hot-path logging.
    let mut logged_allowed_initial = false;
    let mut logged_rejected_initial = false;
    let mut logged_quic_accept = false;

    println!("READY continuous Windows source {} ; allowed phone IP {} ; mutual TLS mandatory", local, allow_ip);
    let mut trace = WindowsTrace::default();
    let mut reader_summary: Option<Box<wasapi_pipe::ReaderSummary>> = None;
    let result = (|| -> Result<()> {
    loop {
        if deskfuse_quiche_lab::session_control::stopping() {return Ok(())}
        let now = Instant::now();
        if capture.is_some() && capture_started.is_none() &&
            origin.is_some_and(|start| start.elapsed() >= CONNECT_LIMIT) {
            return Err("native endpoint/capture startup deadline expired".into());
        }
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
            if origin.is_none() {
                if from.ip() == allow_ip && !logged_allowed_initial {
                    eprintln!("TRACE Windows PREAUTH_UDP_RX allowed_ip={} peer={} bytes={}", allow_ip, from, n);
                    logged_allowed_initial = true;
                } else if from.ip() != allow_ip && !logged_rejected_initial {
                    eprintln!("TRACE Windows PREAUTH_UDP_FILTERED source_ip={} expected_ip={}", from.ip(), allow_ip);
                    logged_rejected_initial = true;
                }
            }
            if from.ip() != allow_ip || peer.is_some_and(|p| p != from) { continue; }
            if conn.is_none() {
                let header = match quiche::Header::from_slice(&mut bytes[..n], quiche::MAX_CONN_ID_LEN) {
                    Ok(h) if h.version == quiche::PROTOCOL_VERSION => h,
                    _ => continue,
                };
                conn = Some(quiche::accept(&header.dcid, None, local, from, &mut cfg)?);
                peer = Some(from);
                if !logged_quic_accept {
                    eprintln!("TRACE Windows PREAUTH_QUIC_ACCEPT peer={}", from);
                    logged_quic_accept = true;
                }
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
                if let Some(capture) = capture {
                    if let Some(ref expected) = expected_peer {
                        require_enrolled_certificate(c.peer_cert(),expected)?;
                    }
                    println!("AUTH Windows: Android client verified; starting native WASAPI capture");
                    source = Some(match capture {
                        CaptureSource::Endpoint {exe,endpoint} => wasapi_pipe::spawn(exe,endpoint,seconds,local)?,
                        CaptureSource::DeskFuse {exe,enrolled_peer,..} =>
                            wasapi_pipe::spawn_deskfuse(exe,enrolled_peer,seconds,local)?,
                    });
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
            if let Some(receiver) = source.as_ref().filter(|_| !capture_completed) {
                // Real WASAPI events wake this QUIC owner via the local UDP socket.
                // Never synthesize a 5-ms send tick or use ACKs as an audio clock.
                for _ in 0..128 {
                    match receiver.try_recv() {
                        Ok(CaptureEvent::Started { at }) => {
                            if capture_started.replace(at).is_some() {
                                return Err("duplicate native source startup event".into());
                            }
                        }
                        Ok(CaptureEvent::Chunk(chunk)) => {
                            if !deskfuse_quiche_lab::session_control::media_allowed(){continue}
                            // Observe before ALL age/QPC filters, so a source
                            // ingress gap cannot be confused with a filter drop.
                            ingress_chunks += 1;
                            if let Some(expected) = next_capture_frame {
                                if chunk.device_frame != expected ||
                                    chunk.flags & 1 != 0 {
                                    capture_source_gaps += 1;
                                    if chunk.device_frame > expected {
                                        capture_source_missing_frames +=
                                            chunk.device_frame - expected;
                                    }
                                    trace.record(TraceEvent::Gap { expected: expected, actual: chunk.device_frame, frames: (chunk.frames) as u64, flags: chunk.flags, qpc: chunk.qpc_100ns, count: capture_source_gaps });
                                }
                            }
                            next_capture_frame = chunk.device_frame.checked_add(chunk.frames as u64);
                            trace.record(TraceEvent::Ingress { ordinal: ingress_chunks, frame: chunk.device_frame, frames: (chunk.frames) as u64, flags: chunk.flags, qpc: chunk.qpc_100ns, offset: chunk.original_packet_offset_frames });
                            if wasapi_pipe::queue_too_old(&chunk) {
                                previous_initial_qpc=None;
                                source_chunks_too_old += 1;
                                trace.record(TraceEvent::Filter { stage: FilterStage::ReaderResidency, frame: chunk.device_frame, frames: (chunk.frames) as u64, qpc: chunk.qpc_100ns, now: source_age::local_qpc_100ns()?, count: source_chunks_too_old, reason: AgeDecision::Expired, sequence: None });
                                continue;
                            }
                            // WASAPI capture QPC spans the *whole* native
                            // pipe and reader queues; enqueue residency alone
                            // is not a source-age bound. Fail closed on QPC
                            // timestamp errors or excessive age.
                            let qpc_now = source_age::local_qpc_100ns()?;
                            let ordinary_age =
                                source_age::classify(qpc_now, chunk.qpc_100ns);
                            let age = source_age::classify_wasapi(
                                qpc_now, chunk.qpc_100ns,
                                chunk.original_packet_offset_frames
                            );
                            if ordinary_age == AgeDecision::FutureTimestamp &&
                                matches!(age, AgeDecision::Fresh { .. }) {
                                // Counts EXACTLY the native-provenanced 5ms
                                // fragments that the former policy discarded.
                                provenance_saved_fragments += 1;
                                trace.record(TraceEvent::Proven { frame: chunk.device_frame, offset: chunk.original_packet_offset_frames, qpc: chunk.qpc_100ns, now: qpc_now, saved: provenance_saved_fragments });
                            }
                            // Pure diagnostics: an adjacent QPC pair may span
                            // a single GetBuffer packet. Match with the native
                            // original-packet CSV by first_device_frame before
                            // concluding both halves share an original packet.
                            // No waiting, reordering, filtering or PCM copies.
                            if let Some((prev_frame,prev_qpc,prev_now,prev_age))=previous_initial_qpc {
                                if prev_frame.checked_add(AUDIO_FRAMES)==Some(chunk.device_frame)
                                    && prev_qpc.checked_add(50_000)==Some(chunk.qpc_100ns) {
                                    adjacent_240_qpc_pairs+=1;
                                    let mixed=matches!(prev_age,AgeDecision::Fresh { .. })
                                        && matches!(age,AgeDecision::FutureTimestamp);
                                    if mixed {
                                        mixed_future_pairs+=1;
                                        trace.record(TraceEvent::Pair { first: prev_frame, second: chunk.device_frame, first_qpc: prev_qpc, second_qpc: chunk.qpc_100ns, first_now: prev_now, second_now: qpc_now, first_age: prev_age, second_age: age, count: mixed_future_pairs });
                                    }
                                }
                            }
                            previous_initial_qpc=Some((
                                chunk.device_frame,chunk.qpc_100ns,qpc_now,age
                            ));
                            match age {
                                AgeDecision::Fresh { age_100ns } => {
                                    max_observed_source_age_100ns =
                                        max_observed_source_age_100ns.max(age_100ns);
                                }
                                AgeDecision::MissingTimestamp => {
                                    invalid_source_qpc += 1;
                                    trace.record(TraceEvent::Filter { stage: FilterStage::InitialMissing, frame: chunk.device_frame, frames: (chunk.frames) as u64, qpc: chunk.qpc_100ns, now: qpc_now, count: invalid_source_qpc, reason: AgeDecision::MissingTimestamp, sequence: None });
                                    continue;
                                }
                                AgeDecision::FutureTimestamp => {
                                    future_source_qpc += 1;
                                    trace.record(TraceEvent::Filter { stage: FilterStage::InitialFuture, frame: chunk.device_frame, frames: (chunk.frames) as u64, qpc: chunk.qpc_100ns, now: qpc_now, count: future_source_qpc, reason: AgeDecision::FutureTimestamp, sequence: None });
                                    continue;
                                }
                                AgeDecision::Expired => {
                                    expired_source_qpc += 1;
                                    trace.record(TraceEvent::Filter { stage: FilterStage::InitialExpired, frame: chunk.device_frame, frames: (chunk.frames) as u64, qpc: chunk.qpc_100ns, now: qpc_now, count: expired_source_qpc, reason: AgeDecision::Expired, sequence: None });
                                    continue;
                                }
                            }
                            if let Some(block) = assembler.push(&chunk) {
                                // Re-evaluate the ORIGINAL first-frame capture
                                // QPC immediately before application offer.
                                // quiche may still queue this plaintext before
                                // later sealing; that limitation is tracked.
                                assembled_blocks += 1;
                                trace.record(TraceEvent::Assembled { ordinal: assembled_blocks, frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, offset: block.original_packet_offset_frames });
                                let assembled_now_qpc=source_age::local_qpc_100ns()?;
                                match source_age::classify_wasapi(
                                    assembled_now_qpc,
                                    block.first_qpc_100ns,
                                    block.original_packet_offset_frames,
                                ) {
                                    AgeDecision::Fresh { age_100ns } => {
                                        max_observed_source_age_100ns =
                                            max_observed_source_age_100ns.max(age_100ns);
                                    }
                                    AgeDecision::MissingTimestamp => {
                                        invalid_source_qpc += 1;
                                        trace.record(TraceEvent::Filter { stage: FilterStage::AssembledMissing, frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, now: assembled_now_qpc, count: invalid_source_qpc, reason: AgeDecision::MissingTimestamp, sequence: None });
                                        continue;
                                    }
                                    AgeDecision::FutureTimestamp => {
                                        future_source_qpc += 1;
                                        trace.record(TraceEvent::Filter { stage: FilterStage::AssembledFuture, frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, now: assembled_now_qpc, count: future_source_qpc, reason: AgeDecision::FutureTimestamp, sequence: None });
                                        continue;
                                    }
                                    AgeDecision::Expired => {
                                        expired_source_qpc += 1;
                                        trace.record(TraceEvent::Filter { stage: FilterStage::AssembledExpired, frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, now: assembled_now_qpc, count: expired_source_qpc, reason: AgeDecision::Expired, sequence: None });
                                        continue;
                                    }
                                }
                                if let Some((req, request_qpc)) = pending_capture_anchor {
                                    if block.first_device_frame >= req.frame &&
                                        fresh_anchor::after_request_capture_qpc(
                                            request_qpc, block.first_qpc_100ns,
                                            block.original_packet_offset_frames) {
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
                                let assigned_sequence=next_sequence as u32;
                                trace.record(TraceEvent::Sequence { sequence: assigned_sequence, frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns });
                                let pcm = captured_packet(assigned_sequence, block.first_device_frame, &block.pcm);
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
                                let offer_qpc_now=source_age::local_qpc_100ns()?;
                                let offer_age=source_age::classify_wasapi(
                                    offer_qpc_now,block.first_qpc_100ns,
                                    block.original_packet_offset_frames
                                );
                                if !matches!(offer_age,AgeDecision::Fresh { .. }) {
                                    expired_source_qpc += 1;
                                    trace.record(TraceEvent::Filter { stage: FilterStage::AfterSequence, reason: offer_age, sequence: Some(assigned_sequence), frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, now: offer_qpc_now, count: expired_source_qpc });
                                    continue;
                                }
                                let admit_now_qpc = source_age::local_qpc_100ns()?;
                                match outstanding.admit(
                                    c, &pcm, block.first_qpc_100ns,
                                    block.original_packet_offset_frames, admit_now_qpc
                                ) {
                                    Ok(QueueAdmission::Queued) => {
                                        offered += 1;
                                        trace.record(TraceEvent::Admitted { sequence: assigned_sequence, frame: block.first_device_frame, qpc: block.first_qpc_100ns, count: offered });
                                        source_egress(&mut egress, &udp, c, false, &mut outstanding, capture.is_some())?;
                                    }
                                    Ok(QueueAdmission::Expired) => {
                                        expired_source_qpc += 1;
                                        trace.record(TraceEvent::Filter { stage: FilterStage::AdmitExpired, reason: source_age::classify_wasapi(admit_now_qpc,block.first_qpc_100ns,
                                                    block.original_packet_offset_frames), sequence: Some(assigned_sequence), frame: block.first_device_frame, frames: (AUDIO_FRAMES) as u64, qpc: block.first_qpc_100ns, now: admit_now_qpc, count: expired_source_qpc });
                                    }
                                    Ok(QueueAdmission::Full) => {
                                        not_queued += 1;
                                        // Source frame is the cross-machine key;
                                        // clocks are NOT assumed synchronized.
                                        trace.record(TraceEvent::QueueDrop { sequence: assigned_sequence, frame: block.first_device_frame, qpc: block.first_qpc_100ns, count: not_queued, age_max: max_observed_source_age_100ns });
                                    }
                                    Err(e) => return Err(format!("captured PCM QUIC enqueue: {e:?}").into()),
                                }
                            }
                        }
                        Ok(CaptureEvent::Finished { outcome, summary }) => {
                            reader_summary = Some(summary);
                            if let Err(reason) = outcome {
                                return Err(format!("native WASAPI source failed: {reason}").into());
                            }
                            // Successful native exit before the requested
                            // acquisition interval is not a healthy transport.
                            if capture_started.is_none_or(|start| start.elapsed() < run_for) {
                                return Err("native WASAPI capture ended before requested duration".into());
                            }
                            capture_completed = true;
                            break;
                        }
                        Err(TryRecvError::Empty) => break,
                        Err(TryRecvError::Disconnected) => {
                            return Err("native WASAPI reader disconnected without completion status".into());
                        },
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
            // Completed acquisition retains its lease until the cleanup barrier below.
            // QUIC and this kernel socket have exactly one egress owner.
            source_egress(&mut egress, &udp, c, false, &mut outstanding, capture.is_some())?;
            if let Some(start) = if capture.is_some() { capture_started } else { origin } {
                // Stop source production at run_for, but leave the authenticated
                // QUIC socket open long enough for final sealed packets and
                // receiver ACK/control work; this is NOT new PCM pacing.
                if start.elapsed() >= run_for + Duration::from_secs(3) {
                    // A successful session must observe native completion,
                    // not merely the absence of further PCM or an idle socket.
                    if capture.is_some() && source.is_some() {
                        return Err("native WASAPI capture missing terminal completion at end of run".into());
                    }
                    let stats = c.stats();
                    if capture.is_some() {
                        println!(
                            "DONE Windows WASAPI source duration={}s real_pcm_blocks_queued={} quiche_queue_full={} source_queue_old={} qpc_expired={} qpc_invalid={} qpc_future={} qpc_proven_fragments={} qpc_max_age_100ns={} quiche_plaintext_expired={} quiche_plaintext_orphans={} quiche_plaintext_absent={} quiche_identity_high_water={} anchor_replies={} capture_discontinuities={} pre_quic_source_gaps={} pre_quic_missing_frames={} ingress_chunks={} assembled_blocks={} adjacent_240_qpc_pairs={} mixed_future_pairs={} last_wasapi_qpc_100ns={} udp_packets={} udp_would_block={} quic_dgram_sent={} quic_lost={}",
                            seconds, offered, not_queued, source_chunks_too_old,
                            expired_source_qpc, invalid_source_qpc, future_source_qpc,
                            provenance_saved_fragments,max_observed_source_age_100ns,
                            outstanding.expired, outstanding.orphaned,
                            outstanding.absent, outstanding.high_water,
                            anchor_replies_sent, assembler.discontinuities,
                            capture_source_gaps,capture_source_missing_frames,
                            ingress_chunks,assembled_blocks,adjacent_240_qpc_pairs,
                            mixed_future_pairs,last_capture_qpc, egress.udp_packets_sent,
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
        // Plaintext cleanup is an independent one-shot obligation, including
        // when an immutable sealed packet is waiting for socket writability.
        // Capture the Instant BEFORE sampling QPC so sampling overhead cannot
        // push the intended cleanup deadline later. Recheck on every wake.
        let expiry_wake = if capture.is_some() && outstanding.len() != 0 {
            let observed = Instant::now();
            outstanding.next_expiry_wait(source_age::local_qpc_100ns()?)
                .and_then(|wait| observed.checked_add(wait))
        } else { None };
        let wake = deadline(&[
            next_deadline(quic_wake, egress.deadline(), source_wake),
            expiry_wake,
            if origin.is_none() { Some(end_waiting) }
            else if capture.is_some() && capture_started.is_none() { origin.and_then(|x| x.checked_add(CONNECT_LIMIT)) }
            else { (if capture.is_some() {capture_started} else {origin})
                .and_then(|x| x.checked_add(run_for + Duration::from_secs(3))) },
        ]);
        let writable = egress.needs_write_readiness();
        let ready = wait_udp(&udp, writable, wake)?;
        if ready.writable {
            if let Some(c) = conn.as_mut() {
                source_egress(&mut egress, &udp, c, true, &mut outstanding, capture.is_some())?;
            }
        }
    }
    })();
    // The reactor is quiescent. No diagnostic writer is reachable from the
    // live owner/reader paths above. Cancel/join the child before its worker
    // releases the endpoint lease. Report missing summary honestly.
    let incomplete_reader = source.is_some() && reader_summary.is_none();
    let cleanup_result = source.as_mut().map(|source| source.stop()).transpose();
    outstanding.clear();drop(egress);drop(conn);
    drop(source.take()); // endpoint lease is last, after acquisition and QUIC
    let flush_result = (|| -> io::Result<()> {
        let mut out = io::stderr().lock();
        trace.flush(&mut out)?;
        if let Some(summary) = reader_summary { summary.flush(&mut out)?; }
        else if incomplete_reader {
            use std::io::Write;
            writeln!(out,"TRACE Windows reader_summary_unavailable=true native_cleanup_confirmed={}",cleanup_result.is_ok())?;
        }
        Ok(())
    })();
    // Never replace the original media/transport failure with a log error.
    result?;
    cleanup_result?;
    flush_result.map_err(|e| format!("deferred diagnostic output failed: {e}").into())
}

#[derive(Copy, Clone)]
struct PlayModes {
    buffer_bursts:u32,
    sharing_policy:u32, usage_policy:u32, reserve_frames:u32,
    fades: bool,
    conceal: bool,
    linear: bool,
    strict: bool,
}
impl Default for PlayModes {
    fn default() -> Self {
        Self { buffer_bursts:0, sharing_policy:0,usage_policy:0,reserve_frames:1440, fades: true, conceal: true, linear: true, strict: false }
    }
}
impl PlayModes {
    fn strict() -> Self {
        Self { buffer_bursts:0, sharing_policy:0,usage_policy:0,reserve_frames:1440, fades: false, conceal: false, linear: false, strict: true }
    }
    // Exact, deliberately boring mode specification: f1c0r1
    // f: fade; c: short-gap concealment; r: adaptive bandlimited resampling.
    fn parse(flags: &str) -> Result<Self> {
        let x = flags.as_bytes();
        if x.len() != 6 || x[0] != b'f' || x[2] != b'c' || x[4] != b'r'
           || ![x[1], x[3], x[5]].iter().all(|b| *b == b'0' || *b == b'1')
        {
            return Err("diagnostic modes must be exactly f0c0r0 .. f1c1r1".into());
        }
        Ok(Self {
            buffer_bursts:0,
            sharing_policy:0,usage_policy:0,reserve_frames:1440,
            fades: x[1] == b'1',
            conceal: x[3] == b'1',
            linear: x[5] == b'1',
            strict: false,
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

impl PendingAnchor {
    fn confirm(&mut self, reply: AnchorMessage, now: Instant) -> Result<bool> {
        fresh_anchor::require_live(self.started, now)?;
        if !reply.confirms(&self.challenge) { return Ok(false); }
        self.confirmed = Some(reply.frame);
        Ok(true)
    }
    fn authorizes(&self, context: SourceContext, frame: u64,
                  applied: u64, now: Instant) -> Result<bool> {
        fresh_anchor::require_live(self.started, now)?;
        Ok(context == self.challenge.context &&
           self.confirmed.is_some_and(|anchor| frame >= anchor) &&
           applied == self.recovery_ticket)
    }
}

fn begin_fresh_anchor(
    pending: &mut Option<PendingAnchor>,
    next_nonce: &mut u64,
    renderer: &mut AndroidAudio,
    context: SourceContext,
    rejection_frontier: u64,
    diagnostics: &mut AndroidTrace,
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
        diagnostics.fault(AndroidFault::AnchorRequest {nonce,floor:rejection_frontier});
    }
    Ok(())
}

#[derive(Default)]
struct NativeObservation {
    headroom:Option<deskfuse_quiche_lab::reorder::Headroom>,
    queued_storage_frames:u64,
}
#[derive(Default)]
struct ClientMedia {
    first_sequence:Option<u32>, last_sequence:Option<u32>,
    received:u64, gaps:u64, late:u64, audio_queue_full:u64,
    recover_pending:u64, recover_started:u64, native_stale:u64,
    denied_reanchor:u64, ingress_frontier:ReceiveFrontier,
    session_context:Option<SourceContext>, anchor_pending:Option<PendingAnchor>,
    fresh_anchor_nonce:u64, authorized_recovery:u64, confirmed_reanchors:u64,
    audio:Option<AndroidAudio>,
    diagnostics:AndroidTrace,
    callback_metrics:Option<deskfuse_quiche_lab::android_audio::CallbackMetricsV8>,
    latest_render_record:Option<(deskfuse_quiche_lab::android_audio::RenderSnapshot,u64)>,
    coherent_records_read:u64,
    strict_baseline:Option<(deskfuse_quiche_lab::android_audio::RenderSnapshot,u64)>,
    continuity_watch:ContinuityWatch,
    source_gap_frames:u64, source_concealed_frames:u64, source_unconcealed_frames:u64,
    reorder:deskfuse_quiche_lab::reorder::Reorder,
}
impl ClientMedia {
    fn stage_media(&mut self,h:Header,wire:&[u8],synthetic:bool,
                   playback_library:Option<&str>,modes:PlayModes,
                   authenticated_at:Option<Instant>)->Result<()> {
        let observed=self.native_observation()?;
        self.stage_media_observed(h,wire,synthetic,playback_library,modes,authenticated_at,observed)
    }
    fn stage_media_observed(&mut self,h:Header,wire:&[u8],synthetic:bool,
                   playback_library:Option<&str>,modes:PlayModes,
                   authenticated_at:Option<Instant>,observed:NativeObservation)->Result<()> {
        let previous=self.ingress_frontier.last_sequence();
        let expected=previous.map(|_|self.ingress_frontier.floor());
        if previous.is_some_and(|seq|h.packet_sequence<=seq) ||
           expected.is_some_and(|floor|h.first_source_frame<floor) {
            if modes.strict {return Err("STRICT PCM violation: retired staged source identity".into())}
            if previous.is_some_and(|seq|h.packet_sequence<=seq) {self.late+=1}
            else {self.native_stale+=1}
            return Ok(());
        }
        if self.anchor_pending.is_some() {
            // The authenticated challenge must qualify a new source anchor
            // before old-frontier distance becomes a playout budget decision.
            self.reorder.clear();
            return self.handle_media(wire,synthetic,playback_library,modes,authenticated_at);
        }
        // Coherent callback state can legitimately be unavailable during a
        // callback. Capacity uses the always-available physical occupancy
        // upper bound, including FIR history; absence is not exhaustion.
        if !self.reorder.fits_budget(h,expected,observed.queued_storage_frames) {
            self.reorder.budget_drops+=1;
            if modes.strict {return Err("STRICT PCM violation: combined playout budget exhausted".into())}
            if let Some(speaker)=self.audio.as_mut() {
                begin_fresh_anchor(&mut self.anchor_pending,&mut self.fresh_anchor_nonce,
                    speaker,SourceContext{epoch:h.media_epoch,stream:h.source_stream_id,
                                         format:h.format_config_id},self.ingress_frontier.floor(),
                    &mut self.diagnostics)?;
                self.recover_started+=1;
            }
            self.reorder.clear();
            return Ok(());
        }
        if !self.reorder.push(h,wire) && modes.strict {
            return Err("STRICT PCM failure: duplicate or full reorder staging".into());
        }
        self.drain_ready(false,synthetic,playback_library,modes,authenticated_at)
    }
    fn headroom(&self)->Result<Option<deskfuse_quiche_lab::reorder::Headroom>> {
        Ok(self.native_observation()?.headroom)
    }
    fn native_observation(&self)->Result<NativeObservation> {
        let Some(speaker)=self.audio.as_ref() else {return Ok(NativeObservation::default())};
        // This legacy atomic status remains available even when a callback
        // makes the newer coherent cursor snapshot temporarily unavailable.
        let stats=speaker.snapshot().map_err(|e|format!("AAudio status: {e}"))?;
        if stats.stream_error!=0 {return Err(format!("AAudio stream failed: code={}",stats.stream_error).into())}
        let state=speaker.playout_state().map_err(|e| format!("AAudio playout state: {e}"))?;
        let fallback=NativeObservation {headroom:None,queued_storage_frames:stats.queued_frames};
        let Some(state)=state else {return Ok(fallback)};
        if state.stream_error!=0 {return Err(format!("AAudio stream failed: code={}",state.stream_error).into())}
        // Recovery tickets remain the existing authoritative atomic API.
        if speaker.recovery_applied()!=self.authorized_recovery {return Ok(fallback)}
        Ok(NativeObservation {queued_storage_frames:stats.queued_frames,
            headroom:Some(deskfuse_quiche_lab::reorder::Headroom {
            primed:state.primed!=0,queued_frames:state.queued_playable_frames,
            callback_frames:state.max_callback_frames,
        })})
    }
    fn drain_ready(&mut self, commit_hole:bool, synthetic:bool,
                   playback_library:Option<&str>, modes:PlayModes,
                   authenticated_at:Option<Instant>)->Result<()> {
        for _ in 0..deskfuse_quiche_lab::reorder::CAPACITY {
            let expected=if self.anchor_pending.is_some(){None}
                else{self.ingress_frontier.last_sequence().map(|_|self.ingress_frontier.floor())};
            let headroom=self.headroom()?;
            let Some(media)=self.reorder.pop(expected,Instant::now(),headroom,commit_hole)
                else {break};
            let before=self.anchor_pending.as_ref().map(|p|p.recovery_ticket);
            let authorized=self.authorized_recovery;
            self.handle_media(&media.bytes,synthetic,playback_library,modes,authenticated_at)?;
            if self.anchor_pending.as_ref().map(|p|p.recovery_ticket)!=before ||
               self.authorized_recovery!=authorized {
                self.reorder.clear();
                break;
            }
        }
        Ok(())
    }
    fn handle_media(&mut self,wire:&[u8],synthetic:bool,
                    playback_library:Option<&str>,modes:PlayModes,
                    authenticated_at:Option<Instant>)->Result<()> {
        let n=wire.len();
        let (h,_)=parse(wire,&Configuration::default())
            .map_err(|e|format!("Android rejected staged PCM header: {e:?}"))?;
        if n!=APP_BYTES || (synthetic && wire!=packet(h.packet_sequence)) {
            return Err("Android rejected staged PCM payload identity".into());
        }
        if self.anchor_pending.is_none() {
            if let Some(ref mut speaker) = self.audio {
                if speaker.recovery_applied() != self.authorized_recovery {
                    begin_fresh_anchor(
                        &mut self.anchor_pending, &mut self.fresh_anchor_nonce, speaker,
                        SourceContext { epoch:h.media_epoch, stream:h.source_stream_id,
                                        format:h.format_config_id },
                        self.ingress_frontier.floor(),
                        &mut self.diagnostics,
                    )?;
                    self.recover_started += 1;
                }
            }
        }
        if let Some(ref pending) = self.anchor_pending {
            let applied = self.audio.as_ref().map_or(0, |speaker|
                speaker.recovery_applied());
            if !pending.authorizes(SourceContext {
                epoch:h.media_epoch, stream:h.source_stream_id,
                format:h.format_config_id,
            }, h.first_source_frame, applied, Instant::now())? {
                // No old or merely future-looking packet
                // becomes fresh without a matching server
                // post-request capture anchor.
                return Ok(());
            }
            // Advance the irreversible source frontier
            // to the server's *post-request* fresh observation.
            // Old dropped playback intervals must never be
            // synthesized back into the newly primed ring.
            fresh_anchor::require_live(pending.started, Instant::now())
                .map_err(|e| format!("recovery authorization: {e}"))?;
            self.authorized_recovery = pending.recovery_ticket;
            self.ingress_frontier.accept_fresh_anchor(
                pending.confirmed.expect("fresh and applied above")
            ).map_err(|e|
                format!("recovery anchor rolled source frontier backward: {e:?}"))?;
            self.anchor_pending = None;
            self.confirmed_reanchors += 1;
        }
        // QUIC peer authentication alone does not revive a
        // retired stream epoch or a previously committed
        // source-frame interval. This irreversible frontier
        // outlives every local AAudio re-prime.
        let previous_source_sequence = self.ingress_frontier.last_sequence();
        let accepted = match self.ingress_frontier.ingest(
            SourceContext {
                epoch: h.media_epoch,
                stream: h.source_stream_id,
                format: h.format_config_id,
            },
            h.packet_sequence, h.first_source_frame, AUDIO_FRAMES,
        ) {
            Ok(v) => v,
            Err(SourceReject::Late) => {
                if modes.strict { return Err("STRICT PCM violation: duplicate or reordered packet".into()); }
                self.late += 1; return Ok(());
            }
            Err(SourceReject::Overlapping) => {
                if modes.strict { return Err("STRICT PCM violation: overlapping authenticated source interval".into()); }
                self.native_stale += 1; return Ok(());
            }
            Err(SourceReject::RetiredContext) => {
                self.denied_reanchor += 1;
                return Err(format!("retired media identity requires a new authenticated configuration; rejected={}",self.denied_reanchor).into());
            }
            Err(SourceReject::Overflow) =>
                return Err("authenticated source-frame range overflow".into()),
        };
        self.session_context.get_or_insert(SourceContext {
            epoch:h.media_epoch, stream:h.source_stream_id,
            format:h.format_config_id,
        });
        self.gaps += accepted.missing_sequences;
        self.source_gap_frames += accepted.missing_frames;
        let expected = accepted.expected_source;
        let missing = accepted.missing_frames;
        // A contiguous media sequence with missing source frames
        // means PCM was omitted BEFORE packet numbering, not
        // ordinary QUIC packet loss. Never manufacture half a
        // signal from repeated silence concealment.
        let recurring_source_hole = self.continuity_watch.observe(
            previous_source_sequence, h.packet_sequence, missing);
        if recurring_source_hole && playback_library.is_some() {
            return Err(format!(
                "SUSTAINED SOURCE CONTINUITY FAILURE: {} consecutive source gaps despite consecutive authenticated media sequences; missing_frames={} first_source_frame={} sequence={} (AAudio stopped, investigate Windows capture/reader/drop counters)",
                self.continuity_watch.repeated_gaps(), missing,
                h.first_source_frame, h.packet_sequence
            ).into());
        }
        // Verify-only / client-capture mode never opens an
        // AAudio stream, so it may collect the complete source
        // failure signature instead of stopping after 8 gaps.
        if recurring_source_hole && playback_library.is_none() &&
            self.continuity_watch.repeated_gaps()==ContinuityWatch::LIMIT {
            self.diagnostics.fault(AndroidFault::VerifyContinuity {
                repeated:self.continuity_watch.repeated_gaps(),source:h.first_source_frame,sequence:h.packet_sequence});
        }
        if modes.strict && missing != 0 {
            return Err(format!(
                "STRICT PCM violation: authenticated source gap frames={} at={} (no concealment or skip permitted)",
                missing, h.first_source_frame
            ).into());
        }
        if missing != 0 {
                self.diagnostics.fault(AndroidFault::Gap {epoch:h.media_epoch,sequence:h.packet_sequence,
                    source:h.first_source_frame,frames:missing,
                    elapsed_ms:authenticated_at.map(|t|t.elapsed().as_millis()).unwrap_or(0)});
                if let Some(ref mut speaker) = self.audio {
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
                                h.format_config_id, 1, true, self.authorized_recovery)
                                .map_err(|e| format!("Android concealment: {e}"))?;
                            match state {
                                Admission::Accepted => {
                                    self.source_concealed_frames += AUDIO_FRAMES;
                                }
                                _ => {
                                    complete_concealment = false;
                                    self.source_unconcealed_frames +=
                                        missing - offset * AUDIO_FRAMES;
                                    begin_fresh_anchor(
                                        &mut self.anchor_pending,
                                        &mut self.fresh_anchor_nonce, speaker,
                                        SourceContext {
                                            epoch:h.media_epoch,
                                            stream:h.source_stream_id,
                                            format:h.format_config_id,
                                        },
                                        self.ingress_frontier.floor(),
                                        &mut self.diagnostics,
                                    )?;
                                    self.recover_started += 1;
                                    break;
                                }
                            }
                        }
                        if !complete_concealment {
                            // The lost packet still advances
                            // the authenticated irreversible
                            // reception frontier. Never
                            // re-insert this range on retry.
                            self.first_sequence.get_or_insert(h.packet_sequence);
                            self.last_sequence = Some(h.packet_sequence);
                            self.received += 1;
                            return Ok(());
                        }
                    } else {
                        // A long gap cannot be backfilled by increasing
                        // latency beyond the fixed phone ring capacity.
                        // Account for it; later renderer generation and
                        // drift recovery will handle this explicitly.
                        self.source_unconcealed_frames += missing;
                        begin_fresh_anchor(
                            &mut self.anchor_pending, &mut self.fresh_anchor_nonce,
                            speaker,
                            SourceContext {
                                epoch:h.media_epoch,
                                stream:h.source_stream_id,
                                format:h.format_config_id,
                            },
                            self.ingress_frontier.floor(),
                            &mut self.diagnostics,
                        )?;
                        self.recover_started += 1;
                        // The current packet establishes a
                        // forward AUTHENTICATED source cursor,
                        // but is NOT offered into retired FIFO.
                        // Only subsequent future packets may
                        // re-anchor after callback recovery.
                        self.first_sequence.get_or_insert(h.packet_sequence);
                        self.last_sequence = Some(h.packet_sequence);
                        self.received += 1;
                        return Ok(());
                    }
                }
            }
        self.first_sequence.get_or_insert(h.packet_sequence);
        self.last_sequence = Some(h.packet_sequence);
        self.received += 1;
        if let Some(ref mut speaker) = self.audio {
            // PCM is verified, authenticated, and source-monotonic
            // BEFORE publication to the AAudio callback ring.
            let samples = &wire[HEADER_BYTES..n];
            match speaker.push_media(
                samples, h.first_source_frame,
                h.media_epoch, h.source_stream_id,
                h.format_config_id, 1, false, self.authorized_recovery)
                .map_err(|e| format!("Android audio renderer: {e}"))? {
                Admission::Accepted => {}
                Admission::Overflow |
                Admission::Discontinuity => {
                    if modes.strict { return Err("STRICT PCM violation: native overflow or discontinuity".into()); }
                    self.audio_queue_full += 1;
                    self.recover_started += 1;
                    begin_fresh_anchor(
                        &mut self.anchor_pending, &mut self.fresh_anchor_nonce,
                        speaker,
                        SourceContext {
                            epoch:h.media_epoch, stream:h.source_stream_id,
                            format:h.format_config_id,
                        },
                        self.ingress_frontier.floor(),
                        &mut self.diagnostics,
                    )?;
                }
                Admission::RecoverPending => {
                    if modes.strict { return Err("STRICT PCM violation: recovery pending".into()); }
                    self.recover_pending += 1;
                    begin_fresh_anchor(
                        &mut self.anchor_pending, &mut self.fresh_anchor_nonce,
                        speaker,
                        SourceContext {
                            epoch:h.media_epoch, stream:h.source_stream_id,
                            format:h.format_config_id,
                        },
                        self.ingress_frontier.floor(),
                        &mut self.diagnostics,
                    )?;
                }
                Admission::Stale => {
                    if modes.strict { return Err("STRICT PCM violation: stale PCM admission".into()); }
                    self.native_stale += 1;
                }
            }
        }
        if self.received % 200 == 0 {
            let mut sample=AndroidPeriodic {
                received:self.received,sequence:h.packet_sequence,source:h.first_source_frame,
                missing:self.gaps,stale:self.late,queue_full:self.audio_queue_full,
                elapsed_ms:authenticated_at.map(|t|t.elapsed().as_millis()).unwrap_or(0),
                ..AndroidPeriodic::default()
            };
            // Keep getter order/cadence unchanged; retain partial status if a
            // getter fails, then propagate that failure exactly as before.
            let observed=(|| -> Result<()> {
                if let Some(ref speaker)=self.audio {
                    sample.audio=Some(speaker.snapshot().map_err(|e|format!("AAudio periodic diagnostics: {e}"))?);
                    sample.timing=Some(speaker.timing_sample().map_err(|e|format!("AAudio off-callback timing sample: {e}"))?);
                    sample.playout=speaker.playout_state().map_err(|e|format!("AAudio periodic playout state: {e}"))?;
                    sample.render=self.latest_render_record;
                }
                Ok(())
            })();
            self.diagnostics.periodic(sample);
            observed?;
        }
        Ok(())
    }
}

fn client(remote: SocketAddr, ca: &str, cert: &str, key: &str, seconds: u64, synthetic: bool, playback_library: Option<&str>, modes: PlayModes) -> Result<()> {
    let mut cfg = config(ca, cert, key)?;
    let mut state=ClientMedia {fresh_anchor_nonce:1,..ClientMedia::default()};
    let mut transport_final:Option<(u64,usize)>=None;
    let mut native_final:Option<(AudioStats,Option<PlayoutState>)>=None;
    let result=(|| -> Result<()> {
    // Prepare the fixed-format native renderer and its immutable sinc table
    // before the handshake can cause Windows to start sending PCM. The
    // callback emits defined silence until authenticated media is admitted.
    if let Some(library)=playback_library {
        let config=deskfuse_quiche_lab::android_audio::OutputConfigV8::new(
            modes.buffer_bursts,modes.sharing_policy,modes.usage_policy,modes.reserve_frames)?;
        let mut renderer=AndroidAudio::open_with_config(library,config)
            .map_err(|e|format!("Android speaker renderer initialization failed: {e}"))?;
        renderer.set_diagnostic_modes(modes.fades,modes.linear)
            .map_err(|e|format!("Android diagnostic mode configuration failed: {e}"))?;
        state.audio=Some(renderer);
        println!("AUDIO Android: renderer prepared before QUIC handshake; backend={}",
            if modes.linear {"bandlimited-sinc128"} else {"identity"});
    }
    let route = UdpSocket::bind("0.0.0.0:0")?;
    route.connect(remote)?;
    let local_ip = route.local_addr()?.ip();
    drop(route);
    let udp = socket(SocketAddr::new(local_ip, 0))?;
    let local = udp.local_addr()?;
    deskfuse_quiche_lab::session_control::register_socket(&udp)?;
    let scid = quiche::ConnectionId::from_ref(&[0x71; 16]); // isolated one-peer lab only
    let mut conn = quiche::connect(Some(if deskfuse_quiche_lab::session_control::active() {"deskfuse-session.local"} else {"deskfuse-lab.local"}), &scid, local, remote, &mut cfg)?;
    let mut egress = Egress::default();
    let started_waiting = Instant::now();
    let mut authenticated_at: Option<Instant> = None;
    let mut logged_initial_udp_tx = false;
    let mut input = [0u8; 65535];
    let mut datagram = [0u8; 1500];

    println!("CONNECT continuous Android sink {} -> {} ; mutual TLS mandatory", local, remote);
    let loop_result=(|| -> Result<()> {
    loop {
        if deskfuse_quiche_lab::session_control::stopping() {return Ok(())}
        service_timeout(&mut conn);
        if conn.is_closed() { return Err("Android QUIC connection closed before receiving complete observation window".into()); }

        // Drain authenticated application DATAGRAMs after EACH kernel UDP
        // datagram. quiche's bounded receive FIFO holds only four entries
        // and evicts its oldest entry on overflow. Reading an entire kernel
        // burst first would discard valid audio before this owner sees it.
        // Keep a bounded socket budget so timers/egress still get serviced.
        for _ in 0..128 {
            let received_udp = match udp.recv_from(&mut input) {
                Ok((n, from)) if from == remote => {
                    match conn.recv(&mut input[..n], quiche::RecvInfo { from, to: local }) {
                        Ok(_) | Err(quiche::Error::Done) => {}
                        Err(e) => return Err(format!("Android QUIC receive: {e:?}").into()),
                    }
                    true
                }
                Ok(_) => {
                    // Unauthenticated foreign traffic cannot terminate music.
                    // QUIC still authenticates every accepted peer packet.
                    state.diagnostics.foreign_udp_packets=state.diagnostics.foreign_udp_packets.saturating_add(1);
                    true
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => false,
                Err(e) => return Err(e.into()),
            };
            if conn.is_established() && authenticated_at.is_none() {
                verified(&conn)?;
                authenticated_at = Some(Instant::now());
                state.diagnostics.authenticated=true;
                state.diagnostics.playback=state.audio.is_some();
                state.diagnostics.fades=modes.fades;state.diagnostics.conceal=modes.conceal;
                state.diagnostics.resample=modes.linear;state.diagnostics.strict=modes.strict;
            }
            if authenticated_at.is_some() {
                for _ in 0..128 {
                    match conn.dgram_recv(&mut datagram) {
                        Ok(n) => {
                            if let Some(ref pending) = state.anchor_pending {
                                fresh_anchor::require_live(pending.started, Instant::now())
                                    .map_err(|e| format!("recovery exchange: {e}"))?;
                            }
                            if let Some(reply) = AnchorMessage::decode(&datagram[..n]) {
                                if reply.kind != fresh_anchor::ANCHOR {
                                    return Err("Android rejected unexpected server recovery request".into());
                                }
                                if let Some(ref mut pending) = state.anchor_pending {
                                    if pending.confirm(reply, Instant::now())? {
                                        state.diagnostics.fault(AndroidFault::AnchorConfirmed {nonce:reply.nonce,source:reply.frame});
                                    } // retired challenge responses are never fresh.
                                }
                                continue;
                            }
                            let (h, _) = parse(&datagram[..n], &Configuration::default())
                                .map_err(|e| format!("Android rejected PCM header: {e:?}"))?;
                            if n != APP_BYTES || (synthetic && datagram[..n] != packet(h.packet_sequence)) {
                                return Err("Android rejected PCM source-frame or synthetic payload identity".into());
                            }
                            state.stage_media(h,&datagram[..n],synthetic,playback_library,modes,authenticated_at)?;
                        }
                        Err(quiche::Error::Done) => break,
                        Err(e) => return Err(format!("Android DATAGRAM receive: {e:?}").into()),
                    }
                }
            }
            if !received_udp { break; }
        }
        // Commit a genuine source hole only after the whole kernel batch
        // has had a chance to supply its missing packet (A,C,B -> A,B,C).
        state.drain_ready(true,synthetic,playback_library,modes,authenticated_at)?;
        // Consumer of coherent callback snapshots is the QUIC owner, not
        // the Android real-time thread. Bound draining work and retain only
        // the most recent complete callback record for diagnostics.
        if let Some(ref speaker) = state.audio {
            for _ in 0..16 {
                match speaker.pop_snapshot()
                    .map_err(|e| format!("AAudio off-callback telemetry: {e}"))? {
                    Some((record, dropped)) => {
                        if modes.strict {
                            let during_steady_state = authenticated_at.is_some_and(|at| {
                                let elapsed = at.elapsed();
                                elapsed >= Duration::from_millis(500) &&
                                elapsed < Duration::from_secs(seconds)
                                    .saturating_sub(Duration::from_millis(250))
                            });
                            if during_steady_state {
                                match state.strict_baseline {
                                    Some((base, baseline_drop)) => {
                                        if record.underruns > base.underruns ||
                                           record.invalidations > base.invalidations ||
                                           record.discarded > base.discarded ||
                                           record.concealed_outputs > base.concealed_outputs ||
                                           record.reprimes > base.reprimes ||
                                           dropped > baseline_drop {
                                            return Err(format!(
                                                "STRICT PCM failure during steady exposure: underruns={} invalidations={} discarded={} concealed={} reprimes={} telemetry_lost={} (startup/shutdown excluded)",
                                                record.underruns - base.underruns,
                                                record.invalidations - base.invalidations,
                                                record.discarded - base.discarded,
                                                record.concealed_outputs - base.concealed_outputs,
                                                record.reprimes - base.reprimes,
                                                dropped - baseline_drop
                                            ).into());
                                        }
                                    }
                                    None => state.strict_baseline = Some((record, dropped)),
                                }
                            }
                        }
                        state.latest_render_record = Some((record, dropped));
                        state.coherent_records_read += 1;
                    }
                    None => break,
                }
            }
        }
        // Telemetry is diagnostic only. Authoritative recovery tickets drive
        // the challenge even when callback records are delayed or dropped.
        if state.anchor_pending.is_none() && authenticated_at.is_some_and(|at|
            at.elapsed() < Duration::from_secs(seconds.saturating_sub(1))) {
            if let (Some(context), Some(speaker)) = (state.session_context, state.audio.as_mut()) {
                if speaker.recovery_applied() != state.authorized_recovery {
                    begin_fresh_anchor(
                        &mut state.anchor_pending, &mut state.fresh_anchor_nonce, speaker,
                        context, state.ingress_frontier.floor(),
                        &mut state.diagnostics,
                    )?;
                    state.recover_started += 1;
                    state.reorder.clear();
                }
            }
        }
        if let Some(ref mut pending) = state.anchor_pending {
            let now = Instant::now();
            match fresh_anchor::retry_action(
                pending.started, pending.retry_at, now,
                pending.confirmed.is_some(),
            ) {
                fresh_anchor::RetryAction::Expired => return Err(format!(
                    "authenticated post-recovery source anchor timed out nonce={} floor={}",
                    pending.challenge.nonce, pending.challenge.frame
                ).into()),
                fresh_anchor::RetryAction::Send => {
                    match conn.dgram_send(&pending.challenge.encode()) {
                        Ok(()) | Err(quiche::Error::Done) => {}
                        Err(e) => return Err(format!("Android source anchor request: {e:?}").into()),
                    }
                    pending.retry_at = now + Duration::from_millis(fresh_anchor::RETRY_MS);
                }
                fresh_anchor::RetryAction::Wait => {}
            }
        }
        egress.service(&udp, &mut conn, false)?;
        if !logged_initial_udp_tx && egress.udp_packets_sent > 0 {
            state.diagnostics.first_udp_tx=Some(egress.udp_packets_sent);
            logged_initial_udp_tx = true;
        }
        if let Some(at) = authenticated_at {
            if at.elapsed() >= Duration::from_secs(seconds + 2) {
                if let Some(ref speaker)=state.audio {
                    let a=speaker.snapshot().map_err(|e|format!("AAudio stats: {e}"))?;
                    let playout=speaker.playout_state().map_err(|e|format!("AAudio final playout state: {e}"))?;
                    native_final=Some((a,playout));
                    if a.stream_error != 0 { return Err(format!("AAudio stream failure code={}", a.stream_error).into()); }
                    if a.rendered_frames == 0 { return Err("AAudio played no authenticated PCM frames".into()); }
                }
                if modes.strict && state.strict_baseline.is_none() {
                    return Err("STRICT PCM failure: no steady-state window with coherent output diagnostics".into());
                }
                if state.received == 0 { return Err("no authenticated PCM DATAGRAMs received".into()); }
                return Ok(());
            }
        } else if started_waiting.elapsed() >= CONNECT_LIMIT {
            return Err("Android never authenticated Windows server".into());
        }

        let until = authenticated_at
            .map(|at| at + Duration::from_secs(seconds + 2))
            .unwrap_or(started_waiting + CONNECT_LIMIT);
        let recovery_wake = state.anchor_pending.as_ref().map(|pending| {
            let timeout = pending.started + Duration::from_millis(fresh_anchor::TIMEOUT_MS);
            if pending.confirmed.is_some() { timeout }
            else { timeout.min(pending.retry_at) }
        });
        // Control-plane health observation only: this deadline never releases
        // PCM, advances a source clock, or paces network packets. Normal PCM
        // traffic checks native errors per datagram; silence still observes
        // a stopped/disconnected native stream within one owner health wait.
        let native_health_wake=state.audio.as_ref()
            .map(|_|Instant::now()+Duration::from_millis(100));
        let wake = next_deadline(
            next_deadline(conn.timeout_instant(), egress.deadline(), Some(until)),
            recovery_wake, deadline(&[state.reorder.deadline(),native_health_wake])
        );
        let ready: Readiness = wait_udp(&udp, egress.needs_write_readiness(), wake)?;
        if ready.writable {
            egress.service(&udp, &mut conn, true)?;
        }
    }
    })();
    // This owner is quiescent, even on a returned protocol error.
    let stats=conn.stats();transport_final=Some((egress.udp_packets_sent,stats.lost));
    loop_result
    })();
    let mut native_status_error=None;
    if native_final.is_none() {
        if let Some(ref speaker)=state.audio {
            match speaker.snapshot() {
                Ok(a)=>{
                    match speaker.playout_state() {
                        Ok(p)=>native_final=Some((a,p)),
                        Err(e)=>{native_final=Some((a,None));native_status_error=Some(e);}
                    }
                }
                Err(e)=>native_status_error=Some(e),
            }
        }
    }
    let complete=result.is_ok();
    if let Some(ref speaker)=state.audio {
        state.callback_metrics=speaker.callback_metrics().ok();
    }
    let audio=state.audio.take();
    finish_client(result,||drop(audio),|| {
        use std::io::Write;
        let mut out=io::stdout().lock();
        state.diagnostics.flush(&mut out,remote)?;
        write_client_summary(&mut out,&state,transport_final,native_final,
            playback_library.is_some(),complete,native_status_error.as_deref())?;
        out.flush()
    })
}

// Production finish path: externally backpressured output is reachable only
// after audio shutdown. Reporting must never replace an earlier media error.
fn finish_client(result:Result<()>,close:impl FnOnce(),write:impl FnOnce()->io::Result<()>)->Result<()> {
    close();
    let written=write();
    result?;
    written.map_err(|e|format!("Android deferred diagnostic output failed: {e}").into())
}
fn write_client_summary(out:&mut impl io::Write,state:&ClientMedia,
    transport:Option<(u64,usize)>,native:Option<(AudioStats,Option<PlayoutState>)>,
    playback:bool,complete:bool,status_error:Option<&str>)->io::Result<()> {
    let prefix=if complete {"DONE"}else{"PARTIAL"};
    if let Some(m)=state.callback_metrics {
        writeln!(out,"AUDIO Android callback_metrics preclose_aggregate=1 measurement_enabled={} measured_calls={} total_wall_ns={} maximum_wall_ns={} clock_failures={} size_le96={} size_le240={} size_le480={} size_le960={} size_le1920={} size_larger={}",
            m.measurement_enabled,m.measured_calls,m.total_wall_ns,m.maximum_wall_ns,m.clock_failures,
            m.size_buckets[0],m.size_buckets[1],m.size_buckets[2],m.size_buckets[3],m.size_buckets[4],m.size_buckets[5])?;
    }
    if let Some((udp,lost))=transport {
        writeln!(out,"{prefix} Android sink received={} first_seq={:?} last_seq={:?} missing_between={} source_missing_frames={} concealed_frames={} unfilled_frames={} stale={} udp_packets={} quic_lost={} reorder_full={} reorder_duplicates={} reorder_budget_drops={} mode={}",
            state.received,state.first_sequence,state.last_sequence,state.gaps,state.source_gap_frames,
            state.source_concealed_frames,state.source_unconcealed_frames,state.late,udp,lost,
            state.reorder.full_drops,state.reorder.duplicates,state.reorder.budget_drops,
            if playback{"native-speaker"}else{"verify-only"})?;
    } else {writeln!(out,"TRACE Android transport_summary_unavailable=true")?;}
    if let Some((a,playout))=native {
        if let Some(p)=playout {
            writeln!(out,"AUDIO Android reconstruction clipped_samples={} xrun_count={}",p.clipped_samples,p.xrun_count)?;
        }
        writeln!(out,"AUDIO Android speaker accepted_frames={} rendered_frames={} silence_frames={} overflow_packets={} queue_full_from_receiver={} callbacks={} queued_frames={} stream_error={} format={}Hz/{}ch drift_ppm={} underrun_events={} reprime_events={}",
            a.accepted_frames,a.rendered_frames,a.silence_frames,a.overflow_packets,state.audio_queue_full,
            a.callback_count,a.queued_frames,a.stream_error,a.actual_sample_rate,a.actual_channels,
            a.drift_correction_ppm,a.underrun_events,a.reprime_events)?;
    } else if playback {writeln!(out,"TRACE Android native_stats_unavailable=true")?;}
    if let Some((r,dropped))=state.latest_render_record {
        writeln!(out,"AUDIO Android committed callback={} source={} support={} fraction_q32={} epoch={} stream={} format={} connection={} playout_generation={} committed_output={} released_source={} discarded_rebuffer={} concealed_output={} coherence_dropped={} coherence_read={}",
            r.callback_seq,r.last_source,r.last_support_source,r.fraction_q32,r.epoch,r.stream,r.format,
            r.connection,r.playout,r.committed_output,r.released,r.discarded,r.concealed_outputs,dropped,state.coherent_records_read)?;
    }
    if playback {
        writeln!(out,"AUDIO Android authenticated_reanchors={} outstanding_anchor={} last_receiver_frontier={}",state.confirmed_reanchors,state.anchor_pending.is_some(),state.ingress_frontier.floor())?;
        writeln!(out,"AUDIO Android recovery_started={} recovery_pending_drops={} native_stale={} denied_contexts={} authorized_ticket={}",state.recover_started,state.recover_pending,state.native_stale,state.denied_reanchor,state.authorized_recovery)?;
    }
    if let Some(error)=status_error {writeln!(out,"TRACE Android secondary_native_status_error={error:?}")?;}
    writeln!(out,"TRACE Android session_complete={complete}")
}

#[cfg(test)]
mod client_staging_tests {
    use super::*;
    #[test]
    fn dedicated_endpoint_is_pinned_to_the_enrolled_tls_certificate() {
        let expected = [0x30, 0x01, 0x02];
        assert!(require_enrolled_certificate(Some(&expected), &expected).is_ok());
        assert!(require_enrolled_certificate(None, &expected).is_err());
        assert!(require_enrolled_certificate(Some(&[0x30,0x01,0x03]), &expected).is_err());
        assert!(require_enrolled_certificate(Some(&[]), &[]).is_err());
    }
    #[test]
    fn production_finish_closes_before_output_and_preserves_primary_error() {
        use std::cell::Cell;
        use std::io::Write;
        struct FailedWriter<'a>(&'a Cell<bool>);
        impl Write for FailedWriter<'_> {
            fn write(&mut self,_:&[u8])->io::Result<usize> {
                assert!(self.0.get(),"writer invoked before audio closed");
                Err(io::Error::from(io::ErrorKind::BrokenPipe))
            }
            fn flush(&mut self)->io::Result<()>{Ok(())}
        }
        for fail_media in [false,true] {
            let closed=Cell::new(false);
            let result=if fail_media{Err("original media failure".into())}else{Ok(())};
            let error=finish_client(result,||closed.set(true),||FailedWriter(&closed).write_all(b"report")).unwrap_err();
            assert!(closed.get());
            if fail_media {assert_eq!(error.to_string(),"original media failure")}
            else {assert!(error.to_string().contains("Android deferred diagnostic output failed"))}
        }
    }
    fn deliver(state:&mut ClientMedia,sequence:u32)->Result<()> {
        let wire=captured_packet(sequence,48_000+u64::from(sequence)*AUDIO_FRAMES,&[0;960]);
        let (header,_)=parse(&wire,&Configuration::default()).unwrap();
        state.stage_media(header,&wire,false,None,PlayModes::default(),None)
    }
    #[test]
    fn actual_client_path_keeps_same_kernel_batch_reordering_out_of_gap_accounting()->Result<()> {
        let mut state=ClientMedia{fresh_anchor_nonce:1,..ClientMedia::default()};
        deliver(&mut state,0)?;
        deliver(&mut state,2)?;
        assert_eq!(state.received,1,"a missing interval must not commit inline");
        deliver(&mut state,1)?;
        state.drain_ready(true,false,None,PlayModes::default(),None)?;
        assert_eq!(state.received,3);
        assert_eq!(state.source_gap_frames,0);
        assert_eq!(state.gaps,0);
        assert_eq!(state.ingress_frontier.floor(),48_720);
        assert!(state.reorder.deadline().is_none());
        Ok(())
    }
    #[test]
    fn actual_client_commits_true_hole_only_after_socket_batch_and_counts_retired_pcm()->Result<()> {
        let mut state=ClientMedia{fresh_anchor_nonce:1,..ClientMedia::default()};
        deliver(&mut state,0)?;deliver(&mut state,2)?;
        assert_eq!(state.source_gap_frames,0);
        state.drain_ready(true,false,None,PlayModes::default(),None)?;
        assert_eq!(state.source_gap_frames,240);
        assert_eq!(state.gaps,1);
        deliver(&mut state,1)?;
        assert_eq!(state.late,1);
        assert_eq!(state.received,2);
        Ok(())
    }
    #[test]
    fn unavailable_coherent_cursor_uses_physical_occupancy_without_resetting_same_batch()->Result<()> {
        let mut state=ClientMedia{fresh_anchor_nonce:1,..ClientMedia::default()};
        // Inject the production observation boundary: callback cursor absent,
        // but the independent atomic storage occupancy remains available.
        // This exercises the same staging/admission method as Android.
        for sequence in [0,2,1] {
            let wire=captured_packet(sequence,48_000+u64::from(sequence)*AUDIO_FRAMES,&[0;960]);
            let (header,_)=parse(&wire,&Configuration::default()).unwrap();
            state.stage_media_observed(header,&wire,false,None,PlayModes::default(),None,
                NativeObservation {headroom:None,queued_storage_frames:1920})?;
        }
        state.drain_ready(true,false,None,PlayModes::default(),None)?;
        assert_eq!(state.received,3);
        assert_eq!(state.source_gap_frames,0);
        assert_eq!(state.recover_started,0);
        assert_eq!(state.reorder.budget_drops,0);
        assert!(state.anchor_pending.is_none());
        Ok(())
    }
}

fn positive_seconds(s: &str) -> Result<u64> {
    let n = s.parse::<u64>()?;
    if !(1..=1800).contains(&n) { return Err("duration must be 1..1800 seconds for the preregistered physical workload".into()); }
    Ok(n)
}

#[cfg(test)]
mod recovery_boundary_tests {
    use super::*;
    fn exchange(started: Instant) -> PendingAnchor {
        PendingAnchor {
            challenge: AnchorMessage { kind:fresh_anchor::REQUEST, nonce:7,
                frame:48000, context:SourceContext {epoch:1,stream:1,format:1} },
            recovery_ticket:3, started, retry_at:started, confirmed:None,
        }
    }
    #[test]
    fn buffered_reply_and_media_after_owner_stall_cannot_complete_expired_exchange() {
        let start=Instant::now();
        let mut p=exchange(start);
        let reply=AnchorMessage {kind:fresh_anchor::ANCHOR, frame:48240,..p.challenge};
        let expired=start+Duration::from_millis(fresh_anchor::TIMEOUT_MS);
        assert!(p.confirm(reply,expired).is_err());
        assert!(p.confirmed.is_none());
        assert!(p.confirm(reply,start+Duration::from_millis(10)).unwrap());
        assert!(p.authorizes(p.challenge.context,48240,3,expired).is_err());
        assert!(p.authorizes(p.challenge.context,48240,3,start+Duration::from_millis(11)).unwrap());
        assert!(!p.authorizes(p.challenge.context,48240,4,start+Duration::from_millis(11)).unwrap());
    }
}

pub fn run_authorized(value:serde_json::Value)->Result<()> {
    let get=|name:&str|->Result<&str> {value[name].as_str().filter(|x|!x.is_empty()).ok_or_else(||format!("missing session {name}").into())};
    if value["version"].as_u64()!=Some(1) {return Err("native session ABI mismatch".into())}
    let seconds=value["seconds"].as_u64().filter(|n|*n>0 && *n<=1800).ok_or("session duration outside grant")?;
    let expected=std::fs::read(get("peerDer")?)?;
    let _session=deskfuse_quiche_lab::session_control::Session::begin(expected)?;
    #[cfg(windows)] {
        // EOF of the inherited control pipe revokes this process's grant.
        std::thread::spawn(|| {
            while let Ok(command)=deskfuse_quiche_lab::session_control::bounded_stdin() {
                if command["command"].as_str()!=Some("media-ready") {break}
                deskfuse_quiche_lab::session_control::enable_media();
            }
            deskfuse_quiche_lab::session_control::stop();
        });
    }
    match get("role")? {
        "server"=> {
            let peer=get("peerId")?;
            deskfuse_quiche_lab::endpoint_lease::validate_peer_id(peer)?;
            let policy=get("enginePeriod")?;
            if !["reference","minimum"].contains(&policy) {return Err("invalid period policy".into())}
            if get("backend")?=="committed" {
                #[cfg(windows)]
                return direct_sender::run(get("bind")?.parse()?,get("remote")?.parse()?,get("ca")?,get("cert")?,get("key")?,seconds,
                    peer,get("sourceDll")?,policy=="minimum");
                #[cfg(not(windows))]
                return Err("committed source requires Windows".into());
            }
            if get("backend")?!="loopback" {return Err("unknown source backend".into())}
            // Setup-only, before source/thread creation. No process may host
            // concurrent native sessions; Session::begin enforces ownership.
            std::env::set_var("DESKFUSE_WASAPI_ENGINE_PERIOD",policy);
            server(get("bind")?.parse()?,get("remote")?.parse()?,get("ca")?,get("cert")?,get("key")?,seconds,
                Some(CaptureSource::DeskFuse {exe:get("capture")?,enrolled_peer:peer,certificate_der:get("peerDer")?}))
        }
        "client"=>client(get("remote")?.parse()?,get("ca")?,get("cert")?,get("key")?,seconds,false,Some(get("library")?),PlayModes::default()),
        _=>Err("unknown native session role".into()),
    }
}

fn run() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    match args.as_slice() {
        [_,mode] if mode=="session-stdin"=>run_authorized(deskfuse_quiche_lab::session_control::bounded_stdin()?),
        [_, mode, bind, allow_ip, ca, cert, key, secs] if mode == "server" => {
            server(bind.parse()?, allow_ip.parse()?, ca, cert, key, positive_seconds(secs)?, None)
        }
        [_, mode, remote, ca, cert, key, secs] if mode == "client" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, true, None, PlayModes::default())
        }
        [_, mode, bind, allow_ip, ca, cert, key, exe, device, secs] if mode == "server-wasapi" => {
            server(bind.parse()?, allow_ip.parse()?, ca, cert, key,
                   positive_seconds(secs)?, Some(CaptureSource::Endpoint {exe,endpoint:device}))
        }
        [_,mode,bind,allow_ip,ca,cert,key,exe,peer,peer_der,secs] if mode=="server-deskfuse" => {
            server(bind.parse()?,allow_ip.parse()?,ca,cert,key,positive_seconds(secs)?,
                Some(CaptureSource::DeskFuse {exe,enrolled_peer:peer,certificate_der:peer_der}))
        }
        [_, mode, remote, ca, cert, key, secs] if mode == "client-capture" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, false, None, PlayModes::default())
        }
        [_, mode, remote, ca, cert, key, library, secs] if mode == "client-playback" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?, false, Some(library), PlayModes::default())
        }
        [_, mode, remote, ca, cert, key, library, secs] if mode == "client-playback-strict" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?,
                   false, Some(library), PlayModes::strict())
        }
        [_, mode, remote, ca, cert, key, library, flags, secs] if mode == "client-playback-modes" => {
            client(remote.parse()?, ca, cert, key, positive_seconds(secs)?,
                   false, Some(library), PlayModes::parse(flags)?)
        }
        [_, mode, remote, ca, cert, key, library, flags, bursts, secs] if mode == "client-playback-buffer" => {
            let mut modes=PlayModes::parse(flags)?;
            modes.buffer_bursts=bursts.parse()?;
            deskfuse_quiche_lab::android_audio::OutputConfigV7::new(modes.buffer_bursts)?;
            client(remote.parse()?,ca,cert,key,positive_seconds(secs)?,false,Some(library),modes)
        }
        [_,mode,remote,ca,cert,key,library,flags,bursts,sharing,usage,reserve,secs]
            if mode=="client-playback-experiment" => {
            let mut modes=PlayModes::parse(flags)?;
            modes.buffer_bursts=bursts.parse()?;
            modes.sharing_policy=sharing.parse()?;
            modes.usage_policy=usage.parse()?;
            modes.reserve_frames=reserve.parse()?;
            deskfuse_quiche_lab::android_audio::OutputConfigV8::new(
                modes.buffer_bursts,modes.sharing_policy,modes.usage_policy,modes.reserve_frames)?;
            client(remote.parse()?,ca,cert,key,positive_seconds(secs)?,false,Some(library),modes)
        }
        _ => Err("usage: continuous-quic-lab server <bind:port> <allowed-ip> <ca> <server-cert> <server-key> <secs> | server-wasapi <bind:port> <allowed-ip> <ca> <server-cert> <server-key> <wasapi-capture.exe> <endpoint-id|default> <secs> | client|client-capture <windows-ip:port> <ca> <client-cert> <client-key> <secs> | client-playback <windows-ip:port> <ca> <client-cert> <client-key> <libdeskfuse_android_audio.so> <secs> | client-playback-modes <windows-ip:port> <ca> <client-cert> <client-key> <libdeskfuse_android_audio.so> <f1c1r1> <secs> | client-playback-buffer <windows-ip:port> <ca> <client-cert> <client-key> <library> <f1c1r1> <bursts:0|2|3> <secs> | client-playback-strict <windows-ip:port> <ca> <client-cert> <client-key> <libdeskfuse_android_audio.so> <secs>".into()),
    }
}

fn main() {
    if let Err(e) = run() {
        eprintln!("FAIL continuous QUIC lab: {e}");
        std::process::exit(1);
    }
}

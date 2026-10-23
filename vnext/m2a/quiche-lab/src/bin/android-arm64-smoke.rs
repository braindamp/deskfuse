//! DeskFuse vNext Android ARM64 native smoke binary (no APK, no networking).
//! Proves Rust + the real quiche crypto stack link for the target, then validates
//! source-indexed audio wire format in a process running on the Android device.
//! It deliberately neither opens sockets nor changes any installed DeskFuse app.
use deskfuse_quiche_lab::{
    encode, parse, Configuration, Header, HEADER_BYTES, KIND_AUDIO, VERSION,
};
use std::error::Error;

fn run() -> Result<(), Box<dyn Error>> {
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
        packet_sequence: 7,
        first_source_frame: 48_000,
        payload_bytes: 960,
        clock_anchor_id: 17,
    };

    let mut packet = encode(&header).to_vec();
    packet.extend((0..960).map(|i| i as u8));
    if packet.len() != 1_000 {
        return Err("invalid application datagram size".into());
    }
    let (decoded, payload) =
        parse(&packet, &cfg).map_err(|e| format!("wire parser rejected test packet: {e:?}"))?;
    if decoded != header || payload != &packet[HEADER_BYTES..] {
        return Err("source-indexed audio test packet mismatch".into());
    }

    // Make the target executable actually call quiche, not just reference the
    // separate DeskFuse header encoder. No credentials or UDP are opened.
    let mut quic = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    quic.set_application_protos(&[b"df-av-m2a-1"])?;
    quic.set_max_recv_udp_payload_size(1350);
    quic.set_max_send_udp_payload_size(1350);
    quic.enable_dgram(true, 4, 4);

    println!(
        "PASS DeskFuse vNext target={} arch={} real-quiche-config=true audio-frames={} first-source-frame={} bytes={}",
        std::env::consts::OS,
        std::env::consts::ARCH,
        decoded.frame_count,
        decoded.first_source_frame,
        packet.len()
    );
    println!("NO NETWORKING: smoke test only; no QUIC peer authentication or Android playback.");
    Ok(())
}

fn main() {
    if let Err(err) = run() {
        eprintln!("FAIL DeskFuse vNext ARM64 smoke: {err}");
        std::process::exit(1);
    }
}

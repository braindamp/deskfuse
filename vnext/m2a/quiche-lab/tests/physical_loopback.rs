//! Same transport executable as the physical Windows→Android gate, first
//! verified over two real local UDP sockets in CI without any devices.
mod common;
use std::net::UdpSocket;
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

#[test]
fn physical_lab_mutual_tls_and_windows_origin_pcm_roundtrip() -> common::LabResult<()> {
    let dir = tempfile::tempdir()?;
    common::issue_test_identities(dir.path())?;
    let cert_dir = dir.path();
    // Reserve a free loopback UDP port, then release it for the fixture.
    let probe = UdpSocket::bind("127.0.0.1:0")?;
    let bind = probe.local_addr()?;
    drop(probe);

    let exe = env!("CARGO_BIN_EXE_physical-quic-lab");
    let mut server = Command::new(exe)
        .arg("server")
        .arg(bind.to_string())
        .arg("127.0.0.1")
        .arg(cert_dir.join("ca.pem"))
        .arg(cert_dir.join("server.pem"))
        .arg(cert_dir.join("server.key"))
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;

    // The server binds its UDP socket immediately; no network access outside
    // 127.0.0.1. QUIC retransmits handshake packets if startup races occur.
    thread::sleep(Duration::from_millis(150));

    let client_result = Command::new(exe)
        .arg("client")
        .arg(bind.to_string())
        .arg(cert_dir.join("ca.pem"))
        .arg(cert_dir.join("client.pem"))
        .arg(cert_dir.join("client.key"))
        .output();

    // If the client exits early, don't leave the server listening 30 seconds.
    if client_result.as_ref().map_or(true, |v| !v.status.success()) {
        let _ = server.kill();
    }
    let server_output = server.wait_with_output()?;
    let client_output = client_result?;

    assert!(
        client_output.status.success(),
        "client failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&client_output.stdout),
        String::from_utf8_lossy(&client_output.stderr)
    );
    assert!(
        server_output.status.success(),
        "server failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&server_output.stdout),
        String::from_utf8_lossy(&server_output.stderr)
    );

    let client_text = String::from_utf8_lossy(&client_output.stdout);
    let server_text = String::from_utf8_lossy(&server_output.stdout);
    assert!(client_text.contains("PASS Android received 16"), "{client_text}");
    assert!(server_text.contains("PASS Windows sender: 16"), "{server_text}");
    println!("PASS QUIC mutually authenticated local Windows-source/Android-role DATAGRAM roundtrip");
    Ok(())
}

//! Functional smoke for the persistent synthetic PCM transport, not M2a acceptance.
mod common;

use std::net::UdpSocket;
use std::process::{Command, Stdio};
use std::thread;
use std::time::Duration;

#[test]
fn continuous_source_clock_datagrams_over_authenticated_udp() -> common::LabResult<()> {
    let dir = tempfile::tempdir()?;
    common::issue_test_identities(dir.path())?;
    let dir = dir.path();

    let probe = UdpSocket::bind("127.0.0.1:0")?;
    let addr = probe.local_addr()?;
    drop(probe);
    let exe = env!("CARGO_BIN_EXE_continuous-quic-lab");
    let mut server = Command::new(exe)
        .arg("server")
        .arg(addr.to_string())
        .arg("127.0.0.1")
        .arg(dir.join("ca.pem"))
        .arg(dir.join("server.pem"))
        .arg(dir.join("server.key"))
        .arg("1")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;

    thread::sleep(Duration::from_millis(100));
    let result = Command::new(exe)
        .arg("client")
        .arg(addr.to_string())
        .arg(dir.join("ca.pem"))
        .arg(dir.join("client.pem"))
        .arg(dir.join("client.key"))
        .arg("1")
        .output();
    if result.as_ref().map_or(true, |output| !output.status.success()) {
        let _ = server.kill();
    }
    let server_output = server.wait_with_output()?;
    let client_output = result?;
    let a = String::from_utf8_lossy(&server_output.stdout);
    let b = String::from_utf8_lossy(&client_output.stdout);
    assert!(server_output.status.success(), "server:\n{a}\n{}", String::from_utf8_lossy(&server_output.stderr));
    assert!(client_output.status.success(), "client:\n{b}\n{}", String::from_utf8_lossy(&client_output.stderr));
    assert!(a.contains("DONE Windows source"), "{a}");
    assert!(b.contains("DONE Android sink received="), "{b}");
    println!("PASS 1-second continuous encrypted source-indexed UDP/QUIC lab without stop-and-wait echo");
    Ok(())
}

//! Fixed installed role. The lab's JSON/stdin/path options are not accepted.
fn main() {
    let result = deskfuse_quiche_lab::broker_control::registration(std::env::args_os().skip(1))
        .and_then(|registration| {
            #[cfg(windows)]
            { deskfuse_quiche_lab::broker_control::windows::run(registration) }
            #[cfg(not(windows))]
            { let _ = registration; Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "Windows worker required")) }
        });
    if let Err(error) = result {
        // Neither control payloads nor keys/paths are diagnostic output.
        eprintln!("DeskFuse AudioWorker stopped: {:?}", error.kind());
        std::process::exit(2);
    }
}

# M2a — quiche 0.30.0 authenticated DATAGRAM foundation

**Current scope:** Rust/C++ identical 40-byte PCM16 packet layout + real quiche TLS handshake using an ephemeral test CA, signed localhost certificate and a verified client SNI. The first integration test exchanges QUIC packets *inside the test process*, not over a real UDP socket. No real Android, audio device, driver or production credential is used.

This test only authenticates the **server** to its client. Production architecture still requires authenticated client identity (or a securely authenticated pairing/authorization control channel), real UDP pacing/readiness, bounded stateless retry/CID routing, protocol timer service, clock-map conservatism, ciphertext policy trials, Android native arm64 build and stress qualification.

## Windows prerequisites

- Rust stable x86_64-pc-windows-msvc (tested user's Rust: 1.99.1)
- Visual Studio 2022 C++ toolchain, CMake, Ninja (already found)
- **NASM executable** on PATH. BoringSSL/quiche builds require it on Windows.
- Internet access for initial crate retrieval from crates.io.

## Build and test

In your **Visual Studio Developer PowerShell**, from the *separate* `D:\Coding\DeskFuse\vnext-wp001` checkout:

```powershell
git pull --ff-only origin feature/vnext-wp001-native-lab
nasm -v
cargo test --manifest-path vnext/m2a/quiche-lab/Cargo.toml -- --nocapture
```

If NASM is absent, install the official `NASM.NASM` winget package in a separate elevated console only if the package requests elevation, then open a fresh Visual Studio Developer PowerShell and verify `nasm -v`.

Cargo normally produces a `Cargo.lock`. Commit it once the first validated build locks transitive crypto dependencies; the crate already pins quiche exactly to 0.30.0. The `target` directory is ignored. Do not add temp CA cert/key files to Git.

## Expected results and interpretation

- Four wire-format tests must pass in the Rust library.
- `verified_tls_quiche_delivers_source_indexed_datagram` must pass: TLS-authenticated quiche connection established, 240 source frames represented by 1,000 application bytes, receiver parsed original first-source-frame identity and payload.
- A failing handshake is a blocker, **not** a reason to set `verify_peer(false)` on the client or skip the integration test.
- GitHub Actions covers Linux + Windows native builds on this branch, without uploading keys or running any app/driver installation.
- Even all-green status proves **in-process QUIC crypto**, not acoustic, Android, Wi-Fi, transport scheduling or mutual endpoint authentication.

See `../PREREGISTERED_GATES.md` for the complete hardware feasibility gates.

# DeskFuse vNext — M2a transport feasibility

**Status:** A small, standalone 40-byte audio wire-format prototype. This is NOT a functioning network sender/receiver and is NOT an authenticated QUIC implementation.

The header parser is allocation-free and uses explicit network-byte-order fields. It must be called only on datagram plaintext that has already passed QUIC peer authentication. It borrows the input bytes; the caller must not outlive the authenticated ingress buffer. Media epoch, source stream and format must match a reliably authenticated configuration. Separate source/play/connection generations remain part of the control-plane session contract.

## Windows (Visual Studio Developer PowerShell)

Run from your separate `D:\Coding\DeskFuse\vnext-wp001` checkout:

```powershell
git pull --ff-only origin feature/vnext-wp001-native-lab
cmake -S vnext/m2a -B vnext/m2a/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build vnext/m2a/build --parallel 2
ctest --test-dir vnext/m2a/build --output-on-failure
```

Build `vnext/wp001` independently to rerun the earlier timing/ownership tests. These steps do not install a driver, start any network service, change the Android app or touch your working DeskFuse installation.

## Next work package (not yet claimed)

Pinned quiche v0.30.0 on Windows x64 and Android arm64; real peer verification and mutually authenticated test credentials; Rust-owned relative monotonic waits; sample-indexed encrypted DATAGRAM send/receive; controlled comparison of encrypted-packet expiry policies; receiver verification and reproducible load/latency traces. Don't install unsigned binaries or weaken Defender.

See `PREREGISTERED_GATES.md` for the tests we must pass **before** calling the QUIC approach viable.

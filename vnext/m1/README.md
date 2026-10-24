# M1 — native Windows WASAPI capture source (isolated vNext)

This is a **new C++20 Windows capture adapter**, separate from the working
DeskFuse Windows app, virtual audio driver and Android APK. It does not install,
enable, disable, reroute, or modify audio devices.

The first target is a **selected Windows render endpoint**, captured using
WASAPI shared-mode loopback with `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`. Windows
10 19045 supports event-driven loopback. The currently supported capture
engine formats are **48,000 Hz, two-channel, float32 or PCM16**; unsupported
source formats are rejected rather than silently relabeled as 48 kHz.
Source device positions and WASAPI QPC timestamps are preserved as separate
metadata. The capture event owns each input buffer until ReleaseBuffer; an
eight-block bounded handoff ensures it is not held while an output pipe blocks.

## Build on the Windows host (VS 2022 Developer PowerShell)

```powershell
cd 'D:\Coding\DeskFuse\vnext-wp001'
cmake -S vnext/m1 -B vnext/m1/build -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build vnext/m1/build --parallel 2
.\vnext\m1\build\deskfuse-wasapi-capture.exe --list
```

The `--list` output includes device display names and exact `IMMDevice`
IDs. Selecting an endpoint for capture **does not make it Windows' default
playback device**. To capture an application, play audio through that
endpoint using the application's or Windows' own routing controls, without
modifying the existing DeskFuse driver.

To make a **local-only recording** for the first hardware capture check:

```powershell
New-Item -ItemType Directory -Path 'D:\DeskFuseLocalData' -Force | Out-Null
.\vnext\m1\build\deskfuse-wasapi-capture.exe --device-id '<ID from --list>' --out 'D:\DeskFuseLocalData\m1.dfpcm' --seconds 10
```

Or omit `--device-id` for the default multimedia render endpoint.
The output refuses to overwrite existing files. No tests upload or commit
captured user audio; never place `.dfpcm` files in the public checkout.

## Binary capture handoff to Rust QUIC

Native mode `--out -` writes **binary data only** to stdout and diagnostics to
stderr. Use it through a pipe, not directly in an interactive terminal.
Each record has a 40-byte **little-endian** local IPC header and 1..240
interleaved PCM16 stereo frames:

| Offset | Field |
| --- | --- |
| 0 | ASCII `DFWC` (4 bytes) |
| 4 | u32 version = 2 |
| 8 | u64 first capture-device source frame |
| 16 | u64 WASAPI QPC timestamp in 100-ns units; zero when invalid |
| 24 | u32 frame count |
| 28 | u32 WASAPI capture flags |
| 32 | u32 PCM bytes = frames * 4 |
| 36 | u32 offset of this fragment within the original WASAPI packet (frames) |
| 40 | little-endian PCM16 interleaved stereo data |

The Rust-side `vnext/m2a/quiche-lab/src/wasapi_pipe.rs` reads these records
through a bounded eight-slot channel, aggregates exactly 240 contiguous
device frames per QUIC DATAGRAM and wakes the QUIC socket-readiness loop with
a **local non-authenticated notification**. This notification is never treated
as a remote QUIC peer. Rust alone owns the TLS/QUIC connection.

New `continuous-quic-lab` modes:

- Windows: `server-wasapi <bind:port> <allowed-phone-ip> <ca> <server-cert>
  <server-key> <wasapi-capture.exe> <device-id|default> <seconds>`
- Android: `client-capture <windows-ip:port> <ca> <client-cert>
  <client-key> <seconds>`

The Android receiver verifies authenticated packet format, sequence, source
frame identity and bounded payload. `client-capture` is silent verification;
`client-playback-modes` uses the native AAudio renderer. Unlike
the deterministic synthetic fixture, its actual PCM content cannot be
recomputed from a sequence number.

## Not yet qualified

This first adapter is intended to establish *actual captured Windows audio
entering the authenticated transport*. It is not the final capture/transport
contract: the production path still needs a unified native ownership adapter,
supported source-rate/channel conversion policy, capture-domain timestamp
uncertainty, operational reconnect qualification and sustained physical load
qualification. Source-QPC admission, native AAudio rendering and output-demand
clock correction now exist, but are not full product qualification. No synthetic 5-ms clock or QUIC ACK clock belongs in
production. See `vnext/m2a/PREREGISTERED_GATES.md`.

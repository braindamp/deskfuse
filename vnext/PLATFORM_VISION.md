# DeskFuse vNext — multi-platform / multi-endpoint direction

**Architecture intent, not shipped feature list.** Primary acceptance target remains Windows → Xiaomi Android over authenticated low-latency audio transport and then video. Expand platform coverage without altering existing working DeskFuse app/driver until tested.

## Share only what must be shared

**Portable core (Windows, Linux, macOS, Android and potentially other endpoints):**
- Explicit capability negotiation: roles (source, sink, bidirectional), audio/video codecs, format and layout, input/output devices, display resolution/frame rate and authentication policy.
- QUIC 1.3 identity, pairing, permission model and bounded encrypted media/control transports; transport acknowledgements never act as the media clock.
- Source frame indexes, epochs, origin clock anchors, timestamp mapping uncertainty, receiver ownership, loss accounting, feedback leases and congestion/freshness policy.
- Deterministic media pipeline state machine and core tests, with no periodic sender cadence superimposed on source clock.
- Cross-platform observability (frame-id tracing, sender-to-receiver age, queue occupancy, sink drift and continuity metrics).

**Platform adapters (not portable):**
- Windows: WASAPI source clock/capture, signed virtual WaveRT endpoint, WGC/D3D capture and hardware encoders.
- Linux: PipeWire source/sink, Wayland/X11/PipeWire screen capture and hardware encoding, PipeWire virtual-device integration subject to desktop compositor capabilities.
- macOS: CoreAudio with host-time clock attribution, ScreenCaptureKit, VideoToolbox and OS-entitled virtual-display/device capabilities; macOS permission prompts and signed/notarized delivery need dedicated design.
- Android: AAudio/Oboe (AudioTrack fallback), surface/video decoding, platform audio focus/permissions and lifecycle. Other future platforms need their own adapters.

A device may be a source, sink or both, but bidirectional sessions, multi-sink A/V sync, discovery and relays require explicit new security and clock designs; avoid assuming that a phone-like endpoint implementation generalizes automatically. Discovery is advisory; explicit authenticated pairing controls authorization.

## Evidence gates

1. **Now:** Common Rust/quiche protocol tested on Linux and Windows in GitHub CI, plus macOS Apple Silicon and Intel as a new build/test matrix. QUIC test is in-process; it is not production pairing, network behavior or a Mac app.
2. **Next:** Real authenticated Windows UDP ↔ Android arm64 transport; validate security, shutdown, packet expiry and congestion with prescribed adversarial loads.
3. **Then:** Native Windows audio sender ↔ native Android audio sink; measured acoustic continuity, end-to-end latency, battery and drift.
4. **Later:** Video integration. Only after the Windows→Android experience qualifies: experimental Linux→Android and macOS→Android adapters under the same acceptance gates. Additional endpoint types get distinct capability, lifecycle and security tests.

## Build/disk policy

- CI compiles other OS targets on GitHub-hosted machines, not the developer's local C: / D: drives.
- Locally, Cargo's crate and toolchain caches usually use the Windows profile drive; Cargo target outputs are under the isolated project's `target/` on D:.
- Keep Android SDK/NDK and future emulator images on D: if C: is tight. Emulators are not required for the currently authorized physical Xiaomi.
- Never purge active build directories while compiling or change the installed/working DeskFuse driver or routes as part of CI.

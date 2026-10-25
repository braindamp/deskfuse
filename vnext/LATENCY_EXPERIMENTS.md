# Independent audio latency experiments

## 25 October: earlier-capture implementation checkpoint

Implemented the explicit DeskFuse source session (`server-deskfuse`) with an
enrolled peer ID and pinned public DER client certificate. Provisioning runs
off the QUIC owner after authentication. The broker lease remains in the source
worker until native capture exits; cancellation stops/joins capture before
lease release. No default lookup/restoration or physical-output fallback exists
on this route. Existing endpoint-loopback mode remains the comparator.

Completed/reviewed the native minimum-period companion: legal queried values,
matching format, already-active handling, period readback and cleanup; silent
render service uses audio events. Capacity and engine-period controls remain
independent. Windows runner accepts `-DeskFusePeerId <enrolled-hash>` instead of
`-EndpointId`, plus `-WindowsEnginePeriod reference|minimum`; it pins the locally
provisioned public client certificate and requires rebuilt receipt capabilities.

Verification: isolated Release capture build and its two CTests passed; Release
Windows sender build and six sender tests passed. Library checks:36 passed in
sandbox; the remaining TLS check passed with normal Windows crypto access.
Existing broker Release build passed. No physical playback/device creation,
driver installation or routing change was run. Android artifacts and the new
four-artifact build receipt have NOT been rebuilt for this checkpoint. The
candidate must not be deployed using the old73ef3d4 receipt.

Installed service binary SHA256 matches DriverStore package599f40d336ee6bcb,
version10/21/2026,16.1.56.615; it is not established as the current repo candidate.
All four stored packages had valid signatures. Runtime source/period remains
unqualified until an approved endpoint activation.

NEXT: finish W-E1 physical gate using the exact enrolled identity; then W-E2
fractional driver clock/late notification correction; W-E3 immediate committed
PCM export and W-E4 direct Rust mapping/freshness/fence integration. Those driver
changes are NOT implemented. Follow EARLIER_CAPTURE_IMPLEMENTATION_PLAN.md.
Do not call this checkpoint proven earlier capture or a completed full plan.

This experimental branch extends the existing native WASAPI → authenticated
QUIC DATAGRAM → custom AAudio/sinc128 path. Reference settings remain MM-1,
48 kHz stereo PCM16, 240-frame packets, shared/default usage, hardware buffer
policy 0, raw native reserve 1440, ±400 ppm/100 ppm/s, fades/concealment enabled.
No new default is physically qualified.

## Windows feasibility

Read-only MM-1 query on 25 October 2026 reported a compatible 48 kHz stereo
mix and default/minimum/maximum/current shared engine periods all **480 frames**.
There is no supported smaller-period selection on this endpoint. The query
does not initialize/start a stream or change endpoint/default routing:

```powershell
.\vnext\m1\build\deskfuse-wasapi-capture.exe --capabilities --device-id '<MM-1 endpoint ID>'
```

| Option | Mechanism and decision |
|---|---|
| W0 | Preserve event-driven MM-1 loopback as control; original GetBuffer packets are the earliest source presently exposed to DeskFuse. |
| W1a | Implemented, opt-in zero requested capture duration versus existing 200 ms capacity. Actual allocation/engine period are reported. No latency saving is asserted. |
| W1b | Query implemented; smaller-period execution unavailable for current MM-1. No silent companion or unsupported LOOPBACK/IAudioClient3 flag combination is installed. |
| W2 | Source contains a render-only WaveRT endpoint, not a direct user-mode PCM bridge. Endpoint enumeration reported MM-1 and NVIDIA monitor only, not an active DeskFuse endpoint; this does not establish driver-package installation state. Provisioning/routing/driver work requires separate approval. |
| W3 | Existing nonblocking SPSC → pipe → bounded Rust reader remains. Shared memory could improve post-capture handoff, not earlier source availability. No change without boundary evidence. |
| W4 | Process-loopback can change source selection; a new APO/driver tap is a larger design. Neither establishes an earlier equivalent system mix here. |

The 200 ms WASAPI request is capacity, not measured delay. Legal periods depend
on actual endpoint format/mode. Capture accepts 48 kHz stereo float32/PCM16,
using its existing conversion; unsupported formats fail rather than being
mislabelled. Per-window selection does not imply an independent audio stream.
Simultaneous virtual-endpoint plus physical-speaker playback would require an
explicit additional routing/sink design; it is not an implemented property.

The existing optional original-packet trace now accepts all packet sizes in
fixed preallocated storage and reports omissions. It still writes only after
capture stops. Small packets cannot falsely claim two complete 240-frame
fragments. Native packet count/min/max and negotiated capacity/period are reported
without active-path console output. No driver or endpoint provisioning changes.

## Android and reserve contracts

ABI v8 adds a validated 32-byte startup configuration and additive symbols.
Historical records/entry-point signatures remain unchanged. The new Rust loader
requires ABI 8 before structure I/O; deploy matching Rust/native binaries.

- Sharing: 0 shared, 1 exclusive requested. Actual grant is reported; shared
  fallback is not exclusive success.
- Usage: 0 leaves platform default unset; 1 requests GAME. Actual usage and
  device are reported. No invisible fallback/reopen loop.
- Native raw reserve: 1440/1200/960/720/480 frames. Startup and re-prime both
  require callback demand plus that same follower target. With fades+sinc,
  nominal playable remainder excludes 96 future-support frames; this is not
  complete output latency.
- Impossible demand latches a terminal status, supplies/accounted silence and
  preserves source/phase until the existing owner health check closes the
  experiment. It cannot grow the ring or silently change reserve. Smaller
  reserves can truthfully starve on consecutive large pulls; safety is not
  a continuity guarantee.
- Fixed callback-size buckets are reported as pre-close aggregates. Optional
  `-MeasureCallbacks` adds two monotonic clock observations per callback and
  reports wall time, including scheduling interruption; it is not CPU time.
  Compare timing-enabled runs only with matching instrumentation. No callback
  output, allocation or blocking was added; ordinary runs do not read clocks.

The existing runner accepts `-WindowsCapturePolicy reference|minimum`,
`-AndroidSharingPolicy 0|1`, `-AndroidUsagePolicy 0|1`,
`-NativeReserveFrames 1440|1200|960|720|480`, `-MeasureCallbacks`, and explicit
`-WindowsCaptureExecutable`/`-BuildReceipt` for protected archived sets.
Receipt verification matches artifact role/hash/profile independently of old
absolute locations and separates build-source commit from runner checkout.

## Build, comparison and rollback

Use the existing matching Release builder from this isolated branch, with
the existing VS/NDK/Rust environment:

```powershell
.\vnext\qualification\build-audio-pair.ps1 -LabRoot $PWD.Path `
  -ExpectedBranch experiment/vnext-audio-latency -RustProfile release
```

Existing native CTests, Rust tests and Python provenance/presentation checks
cover the changed paths. Build verification is not device qualification.

The standalone Audio Lab is maintained separately, outside this repository.
It invokes the existing runner; it is not another transport/audio engine.
Compare default settings on the new matched set first, then independent W1a,
A1, A2 and reserve-25 ms arms. Exclusive+GAME follows reviewed individual results.
Do not reduce hardware buffer size alongside native reserve or other controls.
User readiness precedes playback and user listening feedback precedes the next
run. Start copies native test binaries to `/data/local/tmp`; no APK/driver is
installed and MM-1 routing is unchanged.

Rollback closes the experiment and chooses the protected matching ca747d8
binary set with its original receipt/default settings. Keep other historical
references. Do not restore defaults or remove devices that this task did not
change. Private credentials, identifiers, raw evidence and recordings stay local.

Unimplemented/unqualified: W1b execution on a capable alternative endpoint,
virtual-endpoint provisioning, direct PCM/shared-memory handoff, adaptive reserve,
alternative Android routes, CPU/energy measurement and acoustic qualification.
Windows QPC, Android monotonic time and acoustic clocks cannot be subtracted
without an actual clock model. Hardware capacity is not queued/acoustic latency.

Primary references: [WASAPI initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize),
[IAudioClient3 periods/flags](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-initializesharedaudiostream),
[AAudio negotiation](https://developer.android.com/ndk/guides/audio/aaudio/aaudio).

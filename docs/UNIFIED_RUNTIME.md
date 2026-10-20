# Unified DeskFuse runtime

This describes the code consolidated into `main` by PR #11 (`1b9165c`). Both
unified Windows/native and Android CI runs passed on its source head `c26732c`,
including actual Kotlin/C# encrypted TCP interoperability. Physical-device
acceptance is deferred by the owner.

## Build and packaging

The Windows host builds `windows/native/FocusWindow.Gpu` through CMake and
packages `FocusWindow.Gpu.dll` beside the executable. Install the VS 2022 C++
toolchain, Windows SDK and CMake build component; put CMake on PATH. No POC
executable is built, invoked or packaged.

```powershell
dotnet publish windows/agent/FocusWindow.Agent/FocusWindow.Agent.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -warnaserror -o out/unified-windows
dotnet test windows/agent/FocusWindow.Tests/FocusWindow.Tests.csproj -c Release
cmake --build windows/native/FocusWindow.Gpu/.build --config Release --target FocusWindow.Gpu.ContractTests
ctest --test-dir windows/native/FocusWindow.Gpu/.build -C Release --output-on-failure
# From android/, with Android SDK 35 and Java 17 or 21 configured:
./gradlew :app:testDebugUnitTest :app:assembleDebug :protocol3:check :protocol3:interopJar --no-daemon
# From the repository root, with Java on PATH, run the actual Kotlin/C# TCP bridge:
# DESKFUSE_KOTLIN_INTEROP_JAR must be the absolute path to
# android/build/protocol3/deskfuse-protocol-v3-kotlin.jar.
dotnet test windows/agent/FocusWindow.Tests/FocusWindow.Tests.csproj -c Release --filter FullyQualifiedName~ProtocolV3InteropTests
```

The normal CI workflow runs these software gates and provides a Windows x64
host directory and Android debug APK as artifacts. Debug APKs are development
artifacts, not signed production releases. Native ABI tests reject invalid
arguments without opening a real HWND, adapter, virtual display or driver.
The same native test target exercises the production latest-frame queue, including
the final update after a burst, 144 Hz to 60 fps pacing, bounded surface ownership,
duplicate timestamps and shutdown. CI also compiles Android's actual crypto sources
into a test-only JVM harness and exchanges authenticated records with C# over TCP.

## Actual application source

An approved display session uses a PC-configured executable:

```text
FocusWindow.Agent --workspace-app "C:\path\Application.exe" --workspace-args "application-specific new-instance arguments"
```

This command describes a runtime configuration; it is not an authorization to
start hardware tests automatically. The executable path and arguments come from
the PC owner, never arbitrary phone text. Missing source configuration fails
before monitor allocation. Single-instance handoff, child-process launchers and
applications unable to provide the requested PMv2 window are explicitly refused.

The host positions only a new window in the launched process, verifies the
identified virtual monitor, actual DPI, DWM frame bounds and exact WGC size, and
streams that HWND. Original physical desktop windows are not moved or resized.
The source catalog retains one owned app per enrollment across reconnects within
the host process. An independent attachment lease prevents competing capture
owners. Disconnect releases GPU resources and the owned virtual display, but
never kills the app or discards unsaved documents. Windows may rehome that
DeskFuse-created window when its virtual display departs. Restarting the host
does not adopt unknown existing windows.

Requested frame-rate changes reopen the GPU stream through the normal session
reconnect and reuse that enrollment's app; they do not relabel an encoder that
is still running at its old cadence. Preferences are retained within this host
process and capped by the actual negotiated display/phone refresh limits.

Pointer coordinates use captured DWM frame pixels, including nonclient chrome.
Input is blocked if source geometry changes, another window covers the target,
or keyboard focus belongs elsewhere. Relative motion is clamped to the source.
Separate owned dialogs/popups are not composited into the source; arbitrary app
clones, complete modal-dialog workflows and runtime app switching still need
qualification. Explicit mainline window-list/focus, file and clipboard actions
remain separate supported controls; focusing a physical window does not replace
the GPU source or make it an independent clone.

## Runtime load and bandwidth

Quality and functionality take precedence; the optimization target is runtime
CPU/GPU/memory load and especially bandwidth, not source-code size.

- One MTA thread owns native open/read/close and COM lifetime. No per-frame tasks
  or steady full-frame CPU readback.
- Two registered GPU texture slots, one latest pending WGC capture and one
  queued encoded packet. Pacing retains the newest pending update until the next
  encode deadline, including a final repaint followed by idle. Repeated compositor
  timestamps are rejected before GPU copy. Different pictures are not guessed
  from arrival rate; idle periods do not generate padding video.
- Original compositor QPC reaches transport PTS. The advertised cadence is the
  configured cap, not a measurement of delivered frames.
- NVENC keeps the POC SDR BT.709 full-range and H.264 4:2:0 contract, with VBR
  under the configured scene bitrate budget, no filler, lookahead or B-frame
  queue. SPS/PPS/IDR is requested on startup and recovery rather than emitted
  periodically. An idle screen can recover from the retained GPU texture.
- Bounded video backpressure drops obsolete capture before encode, preserving
  H.264 dependencies instead of causing repeated large recovery frames.
- A single ordered AES-GCM writer prioritizes control and recent PCM audio,
  then alternates video/file work. This cannot interrupt a record already being
  transmitted; TCP head-of-line blocking and PCM bandwidth remain limitations.
- Encryption writes directly into the final framed network buffer and reuses
  authenticated per-direction cipher instances. This removes a full-packet
  allocation/copy and repeated key setup while preserving the existing wire bytes,
  nonce ordering and replay protection.
- Five-second host diagnostics report measured delivered video fps and total
  encrypted outbound kbit/s, including control/audio/files. No bandwidth,
  fidelity or latency improvement is claimed as measured on physical hardware.

## Device Bridge and retained mainline behavior

The cumulative feature stack through `e62f371` is an ancestry parent of merge
`c12367c`, alongside the GPU integration history. It includes composable peer
capabilities, authenticated per-device accessory leases, PC loopback playback,
remembered Android audio preferences, foreground microphone capture and explicit
per-enrollment cable routing. Source factories still preserve the encoded GPU
seam; the old CPU/FFmpeg loop is not restored as the primary video path.

PC audio remains off unless the host uses `--enable-pc-audio`. The experimental
microphone cable requires `--experimental-mic-cable`,
`--phone-mic-output-id` and `--phone-mic-device-id`; a media peer cannot acquire
display/input/file privileges. These are transport/routes, not Windows PnP
speaker, microphone or camera endpoints. Accessory speaker/touchpad/camera UX and
native providers are unfinished; Android still initiates display sessions by
default. Android runtime permissions remain authoritative, and microphone
capture stops when its Activity leaves the foreground.

Native 2/6/8-channel output uses actual speaker masks. A 7.1 source negotiated
down to 5.1 sends six output channels with rear/side folding; unsupported output
gets a speaker-aware stereo downmix. Genuine multichannel routing requires real
output hardware and remains physically unvalidated.

## Safety and remaining release gates

The current FocusIdd backend still admits only one display owner. A process file
lease plus an initial driver-status check refuses an existing unowned monitor;
only a monitor this backend created may be removed. Physical display drift ends
the session. The LAN host refuses an elevated token. No installed driver, ACL,
registry, global DPI or physical topology changes are part of software checks.

An installed driver that denies normal-user access still needs an approved
permission solution/local privileged boundary; this integration does not install
one. WGC/NVENC on a real app, exact phone pixels/DPI, Android color/decode,
congestion/idle recovery, dialog/focus behavior, A/V/microphone latency and
disconnect/crash display cleanup must be accepted on an authorized test setup.
Other GPU encoders remain unsupported and fail explicitly. Legacy encoder test
utilities remain in source pending real acceptance; they are not the shipping
video composition. Obsolete CPU-session fixtures are explicitly quarantined,
with GPU/bridge/session coverage in `UnifiedSessionTests`; independent protocol
denial/version tests no longer require FFmpeg. Standalone old encoder tests
remain optional when their external tools are provided.

The owner deferred physical-device acceptance until after the code is built.
The unified tree is consolidated into `main` after software validation; this is
not a physical acceptance or production-release claim. The original POC branch
remains read-only and retains its history.

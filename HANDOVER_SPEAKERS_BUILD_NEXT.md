# BUILD NEXT: native speakers in both directions

This is an implementation handoff, not a request for another planning round.
The owner is handing work to another agent because of model credit limits.
**BUILD and DELIVER the next working slice; do broad regression afterwards.**
Do not spend time rewriting the documentation trail. Preserve the POC and its
history: its useful GPU technology is already integrated into the main product.

## 1. Exact assignment and owner decisions

Build these two routes:

1. **Android -> PC speakers:** Android selects the PC in Android's **normal
   system audio-output picker**. Received audio plays through **Speakers (MM-1)**
   on this Windows PC. This must be a native output route, not a DeskFuse-only
   playback-capture button pretending to be a system device.
2. **Windows -> Android speakers:** the Android phone appears as an actual
   Windows playback endpoint in the list under the Windows taskbar speaker
   icon. Selecting it sends Windows playback to the phone's physical output.

The owner explicitly chose the Android system picker when asked. A future
DeskFuse button may control these routes but is not the initial deliverable.
Speakers come before microphone/camera. Do not switch back to a mic-first plan.

The competitive intent is seamless native integration through one trusted
product, with low runtime load, especially bandwidth. Code size is not the
optimization target. Preserve functionality already in main.

## 2. Checkout, code delivered, and live state

- Work only in `D:\Coding\DeskFuse`; actual Git root is
  `D:\Coding\DeskFuse\v0.3`. Reading other projects under `D:\Coding` is allowed.
- Repository: https://github.com/braindamp/deskfuse ; current branch `main`.
- Latest audio source commit at this handoff:
  **`6e40b844f63e32d0ccd09f812eab4d2bbdf2f782`**, pushed to main.
  The handover itself is committed afterwards. Inspect HEAD; do not reset.
- `windows/audio/DeskFuse.Audio/DeskFuse.Audio.csproj` is a **library referenced
  by the existing agent**, not another running LAN host or detached POC.
- `SpeakerCommands.cs` implements native Windows Bluetooth receiver/discovery
  using `Windows.Media.Audio.AudioPlaybackConnection`.
- Agent CLI now dispatches `speakers ...` into that library in process.
- The agent target framework is now `net8.0-windows10.0.19041.0` so C#/WinRT
  APIs compile. It remains .NET 8. Core/test targets are unchanged.
- `Program.ListAudioDevices` no longer crashes the entire enumeration when a
  stale MMDevice's FriendlyName property store throws COMException.
- CI source path filters include `windows/audio/**` on push and PR.
- **No virtual audio driver/provider has been implemented or installed.**
  Do not report Windows -> phone native playback as completed.
- **No Android audio changes were made during this speaker slice.**

The previous live display host was left running from
`out/unified-windows/FocusWindow.Agent.exe`, with its PID recorded in
`out/acceptance/continuation/host.pid`. Discover current ownership before stopping
anything. The audio candidate is separately published under
`out/acceptance/speakers/windows/`; it has not replaced the live display binary.
Do not kill Notepad or discard unsaved documents when restarting a host.

## 3. Actual hardware facts, not guesses

Native discovery reports:

```text
Output [current]: Speakers (MM-1)
Output: ROG PG278QR (NVIDIA High Definition Audio)
No Bluetooth radio is available to Windows.
No paired Bluetooth audio source.
```

Both `Get-PnpDevice -Class Bluetooth -PresentOnly` and WinRT
`Radio.GetRadiosAsync()` found no Bluetooth radio. Do not spend iterations
calling StartAsync without hardware. The owner needs an available Bluetooth
adapter/radio and OS pairing for the native Android -> PC path. This does not
block building the Windows -> phone endpoint.

This PC is Windows 10 build 19045, current user `HANGBAK\Arne`, ordinary
non-elevated desktop process. The previous agent used codexlab; do not migrate
DPAPI/enrollment state between accounts. Existing enrollment works as Arne.

The connected phone is Xiaomi 24117RN76E, Android 16, 1080x2400, density 450
(DPR 2.8125). ADB is `D:\Android\sdk\platform-tools\adb.exe`; get the current
serial with `adb devices` rather than treating it as identity. Debug package:
`com.focuswindow.client.debug`, activity `com.focuswindow.client.MainActivity`.
USB currently provides developer access; the DeskFuse encrypted stream uses LAN.

## 4. What the receiver currently does and does not do

Use the integrated candidate:

```powershell
$audioHost = 'D:\Coding\DeskFuse\v0.3\out\acceptance\speakers\windows\FocusWindow.Agent.exe'
& $audioHost speakers devices
& $audioHost speakers receive --source-id '<ID printed by devices>' --output-name MM-1
```

Implementation in `SpeakerCommands.RunAsync`:

1. Read current Windows multimedia render endpoint using NAudio.
2. If `--output-name` is supplied, refuse a mismatching current output.
3. Enumerate paired Bluetooth audio sources with
   `AudioPlaybackConnection.GetDeviceSelector()` / `DeviceInformation.FindAllAsync`.
4. Resolve the explicit `--source-id`; call `TryCreateFromId`, `StartAsync`,
   then `OpenAsync`; keep the connection alive until cancellation.
5. Print native state changes, detach the event handler, dispose the connection
   on exit. Agent Ctrl+C passes its shutdown token to the library.

**Important limitation:** `--output-name MM-1` is a preflight guard. It does not
pin an arbitrary physical output or call a default-device setter. The Windows
Bluetooth playback API currently supplies playback through Windows' routing.
`OutputWatch` registers `IMMNotificationClient` and stops the receiver if the
multimedia default changes or the selected output departs. This avoids a
phone -> PC -> phone loop when a future phone playback endpoint becomes default.
No defaults, pairing state, radio state or registry policy are modified.

For simultaneous independent routes, investigate supported per-session routing
to MM-1. Do not remove OutputWatch and claim it is solved. The wrapper currently
has no supported API binding the incoming Bluetooth renderer to a selected
MMDevice. Per-app Windows audio routing may supply the user-controlled route;
verify actual renderer ownership instead of inventing an IAudioPolicyConfig hack.
The two individual directions can be delivered before claiming simultaneity.

Android's picker may show the PC's Bluetooth name, not the physical USB MM-1's
name. MM-1 is the PC-side playback destination. Do not rename the entire PC or
Bluetooth adapter behind the owner's back to fake a speaker-specific device.

Official implementation reference:
https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback

## 5. Next code to build: real Windows phone playback endpoint

The missing OS-facing component is a **per-enrolled-phone render endpoint**.
Build this first while native Bluetooth hardware is unavailable.

Use a dedicated audio provider under `windows/audio/`; do not modify/reinstall
the working FocusIdd display driver. Do not substitute an existing external
audio cable, name a logical registry entry "device", or merely mirror MM-1.
The endpoint must be enumerated by Windows as a genuine render device, with
stable identity and real removal/unavailability when its phone disconnects.

### Official source is already available locally

Ignored reference checkout:
`out/reference/windows-driver-samples/audio/sysvad/`

Upstream Microsoft sample revision:
`4d4618400525f9b2ce66fa2e267f58a55043be40`.
It was fetched with a sparse checkout of `audio/sysvad`; upstream root `LICENSE`
is present. Nothing from it has been copied into shipping source yet.

Relevant files:

- `TabletAudioSample/TabletAudioSample.vcxproj`: kernel WaveRT/PortCls build;
  dependencies include portcls.lib, stdunk.lib, libcntpr.lib.
- `TabletAudioSample/minipairs.h`: `g_RenderEndpoints` currently advertises
  speaker, headphones, HDMI, SPDIF; `g_CaptureEndpoints` advertises microphones.
  Create a speaker-only configuration; do not ship all sample devices.
- `adapter.cpp`: installs the endpoint minipairs. Keep per-device state in the
  adapter instance, not one process-global "active phone".
- `EndpointsCommon/minwavert.cpp`, `minwavertstream.cpp`: DMA buffer positions,
  stream clock, notification timing and pin handling.
- `EndpointsCommon/speakerwavtable.h`: render/offload/loopback pin descriptors,
  supported formats and AudioEngine connections.
- `TabletAudioSample/ComponentizedAudioSample.inx`: reference INF only. Its
  current model decoration has a **22621 minimum**. Copying it unchanged would
  exclude this Windows 10/19045 PC. Target an actually supported Windows 10
  build in both code/API usage and INF, not just by relabeling the INF.

Create a production audio project/package with its own DeskFuse IDs and service
name. Retain the upstream license if source is vendored. Prefer the smallest
active driver configuration, rather than enabling sample HDMI, microphones,
keyword detection, APO demonstration effects, offload paths and tone sources.
Do not spend time refactoring the whole Microsoft sample for aesthetics.

**Critical sample trap:** render output must be the application's real samples.
The sample stream code contains tone generation (`m_ToneGenerator.GenerateSine`)
and a hardware-style loopback pin. Do not assume that starting WASAPI loopback
on an untouched sample produces the rendered application audio. Prefer an
appropriate render-only topology with software-engine loopback; if retaining a
hardware loopback pin, implement its actual connection to render samples.
Inspect pin formats/connections rather than forwarding the sample sine wave.

Disable sample disk dumps: `adapter.cpp` has `g_DoNotCreateDataFiles = 1` by
default, and stream paths conditionally call `m_SaveData`. Preserve the disabled
state and avoid production audio-to-file writes. Keep the proper WaveRT clock
and event notifications; an endpoint whose level meter moves but has no working
clocked sample path is not the deliverable.

### Presence, privileges, identity

Reuse `DevicePeerRegistry.Identities(deviceId).Speakers`; its key already hashes
host scope + authenticated enrollment + capability. It is a logical key, not yet
a PnP ID. Map it deterministically to provider instance ID / KS interface naming.
Do not use IP address, socket, random per-connect GUID or a singleton endpoint.

Prototype actual provider device creation/availability before building an
elaborate manager. An installed WaveRT driver plus software-enumerated instances
is a candidate: examine `SwDeviceCreate` with the provider's hardware ID,
stable per-phone instance ID, and an owned lifetime handle. Account for its
callback/lifetime rules and verify security requirements on Windows 10.
Do not assume normal-user PnP creation succeeds just because a C# wrapper exists.

If creation/removal requires privilege, keep a **narrow local endpoint broker**
separate from the unelevated LAN host. Restrict it to DeskFuse audio instances,
the authenticated Windows user, bounded validated identity/label inputs and
connection-owned lifetimes. No arbitrary driver paths, device IDs or privileged
filesystem operations. Closing the owning host/broker lease must make the device
unavailable without deleting its persistent identity/preferences.

Build/package this code before asking about installation. No driver was
installed in this continuation. Do not enable test signing, disable Secure Boot,
modify FocusIdd or write system driver/security settings as an unannounced shortcut.

## 6. Wire Windows endpoint samples into the existing secure transport

Do not invent another unencrypted UDP audio protocol to get the first sound.
Use the existing authenticated per-device `PhoneConnection` and PCM lane first.

### Windows code seams

- `windows/agent/FocusWindow.Agent/WindowsLoopbackAudioCapture.cs`
  currently constructs **default-output** `WasapiLoopbackCapture()`. Add an
  explicit MMDevice/endpoint-ID path for the phone's actual render endpoint.
  A missing/unavailable phone endpoint must fail closed, never fall back to
  capturing MM-1 or the PC's mixed default output.
- `FocusWindow.Core/Session/HostConfig.cs` defines `IHostAudioCapture` and
  `HostServices.CreateAudioCapture(layouts, maxBytes)`. Add the explicit
  per-phone speaker-provider/capture seam while retaining existing optional
  mixed-PC loopback behavior. Name/scoping must distinguish the two routes.
- `PhoneSession.HandleAudio` demonstrates the existing setup:
  allocate stream ID -> send `ServerMessages.AudioState("started", ...)` ->
  capture.Start(samples => conn.TrySendAudio(samples.Span, streamId)).
  Teardown disposes the capture before releasing endpoint ownership.
- `FocusWindow.Core/Session/DevicePeerSession.cs` already accepts authenticated
  accessory peers without a monitor, validates capabilities and attaches a
  per-enrollment lease. Extend it to handle **speakers** and audio controls.
  It currently handles only microphone controls and keepalive.
- `DeviceCapabilities.ActiveForAccessory` currently returns an empty list.
  Report Speakers active only after a real provider endpoint is ready.
- `SessionHost` already supports concurrent accessory/display peers and a
  separate exclusive display admission boundary. Reuse it. A speaker-only
  session must not create a monitor or require `--workspace-app`.

Wire facts:

- PC -> phone `KIND_AUDIO = 5`; decrypted payload is 4-byte stream ID followed
  by PCM16LE. Preserve the existing byte order; inspect `TrySendAudio` and
  `LanTransport` rather than introducing a different envelope.
- AudioState communicates sample rate, channel count and speaker layout.
- `PhoneConnection.TrySendAudio` uses the existing bounded prioritized writer.
  Capture callbacks must not block on network writes. Encryption sequence and
  socket write order must remain identical; do not add a competing AES writer.
- Negotiate actual output support. Existing `PcmSpeakerLayout` supports native
  layouts and real downmixing. Keep multichannel behavior; don't hardwire the
  product boundary to 48kHz stereo just for a first implementation.

Windows loopback capture is a useful user-mode extraction candidate for the
new endpoint, provided it receives actual mixed samples with the chosen topology.
If kernel/user ring transport becomes necessary, keep it bounded and clocked;
do not add repeated allocations, packet queues or raw kernel pointers to solve
what WASAPI already supplies.

## 7. Android side: speaker availability must not depend on video

Existing paths:

- `transport/LanTransport.kt`: authenticated connection, AudioState control and
  KIND_AUDIO decoding already exist.
- `media/PcAudioOutput.kt`: AudioTrack configuration, real layout support,
  PCM writes and stop/dispose already exist. Reuse it.
- `session/SessionController.kt`: `requestPreferredPcAudio`, AudioState handling
  and `pumpAudio` show the current display-coupled playback flow.
- MainActivity currently starts a display Hello; it does not expose the complete
  accessory-speakers lifecycle. Server readiness alone does not complete this.

Implement a speaker-only authenticated client using the existing transport.
Send a Hello with role `accessory`/`media` and capabilities `["speakers"]`; do not
wait for a video Target, SurfaceView or virtual monitor before becoming ready.
After the host has actually provisioned the endpoint, request/enable its PCM
stream using the existing audio control semantics. The existing PC sound toggle
for default-output mirroring must retain its distinct meaning.

Retain enrollment and independent capability choices. Connection ownership
must remove only that phone's endpoint, and an older socket's cleanup must not
remove the replacement lease. `DevicePeerRegistry` already handles this race.

**Prevent a second feedback route:** if Android's system output is currently
the PC over Bluetooth, an unqualified default AudioTrack on the phone may send
Windows -> phone audio straight back to the PC. For the phone-speaker capability,
choose the intended local Android `AudioDeviceInfo` explicitly with
`AudioTrack.setPreferredDevice`, and check actual routing after playback begins.
Do not change Android's global route behind the user's back. Phone speakers,
headphones and the remote-PC route are distinct destinations.

Put sustained playback/connection ownership in an appropriate Android service
lifetime, not only the video Activity. Current `SessionService` is declared
`dataSync`; do not silently reuse that classification as a permanent playback
service. Declare the service type/permissions actually required by playback or
the connected-device lifecycle. Keep this scoped to delivering sound, not a
large background-permissions framework. No microphone/camera capture permission
is needed merely to play received PC samples.

Do not implement AudioPlaybackCapture/MediaProjection as the Android system
speaker route: the owner explicitly chose native output selection. Those APIs
capture eligible apps; they do not register a normal Android system audio output.

## 8. Runtime/performance directives

- Build the real sample path first; do not optimize fake endpoints/status labels.
- Use native Windows Bluetooth transport for Android -> PC; its codec/policy is
  owned by the OS. Do not claim losslessness or surround from a Bluetooth route.
- Reuse native AudioTrack and WASAPI for the reverse path. No per-frame/task
  factories, giant jitter buffers, disk recording or unbounded PCM queues.
- Current 48kHz stereo PCM16 costs 1.536 Mbit/s before framing. Treat this as an
  initial working wire path, not the final bandwidth target. Add negotiated
  compression/quality modes after delivery without silently reducing quality.
- Keep source clocks/sample positions available for synchronization. Current
  PCM envelope lacks a full clocked media protocol; do not fake latency metrics.
- Do not let file/video backpressure indefinitely block audio. Existing priority
  is helpful, but all media currently shares ordered TCP. A separate authenticated
  latency-sensitive media lane is later work, not permission to bypass encryption.

## 9. Build environment and exact current artifacts

From the Git root:

```powershell
$env:PATH = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;' + $env:PATH
dotnet publish windows/agent/FocusWindow.Agent/FocusWindow.Agent.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -warnaserror -o out/acceptance/speakers/windows
```

Keep the entire publish directory, including `DeskFuse.Audio.dll`, WinRT runtime
assemblies and `FocusWindow.Gpu.dll`. Do not run an old standalone
`out/speakers/DeskFuse.Audio.exe` as the product; that was an intermediate build
before the audio project became a library.

Windows SDK/WDK 10.0.26100.0 is installed under
`C:\Program Files (x86)\Windows Kits\10`; 22621 include headers also exist.
The WDK `build/10.0.26100.0/WindowsDriver.KernelMode.*` targets are present.
VS 2022 Community is installed. Audio WDK project compilation has **not** yet
been attempted; verify the VS driver toolset selection rather than reinstalling
the known-good display driver.

Android Java:
`D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1`; SDK: `D:\Android\sdk`.
Build from `android/` with JAVA_HOME / ANDROID_HOME / ANDROID_SDK_ROOT set:
`./gradlew.bat :app:assembleDebug --no-daemon`.

Installed debug APK uses codexlab's old default debug signer. Arne's default
signer differs. The owner explicitly authorized this session to use
`C:\Users\codexlab\.android\debug.keystore` for update signing. Do not uninstall
and lose enrollment. The working re-signed display APK is at
`out/acceptance/continuation/app-debug-signed.apk`; no new audio APK exists yet.
Do not search unrelated keys or commit signing material.

If the default sandbox fails with `helper_sandbox_lock_failed`, use scoped
`require_escalated` execution. This grants tool access, not an elevated Windows
process. Start background helpers with `-WindowStyle Hidden` and scoped logs.

## 10. Preserve these already-working parts; avoid detours

The GPU POC was integrated by PR #11. Native WGC -> D3D11 visible-frame ROI ->
NVENC -> AES-GCM -> Android hardware decode works, with phone typing confirmed.
The 1080x2400 phone uses verified 288 DPI/300%, compared with requested 270 DPI:
6.7% larger UI, 1:1 pixels, no resampling blur. Do not restart DPI research or
replace the integrated GPU engine with a CPU/FFmpeg default.

Minimizing caused a fatal-error reconnect loop; that loop is fixed. The owner
said minimize is normal Windows behavior and asked to defer further UX work.
The current stop/manual-retry screen is an interim limitation. Do not build a
minimize framework now.

Future layout is full immersive fullscreen with swipe-down Android system bars.
The owner deferred it until looking at old Desklink code. Do not switch to the
previous safe-area/letterbox recommendation or do layout work in this task.

Current product/architecture decisions are in
`plans/DeskFuse_Product_Deliverables_2026-10-20.md`. Read specific details as
needed; do not clean up stale prose or reopen the already-completed POC merge.

The receiver candidate builds with warnings as errors. Native discovery and
missing-source failure were exercised. Existing .NET tests ran before the
owner's build-first clarification: 184 passed, 10 historical/optional skips.
No Bluetooth playback or new Windows playback endpoint has been demonstrated.
Keep that boundary honest. Proceed with code delivery; don't turn this handoff
into another test/planning exercise.

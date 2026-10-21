# Handover: the DeskFuse phone-speaker route (Windows → Android)

For a strong reasoning model picking this up. Everything below is measured on
the target machine. Read `ERRORS.md` first — it records the mistakes that
cost the most, with the guard for each.

---

## 1. What the product must do

A phone enrolled on a Windows PC appears in the Windows sound list as **its
own playback device**. The user selects it, and the PC's sound follows to
the phone. The Windows volume control is the phone's volume control.

The other direction (Android → PC through Android's own system output picker)
is a separate route that needs a Bluetooth radio this PC does not have. It is
not the subject of this handover.

## 2. Where it stands

### Proven, on this machine

| Fact | How to see it |
| --- | --- |
| The driver builds, signs, catalogues, installs and **loads** | `pnputil /enum-drivers` → `oem127.inf`; `pnputil /enum-devices /class MEDIA` → the DeskFuse device `Status: Started` |
| The endpoint is a **real render endpoint** in the Windows sound list | `out\acceptance\speakers\windows-final\FocusWindow.Agent.exe speakers devices` → `Speakers (DeskFuse Xiaomi 24117RN76E)` |
| It **accepts real application audio** | the earlier "proof" was wrong (SysVad's hardware loopback generates a 3 kHz tone); the endpoint now exposes an ordinary two-pin shared-mode render filter, and the capture returns the application's samples |
| One **device instance per phone**, with a stable path | `ROOT\DESKFUSEAUDIO\DESKFUSE-SPEAKERS-F8D6479478A5FDE3C725C797508F38E9` (the enrollment hash) |
| The phone's **accessory session** authenticates with the host | host log `Authenticated accessory peer joined: a9e4c4f7-…` |
| The host creates the phone's endpoint and names it | host log `Phone speaker endpoint ready: Speakers (DeskFuse Xiaomi 24117RN76E)` |
| The phone **starts its output** when the PC says the capability is live | phone log `bridge state: requested=[speakers] active=[speakers]`, then `starting the phone audio output` |
| The Windows volume **moves the phone's own media slider** | the phone's `STREAM_MUSIC` index followed the PC's level (8 → 10) |
| The transport **drops nothing** | host log `phone audio: 1014 packets sent, 0 dropped` (seen at 03:05, before later regressions) |

### Broken right now

**Toggling "Phone speaker" does nothing.** The host log shows **no**
`Authenticated accessory peer joined` for the speaker session, so the phone's
*accessory* connection never reaches the host — even though:

* the app is installed and running (the display session works: ~70 fps),
* `SpeakerService` is alive and is a foreground service with
  `types=0x2` (mediaPlayback),
* the phone's `SpeakerController` contains a working `BridgeHello` and the
  `start_phone_speakers` request.

**It worked at 03:05** (host log: `phone audio: 9166 packets sent, 0 dropped`,
and the owner heard the sound). Everything since then was mine, and the
regressions are itemised in `ERRORS.md` (#26, #27 and the churn fix).

### The three most likely causes, in order

1. **The phone's speaker service does not (re)connect.** The service was
   killed by each APK reinstall, and the `SpeakerController` only reconnected
   after a fix landed in the last commit. Check whether the service's
   `SpeakerController` actually calls `transport.connect` after the toggle.
2. **The transport is refused by the host's accept queue.** The host's
   backlog was 4 and the accept loop could wedge; it is now 64 and the loop
   survives exceptions, but the fix is unverified in the live path.
3. **The host never receives `start_phone_speakers`.** The host logs
   `the phone asked for its endpoint's audio` the moment it does. If that
   line is absent while the accessory peer *is* joined, the phone is not
   sending it.

### The exact diagnostic sequence

```
# 1. phone on, speaker toggled on
adb logcat -d | grep -iE "DeskFuseSession|DeskFuseSpeaker|DeskFuseTransport" | tail -20
# 2. host side (the host writes to out\acceptance\speakers\host-final.log)
grep -E "accessory|endpoint ready|phone asked|phone audio" out/acceptance/speakers/host-final.log | tail -10
# 3. the broker
tail -5 out/acceptance/speakers/broker.log
# 4. the endpoints Windows offers
out\acceptance\speakers\windows-final\FocusWindow.Agent.exe speakers devices
```

Interpret them like this:

| Phone log | Host log | Meaning |
| --- | --- | --- |
| no `DeskFuseSession: start:` | nothing | the service never started the controller — a service/toggle problem |
| `connecting to 192.168.2.107:47810` | nothing | the TCP connect is not completing — host accept queue |
| `bridge state: … active=[speakers]` | `Authenticated accessory peer joined` | the protocol works; the request is missing on the phone |
| — | `the phone asked for its endpoint's audio` | the request arrived; the capture is the problem |
| — | `phone audio: N packets sent, 0 dropped` | the sound is on the wire; the phone's playback is the problem |

## 3. The code map

| File | What it does |
| --- | --- |
| `windows/driver/DeskFuseAudio/` | the KMDF WaveRT driver (vendored from Microsoft's SysVad sample, MS-PL, `LICENSE.upstream`). `src/minipairs.h` is the endpoint configuration (one speaker endpoint, no capture). `DeskFuseAudio.inf` is a classic KMDF INF for Windows 10 19045. |
| `windows/audio/DeskFuse.AudioBroker/` | the only elevated process. Creates the phone's device instance (`DeviceInstance.cs` + `SetupApi.cs`), keeps it for the enrollment, names it, and resolves the endpoint. `Program.cs` holds the pipe protocol and the lease logic. |
| `windows/audio/DeskFuse.Audio/PhoneEndpointContract.cs` | the identity: the endpoint id, the device name, the peer-hash rules. |
| `windows/audio/DeskFuse.Audio/PhoneEndpointBrokerClient.cs` | the host's side of the broker pipe. |
| `windows/agent/FocusWindow.Core/Session/PhoneSpeakerRoute.cs` | the route: provision the endpoint, capture it, pace the audio to the phone, mirror the volume. |
| `windows/agent/FocusWindow.Core/Session/DevicePeerSession.cs` | the accessory session: handles `start_phone_speakers`. |
| `windows/agent/FocusWindow.Agent/WindowsLoopbackAudioCapture.cs` | the capture of one explicit endpoint (never the default output). |
| `windows/agent/FocusWindow.Agent/PhoneSpeakerProvider.cs` | the provider the route uses; owns the endpoint and its volume. |
| `android/.../session/SpeakerController.kt` | the phone's speaker client: the accessory hello, the request, the playback. |
| `android/.../session/SpeakerService.kt` | the media-playback foreground service that owns the speaker session. |
| `android/.../media/PcAudioOutput.kt` | the AudioTrack: the explicit built-in speaker, the volume, the partial-write carry. |
| `docs/av-sync-design.md` | the audio/video alignment design (not built). |
| `ERRORS.md` | every error, its cause, and its guard. |

## 4. The traps, all of which have already cost time

1. **Never change a numeric constant from memory.** `DIF_REGISTERDEVICE` is
   `0x19` (25). The wrong value produced `ERROR_DI_DO_DEFAULT`
   (`0xE000020E`) and hours of confusion. The API contracts are quoted from
   the local SDK in `challenge.md`.
2. **The device-info set must be class-less** for a caller-chosen instance
   id, and the device name must be the **full instance path**
   (`Root\DeskFuseAudio\deskfuse-speakers-<hash>`).
3. **Do not destroy the device Windows is playing through.** That emptied the
   sound list and could leave the shell's sound flyout unusable. The endpoint
   now belongs to the enrollment, not to a session.
4. **A new control message on one side only kills the session.** The phone
   now skips messages it does not understand; there is still no regression
   test for it (`ERRORS.md` #27, and the TODO in
   `docs/handover-phone-speaker.md`).
5. **Kotlin 2.0.21's K2 parser rejects a C-style `for` loop** in this
   project with a for-in parse error. Use `repeat(n) { i -> … }`.
6. **The Android sources are CRLF.** A Python replacement written with `\n`
   patterns silently does nothing. Confirm the file changed.
7. **Never accept "there are bytes" as proof of audio.** SysVad's hardware
   loopback generates a tone; the diagnostic now checks the frequency content
   and the silence before and after.
8. **One UAC prompt per change, and never while the owner is working.** A
   host restart drops their session, and each restart leaves another Notepad
   behind (the host never kills apps with unsaved documents).

## 5. How to build and deploy

```powershell
# agent (the host). Must be published to a staging folder: the running
# agent holds its own files.
cd D:\Coding\DeskFuse\v0.3
$env:PATH = $env:PATH + ";C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin"
dotnet publish windows/agent/FocusWindow.Agent/FocusWindow.Agent.csproj -c Release -r win-x64 `
  --self-contained true -p:Platform=x64 -warnaserror -o out/acceptance/speakers/windows-next

# broker
dotnet publish windows/audio/DeskFuse.AudioBroker/DeskFuse.AudioBroker.csproj -c Release -r win-x64 `
  --self-contained true -o out/acceptance/speakers/broker-next

# phone
cd android
$env:JAVA_HOME = D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1
$env:ANDROID_HOME = D:\Android\sdk
./gradlew.bat :app:assembleDebug --no-daemon
D:\Android\sdk\platform-tools\adb.exe install -r app/build/outputs/apk/debug/app-debug.apk
```

**The host must run as the interactive user** (`HANGBAK\Arne`), never
elevated: it refuses to start elevated, by design. The scripts in
`out\acceptance\speakers\` do this with the scheduled task
`DeskFuseFinalHost` (`swap-final.ps1` is the one that installs a new build
and restarts the host; `restart-broker3.ps1` restarts the broker). Each needs
one UAC approval.

The driver is installed already; rebuilding it needs the Arne signing
certificate (`/p:TestCertificate=<thumbprint> /p:GenerateTestCertificate=false
/p:Inf2CatUseLocalTime=true`).

## 6. The next thing to try

1. Make the phone's speaker session connect and stay connected. That is the
   blocker; everything downstream of it was working at 03:05.
2. Then confirm, with a signal rather than bytes: a known tone played into
   the phone's endpoint must come back on the phone with that frequency, and
   be silent before and after. The capture side already has this check
   (`windows/audio/DeskFuse.Audio/LoopbackSignalProbe.cs`); the phone side
   needs the equivalent.
3. Then the audio/video alignment (`docs/av-sync-design.md`): the host stamps
   both streams with one clock (`FocusWindow.Core/Media/HostClock.cs`), the
   phone measures the offset (`media/SyncMeasurement.kt`), and the picture is
   the master. Media mode spends latency for exact agreement; gameplay mode
   does not spend latency on the sound and instead shortens the video path.

## 7. State of the machine

* Host: `out\acceptance\speakers\windows-final\FocusWindow.Agent.exe`,
  running as the interactive user (PID changes on every restart).
* Broker: `out\acceptance\speakers\broker-real\DeskFuse.AudioBroker.exe`,
  elevated, the only privileged process.
* Driver: `oem127.inf` installed; the built package is under
  `out\acceptance\speakers\driver-real\`.
* Phone: `com.focuswindow.client.debug` installed, enrollment preserved
  (never uninstall it); the USB DAC "MM-1" shows `Status: Disconnected`
  because it is unplugged, not broken.
* The phone's media volume is at 15/15 and not muted.

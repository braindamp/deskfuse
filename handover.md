# DeskFuse audio continuation — 2026-10-21

## Goal and immediate task

Finish Windows → Android phone playback as a real, individually named Windows output device. The difficult device creation and genuine-audio driver work is implemented, installed and verified. **The updated real-audio path has not yet been heard on the phone.** Next work is a normal music/video playback check and any concrete fix that reveals. Build and deliver; do not restart architecture planning or documentation cleanup.

Work in `D:\Coding\DeskFuse\v0.3` (actual Git root), branch `main`. Read anything in D:\Coding, modify only D:\Coding\DeskFuse. Run as interactive **HANGBAK\Arne**, not codexlab. The user prioritizes runtime load, especially bandwidth, over implementation size. Preserve the integrated GPU workspace and existing features. POC source/history must remain.

## Current deployed state

- Final normal-user LAN host: PID **52764**, `out\acceptance\speakers\windows-final\FocusWindow.Agent.exe`. Launch args: `--workspace-app C:\Windows\System32\notepad.exe --fps 60 --bitrate 20000 --log D:\Coding\DeskFuse\v0.3\out\acceptance\speakers\host-final.log`.
- Final elevated audio broker: PID **52960**, `out\acceptance\speakers\broker-real\DeskFuse.AudioBroker.exe`. Logs `broker-real.log` / `broker-real.err.log` in the same speakers directory. Verify process identity before using any recorded PID; PIDs can change.
- Current driver package **oem127.inf**, version **10/21/2026 0.57.52.884**, built package `out\acceptance\speakers\driver-real\DeskFuseAudio\`. SYS size 69,392 bytes. Normal PnP installation completed. No security settings changed.
- Android APK **already installed**, preserving enrollment/data: `out\acceptance\speakers\app-audio-fixed.apk`. Signed with the same debug certificate as the previous app. Public signer SHA256: `e8a1b33378ef147244065f61b11e596810517f8c8deaa00ab864f07bb6a0210e`.
- **Phone app intentionally force-stopped. Do not automatically start playback.** User needs to reopen Focus Window, enable Phone Speaker, select the Xiaomi Windows output, and try ordinary audio at a comfortable phone media volume. No diagnostic tones on the phone.
- USB ADB: `D:\Android\sdk\platform-tools\adb.exe`; serial `oj6pjjhaayjvlntc`; Xiaomi model 24117RN76E; package `com.focuswindow.client.debug`; activity `com.focuswindow.client.MainActivity`. Media session uses existing encrypted LAN, not ADB transport. Last LAN addresses: PC192.168.2.107, phone192.168.2.114, port47810.
- Obsolete generic **ROOT\MEDIA\0000 / DeskFuse Phone Speakers has been removed**, per user request. Do not restore it. No phone endpoint while the phone is disconnected is correct. Matrix test nodes were removed too.
- Existing Arne enrollment/DPAPI state remains in `%LOCALAPPDATA%\FocusWindow`. Do not rotate pairing, change accounts, or pass `--new-pairing`. Existing Notepad documents must survive.

## What was fixed — do not undo

### 1. Actual SetupAPI contracts

`windows/audio/DeskFuse.AudioBroker/DeviceInstance.cs` now creates the EXACT caller-chosen stable path `Root\DeskFuseAudio\deskfuse-speakers-<32hex enrollment hash>`.

The old `challenge.md` contains false API declarations/conclusions. Correct contracts: DIF_REGISTERDEVICE **0x19**, not0x18; SP_DEVINFO_DATA **32 bytes on x64**, binary GUID pointer to SetupDiCreateDeviceInfoList; property setter **five arguments**, getter includes output type pointer. Registration has no invented class-install payload. `0xE000020E` was ERROR_DI_DO_DEFAULT, caused by calling the wrong installation function. Service/manufacturer come from the INF. DiInstallDevice binds the existing installed package to this exact node.

Skip DiInstallDevice when reopening an existing correct node with DN_STARTED and problem0. Reinstalling healthy nodes restarts their subtree: Windows kept the old endpoint active for about4.4seconds, then deleted it, leaving capture on a stale GUID. `RemoveAsync` now holds the broker device gate until the old active endpoint actually disappears (bounded20seconds), so immediate replacement cannot match a dying endpoint. Never cache a GUID across actual removal/recreation; stable identity is the PnP path, not the endpoint GUID.

`Program.cs` matches only active render endpoints whose property store names the exact instance path, including `{1}.ROOT\...` form. No friendly-name/single-candidate/shared-device fallback. Broker responses use named record fields. Retained named-pipe connections are endpoint leases; each phone session has an independent client. Generation ownership prevents an old disconnect removing a newer lease. One-shot `speakers endpoint create` immediately exits and releases its lease; use the live host or matrix instead.

### 2. Multiple adapters

The vendored SysVad `NewAdapterCommon` explicitly allowed only one adapter, causing Code10 on the second node. Removed that singleton and disconnected unused sample render-to-disk code/static state from the build. Per-adapter objects and PortCls subdevice registration remain independent. Do not reintroduce shared adapter/global endpoint ownership.

### 3. Critical fake-loopback discovery

**Earlier claims of genuine application audio in challenge.md and previous logs were wrong.** SysVad's advertised hardware loopback pin continuously generated a **3kHz, 50% amplitude sine**, regardless of application playback. A diagnostic that merely checked nonzero bytes falsely passed it; it also interpreted float capture as PCM16. The user heard a very loud sustained beep. Root immediately stopped the app, and the user confirmed silence. **Do not repeat diagnostic tones through the phone.**

The fix is already built and installed: `src/speakerwavtable.h` / `minipairs.h` expose an ordinary **two-pin WaveRT render filter**, system sink0 → bridge1, physical topology connection1, flags0, no hardware audio-engine node, no offload or hardware loopback pin. Windows supplies the shared mixer and genuine WASAPI software loopback. Existing six PCM16 stereo rates24/32/44.1/48/88.2/96k and mode defaults remain; default48k. Existing capacity6 for concurrent Windows processing modes remains. Synthetic capture is unreachable through advertised pins. This is the documented Windows fallback: https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording .

### 4. Object lifetimes and volume

- `PhoneSpeakerProvider.cs` retains MMDevice for the endpoint's lifetime; disposing it in the constructor invalidated its cached AudioEndpointVolume COM wrapper. Device owns that wrapper; unsubscribe then dispose device once. Windows mute now sends zero volume.
- `WindowsLoopbackAudioCapture.cs` also retains its explicit MMDevice until capture disposal.
- `DevicePeerSession.cs` uses `await using` for PhoneSpeakerRoute, including provision failures. Removed duplicate provider release and fixed successful retry to continue into capture.
- `PcAudioOutput.kt` caches Windows volume before AudioTrack exists and applies it BEFORE play. Previously the initial volume update was discarded and new tracks used full gain. Cache survives stop/restart. Android system media volume remains user's control.
- Existing speaker-toggle edits in MainActivity/SpeakerService are preserved and committed; service running state determines on/off action.

## Definitive evidence

Read `out\acceptance\speakers\matrix-real.log` (completed **01:01:11**, exit0). This supersedes all earlier audio proofs.

Two simultaneous temporary caller-chosen PnP devices both started, problem0, distinct endpoints. Healthy reopen kept its endpoint after5seconds. Removal waited for actual endpoint departure; recreation retained the exact PnP path and correctly resolved a NEW endpoint GUID. All test nodes were removed.

On a temporary identity with **no connected phone**, the corrected diagnostic measured:

```
Capture: IEEE float32, 48000Hz, stereo
Baseline: 0 frames / RMS0
Supplied signal: 33120 frames, RMS0.007051, peak0.009979
1kHz energy100.0%; inherited3kHz energy0.0%
After Stop +500ms drain: 0 frames / RMS0
genuine-application-loopback=True
```

`LoopbackSignalProbe.cs` and `SpeakerCommands.cs` now require explicit endpoint AND peer hash, verify exact instance ownership, decode actual sample format, require signal-frequency correspondence, and require silence before/after playback. Nonzero bytes alone never establish success. Broker `matrix` invokes this ONLY on its random temporary, unconnected identities. Do not run `audioroute` on the user's connected phone.

Final builds: driver signed/catalogued without errors; Windows host/broker published self-contained win-x64 with warnings as errors; Android assembleDebug and signature verification succeeded. Full regression result: **187 passed, 10 existing optional skips, zero failures**.

## Source and commits

All functional work committed on local main (not pushed in this session):

- `1bd758f` multi-adapter driver / removal of sample disk dumping.
- `902584a` retained endpoint objects and broker leases; three real pipe regression tests.
- `033f899` correct stable-ID SetupAPI implementation and removal/reuse lifecycle.
- `8de0a33` genuine Windows mix, proper signal proof, Android initial volume and Windows mute.

Key files: `windows/audio/DeskFuse.AudioBroker/{DeviceInstance.cs,Program.cs}`, `windows/audio/DeskFuse.Audio/{PhoneEndpointBrokerClient.cs,SpeakerCommands.cs,LoopbackSignalProbe.cs}`, `windows/agent/FocusWindow.Agent/{PhoneSpeakerProvider.cs,WindowsLoopbackAudioCapture.cs}`, `windows/agent/FocusWindow.Core/Session/{DevicePeerSession.cs,PhoneSpeakerRoute.cs}`, `windows/driver/DeskFuseAudio/src/{speakerwavtable.h,minipairs.h,common.cpp}`, `android/app/src/main/java/com/focuswindow/client/media/PcAudioOutput.kt`.

## Next directives

1. Verify current host/broker paths and read `host-final.log`, `broker-real.err.log`. They are already deployed; don't rebuild/reinstall unless evidence calls for it. Let the user reopen the phone app and enable Phone Speaker. Resolve the NEW endpoint via the broker; never use old GUIDf796... or4389... from history. Expected label `Speakers (DeskFuse Xiaomi 24117RN76E)`; peer hash `f8d6479478a5fde3c725c797508f38e9`.
2. Use ordinary user-chosen music/video at low comfortable volume, then pause. Confirm real sound and silence, initial Windows volume/mute, one Xiaomi device, and endpoint departure when speaker service disconnects. This is the remaining live acceptance, **not yet completed with the real-render driver**. Investigate concrete failures only; no general edge-case framework. Phone force-stop command if unwanted audio occurs: `adb shell am force-stop com.focuswindow.client.debug`.
3. Continue delivering the intended speaker experience. Android → MM-1 via Android's SYSTEM output picker still requires a native Bluetooth route; this PC has **no Bluetooth radio**. Existing receiver code uses AudioPlaybackConnection. Do not replace the system-picker requirement with app-only playback capture, and don't spend time on that hardware blocker until a radio exists.

Hard constraints: no Secure Boot/test-signing/trust changes, no FocusIdd driver changes, LAN host stays unelevated, only audio broker/helper elevated. Use normal PnP APIs. Preserve physical screens, GPU stream, enrollment and documents.

## Rebuild details if actually needed

MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`; WDK10.0.26100.0, KMDF1.27. Existing **already trusted** Arne signing cert: `5287E7F3AFE30E74B28C255341E9B4DCD05EEB90` in CurrentUser My with private key, already LocalMachine Root + TrustedPublisher. Build with `/p:TestCertificate=<thumbprint> /p:GenerateTestCertificate=false /p:Inf2CatUseLocalTime=true`; midnight local/UTC mismatch otherwise causes future-date catalog failure. Use separate OutDir/IntDir under out/acceptance/speakers. Existing driver build/install/validation scripts and logs are there; avoid old scripts that broadly remove ROOT\MEDIA\* or trust certificates again.

Android: JDK `D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1`; SDK `D:\Android\sdk`; build tools35.0.0. User explicitly authorized `C:\Users\codexlab\.android\debug.keystore` for preserving installed debug-app identity. Build normal debug APK then sign a separate output with that existing androiddebugkey and verify public signer matches above. Do not uninstall the app. `adb install -r` preserves enrollment. Final APK already installed; don't reinstall merely to repeat checks.

Host build requires CMake from VS2022 bundled CMake bin on PATH; project automatically builds/packages FocusWindow.Gpu.dll. Start host from a fresh normal-user process. A temporary Ctrl+C helper inherited an ignore-Ctrl+C flag into a child host; don't reuse those helper scripts blindly. Current final host was launched normally. Before forcibly stopping a host, disconnect phone and confirm virtual-display cleanup; never kill Notepad or mutate physical display configuration.

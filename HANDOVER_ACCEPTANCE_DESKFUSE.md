# DeskFuse: continue here — acceptance and fixes

Updated 2026-10-20. This is the short handover for a fast/flash model.

## Latest live checkpoint — read this before the historical baseline

**The unified application now streams real Notepad and accepts phone typing.**
The owner confirmed both on 2026-10-20. See
[the measured result](docs/ACCEPTANCE_RESULT_2026-10-20.md) for artifacts and limits.

- Run the host as the logged-in desktop user **Arne**, without elevation. The
  installed FocusIdd protocol-2 driver works in this context. Do not migrate
  codexlab enrollment/DPAPI state or create another host account.
- WGC on this Windows 10 machine includes invisible window borders: its
  `1114x2417` capture contains the measured `1080x2400` DWM visible frame at
  offset `(17,0)`. Production capture now copies that ROI on the GPU, without
  resizing, CPU readback or encoding the unused backing desktop.
- This phone requests 270 DPI; Windows renders at the verified nearest step,
  288 DPI (300%). The app layout is 6.7% larger, with **1:1 pixels**. This size
  difference introduces no resampling blur. Never label it exact native density.
- Native PMv2 probe and per-backing EDID serial changes from the previous model
  are retained. CLI values such as `--workspace-args "--new-window"` now parse;
  `--workspace-args=--version` handles ambiguity with a host switch.
- Updated phone UI is installed: STATS/HIDE instead of persistent technical
  cards. End removes the owned monitor; physical displays remain unchanged.
  Reopening Android against the same running host reuses its Notepad process.
  The earlier unsaved test document remains open separately; preserve it.
- Local checks: 184 .NET passed, 10 optional/historical skipped; 21 Android
  tests; native capture/pacing contracts; Kotlin crypto and actual C#/Kotlin TCP.
- Minimizing the source exposed a fatal-error reconnect loop. Android now
  routes fatal host stages through its lifecycle events and cancels automatic
  retry; late decoder stats cannot revive the failed target. Explicit retry
  restores only the catalog-owned minimized window without activation. Two
  transport regression tests pass; live minimize/retry retest is in progress.
- A debug APK built under Arne has a different signer from the installed build.
  The owner explicitly authorized using codexlab's **default Android debug key**
  for this update. The installed signed APK is under ignored
  `out/acceptance/continuation/app-debug-signed.apk`. Preserve enrollment; do not
  uninstall as a signature-mismatch shortcut or commit signing material.

**Next focused work:** the owner identified Android's status bar covering the
Windows title bar. Measure a usable workspace below system bars/cutout and
reserve room for controls; size the app and encoded frame to that area, with
integer offsets and matching touch mapping. The profile already carries safe
insets, but current adaptive calibration still uses full-panel dimensions.
Do not merely shrink the existing full-panel stream. Immersive fullscreen can
be a later option. Smaller frame area does not remove Windows' density-step
constraint. Also investigate continued capture/encode activity on a static
document before claiming idle-bandwidth optimization. Preserve the working
video/input case while making these changes.

## Immediate assignment

Help the owner run [the acceptance guide](docs/ACCEPTANCE_TESTING.md), collect
actual results, and fix failures in the unified application. Start with its
first-session steps. Do not restart integration, write another plan, or rerun
the detached POC as a substitute for the product.

The owner explicitly wants code built first and will accept the hardware
behavior afterwards. The unified code is already merged. Acceptance is the
next task, not a reason to undo that merge.

## Verified starting state

| Item | State at this handover |
| --- | --- |
| Checkout | `D:\Coding\DeskFuse\v0.3` — actual Git root |
| Allowed work area | Modify only inside `D:\Coding\DeskFuse`; reading elsewhere in `D:\Coding` is allowed |
| Working branch | `main`; inspect current HEAD before doing anything |
| Repository | <https://github.com/braindamp/deskfuse> |
| Unified merge | PR #11, merge `1b9165cb12d34c1f4f14bf888d5acabff7fbc412` |
| Validated source head | `c26732cee2ecb632993d2110d3a191e7ac61e3a9` |
| Subsequent baseline | `dc71c03` changes documentation only; this handover is a later documentation increment |
| Main CI | [39369997315 — success](https://github.com/braindamp/deskfuse/actions/runs/39369997315) |
| Local software tests | 172 .NET passed, 10 historical/optional skipped; 19 Android tests; native ABI/pacing contracts; actual Kotlin/C# encrypted TCP interoperability |
| Windows build | `D:\Coding\DeskFuse\v0.3\out\unified-windows\` — keep the entire directory |
| Android debug APK | `D:\Coding\DeskFuse\v0.3\android\app\build\outputs\apk\debug\app-debug.apk` |
| PRs | #5 merged; superseded #6–#10 closed; #11 merged |
| Original POC | `poc/gpu-phone-workspace-wgc`, unchanged at `08fa683ab242459440388ff56d20e1ddeb97c318`; read-only |

This table records the earlier integration baseline; the live checkpoint above
supersedes its test counts and hardware status. Verify current refs and artifact
availability; never reset or overwrite new work to reproduce this snapshot.

## Read only these first

1. This handover.
2. `docs/ACCEPTANCE_TESTING.md` — commands, owner actions, pass/fail checks.
3. `docs/UNIFIED_RUNTIME.md` — implemented behavior and limitations.

`HANDOVER_UNIFIED_DESKFUSE.md` and the old plans contain historical instructions
about a draft PR and hardware-before-merge gates. Do not replay those obsolete
steps. Use them only when a particular technical detail is needed.

## What the one application now contains

- C# host builds and loads `FocusWindow.Gpu.dll` in process: WGC → D3D11 →
  NVIDIA NVENC → in-memory H.264 → existing authenticated AES-GCM transport.
  No shipping POC executable and no default full-frame CPU/FFmpeg rendering loop.
- Real source selected locally with `--workspace-app` / `--workspace-args`.
  Only a newly launched process's own HWND is placed on the owned virtual
  monitor. Actual PMv2 DPI, DWM bounds and exact negotiated dimensions are checked.
  The same app/documents survive reconnect within the host process.
- Full Device Bridge stack reconciled with newer mainline enrollment, input,
  file, clipboard and window controls. PC audio, remembered phone preferences,
  foreground phone microphone, capability isolation and channel-aware sound remain.
- One MTA GPU owner; two texture slots; latest pending capture; bounded encoded
  output and transport backpressure. The final update survives a pacing delay.
  Source timestamps survive; startup/recovery requests SPS/PPS/IDR.
- VBR without filler/periodic IDRs; direct encryption into the final network
  record and reused ciphers. Optimization means **runtime resource load,
  especially bandwidth**, at equivalent quality. Code size is not the objective.

## Known limits — classify correctly

1. **Installed driver access:** the normal-user host may be denied by the existing
   FocusIdd ACL. A narrow local privileged broker is not implemented. If denied,
   capture the error and fix this boundary in code; do not elevate the LAN host,
   reinstall the driver or weaken its ACL as a shortcut.
2. This GPU backend requires supported NVIDIA/NVENC hardware and Windows WGC.
3. Source must be a directly launched, independent PMv2 window accepting the
   exact size. Child-process launchers, single-instance handoff, separate modal
   popup composition and general source switching are not completed. Do not
   assume modern Notepad/browser launcher executables qualify without testing.
   Argument parsing now accepts values starting with `--`; source compatibility
   still depends on the application itself.
4. One exclusive display owner with the current host-global FocusIdd backend.
   Accessory-only peers do not acquire display/input/file privileges.
5. PC sound and temporary microphone-cable routing are not Windows PnP speaker
   or microphone endpoints. Native camera/mic/speaker providers and Android
   accessory-mode UX remain unfinished. Microphone is foreground-only.
6. TCP record head-of-line blocking and uncompressed PCM bandwidth remain.
   Do not describe advertised fps, UI latency estimates or host outbound kbit/s
   as measured end-to-end performance or video-only bitrate.

## 2026-10-20 display-density addendum

- Windows 10 restores the last mode it remembers per EDID serial. Reusing the
  phone's single stable serial across `1080x2400 -> 1800x2400` forced
  `1080x2400` (and earlier `1920x1080`), even when `1800x2400` was created first.
- Adaptive calibration now scopes the EDID serial to phone + backing size +
  refresh. The session identity remains `phone.DeviceId`; this is a display
  cache key, not a second session identity. See `docs/DESIGN_DISPLAY_STATUS.md`
  and the hardware update in GPU plan section 4.1.
- Lab evidence: `out/acceptance/mode-matrix/host-serial-120-settle0` fails with
  the host's stable serial; `out/acceptance/mode-matrix/perbacking-1800-first`
  succeeds with the per-backing serial.
- Published host: `out/unified-windows` (rebuilt 2026-10-20). Live host
  acceptance with Xiaomi 24117RN76E passed for Notepad video, input and clean
  disconnect; see the latest checkpoint above.

## Continue with evidence, then code

1. Run `git status --short`, inspect HEAD and, if network is available, fetch refs.
   Stay in the existing checkout. Preserve user changes and POC history.
2. Tell the owner to open the acceptance guide. Establish only missing facts:
   phone model/Android version, NVIDIA GPU, Windows version, and intended app's
   absolute executable path/new-instance arguments. Existing driver readiness is
   checked by the guide. Do not ask them to repeat the integration story.
3. Guide one checkpoint at a time: startup → enrollment → actual picture →
   geometry/input → idle/recovery → clean disconnect. Stop at the first failing
   checkpoint and obtain its exact error/log excerpt. Do not ask for every test
   before diagnosing the first failure.
4. The document request itself did not authorize you to install APKs, launch a
   display session or manipulate hardware automatically. The owner can execute
   the guide; follow explicit authorization for any agent-operated live testing.
   Continue read-only diagnosis and code work without repeated permission loops.
5. Fix the responsible production component; add a meaningful regression test
   where possible. Run the relevant check, commit the fix and keep the canonical
   mainline current. Use a short review branch if repository rules require it;
   do not resurrect the old feature stack or make another maintained fork.
6. Update the acceptance result with the tested commit, artifact, scenario and
   observed outcome. Retest that scenario after the fix. Never turn an unrun
   test into PASS because CI is green.

## Failure → code lookup

| First failure | Start here |
| --- | --- |
| Discovery/enrollment/approval | `windows/agent/FocusWindow.Core/Session/SessionHost.cs`, `windows/agent/FocusWindow.Agent/HostSupport.cs`, Android `MainActivity.kt` / `transport/` |
| Driver denied, display ownership/cleanup | Agent `Display/WindowsDisplayBackend.cs`, `Display/DriverDevice.cs`, Core `Session/PhoneSession.cs` |
| App exits/handoff, wrong size/DPI, source closes | Agent `Media/WorkspaceApplicationSource.cs`, `WorkspaceApplicationCatalog.cs`, Core `Display/ScaleEnforcer.cs` |
| WGC/NVENC startup, lost final repaint, IDR | `windows/native/FocusWindow.Gpu/src/{capture.cpp,latest_frame_queue.h,nvenc_encoder.cpp,gpu_pipeline.cpp}`, Agent `Media/NativeGpuPipeline.cs` |
| Phone black/corrupted picture | Android `media/VideoDecoder.kt`, `transport/LanTransport.kt`, `session/SessionController.kt`; correlate host Target and keyframe logs |
| Touch/keyboard reaches wrong window | Agent `Input/SendInputSink.cs`, `Media/WorkspaceApplicationSource.cs`, Core `Session/PhoneSession.cs` |
| Audio/mic/channel routing | Agent `WindowsLoopbackAudioCapture.cs`, `WindowsPhoneMicrophoneCableSink.cs`; Core `Session/PhoneMicrophoneRoute.cs`, Android `media/` |
| Congestion/counter/replay failures | Core `Transport/PhoneConnection.cs`, `SecureChannel.cs`, Android `transport/SecureRecordWriter.kt` |

Rebuild/test commands are in `docs/UNIFIED_RUNTIME.md`. Local toolchain details:
VS 2022 Community CMake lives at
`C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin`;
Java 17 at `D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1`; Android SDK at
`D:\Android\sdk`. Put CMake/Java `bin` on PATH as needed. If the default sandbox
fails with `helper_sandbox_lock_failed`, request the supported execution
escalation for the scoped command rather than changing filesystem permissions.

Keep raw acceptance evidence under ignored `out/acceptance/`. Do not commit
pairing keys, DPAPI state, approval databases, device identifiers or private
documents. Commit a sanitized result summary and code fixes only.

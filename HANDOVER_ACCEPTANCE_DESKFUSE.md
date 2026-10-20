# DeskFuse: continue here — acceptance and fixes

Updated 2026-10-20. This is the short handover for a fast/flash model.

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

These results prove software checks, not a working physical phone session.
Verify current refs and artifact availability; never reset or overwrite new work
to reproduce this snapshot. No real hardware acceptance was performed in the
integration chat.

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
   **Startup issue found while writing this handover:** `Program.Opt` currently
   rejects option values starting with `--`, including a `--workspace-args` value
   such as `--new-window`. This is a code-inspection finding, not a live test.
   Fix and test argument parsing before using a source that requires such flags;
   for a first session, select an app that needs no launch arguments.
4. One exclusive display owner with the current host-global FocusIdd backend.
   Accessory-only peers do not acquire display/input/file privileges.
5. PC sound and temporary microphone-cable routing are not Windows PnP speaker
   or microphone endpoints. Native camera/mic/speaker providers and Android
   accessory-mode UX remain unfinished. Microphone is foreground-only.
6. TCP record head-of-line blocking and uncompressed PCM bandwidth remain.
   Do not describe advertised fps, UI latency estimates or host outbound kbit/s
   as measured end-to-end performance or video-only bitrate.

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

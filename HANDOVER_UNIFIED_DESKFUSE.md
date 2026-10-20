# START HERE — DeskFuse unified integration and development handover

**Date:** 2026-10-20  
**Executed update:** Device Bridge is now reconciled into the GPU-first tree in
merge `c12367c` (parents include the complete `e62f371` feature stack). Further
implemented source/lifecycle/bandwidth work and current limitations are recorded
in [`docs/UNIFIED_RUNTIME.md`](docs/UNIFIED_RUNTIME.md). Sections 3–7 below describe
the original handoff state and work order; they are retained as historical context.
**Owner follow-up:** continue building and consolidating the code; physical-device
acceptance will follow later. The older hardware-before-merge instructions below
are historical and superseded by that direction. Software checks do not establish
physical acceptance.

**Local software verification for the executed increment:** Windows x64
self-contained publish with bundled native DLL; hardware-free native ABI and
capture-pacing contracts; .NET **172 passed / 10 legacy or optional skipped**; Android **19 unit tests**
and debug APK; actual Kotlin/C# encrypted TCP interoperability; plan-only
PowerShell 5.1 smoke. Real WGC/NVENC/Android decode,
bandwidth/latency/color and installed-driver permissions were not exercised.

**Repository:** https://github.com/braindamp/deskfuse (private; use the connected GitHub app/authorized checkout)  
**Repository directory:** repository root `.`  
**Primary work branch:** `integration/unified-gpu-workspace-20261020`  
**Integration PR:** https://github.com/braindamp/deskfuse/pull/11  
**Destination:** `main`, after the unified code and software checks pass; physical acceptance follows later per the owner.
**Other agent's proven POC branch:** `poc/gpu-phone-workspace-wgc`, **READ ONLY**.  
**Functional branches:** draft PRs #5–#10, source for reconciliation, not independently maintained end products.

## 1. Instructions from the project owner (highest priority)

**ONE CODEBASE, ONE DESKFUSE PRODUCT.** The POC has concluded and **proven technology must be used to replace inferior production rendering code**. It must not remain a sidecar/independent GPU workspace executable, fork or permanent alternate product. Preserve POC commit history, bring WGC/D3D11/NVENC/true-DPI/color code into the production application, and later dissolve the POC development branch when the single app is operational.

Consolidate Device Bridge work as well, keep all ongoing development against one coherent mainline. The project is *DeskFuse*: **FUSE** means bidirectional sharing of all supported enrolled device capabilities. A phone can supply camera and microphone, another phone a touchpad with left/right buttons, a tablet an extended Windows monitor and speakers; devices may supply multiple capabilities simultaneously. Do not architecturally impose a single-device/single-capability cap. The current FocusIdd monitor backend nevertheless has **one exclusive display owner** until safe per-display lifecycle is actually implemented. Media-only sessions must never seize display/input/file privileges.

**Performance is a first-class constraint.** Preserve hardware-native video texture and color pipeline, avoid unnecessary full-frame CPU readbacks, excessive queues and latency; maintain source timing/accurate input geometry, H.264 keyframe recovery, responsive audio, 2.0/5.1/7.1 support when real output hardware supports it, and proper channel-aware fallback. Do not optimize by discarding capabilities or hiding false native resolution. No Windows physical desktop rearrangement or movement of physical HWNDs for grid layouts. A mirrored physical HWND is *not* automatically an independently interactive clone.

Android permissions remain OS-owned: enrollment is trust and capability discovery, not a silent bypass. Current microphone is foreground-only. The product roadmap includes **possible future Pro background use** (microphone/audio with UI closed or screen off, compliant with Android foreground-service/notification rules); it is **not yet implemented or a decided paywall**. Windows device routing should ultimately expose genuine stable per-enrollment camera, microphone and speaker PnP endpoints; the temporary audio cable and PC loopback are NOT native Windows endpoints.

**Owner explicitly allows an incompletely compilable provenance/structural checkpoint.** Prefer useful compilable increments, but do not retain the old CPU/FFmpeg path as the default just to keep intermediate changes green. Do not claim final integration or physical hardware acceptance until it is real. Work on code rather than producing a third plan.

**Strict safety:** Do not modify the original POC branch, installed driver/driver ACL, physical Windows display topology, global DPI, registry, or user's hardware. Do not run a network host elevated as a long-term solution. Respect the merge window before modifying `main`. No physical-device testing is currently requested.

## 2. Read these specific sources (no additional briefing needed)

**Canonical plans and product intent**
1. `plans/DeskFuse_One_Codebase_Integration_Implementation_Plan_2026-10-20.md` on `main`: the full production consolidation strategy, file-by-file POC disposition and completion gates. https://github.com/braindamp/deskfuse/blob/main/plans/DeskFuse_One_Codebase_Integration_Implementation_Plan_2026-10-20.md
2. `docs/DeskFuse_GPU_Workspace_Integration_Plan.md` on the integration branch (copied from the POC branch, unchanged at handoff): the GPU agent's detailed structural integration approach. https://github.com/braindamp/deskfuse/blob/integration/unified-gpu-workspace-20261020/docs/DeskFuse_GPU_Workspace_Integration_Plan.md
3. `plans/DeskFuse_Product_Deliverables_2026-10-20.md` on `feature/audio-surround-negotiation`: FUSE vision, per-enrolled-device endpoint decision, multichannel fidelity, latency and future background operation. https://github.com/braindamp/deskfuse/blob/feature/audio-surround-negotiation/plans/DeskFuse_Product_Deliverables_2026-10-20.md
4. `docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md` on `poc/gpu-phone-workspace-wgc`: accepted technical source/measurements. https://github.com/braindamp/deskfuse/blob/poc/gpu-phone-workspace-wgc/docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md

**Actual code and PRs**
- **#11** https://github.com/braindamp/deskfuse/pull/11 — **start here** for real unified implementation. It is a draft integration PR against `main`, not a finished release.
- **#10** https://github.com/braindamp/deskfuse/pull/10 — latest stacked Device Bridge head, `feature/audio-surround-negotiation`. Contains all changes stacked on #9 → #8 → #7 → #6 → #5. Do not mistake #10's 12-file PR diff against its immediate parent for the full cumulative feature stack: compare this branch against `main` for the complete 26-file inventory.
- **#5** PC mixed sound output to Android using authenticated WASAPI loopback/AudioTrack.
- **#6** authenticated media-only accessory peers and persistent logical per-device endpoints/reconnect leases.
- **#7** composable capabilities, explicit exclusive display admission, no single-peer product assumption.
- **#8** per-host remembered PC sound toggle and reconnection.
- **#9** Android microphone capture and encrypted PCM to an explicitly selected temporary third-party Windows virtual-audio-cable render endpoint; not an installed DeskFuse PnP microphone.
- **#10** native channel-mask-aware 2/6/8-channel sound output negotiation and speaker-aware downmix.
- Original POC branch's tested native WGC/NVENC source is historical/proven reference only, not where further work should be committed. The older POC-only merge plan is **superseded as an end state**.

## 3. Exact Git state at this handover (re-fetch refs before working)

- `main` at **`6cf1dbbd4099b617f92e1713c9c1cfbf730f85e3`**. It contains the canonical one-codebase plan but not the full GPU/Device Bridge implementations.
- Original read-only POC `poc/gpu-phone-workspace-wgc` at **`08fa683ab242459440388ff56d20e1ddeb97c318`**.
- Unified branch `integration/unified-gpu-workspace-20261020` at **`d5c1ce8d48d6376aa102e6b8d05120c35d804a42`** before this handover documentation commit.
- PR #11: **open, draft, mergeable**, not merged. Contains **both POC and main parents** via commit `9acfa5c` for provenance.
- The last integrated GPU-only CI checks **passed**: https://github.com/braindamp/deskfuse/actions/runs/39354725578 and https://github.com/braindamp/deskfuse/actions/runs/39354728161 (Windows host + native DLL and Android software build/tests). This means **compile-time software checks only**, not a proven real focused-app/video session.
- Draft PRs #5–#10 are **open and not merged**. Latest full feature branch head `feature/audio-surround-negotiation` at **`e62f3719e44436e3f0355b939fa00e8f52a93d6e`**.
- There is **no approved integrated product build combining PR #11 and PRs #5–#10 yet**. Do not merge into `main` based solely on PR #11's currently passing GPU-only CI.

## 4. What is actually in PR #11 already

1. **POC provenance**: two-parent `9acfa5c` records accepted POC lineage without rewriting its history.
2. **Production native GPU source** under `windows/native/FocusWindow.Gpu/`: original proven `capture.*`, `common.*`, `nvenc_encoder.*` promoted; `gpu_pipeline.cpp` plus `include/focus_window_gpu.h` versioned in-process C ABI; no POC file-writing H.264 output in production. Includes NVENC IDR and SPS/PPS support, memory access units, bounded payload contract, initial frame pacing and D3D11 slot handling.
3. **Managed seam** `windows/agent/FocusWindow.Core/Media/IVideoPipeline.cs` exposes encoded H.264 AU + capture timestamps. `windows/agent/FocusWindow.Agent/Media/NativeGpuPipeline.cs` invokes the DLL. `WorkspaceWindowHost.cs` is a currently **blank managed WGC window**, not yet a usable selected app source.
4. **GPU-first `PhoneSession`**: normal authenticated video control uses encoded WGC/NVENC packets via the existing AES-GCM `PhoneConnection.TrySendFrame`, not the driver CPU BGRA/FFmpeg input as primary. Adds IDR resync, session version and WGC window geometry. Legacy classes may still be present but not used on the main GPU path.
5. **Native DPI**: `ScaleEnforcer.cs` re-applies/read-backs the selected **virtual monitor** scale after the phone profile is negotiated; native backing calibration is the default intended mode, with honest error if unverified.
6. **Build/CI**: `FocusWindow.Agent.csproj` invokes CMake to produce and bundle `FocusWindow.Gpu.dll`; `.github/workflows/dpr-challenge-ci.yml` covers native/GPU sources on Windows 2022 (and Android software tests). No separate GPU POC EXE is built as part of the shipping path.
7. The GPU agent's integration plan is present under `docs/` in the same branch.

**NOT YET COMPLETE:** selection/launch and lifecycle of an actual independent Windows app in the workspace HWND, production session geometry/rotation and input arbitration validation, mature display/privileged helper ownership, full POC scale identity adaptation, full end-to-end color/decode/latency measurements and Device Bridge reconciliation. The empty managed form is not itself "focused Windows app streaming." Old POC test executables are only historical code in original branch, not installed as the product. Other GPUs still need capability-based backends rather than false NVENC promises.

## 5. Immediate work order: implement and commit, not just plan

### A. **FIRST PRIORITY — consolidate all Device Bridge code into PR #11's one product tree**

Read cumulative difference of `feature/audio-surround-negotiation` from `main`. The previously verified count was **26 changed paths**, of which only **four overlap** with PR #11:
1. `windows/agent/FocusWindow.Agent/Program.cs`
2. `windows/agent/FocusWindow.Core/Session/HostConfig.cs`
3. `windows/agent/FocusWindow.Core/Session/PhoneSession.cs`
4. `windows/agent/FocusWindow.Agent/FocusWindow.Agent.csproj`

Carry all **22 non-overlapping** Device Bridge source/document changes into the unified integration branch, preserving final committed content; integrate these **four files manually**. Do NOT overwrite the GPU-first `PhoneSession` with the branch's old FFmpeg/CPU frame loop:
- `PhoneSession`: preserve `IVideoPipeline`, `PumpEncodedGpuVideoAsync`, native-DPI window input geometry, IDR handling and teardown; add approved `PhoneMicrophoneRoute`, `ClientAudio` and `ClientMicrophone` handlers, `PhoneConnection` PCM controls, host advertised bridge availability, and audio start/stop/cleanup. Drive mic packet pump under linked session cancellation. Preserve safe server send and per-enrollment authorization.
- `HostConfig.cs`: preserve `CreateVideoPipeline` **and** Device Bridge options/services: `AllowPcAudio`, `AllowPhoneMicrophone`, `IHostAudioCapture`, `IPhoneMicrophoneSink`, `CreateAudioCapture`, `CreatePhoneMicrophoneSink`, `CanUsePhoneMicrophone`, `DisplayAdmission`. Keep fields optional when appropriate and contract shape consistent.
- `Program.cs`: preserve `NativeGpuPipeline` injection and default native-DPI calibration; add sound options, `audio-devices` read-only inspection, per-device experimental mic cable (default off), and `WindowsLoopbackAudioCapture` injection. Do not drop original CLI/diagnostics or introduce elevation.
- `FocusWindow.Agent.csproj`: preserve native CMake build/package targets and add **NAudio** dependency from #5–#10.

Also validate Android `Protocol.kt`, `SessionController.kt`, `LanTransport.kt`, `MainActivity.kt`, `SessionChrome.kt`, `AndroidManifest.xml`, C# `Messages.cs`, `SessionHost.cs`, `PhoneConnection.cs` together. Accessory-only peer roles must not own a monitor. Do not assume Android already initiates `bridge_hello`: protocol models may exist without full Android accessory-session UX. A microphone-to-third-party-cable stream is NOT a Windows-enumerated microphone endpoint.

After these changes, run full integrated native/.NET/Android CI again. Correct failures in the **unified** branch. Do not create another maintained integration fork.

### B. **SECOND PRIORITY — make GPU streaming a real focused Windows application**

Replace the blank `WorkspaceWindowHost` (currently a top-level managed WinForms window) with a real selected/managed Windows application-source workflow on the owned virtual monitor, and accurate WGC HWND and touch transforms. Respect that an existing physical HWND capture is a shared mirror, not a separate clone. Never manipulate original physical windows for grid layout; use DeskFuse-owned/explicitly selected source windows and transparent ownership. Preserve Windows' application focus and keyboard arbitration semantics.

Fix GPU thread ownership, lifetime, native ABI buffer/packet safety, UI/hardware capability detection, stable profile-specific DPI, reopen/reconnect and IDR, and prevent false color/DPI claims. Normal Windows sessions must deliver decodable real app content, not merely a blank source window.

### C. **THIRD PRIORITY — meet integration/release gates and merge**

Follow the acceptance criteria in the canonical `plans/DeskFuse_One_Codebase_Integration_Implementation_Plan_2026-10-20.md`: one shipping Windows host + native DLL + Android app; no standalone POC runtime; native one-to-one phone pixels, correct actual DPI/color, responsive target input, bounded low-latency source/encoder/transport, healthy teardown/crash recovery and physical Windows safety. Native speaker/camera/mic PnP endpoints, general multi-display hardware backend and paid background use are **unfinished product milestones**, never misrepresent them as integrated merely because the transport foundation exists.

Do not invent or waive physical validation. GitHub CI can catch source-level regressions; on-device FocusIdd/WGC/NVENC/Android throughput/latency requires later device acceptance. Preserve exclusive display admission on the current host-global FocusIdd backend. Make one final reviewed merge into `main` **after integration conditions**, then close/retire the superseded POC and feature PRs as independent development lines. Continue further product coding from `main`.

## 6. Critical constraints and pitfalls

- **Single codebase != single programming language**: a production C++ native GPU DLL compiled and packaged by the C# host is part of the same app. Do not make a separately launched GPU helper/POC runtime.
- Windows GPU native code can produce H.264, but a blank WGC source does not produce a usable focused Windows application.
- The original POC's 2100×3120 backing + 350% effective Windows scale + phone-sized 1440×3120 HWND was demonstrated on evaluated hardware, **not all phones**. Scale/identity must be verified each time.
- User's Windows physical desktop windows and monitors should not be moved, resized, made primary, or have system-wide scaling changed just to feed the phone.
- POC FocusIdd access currently may require elevation because of installed driver ACL. Design a **narrow local privileged boundary** when needed; never elevate the general LAN/session host or weaken ACL.
- The old CPU/frame source/FFmpeg implementation should not become the production-default again. Remove obsolete portions once the native path passes real acceptance. Honest explicit hardware-specific compatibility backends are permissible, not two competing session designs.
- For live media, 2.0/5.1/7.1 output requires native matching channel masks; unsupported hardware gets a proper speaker-aware downmix. Plan media transport scheduling so large GPU video/file transfers cannot stall microphone, audio or input.
- Treat the old POC-only merge plan and local `gpu-phone-workspace-evidence/` as historical safety/evidence. That folder is intentionally Git-ignored; **not needed to begin coding** from this handover.
- Any action involving installed driver, Windows physical topology or actual user PC hardware requires a separately authorized test context; do not touch it automatically.

## 7. First action for the next model

**Fetch current GitHub refs, inspect PR #11's head, compare it to `feature/audio-surround-negotiation`, read the four overlapping files from BOTH branches, implement Device Bridge reconciliation directly on `integration/unified-gpu-workspace-20261020`, commit, and run unified CI. Then continue the GPU application-source and lifecycle integration.**

Do not ask the owner to repeat project context already in this file. Do not merely produce another implementation plan; **write and integrate code**. Report honestly what is working and what remains unvalidated. `main` remains the final destination for this one application, not a second production codebase.

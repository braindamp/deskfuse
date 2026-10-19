# DeskFuse — authoritative development handover (updated)

**Version:** 2.0 · **Updated:** 20 October 2026  
**Repository:** https://github.com/braindamp/deskfuse  
**Prior handover:** `HANDOVER_START_HERE.md` in the 19 October clean-handover ZIP; this update supersedes its stale *status and immediate-next-action* sections.  
**Primary references:** [`plans/DeskFuse_Consolidated_Build_Plan_v0.3.md`](https://github.com/braindamp/deskfuse/blob/main/plans/DeskFuse_Consolidated_Build_Plan_v0.3.md), [`DeskFuse_v0.3_PI_HANDOVER.md`](https://github.com/braindamp/deskfuse/blob/main/DeskFuse_v0.3_PI_HANDOVER.md), [`DeskFuse_v0.3_RETURN_HANDSHAKE.md`](https://github.com/braindamp/deskfuse/blob/main/DeskFuse_v0.3_RETURN_HANDSHAKE.md), and [`docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md`](docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md).

> **Read this first — current status override.** The earlier Windows/Android integration **test campaign associated with the v0.3 return handshake is concluded**, per the project owner's update. The Phase 0–1 seven-item hardware checklist was also exercised: driver, monitor creation, physical monitor preservation, native frame capture, DPI readback, ruler and teardown were tested; the explicit 350% attempt has also been made. **The substantive unresolved acceptance requirement is genuine 350% / 336 DPI at the exact 1440×3120 Windows virtual monitor.** “Tests completed” does *not* mean every one of the handshake's 48 test IDs passed: retain individual PASS/FAIL/BLOCKED verdicts from the return evidence and do not silently promote them. The current GitHub `main` documents are historical acceptance contracts, not a verified all-PASS report. **Do not reopen the test campaign as the next task** unless a new defect or missing acceptance artifact specifically requires it.
>
> **The GPU/window-capture proof of concept is already running with a separate agent under previously delivered instructions. Do not change that agent's specification or interfere with its branch. Wait for its results. It is NOT the focus of this handover's recipient.** Do not implement it, take over its branch, re-run it, or merge its work pre-emptively. Evaluate its measured handback when the project owner returns with it.

## 1. Mission: a superior phone-first Windows productivity workspace

DeskFuse (previously Desklink) aims to be a best-in-class **Windows ↔ Android phone/tablet experience**, not a demo of a display driver. Preserve the complete competitive wedge: clear native-looking Windows text at phone-appropriate density, responsive touch/input, high-quality low-latency GPU streaming, secure accountless LAN onboarding, meaningful window/application productivity, audio, clipboard and files, resumption, pen/foldable/refresh-rate improvements, gaming support where feasible, and eventually scoped bidirectional Android functions. Preserve the strongest original Desklink C workflow ideas; port deliberately to .NET/Kotlin rather than running a second unprotected server.

**Native DPR is a non-negotiable quality requirement**, but not the product's only objective. A successful 1440×3120 pixel feed at Windows 225% is *not* the same as phone-correct DPR 3.5 / Windows 350%. Likewise a CPU crop, GPU crop, individual-window capture or browser-emulated CSS DPR can be useful but must be labeled as a different capability than the original exact-monitor Approach A acceptance gate.

## 2. Current status at a glance

| Workstream | Current reading | Next ownership / action |
| --- | --- | --- |
| Phase 0–1 seven hardware checks | **Executed/concluded as a test campaign.** Existing FocusIdd, native capture, physical-monitor snapshot/teardown and ruler diagnostics have been exercised. | Preserve evidence; do not launch another full POC cycle. Native 1440×3120 @ 336 DPI remains unsatisfied. |
| v0.3 48-test Windows↔Android return handshake | **Test execution concluded, per owner.** The detailed return matrix/acceptance verdicts are not present in this handover, so no 48/48 PASS or full end-to-end certification claim. | Consult the actual returned test evidence if needed for a merge or release decision; don't rerun the campaign by default. |
| Exact-monitor native DPI / Approach A | **Unresolved technical blocker.** 1440×3120 supports/actually delivers only 225% effective DPI in the independent native witness after attempted unsupported 350%. | Retain explicit open requirement; research only when there is a discriminating, reversible experiment. |
| Supported high-DPI larger monitor | **Measured:** 2100×3120 virtual monitor with reported 350%; independent Win32 PMv2 window sees 336 DPI. | Foundation for a *separate* window-based research path; not Approach A success. |
| GPU 1440×3120 HWND → WGC/D3D11 → hardware encoder POC | **Separate agent is already executing the handed-off POC; results pending.** | Review its independent branch and returned native-ruler/video measurements, latency benchmarks, client-monitor identity and reconnection-scale results. **Not this agent's task.** |
| Window-first workspace and browser-specialized pipelines | **Product architecture direction documented**, not implemented/validated. | Design subsequent product interfaces, priorities and user journeys, without interfering with the delegated POC. |
| Touch/keyboard/gamepad and privileged operation architecture | **Future design hints**, not shipped requirements already tested. | Assess APIs, feasibility, security boundaries and product-stage sequencing as described below. |

### DPI measurement boundary (hard evidence)

- Device profile example: **1440×3120**, Android DPR **3.5** ⇒ target Windows scale **350%**, target effective DPI **336**, desired layout approximately **411.4×891.4 DIPs**, and a 100-DIP rendered ruler **350×350 source pixels**.
- On FocusIdd **1440×3120**, Windows `DisplayConfigGetDeviceInfo(-3)` reported maximum **225%**. An explicit unsupported `-4` request could leave `-3 current=350%`, but an **independent native PMv2 `GetDpiForWindow` measured 216 DPI**, so the request did **not** establish 350% actual rendering. The historical WinForms probe's 96-DPI reading is not the independent authority.
- On FocusIdd **2100×3120**, supported 350% yielded native PMv2 **336 DPI**. A fresh **350×350 captured-pixel ruler** on the *new native GPU-window path* remains for its delegated agent to prove; historic readings alone should not be promoted to a new pipeline PASS.
- EDID diagonal changes influenced Windows' **recommended** scale but not the **maximum**; portrait, landscape, and square modes matched an empirical roughly **800×600 effective-DIP minimum**. This is an inferred Windows behavior on the tested configuration, **not a documented OS-internal rule**.
- The original Phase 0–1 checklist's scale *attempt* is finished. **Product native-DPI success is not achieved.** Distinguish completion of experiment, completeness of evidence and satisfaction of design requirements.

Evidence and code: [`docs/DPR_800x600_EVIDENCE_2026-10-19.md`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/docs/DPR_800x600_EVIDENCE_2026-10-19.md), [`tools/df-density-readonly.cpp`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/tools/df-density-readonly.cpp), [`DprChallengeLab.cs`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/windows/agent/FocusWindow.Agent/DprChallengeLab.cs). The diagnostic experiment used a known-good driver, did not reinstall it, and checked physical-monitor preservation.

### DPI boundary investigation — record and experiment status

**Measured on this Windows/FocusIdd configuration (19 October 2026):**

| Virtual display (physical pixels) | Windows reported maximum scale | Logical/effective size at that maximum |
| --- | ---: | ---: |
| 1440×3120 | 225% | 640×1386.7 DIPs |
| 1440×3900 | 225% | 640×1733.3 DIPs |
| 1600×3120 | 250% | 640×1248 DIPs |
| 1920×3120 | 300% | 640×1040 DIPs |
| 2100×3120 | 350% | 600×891.4 DIPs |
| 3120×1440 | 225% | 1386.7×640 DIPs |
| 2560×1080 | 175% | 1462.9×617.1 DIPs |
| 1200×1200 | 150% | 800×800 DIPs |

The observations fit the **largest offered scale step** for which `min(width,height)/(scale/100) >= approximately 600 DIPs` **and** `max(width,height)/(scale/100) >= approximately 800 DIPs`. Rotation and the square case contradict a width-only minimum. **The exact thresholds, their implementation inside Windows, and portability to another build are NOT proven by discrete scale-step data**. Do not claim that an 800×600 application UI guideline documents a kernel DPI algorithm.

**Already attempted:** EDID diagonals 80/170/420 mm (recommended scaling changed but maximum did not); unsupported 350% `-4` write at 1440×3120 (stored 350%, but native PMv2 HWND measured 216 DPI); supported 350% at 2100×3120 (independent native PMv2 HWND measured 336 DPI). Historical WinForms HWND measurements sometimes incorrectly showed 96 DPI; the native window and actual captured pixel ruler are the relevant acceptance witnesses.

**Prepared but not confirmed executed:** [`tools/verify-effective-desktop-boundary.ps1`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/tools/verify-effective-desktop-boundary.ps1) on the diagnostics branch. Its optional **DPI-read-only** five-mode test compares 2100×2800 vs 2100×2798 (the 800-DIP long-side boundary at 350%) and 1200×1600 vs 1200×1598 / 1198×1600 (both dimensions around the 200% threshold). No hardware readback for those exact five modes has been provided. **This is not a dependency for the separate WGC POC and not an instruction to reopen the concluded Phase 0–1 test campaign**. Only run if future architecture decisions require extra corroboration, and only after guarding the globally shared driver.

**Unmet Approach A acceptance:** Windows must expose an **exact 1440×3120 monitor**, an independent native PMv2 window on it must receive **336 DPI**, and an unresized **1440×3120 captured frame** must contain a **350×350 physical-pixel ruler for 100 DIPs**. A 2100-wide high-DPI monitor with a 1440×3120 WGC-captured window is a valuable **different pipeline**, not success on that exact-monitor gate. Browser-managed CSS DPR is likewise distinct from native Win32 DPI.

**References:** [Empirical DPI evidence](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/docs/DPR_800x600_EVIDENCE_2026-10-19.md) · [Native Win32 witness](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/tools/df-density-readonly.cpp) · [WGC POC spec](docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md) · [Window-first/browser architecture](docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md).

## 3. Repository, branches and change boundaries

**Canonical GitHub project:** https://github.com/braindamp/deskfuse — treat `main` as the shared integration branch; its README, P2 implementation and handshake contracts may lag independently completed test evidence.

- `main` — shared app/product code and v0.3 plan; other agents may merge accepted work here. **Do not directly commit a speculative branch into `main`.**
- `diagnostics/dpi-propagation-20261019` — isolated experimental DPI evidence and native-ruler work; the physical FocusIdd installation is **shared system-wide** even when Git worktrees are separate.
- `poc/gpu-phone-workspace-wgc` — **GPU/WGC POC branch assigned to its separate coding agent**. It may be created/owned by that agent; do not assume it exists or take it over.
- `docs/window-first-architecture-20261019` — product strategy and handover documentation branch. Documentation changes may be reviewed/cherry-picked independently of code.

**Known Windows working folders:** `D:\Coding\DeskFuse\dpr-lab` (current testing worktree); `D:\Coding\DeskFuse\dpr-propagation` (separate diagnostic worktree); historical guarded baseline `D:\Coding\DeskFuse\DeskFuse_Phase_0_1_BuildFix_v0.2.1`; old v0.3 handoff directory `D:\Coding\DeskFuse\v0.3`. These are historical or reported locations, **not instructions to overwrite**. Never infer which folder is active without inspecting it.

**Do not alter the installed FocusIdd driver**, INF/WDK installation, protected shared driver ABI, physical monitor topology or OS/global DPI settings for a documentation/product-planning task. The driver uses protocol v2. Close/verify any active FocusIdd session before future hardware tests; driver heartbeat is not an independent system recovery mechanism. New experimental work belongs in its own branch and worktree.

## 4. Source code and documents worth reading

| Location (GitHub `main`, unless a branch is named) | Why it matters |
| --- | --- |
| [`plans/DeskFuse_Consolidated_Build_Plan_v0.3.md`](https://github.com/braindamp/deskfuse/blob/main/plans/DeskFuse_Consolidated_Build_Plan_v0.3.md) | Current consolidated P0–P5 roadmap; retain its security, Android, media, diagnostics, and product feature scope. Historical 'unverified' language is a snapshot, not a demand to rerun finished tests. |
| [`DeskFuse_v0.3_RETURN_HANDSHAKE.md`](https://github.com/braindamp/deskfuse/blob/main/DeskFuse_v0.3_RETURN_HANDSHAKE.md) | 48-case acceptance contract. Execution has been reported complete; case-by-case outcomes still require the actual returned artifacts to assert PASS. |
| [`windows/agent/FocusWindow.Core/Session/PhoneSession.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Session/PhoneSession.cs) | Phone session lifecycle, frame loop, input geometry and existing BGRA→FFmpeg pipeline. Not a demonstrated zero-copy GPU path. |
| [`windows/agent/FocusWindow.Agent/Display/WindowsDisplayBackend.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/WindowsDisplayBackend.cs) | Virtual display lifetime, true measured DPI, scale reads and protected topology identification. |
| [`windows/agent/FocusWindow.Core/Display/AdaptiveViewportCalibration.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/AdaptiveViewportCalibration.cs) | Exploratory larger-backing-display calibration. Exists as experimental capability; not a resolution of Approach A. |
| [`windows/agent/FocusWindow.Core/Display/SurfaceFrameSource.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/SurfaceFrameSource.cs) | Latest-wins frame behavior and latency discipline. |
| [`docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md`](docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md) | **Current strategic design:** sources, virtual desk, selective rendering, browser pipeline and identity. Design only. |
| [`docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md`](docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md) | Binding assignment of the **other** GPU POC agent. Contains per-phone identities, verified scaling on reconnect, native ruler, exact 1440×3120 WGC output, metrics and cleanup. **Do not duplicate its implementation.** |
| `strategy/` and old `reference_sources/` from the clean handover ZIP (where present) | Historical market/gap spreadsheet and original Desklink C app/window workflow ideas; useful to preserve the complete competitive feature backlog. Do not treat old gap claims as descriptions of present FocusIdd code. |

## 5. Window-first product direction — important forward design decision

**Default phone experience: a collection of Windows activities, not a permanently mirrored desktop.** The user sees a zoomable, organized **2D or 2.5D virtual desk**, with cards for windows, browser sessions, documents and activities, grouped by topic/project. They can pan, pinch-to-zoom, drag/rearrange, arrange related browser windows in the same work area, and tap a card to focus that one activity. Focused content should be streamed at the **phone's true output pixel dimensions and appropriate DPI/layout**, with optimized latency. Unfocused sources should be cached, paused or updated slowly. Full desktop capture remains a **separate, explicitly invoked source/pipeline** for workflows that actually require it.

**Window abstraction:** Keep `WindowSource`/stable logical source identity separate from capture backend and rendering surface. Plan for a `RenderPipeline` selector choosing one of:

1. **Arbitrary native Windows applications:** genuine PMv2 native HWND on a supporting Windows monitor → `Windows.Graphics.Capture` (WGC) → Direct3D → suitable hardware encoder. Exact DPI, 1:1 phone-size pixels, popups/menus, permission and latency must be measured; delegated POC evaluates the initial native HWND route.
2. **Existing ordinary Chrome/Edge/Electron windows:** general WGC capture. Do not assume remote debugging, app profile takeover or automatic ability to change those windows' CSS DPR.
3. **DeskFuse-managed browser content:** research Chromium CDP `Emulation.setDeviceMetricsOverride` for real CSS viewport/deviceScaleFactor, and compare CDP `Page.startScreencast` against GPU-capable **CEF accelerated offscreen**, **Electron offscreen shared texture** and possibly **WebView2 composition** routes. A 360-CSS-pixel layout rendered at 3× can yield 1080 actual pixels **for managed web content without a DPI-constrained Windows monitor**. This is a **different promise from OS-wide PMv2 336 DPI**, and CDP image screencast is not known to be zero-copy/60–120 FPS capable. Select by benchmarks and session/auth/profile compatibility, not by intuition.
4. **Explicit full-desktop mode:** FocusIdd/DXGI/WGC capture as separate compatibility escape hatch, not the base abstraction for every activity.

**Per-phone identity:** Map an enrolled *logical client* to a deterministic OS-visible virtual monitor identity where supported. Different clients must not inherit each other's display preference buckets. Windows remembering DPI is useful but **never trusted**: on *every* attach read the actual virtual display scale and independent PMv2 HWND DPI, reapply only the supported requested scale if necessary, wait, read back, and gate video on a real native pixel ruler. The **A→A→B→A** reconnect test is delegated to the GPU agent. Credentials must not be used raw as display serials or placed in logs.

**Scope discipline:** The spatial desk/browser pathways are product architecture inputs, **not orders to enlarge the active GPU POC**. Preserve the new product model through interfaces, event schemas and milestones while the separate agent works.

## 6. Additional design hints — input, games and privileged Windows behavior

These are **future product requirements/research leads**; add them to planning and architecture, not to the concluded POC or delegated WGC assignment.

### Phone-native touch and alternative input

- **Real touch / pen:** Investigate Windows' supported `CreateSyntheticPointerDevice` + [`InjectSyntheticPointerInput`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput), rather than treating all phone gestures as mouse clicks. Provide real press/move/release, multi-touch and pen data where supported. The API specifies `ptPixelLocation` in **virtual-screen physical pixel coordinates**; map phone input through the focused source's device scale, crop/window origin, zoom and display topology **exactly 1:1**, with explicit focus/input ownership. Preserve mouse and keyboard as separate selectable input modes and handle Android IME/text semantics.
- **Games:** Plan keyboard/mouse and optional virtual gamepad mapping. **ViGEm/ViGEmBus** is a historical candidate, but its upstream project is **retired/archived (2023)**; check supported alternatives, security/signing, driver maintenance, anti-cheat compatibility and user consent before choosing it as a product dependency. Do **not** silently install an input driver in a POC.
- The input target is **the focused window/activity**, not an unconditional global `ClipCursor` or synthetic pointer broadcast to the user's physical monitors. Full-desktop/game mode can have a distinct capture/input routing policy.

### Privileged broker / service concept — keep the security reality explicit

- Investigate a small, **privileged Windows service (potentially LocalSystem/SYSTEM)** for the operations that truly need elevation: lifecycle management, trusted-device policy, authorized system-session coordination and protected functionality. Use a **separate least-privileged interactive per-user agent** for normal HWND capture, Windows focus, multimedia processing and UI. Use authenticated, least-privileged IPC and explicit user consent; do not run the entire capture/network stack as SYSTEM by default.
- **Secure desktop, UAC consent prompts, lock screen and Ctrl+Alt+Del (secure attention sequence) must each be tested and designed separately.** A process being SYSTEM does **not**, by itself, mean a service in isolated Session 0 can capture or control the user's interactive desktop or the Winlogon secure desktop. Microsoft documents Session 0 isolation and limitations of interactive services. SAS is protected and any supported `SendSAS` route depends on documented privileges and Windows policy; it is **not** the same as injecting three ordinary keystrokes. Don't promise capture/control of protected UI until the supported security model and actual device results are established.
- Where Windows protects an operation or where consent would be inappropriate, **fail closed and explain it**; never attempt credential bypass, stealth hooks, unsupported desktop access, or UAC suppression. Consider platform/version compatibility, signed installation, recovery and attack surface before turning any privileged design into code.

Official references: [Interactive Services / Session 0](https://learn.microsoft.com/en-us/windows/desktop/services/interactive-services), [SendSAS prerequisites](https://learn.microsoft.com/en-us/windows/win32/api/sas/nf-sas-sendsas), [Windows synthetic touch](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput), [ViGEmBus retirement](https://docs.nefarius.at/projects/ViGEm/End-of-Life/).

## 7. Remaining priorities and execution order

The ordered P0–P5 tasks in the consolidated plan still define the implementation program, but **do not confuse an already-executed test campaign with the next active workstream**. User-reported tests completed; check the return evidence only when making merge/acceptance claims. DPI remains an explicitly open design/technical issue and is not secretly replaced with 225% or browser zoom.

| Product milestone | Next relevant focus after old test execution | Boundary |
| --- | --- | --- |
| Current: product architecture | Define `WindowSource` identity/lifecycle, focus and activation contracts, spatial desk data model, backend capability/latency matrix, touch transform contract, privacy rules and fallback behaviors. | Design and small isolated proof steps; no WGC POC takeover, no driver reinstall. |
| P2 integration follow-up | Triage concrete issues or missing artifacts from the *completed* Windows↔Android handshake only if the returned verdict/evidence says they remain. | Do not restage all 48 tests simply because the original acceptance document is still written in future tense. |
| P3 window-first productivity | Inventory, focus/activate, Quick View, source pinning/grouping, clipboard, files, topic organization, optional managed-browser research and phone-native UI. | Reuse original Desklink workflows securely; do not make full-desktop capture the only UI. |
| P4 performance and modality | Frame timing and text clarity, 60/90/120 Hz where supported; audio, pen, true touch, keyboard/IME, virtual gamepad candidates; network resumption, metrics. | Benchmarks must demonstrate FPS, latency, quality, battery/VRAM costs. |
| Security/service R&D | Privileged broker design, SAS/UAC/secure-desktop feasibility and permissions; signing, auditing, least privilege. | Not solved by SYSTEM alone; only supported/consented operations. |
| P5 quality/release | Signed components, installer/uninstaller/rollback, default security, documentation, reliable upgrades. | Never elevate or install driver without user approval. |
| Unresolved native-DPI research | Exact 1440×3120 Windows display truly delivering 336 DPI and a 350px ruler. | Remains a separate blocker; investigate with discriminating safe research rather than repeating old -4 attempts. |

## 8. Evidence, truthfulness and safeguards

- **Do not assert universal certification.** The owner says the earlier tests are completed; inspect `RETURN_SUMMARY.md`, `TEST_MATRIX.csv`, `TEST_REPORT.md`, logs/ZIP manifest when available before asserting what passed, failed or was blocked. Do not mistake a completed test run for a passed acceptance gate.
- **Don't erase 350% failure:** `DisplayConfigGetDeviceInfo(-3)` holding `350%` above a 225% cap is insufficient. Use independent native HWND DPI and captured pixel ruler as the native-rendering witness.
- **Don't mislabel architectures:** successful HWND/WGC streaming from a 2100-wide monitor is not a true 1440-wide Windows monitor. Successful managed browser DPR is not native OS DPI for arbitrary Win32 apps. Both may be valuable product capabilities.
- **No untrusted privileged shortcuts:** don't change physical screens, registry/global scaling, install software drivers or interact with the secure desktop during a documentation/design pass. Credential/device IDs and personal desktop imagery must be protected in tests and handbacks.
- **Do not let parallel agents collide:** only one FocusIdd owner at a time; Git worktrees do not isolate installed Windows drivers. The separate WGC agent owns its own branch and results until review.

## 9. Next-session / next-agent instruction

> Read this `HANDOVER_START_HERE.md` first, then `docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md` and `plans/DeskFuse_Consolidated_Build_Plan_v0.3.md`. The historical v0.3 return-handshake test campaign has **already been executed/concluded** per the owner; use its real artifacts to audit acceptance when needed, not to reopen it reflexively. The original Phase 0–1 hardware checklist was exercised, but exact **1440×3120 at genuine Windows 350%/336 DPI** remains an explicit unsatisfied requirement. A separate agent is **already executing** the previously handed-off 2100×3120→1440×3120 GPU/WGC phone-window POC with per-phone monitor identity and verified reconnection scaling; that POC is **not your task**. Your focus is to continue DeskFuse as a window-first phone productivity system: a spatial topic-organized desk of individual windows, an optimized per-window focused stream, browser-specific offscreen/CDP options when appropriate, and separate optional whole-desktop mode. Keep proper real touch, mouse/keyboard, gamepad feasibility, and an optional privilege-separated service for permitted OS interactions in the future design backlog; don't assume SYSTEM alone captures UAC/lock screens or Ctrl+Alt+Del. Preserve feature scope, security, honest evidence and branch/worktree isolation. Propose next architecture/product milestones from the consolidated plan without modifying the known-good driver or interfering with the delegated POC.
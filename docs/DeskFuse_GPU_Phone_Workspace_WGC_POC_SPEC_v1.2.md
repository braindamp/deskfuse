# DeskFuse — GPU Phone Workspace / Windows Graphics Capture POC

**Specification:** DF-GPU-WGC-POC-001 · **Version:** 1.2 · **Prepared:** 2026-10-19  
**Repository:** https://github.com/braindamp/deskfuse  
**Proposed isolated branch:** `poc/gpu-phone-workspace-wgc`  
**Priority:** Research POC alongside **Approach A**; this is **NOT** an Approach A replacement or permission to modify the installed driver.  
**Related product direction:** [Window-first workspace and adaptive rendering pipelines](https://github.com/braindamp/deskfuse/blob/docs/window-first-architecture-20261019/docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md) (future design; not a POC implementation requirement).

> **Agent assignment:** Build and measure a Windows 10-compatible proof of concept that displays a **1440×3120-pixel native Per-Monitor-V2 Win32 window** on the already-supported **2100×3120 FocusIdd virtual monitor at 350%/336 DPI**, captures the window **directly to a Direct3D 11 GPU texture** via **Windows.Graphics.Capture (WGC)**, and proves an **exact unresized 1440×3120 capture/encode path**. Additionally, implement/test **stable per-phone virtual-monitor identities and reconnection-time scale verification/reapplication**, so Windows/DeskFuse can retain the correct configuration per client without assuming it is applied. Record real latency, copy, VRAM, and compatibility costs. Do not touch `main`, install or replace FocusIdd, change physical displays, or claim the genuine 1440×3120 monitor problem is solved.

---

## 1. Context and non-negotiable distinctions

The overall product is **DeskFuse**, a phone-first Windows-to-Android workspace/streamer. Its winning feature set is substantially broader than this POC: responsive touch input, seamless Windows applications, high-quality text, GPU encoding, low latency, audio/video calls, gaming compatibility, robust pairing, recovery, and future developer-mode experiments. The present POC concerns **only GPU capture of a phone-size window with true app DPI**, not all product functionality.

### Original Approach A — still the main, independent acceptance target

- Windows sees an **exact 1440×3120 virtual monitor**.
- An independent native PMv2 HWND on that actual monitor obtains **336 DPI / 350%**.
- A 100-DIP reference square measures **350×350 pixels** in an **unscaled 1440×3120 captured frame**.
- No larger backing monitor, crop, or resampling is required. **Not yet achieved.**

### Distinct GPU-phone-workspace POC — this assignment

- Windows sees a **2100×3120 virtual monitor at genuine 350% scaling**.
- A separate, exact **1440×3120 borderless native window** lives on that monitor, receiving 336 DPI.
- WGC captures **that HWND**, yielding an exact **1440×3120 Direct3D frame** without image scaling. GPU encode uses that phone-size frame, not the 2100×3120 full monitor.
- The remaining 660×3120 monitor region is **not transmitted**. Whether it incurs composition/capture/GPU cost **must be measured, not assumed zero**.
- This can succeed at the app-window level **without** solving Approach A's Windows-monitor-level DPI limit. It is a product architecture candidate only if functionality, GPU cost and multiwindow compatibility prove acceptable.
- **Device-specific virtual monitor identity is an explicit requirement.** Each known phone must map to a stable virtual display identity across reconnects; different phones must not accidentally inherit each other's Windows monitor preferences. An identity alone is **not** evidence that Windows has restored scaling or other settings.

### Window-first product architecture — design constraint, NOT extra POC scope

The eventual DeskFuse phone UI is primarily a **spatial collection of individual windows and activities**, not a permanent full-desktop mirror. The user can pan/zoom a phone-native **2D/2.5D desk**, drag windows into topic/project groups, and **focus one selected window**. In focused mode the host chooses the best available rendering backend and sends **phone-sized, high-DPR, low-latency content**; the other cards may show cached or lower-rate previews. Full-desktop streaming is a **separate, explicitly selected pipeline** for apps/workflows that require it.

The POC must preserve an eventual **source/capture/encoding separation**: distinguish the logical source and its stable identity, the actual window/texture being captured, the physical output pixel size and DPR, input mapping, and the capture backend. This is an architectural consideration and a final handback recommendation; **do not expand the POC to implement a spatial UI, multiple live sources, CDP, CEF, Electron, WebView2, or phone app features**. The current assignment remains one native HWND captured with WGC.

**Browser-specific route is a strong future candidate.** For a browser launched/owned by DeskFuse, Chromium CDP `Emulation.setDeviceMetricsOverride` can request CSS viewport dimensions and `deviceScaleFactor` independently from OS monitor DPI; `Page.startScreencast` can supply frames but may add image encoding/transport overhead and is **not an established GPU-only low-latency pipeline**. Prefer evaluating GPU-capable offscreen embedding for some managed browser sources, e.g. Electron's documented offscreen shared GPU textures or CEF accelerated offscreen paint. Existing arbitrary Chrome/Edge windows should retain the general WGC path unless their process/session is explicitly manageable. Web content's CSS DPR is not equivalent to independent Win32 PMv2 window DPI. Read the companion GitHub strategy document for the pipeline decision table, source management, privacy and input constraints.

**Per-phone stable monitor identity and verified reconnect scaling remain mandatory for this POC**, as specified below; Windows remembering a setting is an optimization, not a correctness signal. The separate coding agent should not wait for the spatial-workspace or browser-research work.

### Known hardware facts (October 19, 2026)

| FocusIdd mode / action | Verified observation |
|---|---|
| 2100×3120; supported 350% `-4` | Windows `-3` scale readback **350%**; independent native Win32 PMv2 `GetDpiForWindow` **336 DPI** |
| 1440×3120; unsupported attempted 350% | `-3 current=350%` but maximum **225%**; independent native Win32 window **216 DPI**, i.e. real native scaling **225%**, **not** 350% |
| Physical monitor / EDID tests | Scale max tracks geometry, not EDID physical diagonal; portrait, landscape and square modes fit an **approximately 800×600 effective-DIP floor**; OS-internal mechanism is not established |
| Old WinForms probe | Sometimes returns **96 DPI** even while independent native Win32 process returns 336 DPI. **Do not use its 100×100 ruler as the authority.** |

There is an **existing CPU-crop/adaptive-DPI prototype** in the repository. It is *not* a zero-copy implementation and is not this POC's output. Keep it intact for baseline/reference and do not integrate this unproven path into production sessions.

---

## 2. Deliverable and exact success criteria

### Required functional criteria (all mandatory for a PASS)

1. **Monitor/source isolation:** attach *only* the existing known-good FocusIdd monitor at **2100×3120@60 Hz**. Use the installed protocol-2 driver, without PnP/driver install, driver update, EDID reconfiguration, registry writes, or physical-display SET. Refuse to start if `driver-status` shows `monitor arrived 1` or an active DeskFuse session owns the display.
2. **Native DPI:** establish the monitor at *within-cap*, supported **350%**; read it back and independently verify **native PMv2 HWND = 336 DPI**. Use `GetDpiForWindow` and DPI awareness context on the HWND that will be captured, not `GetDpiForMonitor` alone. Report failure, do not silently continue at 96/216 DPI.
3. **Exact HWND geometry:** create a separate C++/Win32 **WS_POPUP** test window with **1440×3120 physical-pixel WINDOW and CLIENT dimensions** (or report precisely why a particular compositor API prevents this). Position relative to the **actual virtual display origin** detected through CCD/GDI topology, not `(0,0)` and not a hard-coded `DISPLAY23`. Support nonzero or negative desktop coordinates.
4. **Pixel ruler:** draw a 100-DIP square at 336 DPI (**350×350 actual green pixels**, preferably on a magenta background) and include an additional high-frequency checkerboard / one-pixel line pattern to reveal resampling. **Measure actual output pixels**, do not infer size from DPI alone.
5. **Direct GPU capture:** use **WGC** targeting this **HWND** via `IGraphicsCaptureItemInterop::CreateForWindow`, capture frames as **D3D11 textures**. The captured content must be **1440×3120**. No scaling, viewport stretching, CPU BGRA readback in the sustained capture path, or encoding of a 2100×3120 intermediate frame. A *one-off* GPU→CPU diagnostic readback to inspect pixels is permitted and must be labelled separately.
6. **Hardware-encode demonstration:** for supported test hardware, pass a **1440×3120** GPU input texture into a hardware H.264 encoder (NVENC preferred if installed NVIDIA hardware is capable) and produce a short playable `.h264`/`.mp4` test artifact. If hardware/resource interop is not supported, report a distinct **ENCODER_BLOCKED** verdict instead of falsely claiming end-to-end GPU zero-copy. Frame dimension and no-scaling assertions are mandatory.
7. **Performance evidence:** measure *actual* capture/transfer/encode timings and GPU usage against the pre-existing CPU BGRA+FFmpeg path. A convincing result requires **no observed scaling** and a documented latency/overhead profile, not a guessed `0%` additional GPU usage or `<0.02 ms` GPU copy.
8. **Cleanup:** on normal exit, error, Ctrl+C, timeout, or encoder failure, close captures, windows, frame pools, sessions, GPU resources and virtual display. After exit, verify `driver-status` → `monitor arrived 0` and independent physical monitor snapshot unchanged. The driver has a **10-second heartbeat timeout**, **not an independent guaranteed system rollback**.
9. **Stable virtual-monitor identity, one per phone:** map an enrolled DeskFuse phone's existing stable logical device identifier to a **persistent deterministic FocusIdd monitor identity** (e.g. EDID serial derived from an application-scoped stable identifier using the existing driver protocol). Reconnects of the same phone MUST produce the same effective Windows monitor identity; two different simulated/enrolled phones MUST produce distinct identities. Verify identity through Windows CCD/monitor enumeration and OS-reported monitor/device identifiers, not just by asserting the generated serial strings differ. Treat this as **sequential one-monitor-at-a-time testing**—do not try to run multiple shared FocusIdd monitors simultaneously or replace a monitor owned by another session. Do not use session-specific random serials, phone IP addresses, public MAC addresses, or secrets as raw serial strings. Never change the installed driver or its protocol to accomplish this POC; if the existing serial interface cannot produce stable OS identities, report `IDENTITY_BLOCKED` and the concrete cause.
10. **Explicit reconnection scale enforcement:** on **every** attach/re-attach (including agent restart), query the actual scale caps and current value on the newly identified virtual target. Never assume remembered Windows preferences have been automatically applied. If current != 350%, try to set 350% **only when Windows reports it within the supported cap** and only for the identified FocusIdd virtual source. Wait for settling, then independently verify that the target native PMv2 HWND sees **336 DPI** and that the captured 100-DIP reference square is **350×350 pixels**. A successful API return, saved preferred scale, or `-3 current=350%` alone is insufficient. If Windows resets the display to 100%, the runner must detect it and recover to a validated 350%, or stop with `SCALE_REAPPLY_FAILED` / `SCALE_DPI_MISMATCH`; never stream a falsely certified phone-DPR image. No global DPI registry writes, sign-out, custom system scaling, or physical-monitor changes.

If the WGC path cannot meet a mandatory condition, return a clear stage-specific **FAIL / BLOCKED** verdict and preserve evidence. Do not mark the POC as successful because `-4` returned zero or a texture pointer exists.

### Client identity and preference persistence — explicit acceptance tests

These checks are required for POC completeness and mandatory before any claim of readiness for multi-phone production rollout. They do not imply that Windows automatically restores all display settings on every version.

| Sequence | Required observation and action |
|---|---|
| First attach of simulated/enrolled **Phone A** | Compute stable, non-sensitive virtual-monitor identity `IDA`, attach 2100×3120, query DPI state, apply 350% if supported, verify native 336 DPI + 350px ruler; log CCD/EDID identity and returned DPI values. |
| Disconnect and reconnect **Phone A** | Reuse `IDA` exactly. Observe whether Windows remembered its preference; **regardless**, read back actual scaling and run native/pixel verification again. Reapply legal 350% when necessary. |
| Fresh agent process / Windows login (if test environment supports) | Demonstrate deterministic ID and saved preferred profile survive an agent restart; distinguish `WINDOWS_RESTORED_SETTINGS` vs. `DESKFUSE_REAPPLIED_SETTINGS`. A sign-out is **not required for a POC PASS** and must not be initiated automatically. |
| Attach different **Phone B** | Produce `IDB != IDA` visible in OS monitor identification. Use B's own profile/scale, without leaking A's settings; if B also wants 350%, still validate it independently. |
| Reattach **Phone A** after B | Ensure A's original virtual-monitor identity and intended per-client profile return. Verify actual DPI and captured pixels; no dependence on B's prior state. |
| Simulate reset to **100%** on the virtual display, or recreate after a Windows reset | The agent detects actual 100% and re-applies 350% within reported caps; confirms **336 DPI and 350px ruler** *before* any capture/encode can be marked ready. If a controlled reset cannot be safely performed, log the test as untested, do not change physical displays. |
| Corrupt or rotate a phone ID deliberately in test mode | Distinct identity is handled as a new profile, without borrowing another phone's preferences. Identity re-association/recovery path must be documented; do not silently merge identities. |

**Persistence semantics:** Windows OS monitor preference persistence is opportunistic and version-dependent. DeskFuse must maintain its own per-client **desired** configuration when appropriate and use Windows observations as the source of truth for **effective** configuration. Separate `preferredScale=350` from `scaleReadback` and `effectiveHwndDpi`. A remembered identity is not itself proof that Windows remembered the scaling. Record orientation/mode/refresh/monitor-origin preferences only where supported and safe; do not claim OS-wide window placement or all settings are reliably persisted in this POC.

**Safety detail:** Existing FocusIdd is a shared global device: never bring down a virtual monitor owned by another DeskFuse process. To demonstrate A→B→A, attach each phone in **separate sequential guarded test runs**, after clean teardown and `monitor arrived 0`. Do not try to simulate two simultaneous monitors with a one-target driver. If the existing `MonitorRequest.Serial` / `StableSerial` path does not produce distinct Windows-visible monitor identities, **investigate and report** without installing an experimental driver in this branch.

### Important secondary behaviors (run and record; need not all PASS)

- Parent HWND plus **owned popup**, detached dialog, menu, tooltip and Win32 file-picker: which are included in WGC of the parent, which are excluded, and whether a compositor view can combine them without resizing.
- Window partially overlapped by another app; minimized; hidden; moved; resized; losing focus; virtual monitor unplug/reattach; Windows lock/unlock. A Microsoft sample explicitly notes that **minimized windows are not captured**. Treat limitations as product-design constraints, not bugs to paper over.
- Cursor capture behavior, screen-capture border/permissions, HDR/SDR pixel formats, unsupported/protected content, device loss, encoder GPU mismatch/cross-adapter copies, frame pacing and idle-vs-changing-frame behavior.

**No system-wide `ClipCursor`, global `SPI_SETWORKAREA`, permanent AppBar, user profile DPI registry keys, DLL injection, process hooking, or RDP-session switch in this experiment.** Local/input-mapped clamping can be explored *after* the capture POC, but should not modify existing user/physical desktop state.

---

## 3. Architecture and implementation choices

```text
Known-good FocusIdd (2100×3120 @ 350%, virtual only)
           │
           └── Native PMv2 Win32 test HWND
                  CLIENT 1440×3120 physical pixels, 336 DPI
                         │
                         └── Windows.Graphics.Capture::CreateForWindow(HWND)
                                  │
                                  ▼
                           D3D11CaptureFrame.Surface
                           1440×3120 GPU texture (verify!)
                                  │
                          [optional format conversion
                           on GPU; absolutely no scale]
                                  │
                                  ▼
                           hardware H.264 encoder
                                  │
                                  ▼
                          1440×3120 test bitstream

Historical baseline (NOT the new architecture):
FocusIdd 2100×3120 → read CPU shared BGRA surface
     → CPU viewport crop 1440×3120 → FFmpeg stdin → encode
```

**Primary method:** capture one HWND with WGC. Frame pool via `Direct3D11CaptureFramePool.CreateFreeThreaded` where supported; use `ContentSize`, `Surface`, and compositor's `SystemRelativeTime` to instrument frame arrivals. Probe output texture format, adapter LUID and device capabilities. Window-capture may include nonclient visuals; borderless popup minimizes ambiguity, but **verify** exact content size rather than force-adjusting it and pretending success.

**Comparison path (optional, separate benchmark):** WGC monitor capture or DXGI Desktop Duplication on the 2100×3120 target, then use `ID3D11DeviceContext::CopySubresourceRegion` with a strict **1440×3120 rectangle** into a separate 1440×3120 D3D11 texture. This is explicitly a **GPU-crop comparison** and must remain separately labelled from direct HWND capture. It should not replace the primary method just because it is easier. GPU crop may reduce encoder input pixels but does not prove DWM rendered no pixels in the hidden region.

**Hardware encoder:** choose and document a viable D3D11 texture→codec integration for the actual test GPU (e.g. NVENC's resource registration/mapping). If the live test environment makes this impractical, complete GPU-only capture and timestamps first, report the encoder gap explicitly, and do not integrate production FFmpeg piping in the name of "zero-copy". If a temporary readback encoder fallback is supplied for debugging, label it **CPU_FALLBACK**, not GPU-only.

**Dedicated standalone POC:** native C++17/C++20 executable(s) under `windows/poc/gpu-phone-workspace/` plus a PowerShell wrapper under `tools/`, rather than editing `PhoneSession` or production driver code. Keep ability to launch/attach a separate native renderer/capture process; child lifecycle must be bounded and cancellation-safe.

---

## 4. Concrete code to reuse — verified paths

**Primary DeskFuse repo:** https://github.com/braindamp/deskfuse

The following links were checked against the repository; read them first.

| Reusable code | What to reuse, and caution |
|---|---|
| [`windows/agent/FocusWindow.Agent/Display/WindowsDisplayBackend.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/WindowsDisplayBackend.cs) | Existing monitor creation, **topology-delta identification**, DPI readback, virtual-target validation and removal. `CreateMonitorAsync` can remove an existing virtual display: **preflight driver status BEFORE invoking it**. |
| [`windows/agent/FocusWindow.Agent/Display/DisplayConfigApi.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/DisplayConfigApi.cs) | CCD/GDI enumeration, virtual display targeting, scale caps and scale setting; known warning: undocumented `-3`/`-4` have no stable public contract. |
| [`windows/agent/FocusWindow.Agent/Display/DriverDevice.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/DriverDevice.cs) | Protocol-2 driver IOCTL and health/status/heartbeat control; reuse, **no driver changes**. |
| [`windows/agent/FocusWindow.Agent/Program.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Program.cs) | `driver-status`, PMv2 check, hardware-probe CLI architecture, shutdown/Ctrl+C handling. Do not change standard `run` behavior. |
| [`windows/agent/FocusWindow.Agent/app.manifest`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/app.manifest) | PMv2 DPI-awareness manifest to mirror in the POC's own executable. |
| [`windows/agent/FocusWindow.Agent/Display/ProbeWindow.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/ProbeWindow.cs) | Ruler concept and captured-pixel verification; **not authoritative** because current WinForms probe sometimes reads 96 DPI. |
| [`windows/agent/FocusWindow.Core/Display/DensityMeasure.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/DensityMeasure.cs) | Existing color-box pixel measurements and DPI/ruler evaluation; may be ported/reused for *diagnostic* GPU texture readback. |
| [`windows/agent/FocusWindow.Core/Display/AdaptiveViewportCalibration.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/AdaptiveViewportCalibration.cs) | Research reference for finding a 350%-capable backing resolution; already contains a CPU-crop approach, **not zero-copy**. |
| **Per-phone identity implementation references** | `AdaptiveViewportCalibration.TryRunAsync` already constructs a stable serial using `PhysicalSize.StableSerial(phone.DeviceId)` and passes it in a `MonitorRequest`; inspect [`DensityPolicy.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/DensityPolicy.cs), [`WindowsDisplayBackend.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/WindowsDisplayBackend.cs), and [`DriverDevice.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/DriverDevice.cs) to trace that identity all the way through IOCTL/EDID and Windows. **Do not assume the serial mapping actually yields separate Windows preference buckets until observed.** |
| [`windows/agent/FocusWindow.Core/Session/PhoneSession.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Session/PhoneSession.cs) | Existing `FrameLoopAsync`, CPU BGRA buffer/crop, encoder selection and transmission paths for **baseline measurements only**. Do not modify in this POC. |
| [`windows/agent/FocusWindow.Core/Display/SurfaceFrameSource.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Core/Display/SurfaceFrameSource.cs) | Existing latest-wins source semantics; useful for latency/drop comparison. CPU surface path is **not** a GPU-only solution. |
| [`windows/agent/FocusWindow.Agent/Display/WindowsSurface.cs`](https://github.com/braindamp/deskfuse/blob/main/windows/agent/FocusWindow.Agent/Display/WindowsSurface.cs) | Driver CPU shared-memory read; baseline only. |
| [`tools/run-dpr-challenge.ps1`](https://github.com/braindamp/deskfuse/blob/main/tools/run-dpr-challenge.ps1) | Hardware safety model and timeout; **main's copy may still prompt** and should not be modified for this separate POC. |

**Separate experimental source branch (do not merge wholesale):**  
https://github.com/braindamp/deskfuse/tree/diagnostics/dpi-propagation-20261019

- [`tools/df-density-readonly.cpp`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/tools/df-density-readonly.cpp): independent native PMv2 HWND/DPI witness and a 100-DIP Win32 ruler. Adapt its ruler code for a **1440×3120 window** rather than automatically using the full monitor.
- [`tools/build-native-dpr-witness.ps1`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/tools/build-native-dpr-witness.ps1): builds C++ without requiring the developer to open a VS developer shell.
- [`docs/DPR_800x600_EVIDENCE_2026-10-19.md`](https://github.com/braindamp/deskfuse/blob/diagnostics/dpi-propagation-20261019/docs/DPR_800x600_EVIDENCE_2026-10-19.md): full measured DPI-cap evidence and caveats.

**Upstream Microsoft working example (strongest starter for WGC):**  
https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/tree/master/cpp/ScreenCaptureforHWND  
Its README confirms a Windows 10 1903+ Win32 `HWND` capture example, VS 2022 support, and a minimized-window limitation. Prefer **adapting this verified capture sample** over generating WGC interop boilerplate from scratch. Respect its license when borrowing code.  
Reference: https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/cpp/ScreenCaptureforHWND/README.md

**API documentation:**

- `IGraphicsCaptureItemInterop::CreateForWindow`: https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow
- `Direct3D11CaptureFrame` (`ContentSize`, `Surface`, `SystemRelativeTime`): https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe
- `Direct3D11CaptureFramePool.CreateFreeThreaded`: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded
- NVENC D3D11 input/resource guide: https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html
- `ID3D11DeviceContext::CopySubresourceRegion` (only if comparing GPU ROI): https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion

---

## 5. Branch and worktree instructions — separate agent executes these

**Never change `main`, never `git checkout` the active `dpr-lab` worktree to the experimental branch, and never install/update FocusIdd in any worktree. The installed driver is shared system-wide.**

From a normal PowerShell terminal on the Windows test PC:

```powershell
cd D:\Coding\DeskFuse\dpr-lab

git fetch origin main diagnostics/dpi-propagation-20261019 docs/window-first-architecture-20261019

git worktree list
git status --short

# Create this POC in its own worktree; do not check out main in dpr-lab.
git worktree add -b poc/gpu-phone-workspace-wgc ..\dpr-gpu-wgc origin/main
cd ..\dpr-gpu-wgc

git rev-parse HEAD  # record this precise base commit in evidence / README

# Import only the two handoff documents from the docs branch; do not merge branches.
git restore --source=origin/docs/window-first-architecture-20261019 -- `
    docs/DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md `
    docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md

# Both documentation files are now local uncommitted additions in THIS worktree.
git status --short
```

If `poc/gpu-phone-workspace-wgc` already exists, **do not overwrite** it; use another branch name and record why. Check `git status --short` for the new worktree before editing.

For selectively copying the diagnostic native ruler source if helpful:

```powershell
# Run from the NEW dpr-gpu-wgc worktree only.
git restore --source=origin/diagnostics/dpi-propagation-20261019 -- `
    tools/df-density-readonly.cpp tools/build-native-dpr-witness.ps1
# These become isolated local edits; adapt them and commit as appropriate.
```

Prefer compiling the standalone C++ POC first. Existing host build (source-compatible check):

```powershell
dotnet build .\windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj -c Release -p:Platform=x64
```

Initial hardware preflight (only with known-good installed driver and no active phone session):

```powershell
$agent = (Resolve-Path '.\windows\agent\FocusWindow.Agent\bin\x64\Release\net8.0-windows\FocusWindow.Agent.exe').Path
& $agent driver-status
# Must explicitly say: monitor arrived 0
```

**The standalone GPU POC's exact CLI is to be implemented by the new agent**. Suggested flags:

```text
DeskFuse.GpuWorkspacePoc.exe --plan
DeskFuse.GpuWorkspacePoc.exe --run --mode 2100x3120 --scale 350 \
  --window 1440x3120 --capture hwnd-wgc --fps 60 \
  --duration-sec 30 --out <path>
DeskFuse.GpuWorkspacePoc.exe --run --client-id test-phone-A \
  --mode 2100x3120 --scale 350 --window 1440x3120 \
  --capture hwnd-wgc --verify-reconnect-scale --duration-sec 30 --out <path>
DeskFuse.GpuWorkspacePoc.exe --run --mode 2100x3120 --scale 350 \
  --window 1440x3120 --capture monitor-roi-gpu --fps 60 \
  --duration-sec 30 --out <path>     # Optional comparison only
```

Implement the runner so it is **nonprompting** after explicit `--run`, as requested for previous DPR tests, but refuse unsafe or ambiguous state and print stage-specific errors. `--plan` must not create a monitor.

Commit on this branch in small, reviewable steps; when ready:

```powershell
git status --short
git add windows/poc/gpu-phone-workspace tools/  # narrow explicit paths as appropriate
git commit -m "poc: native PMv2 phone window and WGC GPU capture"
git push -u origin poc/gpu-phone-workspace-wgc
```

**Do not merge into `main` or open an auto-merge PR.** A PR for review only is fine after the evidence is produced.

---

## 6. Implementation milestones and stop/go gates

**M0. Driver-free compile/test first.** Import Microsoft's WGC sample components into a standalone C++ project under the isolated branch. Record Windows SDK/compiler dependencies. Build a pure off-monitor synthetic test of the D3D11 frame buffer and pixel-check logic where possible. No driver test yet.

**M1. Virtual-monitor preflight and accurate DPI.** In a guarded run, derive the per-client stable virtual-monitor identity, bring up **2100×3120@60** with 350% supported scaling. **Always read back the virtual target's current scale and correct it to supported 350% if needed; do not assume Windows has remembered it.** Verify topology, `GetDpiForWindow=336` for the *actual target native window*. Reuse only known-good, virtual-only driver APIs. Before probing native window capture, verify that the 100-DIP ruler is **350×350** in a one-off reference image (GPU readback is permitted for diagnostics). Abort if the native HWND is 96 or 216 DPI.

**M2. WGC direct HWND frame.** Capture a 1440×3120 HWND. Log `GraphicsCaptureItem.Size`, `Direct3D11CaptureFrame.ContentSize`, GPU texture descriptor dimensions, format, QPC timestamps, per-frame `HWND` DPI, monitor identity, and *actual captured green-ruler pixel bounds*. Require exactly **1440×3120 content pixels**. Handle initial frame delay, changes, cancellation, `Closed`, and device loss. **No scaling allowed.**

**M3. GPU-only encoder input.** Connect the captured GPU resource or exact-size GPU copy to H.264 hardware encoding. Record adapter LUID, cross-adapter needs, format conversion and copy count; save a decodable, 1440×3120 bitstream. Make CPU-staging diagnostics explicit and disable them for timed runs. Mark GPU path BLOCKED, not PASS, if a full-frame CPU copy or FFmpeg stdin is required in the steady-state path.

**M4. Performance comparison.** 30 seconds each at 60 FPS target: (a) static/no changes, (b) scrolling a text layout, (c) high-motion checkerboard/animation; (d) optional 120 FPS only if actually supported. Also benchmark the historical CPU surface→CPU crop→FFmpeg path on the same PC at identical output size, codec, quality, refresh and content. Repeat each scenario ≥3 times after warm-up, note environmental variance.

**M5. Compatibility and cleanup.** Test overlapping foreign window, native owned popup, common file picker, minimized window, focus switch, manual monitor removal, encoder crash and Ctrl+C. Document exact capture inclusion/omission and virtual monitor teardown. Verify physical display state, and confirm no registry/driver installation changes.

**M6. Per-client virtual-monitor identity and reconnect/scale recovery (mandatory for completion).** Implement a stable phone→virtual-monitor identity and a distinction between **desired configuration** and **measured effective configuration**. Exercise sequential **A→A→B→A** runs across teardown/reattach (and process restart when possible), logging the OS-reported monitor identity and whether scaling was remembered or actively reapplied. Inject a safe virtual-only initial-100% condition when possible. Never advance to GPU encode before native 336 DPI and 350px ruler verification. An existing driver inability to expose unique OS-visible identities is a clearly scoped **BLOCKED** result, **not** permission to reinstall or change FocusIdd. Document whether Windows remembers preferences and how DeskFuse compensates for failures.

**Stop conditions:** driver protocol mismatch, existing active FocusIdd monitor, wrong physical display topology, erroneous DPI, unexpected frame dimensions, physical-display drift, hardware/codec crash that threatens session stability, or inability to distinguish captured source from the intended HWND. Stop rather than silently switch to resizing or system-wide settings.

---

## 7. Performance metrics and evidence format

Produce **timestamped JSON + CSV + Markdown** under `gpu-phone-workspace-evidence/` (ignored/untracked unless small and intentionally curated). Each run records:

- Git commit SHA, OS build, Windows SDK, monitor topology identity, GPU model/driver, encoder backend, codec/preset/bitrate, 60/120 Hz state, measured window DPI and captured sizes.
- Client-scoped **stable logical device identifier** (an anonymized test pseudonym in shared logs), deterministic derived virtual-monitor serial, Windows-visible monitor/EDID identity, identity-stability evidence on detach/reattach, and **cross-client isolation** results. Avoid logging device secrets or raw long-term credentials.
- On every attachment: **desired scaling**, recommended/current/reported-max before write, whether Windows restored it vs. DeskFuse reapplied it, API result, post-settle scale readback, independent HWND DPI, ruler pixel bounds, **time to DPI-ready**, number of attempts, failure/fallback action and whether capture was correctly gated. Include 100%-reset recovery evidence or reason unavailable.
- WGC compositor `SystemRelativeTime` and local high-resolution QPC; monotonic unit conversions, clock-domain caveats and synchronization uncertainties. Frame arrival vs compositor timestamp; capture→encoder-submit; encoder-submit→bitstream-ready; combined pipeline **p50, p95, p99**; steady-state throughput, dropped/skipped/repeated frames.
- Real GPU copy counts and measured time via **D3D11 GPU timestamp queries**, with disjoint handling, rather than CPU `Stopwatch` around an asynchronous GPU call. GPU utilization/VRAM via OS counters or a labelled measurement tool. CPU %, working set, GPU process/copy/encode utilization where available.
- Texture dimensions and formats at capture/encode stage; whether any staging resource, format conversion, cross-adapter copy, spatial resample, crop or border correction occurred.
- Screenshot/image proof with **100 DIP -> 350×350 pixel ruler** and an independently checked 1-pixel grid, plus test `.h264`/`.mp4` file only if available without exposing sensitive personal desktop content. Prefer deterministic synthetic test patterns over private apps/documents.
- Baseline-to-GPU comparison using identical source scenarios. Compute memory: 2100×3120 BGRA = **26,208,000 bytes** per buffer; 1440×3120 BGRA = **17,971,200 bytes**; hidden width = **8,236,800 bytes** per buffer. These are allocation counts, **not** verified GPU-work numbers.
- Negative tests: parent vs owned popup vs independent dialog, overlap, minimized, occluded, lock/logout, capture failure, monitor removal.

**Never assert an empirical performance threshold from Claude's/other AI's proposed figures.** In particular, `0% additional GPU compute`, `zero DWM passes`, `<0.02 ms GPU crop` and `perfect popup capture` are hypotheses requiring measurement on this PC. Explicitly test whether the GPU-only path actually avoids the FocusIdd CPU shared-memory path and copy-back bottleneck.

### Machine-readable verdicts

Use explicit codes such as:

- `HWND_336_DPI_AND_RULER_350PX_VERIFIED`
- `CAPTURE_1440X3120_GPU_NATIVE_VERIFIED`
- `ENCODE_1440X3120_GPU_PATH_VERIFIED`
- `ENCODER_BLOCKED_<reason>` / `CAPTURE_BLOCKED_<reason>`
- `APP_POPUPS_NOT_IN_HWND_CAPTURE` / `MINIMIZED_NOT_CAPTURED`
- `GPU_BENCHMARK_MEASURED` / `GPU_BENCHMARK_INCONCLUSIVE`
- `PHYSICAL_DISPLAYS_UNCHANGED_AND_FOCUSIDD_REMOVED`
- `PER_CLIENT_MONITOR_IDENTITY_STABLE` / `IDENTITY_BLOCKED_<reason>`
- `CLIENT_SETTINGS_ISOLATED` / `CLIENT_SETTINGS_LEAKED`
- `WINDOWS_RESTORED_SETTINGS` / `DESKFUSE_REAPPLIED_SETTINGS`
- `SCALE_350_READBACK_AND_HWND_336_VERIFIED` / `SCALE_REAPPLY_FAILED` / `SCALE_DPI_MISMATCH`
- `RECONNECT_REVALIDATION_PASS` / `RECONNECT_REVALIDATION_BLOCKED`

Use an overall **PASS** only if M1–M3, M6 (identity and reconnection-scale verification), and the cleanup criteria pass. Mark component-level success and overall **PARTIAL/BLOCKED** otherwise. Do not call this `APPROACH_A_PIXEL_VERIFIED` under any circumstances: this POC uses a 2100-pixel-wide monitor.

---

## 8. Final handback to the DeskFuse lead agent

Deliver:

1. Git remote **branch URL**, base commit SHA, final commit SHA, diff summary, exact build/run commands, and confirmation `main`/installed driver untouched.
2. Working standalone C++ WGC example in `windows/poc/gpu-phone-workspace/` plus guarded nonprompting runner, tests, README and build recipe using installed Visual Studio 2022 Community.
3. Timestamped evidence JSON/CSV, screenshots of the native pixel ruler, capture/encode sample (if feasible), and CPU-vs-GPU performance tables with **p50/p95/p99**.
4. Explicit capability/compatibility matrix for arbitrary apps: HWND-only, owned dialogs, popups, menus, minimized, occlusion, protected content, focus, cursor, and usability for a full phone workspace.
4b. **Forward-compatibility note (design only):** explain how WGC source/window identity, captured GPU texture, frame timestamp, dimensions, DPI evidence and input mapping could later fit a `WindowSource`/`RenderPipeline` contract, alongside managed-browser offscreen sources and the optional full-desktop pipeline. Do not implement the desk UI or browser backend in this POC. Link the companion design note.
4a. Per-client A→A→B→A identity/reconnect results: stable Windows-visible monitor ID, separation of preferred versus effective scale, 100%-initial-state detection/reapply, and native HWND/ruler validation before video readiness. Explain any limits in per-client Windows preference persistence and the compensating DeskFuse logic.
5. A **GO / CONDITIONAL GO / NO-GO** recommendation explaining whether this path could be a production architecture, and **exactly what it does not solve** (not a native 1440×3120 Windows monitor; no automatic full multiwindow desktop capture).
6. Proof after all tests: **physical monitors unchanged, FocusIdd `monitor arrived 0`**, no install or registry writes.

**Do not wait for the DPI lead agent, and do not ask that agent to modify `main` or the driver.** The POC can proceed from the known-good supported 2100×3120/350% measurements without relying on the yet-unverified native ruler additions on the diagnostic branch. If environment/driver ownership conflicts with another active hardware test, stop and coordinate a non-overlapping run; code development can proceed independently.

### Suggested prompt for the separate coding agent

> Read `DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md` entirely **and review** `docs/WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md` from the linked documentation branch; use the latter as future architecture context only. Implement only the isolated GPU Phone Workspace/WGC POC on the `braindamp/deskfuse` repo in a dedicated worktree and branch `poc/gpu-phone-workspace-wgc`, beginning from the current `origin/main`. Use the existing known-good FocusIdd installation unmodified and Microsoft's Win32 ScreenCaptureforHWND example as the WGC reference. Establish 2100×3120 at supported 350%/336 DPI and a true 1440×3120 PMv2 Win32 HWND with 350×350 actual pixels for a 100-DIP ruler. Capture its HWND into native-size D3D11 surfaces, encode with hardware H.264 if supported, and deliver performance/compatibility evidence and honest PASS/BLOCKED verdicts. Additionally implement and validate a **stable virtual-monitor identity per enrolled phone** (A→A→B→A sequential reconnect test), plus **mandatory live scale readback and supported 350% reapplication on every attach**; never trust Windows to remember scaling, and gate capture until the actual native HWND is 336 DPI and its ruler is 350×350 pixels. Report OS identity persistence vs. DeskFuse recovery separately. Do not merge to main, resize frames, modify the installed driver, touch physical monitor settings, or conflate this 2100-wide backing architecture with Approach A's exact 1440-wide monitor. Follow every preflight and teardown gate; do not repeat interactive RUN confirmations.

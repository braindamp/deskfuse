# DeskFuse GPU Workspace — Structural Integration Plan

**Status:** Proposed for review; not an execution approval  
**Code baseline:** `acccc00` on `poc/gpu-phone-workspace-wgc`  
**Document commit:** added on the same branch so the reviewing agent can fetch it  
**Target:** `main` as the one active codebase  
**Supersedes:** the merge-only plan. This is an integration plan, not a branch-preservation plan.

---

## 0. Owner decision this plan must implement

The POC phase is over. The GPU path is the intended final path, not an optional sidecar.
`main` does not have to be production-ready, complete, or runnable at the structural
checkpoint. It must contain the components in the places where the final system will use
them.

The working rules are:

1. **One codebase.** The POC branch is dissolved into `main`; it is not kept as a parallel
   implementation.
2. **GPU is the default video path.** The existing CPU BGRA + FFmpeg path loses authority.
   It is not left as the path that silently runs when the GPU path is present but unfinished.
3. **Structure first, execution second.** Put the correct native/managed components and
   contracts in place first. A structural checkpoint that does not compile or run is allowed.
4. **Do not hide incompleteness.** A non-runnable structural commit is labelled as such and
   produces no performance or acceptance claims.
5. **Old-path tests are disposable.** Tests that only assert the CPU/FFmpeg implementation
   are removed or quarantined. They are not a reason to preserve the old path.
6. **`poc-monitor` is scaffolding.** Its useful logic is moved into the production monitor
   and scale-enforcement path. The command itself remains, if at all, as an internal
   diagnostic. It is not the product entry point.
7. **No driver, ACL, physical-display, registry-DPI, or topology changes.** The installed
   FocusIdd driver and its access policy remain untouched.
8. **No evidence leakage.** Logs, serials, profiles, and run evidence stay under the ignored
   evidence directory and are never committed.
9. **No false claims.** Frame pacing, H.264 timing, GPU-copy timing, M4, and M5 are not
   declared complete by this integration.

If a proposed implementation conflicts with these rules, the rules win.

---

## 1. What must be true after the structural integration

This is the checkpoint the owner asked for: correct components in correct places, ready for
further development, not necessarily runnable.

### 1.1 Required structure

- A production native GPU component exists under a non-POC path and builds as a library
  consumed by the Windows agent, not as a separate EXE that the agent shells out to.
- The managed session owns a `IVideoPipeline` abstraction. `PhoneSession` calls that
  abstraction for video.
- The pipeline is composed of:
  - a production PerMonitorV2 window hosted on the FocusIdd virtual monitor;
  - a WGC capture source producing D3D11 textures;
  - a GPU H.264 encoder backend (NVENC first);
  - an output queue that yields transport-ready H.264 access units.
- `PhoneSession` no longer reads BGRA frames from the driver shared surface and no longer
  feeds `FfmpegEncoder` on the default path.
- Monitor creation, stable per-client identity, scale readback/reapplication, and capture
  gating are part of the production session path, not only the diagnostic monitor command.
- The old CPU/FFmpeg video path is removed from the default composition. If a compatibility
  fallback is kept, it is explicit, named, and not selected silently. The recommended
  integration deletes it.
- Old tests that assert the removed path are deleted. Safety, protocol, authentication,
  transport, display-guard, and DPI-math tests remain.
- The native build target is part of the repository and is added to CI. CI may be red at the
  structural checkpoint; that is expected, not an accident.

### 1.2 Explicitly not required at the structural checkpoint

- A working end-to-end stream.
- A compiling native library or agent.
- A hardware acceptance run.
- M4 performance comparison or M5 compatibility matrix.
- Correct native refresh pacing.
- A finished phone workspace UI.
- A production privilege broker. The existing elevation gate stays as-is.

---

## 2. Current state and the actual gap

### 2.1 Today on `main`

The production video path is CPU-centric:

```text
PhoneSession
  -> IDisplayBackend.OpenFrameSource()
  -> WindowsSurface (driver shared memory, CPU BGRA)
  -> SurfaceFrameSource (latest-wins CPU copy)
  -> optional CPU viewport crop
  -> FfmpegEncoder child process (BGRA stdin, FLV stdout)
  -> FlvDemuxer
  -> PhoneConnection.TrySendFrame(...)
```

This path is integrated, tested, and currently authoritative on `main`.

### 2.2 Today on the POC branch

The POC proves a different path, but only as a standalone executable:

```text
PocMonitorHost (C# diagnostic)
  -> creates/holds the FocusIdd monitor and proves identity/scale

DeskFuse.GpuWorkspacePoc.exe (native, standalone)
  -> creates its own test window on that monitor
  -> WGC captures the HWND into D3D11 textures
  -> NVENC registers those textures and writes H.264 to a file
  -> writes JSON/CSV evidence
```

It does **not**:

- create the production host window;
- run inside `PhoneSession`;
- send access units over `PhoneConnection`;
- use the production monitor/scale path;
- replace the CPU video path;
- preserve native cadence or prove unique-picture handling;
- provide GPU-copy timing.

### 2.3 The integration gap

The missing work is not a cherry-pick. It is new glue code:

1. A production window host that owns a real PMv2 window on the virtual monitor.
2. A native/managed boundary that keeps capture and encoding on the GPU.
3. A session-level pipeline contract that replaces the CPU frame loop.
4. Production scale enforcement and capture gating using the POC-proven logic.
5. Removal of the old path from the default session composition.
6. New build and CI wiring for the native component.

That is the integration action.

---

## 3. Target architecture

### 3.1 Layering

```text
PhoneSession (Core: control, lifetime, transport)
  |
  +-- IDisplayBackend / DensityNegotiator
  |     monitor creation, stable serial, topology identity, cleanup
  |
  +-- ScaleEnforcer (Core or Agent, virtual-target only)
  |     read caps -> detect 100% reset -> reapply supported target -> readback -> gate
  |
  +-- IVideoPipeline (Core contract)
        |
        +-- WorkspaceWindowHost (Agent)
        |     PMv2, borderless, exact client size, message pump, HWND
        |
        +-- NativeGpuPipeline (Agent)
              |
              +-- WGC capture (native): HWND -> D3D11 BGRA textures
              +-- GPU H.264 encoder (native): texture -> Annex B access units
              +-- bounded output queue (native -> managed)
              +-- metrics/capability state
        |
        +-- PhoneConnection.TrySendFrame(...) remains the transport
```

The GPU pipeline is one implementation of the video contract. It is not a second session.
`PhoneSession` still owns authentication, control messages, input, file transfer, keyframe
requests, renegotiation, and teardown.

### 3.2 Production component locations

The exact names can change, but the placement should be:

```text
windows/native/FocusWindow.Gpu/
  CMakeLists.txt
  include/focus_window_gpu.h          C ABI, opaque handles
  src/wgc_capture.cpp                  HWND capture, frame pool, D3D11 slots
  src/nvenc_encoder.cpp                texture registration, H.264 encode
  src/gpu_pipeline.cpp                 capture -> encode -> access-unit queue
  src/color.cpp                        explicit BT.709/range contract
  src/diagnostics.cpp                  capability/selftest only
  tests/                               native tests; no production test window

windows/agent/FocusWindow.Core/Media/
  VideoTypes.cs                        EncodedAccessUnit, frame metadata
  IVideoPipeline.cs                    session-facing contract
  VideoPipelineSettings.cs

windows/agent/FocusWindow.Agent/Media/
  NativeGpuInterop.cs                  LibraryImport/DllImport boundary
  NativeGpuPipeline.cs                 implements IVideoPipeline
  WorkspaceWindowHost.cs               production PMv2 window host
  GpuCapabilityProbe.cs                WGC/D3D11/NVENC availability

windows/agent/FocusWindow.Agent/Display/
  WorkspaceMonitorHost.cs              renamed/moved PocMonitorHost, diagnostic only
  ScaleEnforcer.cs                     proven reapply/readback logic
```

`windows/poc/gpu-phone-workspace/` is removed as a production location. Its proven native
pieces move to `windows/native/FocusWindow.Gpu/`; its test-pattern window moves to native
tests or a diagnostic target. The solution must not keep two copies of the capture/encoder
code.

### 3.3 Native ABI rules

- C++ types must not cross the boundary. Use opaque `void*` handles or integer handles.
- `extern "C"` exports with a versioned header and a single `fwgpu_get_api` entry point.
- One D3D11 device per pipeline; WGC and NVENC must use that same device.
- No steady-state CPU readback of pixels. The only readback is an explicit diagnostic
  (ruler/reference image) and must be labelled as such.
- The native side never writes files in production. The POC's file sink becomes a diagnostic
  sink; production returns access units to managed code.
- Native output is a bounded queue. If managed code falls behind, the policy must be
  explicit: latest-wins for capture, and either drop-before-encode or encode-and-drop at the
  transport boundary. Do not silently grow memory.
- Keyframes must carry SPS/PPS and an IDR. The native encoder must support an explicit
  keyframe request; no “restart the whole process” workaround.
- Timestamps: capture QPC and WGC `SystemRelativeTime` stay distinct. Managed code owns the
  monotonic transport PTS. Do not infer timing from Annex B.
- Color: use one explicit contract. The POC proved full-range BT.709 with the test decoder.
  Do not mix full/limited range between capture, NVENC VUI, and the client. The integration
  must record the chosen contract and test it.

### 3.4 Managed pipeline contract

A shape like this is enough to force the right composition:

```csharp
public sealed record VideoPipelineSettings(
    string MonitorDeviceName,
    RectI MonitorBounds,
    RectI Viewport,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    int ExpectedDpi,
    string ClientId);

public sealed record VideoPipelineInfo(
    string CodecName,
    string PixelFormat,
    string EncoderName,
    int Width,
    int Height,
    int RefreshHz,
    int WindowDpi,
    string WindowAwareness,
    bool ExactCaptureSize);

public interface IVideoPipeline : IAsyncDisposable
{
    VideoPipelineInfo Info { get; }
    Task StartAsync(VideoPipelineSettings settings, CancellationToken ct);
    IAsyncEnumerable<EncodedAccessUnit> ReadAccessUnitsAsync(CancellationToken ct);
    void RequestKeyframe();
    bool IsHealthy { get; }
    string? FailureDetail { get; }
    VideoPipelineMetrics Metrics { get; }
}
```

`EncodedAccessUnit` is moved out of `FlvDemuxer.cs` into a neutral video types file if the
FLV path is deleted. `PhoneSession` consumes `ReadAccessUnitsAsync` and calls
`TrySendFrame`. The pipeline does not own the phone connection.

### 3.5 Session flow after integration

```text
Negotiate display
  -> create FocusIdd monitor with stable per-client serial
  -> enforce scale + readback + gate
  -> create WorkspaceWindowHost on the identified monitor
  -> verify window DPI, client size, and monitor identity
  -> start NativeGpuPipeline (WGC + encoder)
  -> send Target using pipeline metadata
  -> control loop + access-unit pump + housekeeping
  -> teardown: stop pipeline -> destroy window -> remove monitor -> restore scale -> guard
```

`PhoneSession.FrameLoopAsync`, `_frameBuf`, `_visibleFrameBuf`, `SubmitFrameAsync`, and
`RestartEncoderAsync` are removed or reduced to pipeline lifecycle calls. The CPU crop is no
longer needed: the host window is already the exact 1440x3120 viewport, and WGC captures that
HWND directly. If a wider backing monitor is used, the host window is placed at the viewport
origin and sized to `Plan.PixelW x Plan.PixelH`; the hidden region is not captured.

`RequestKeyframe` maps directly to the native encoder's force-IDR operation. A transport
rejection or unhealthy pipeline requests a keyframe or restarts the pipeline, not the old
FFmpeg process.

---

## 4. Monitor identity and scale enforcement

The POC's most important non-GPU result is that Windows does not reliably restore the
desired scale. The integration must move that logic into the production path.

### 4.1 Keep production identity

`DensityNegotiator` already derives `PhysicalSize.StableSerial(phone.DeviceId)` and passes it
to `CreateMonitorAsync`. Do not create a second identity scheme in the GPU pipeline. The
POC helper's profile/serial code is useful for sequential hardware tests, but the session's
identity is the enrolled phone's stable ID.

> **2026-10-20 hardware update.** Windows 10 restores the mode it remembers per EDID serial.
> Using `StableSerial(phone.DeviceId)` while changing a backing from `1080x2400` to
> `1800x2400` forced the old mode (`1080x2400`, and earlier `1920x1080`). The adaptive
> calibration path therefore scopes the EDID serial to
> `StableSerial($"{phone.DeviceId}:adaptive:{width}x{height}@{refresh}")`: stable for the same
> phone+backing+refresh, distinct across backing modes. This is a display cache key, not a
> second session identity; the session and GPU pipeline still use `phone.DeviceId`. See
> `DESIGN_DISPLAY_STATUS.md`.

### 4.2 Add a production `ScaleEnforcer`

On every attach, including reconnect and agent restart:

1. Read the actual scale caps and current value from the identified FocusIdd target.
2. If current equals the desired value, continue.
3. If the desired value is outside the reported cap, fail with `SCALE_REAPPLY_FAILED` or the
   existing native-density failure; do not stream a falsely certified image.
4. If current differs, apply the proven set-scale path only to the identified virtual target.
5. Wait for settle, read back repeatedly, and require the desired value.
6. If a 100% reset is detected, reapply and verify it.
7. Only then create the workspace window and start capture.
8. Restore the original scale on teardown.

This is the POC logic, moved out of `poc-monitor` and into the session. Do not broaden it to
global DPI registry writes or physical monitors. Keep the elevation/ACL gate unchanged.

### 4.3 Capture gate

The pipeline must fail closed if:

- the workspace window is not PerMonitorV2;
- `GetDpiForWindow` is not the expected value;
- the client rectangle is not exactly the requested pixel size;
- the window is not on the identified FocusIdd monitor;
- WGC reports a different content size;
- NVENC cannot register the capture texture or produce a keyframe.

A failure is a named stage, not a black screen or an implicit CPU fallback.

---

## 5. Old path disposition and test deletion

### 5.1 Video path

Recommended hard cut for the structural checkpoint:

- `PhoneSession` references only `IVideoPipeline`.
- `FfmpegEncoder`, `EncoderArgs`, `EncoderPicker`, and `FlvDemuxer` are removed from the
  default video composition. Delete them if no other caller needs them; do not leave a
  silent fallback.
- `HostOptions.FfmpegPath` and the `--ffmpeg` CLI path are removed or marked obsolete.
- `SurfaceFrameSource`/`WindowsSurface` remain only for density probes, diagnostics, and
  driver health. `PhoneSession` no longer calls `OpenFrameSource`.
- `SurfaceReader` and its tests may remain; they test the driver protocol and diagnostic
  capture, not the video path.

This is a conscious capability cut: the structural checkpoint supports WGC + NVENC first.
Other GPU encoder backends and compatibility fallbacks are follow-up work. The owner has
accepted incomplete coverage in exchange for moving `main` to the target architecture.

If the reviewer requires a compatibility fallback, it must implement `IVideoPipeline`, be
selected only by an explicit capability decision, and be labelled as a fallback. It must not
remain the default path.

### 5.2 Tests

Remove or quarantine tests that only assert the old implementation:

- `EncoderArgsTests`
- `FlvDemuxerTests`
- `FfmpegEncoderTests`
- FFmpeg-specific portions of `InteropEndToEndTests`
- any session test that assumes `PhoneSession` reads BGRA frames or owns an FFmpeg process

Keep:

- authentication, pairing, protocol, and transport tests;
- `SessionLifecycleTests` (generic worker/lifetime behavior);
- physical-display guard and DPI math tests;
- driver contract and surface-reader tests used by diagnostics;
- security and access-gate tests.

Add new tests after the contracts exist:

- pipeline state machine: start, keyframe request, resync, teardown;
- bounded access-unit queue and backpressure policy;
- metadata correctness: codec, dimensions, pixel format, refresh, window DPI;
- native ABI smoke test (capability probe, no hardware gate required);
- a fake pipeline test proving `PhoneSession` no longer requires a CPU frame buffer.

The structural checkpoint does not need these tests to pass. They define the next phase.

---

## 6. Build and CI placement

### 6.1 Native build

- Add `windows/native/FocusWindow.Gpu/CMakeLists.txt`, target `FocusWindow.Gpu` (shared
  library, x64, C++20, Windows SDK 10.0.26100.0 or the installed equivalent).
- Link `d3d11`, `dxgi`, `windowsapp`, `ole32`, `user32`, `gdi32`, `dwmapi`, and the NVENC
  import library/runtime as appropriate. NVENC runtime remains dynamically loaded; a missing
  driver is `ENCODER_BLOCKED`, not a load-time crash.
- Copy `FocusWindow.Gpu.dll` next to `FocusWindow.Agent.exe` in build/publish output.
- Keep the POC's `--encoder-selftest` behavior as a native diagnostic target, not as the
  production entry point.

### 6.2 Agent build

- `FocusWindow.Agent.csproj` gets the native DLL as a build artifact/content item.
- `FocusWindow.Core` stays platform-neutral; the native interop lives in the Windows agent.
- `HostServices` gets a factory for the production pipeline, e.g.
  `Func<IVideoPipeline> CreateVideoPipeline`, supplied by `Program.cs`.

### 6.3 CI

- Add a native build job to the Windows CI workflow.
- Add the managed build/test job with `-warnaserror`.
- The native job is required once the runnable milestone is reached. At the structural
  checkpoint it may be red; that is the accepted state.
- Do not use a manual POC-only workflow as the final gate. The normal `main` CI must know how
  to build the production GPU component.

### 6.4 Expected CI sequence

```text
Structural checkpoint: source placement + contracts + old path removed
  -> CI may fail (native and/or managed)
  -> no hardware evidence; no claim of runnable

Make-it-work milestone:
  -> native CMake Release build passes
  -> dotnet build -warnaserror passes
  -> targeted dotnet tests pass
  -> 10 s hardware smoke test passes
  -> main CI green
```

---

## 7. Work packages

These are work packages, not necessarily one commit each. Keep the POC commits intact; do
not rebase, squash, or force-push the pushed branch.

### WP-1 — Native production component

- Move the POC capture/encoder sources into `windows/native/FocusWindow.Gpu/`.
- Split `main.cpp` into library entry points and diagnostic-only code.
- Remove the production dependency on the POC test window and the file sink.
- Define the C ABI and a version/size handshake.
- CMake target produces `FocusWindow.Gpu.dll`.
- Expected result: source is in the production location; build may fail.

### WP-2 — Native interop and managed contracts

- Add `VideoTypes`, `IVideoPipeline`, `VideoPipelineSettings`.
- Add `NativeGpuInterop` and `NativeGpuPipeline` skeleton.
- Add `GpuCapabilityProbe` with named failures.
- Add `WorkspaceWindowHost`.
- Expected result: managed contracts exist; implementation may be stubbed.

### WP-3 — Session rewiring

- Add `HostServices.CreateVideoPipeline`.
- Replace `PhoneSession`'s frame source/encoder/frame loop with `IVideoPipeline`.
- Move keyframe/resync/health handling to pipeline calls.
- Send `Target` from pipeline metadata.
- Remove `_frameBuf`, `_visibleFrameBuf`, CPU crop, and FFmpeg process ownership.
- Expected result: `PhoneSession` structurally targets the GPU pipeline.

### WP-4 — Monitor/scale productionization

- Move the POC's scale readback/reapply/wait/gate logic into `ScaleEnforcer`.
- Invoke it from the production session before window creation.
- Keep identity in `DensityNegotiator`.
- Rename `PocMonitorHost` to `WorkspaceMonitorHost` and the command to a diagnostic name;
  it is not the session's monitor path.
- Expected result: one production scale path, one diagnostic hold tool.

### WP-5 — Remove old path and old tests

- Remove `FfmpegEncoder`, `EncoderArgs`, `EncoderPicker`, and `FlvDemuxer` from the default
  video path.
- Remove the `--ffmpeg` option and related help text.
- Delete/quarantine old media tests as listed in section 5.2.
- Keep diagnostics that use the driver surface; remove the video use of it.
- Expected result: no CPU/FFmpeg video path in `PhoneSession`.

### WP-6 — Structural checkpoint

- Commit the source placement, contracts, session rewiring, and old-path removal.
- It may not compile. It may not run. It is not evidence.
- Do not merge to `main` in a way that claims it is runnable.
- This is the checkpoint the owner asked for.

### WP-7 — Make it work

- Fix native/managed build and ABI.
- Create the window, start WGC, register textures, force IDR, drain access units.
- Send over the existing transport; handle keyframe requests and renegotiation.
- Verify exact window size/DPI and monitor identity.
- Implement cadence/dirty policy; do not hardcode 60 fps or blindly throttle native
  arrivals. Measure actual arrival and encoded cadence, preserve native rate where the
  encoder/transport can carry it, and report the real value.
- Verify color VUI/range end to end.
- Verify teardown: pipeline, window, monitor, scale restore, physical displays, driver
  `monitor arrived 0`.
- Add the native CI job and make targeted gates green.

### WP-8 — Merge and cleanup

- Land the integration on a temporary integration branch; review the structural checkpoint.
- Merge to `main` with a normal merge commit (no rebase/squash/force-push) so the POC history
  remains traceable.
- After `main` contains the integration and the agreed gates are green, delete the POC and
  integration branches.
- Record the merge SHA, CI URL, and the remaining GPU-path limitations in the handback.

---

## 8. Frame pacing, timing, and evidence rules

These are not optional “nice to have” items; they are how the integration avoids repeating
the POC's known defects.

- WGC arrival is not the same thing as a unique picture. The POC saw ~144 arrivals/s while
  the encoder was configured for 60 fps. The new pipeline must measure and choose its
  cadence; it must not silently encode the same picture 144 times or claim 60 fps because
  the option says 60.
- `TargetRecord.RefreshHz` must reflect the actual display negotiation or measured pipeline
  cadence, not an optimistic constant.
- H.264 Annex B has no reliable container timing. The transport PTS comes from the managed
  session's monotonic clock; do not infer it from NVENC output.
- Access units must be complete and independently sendable. Keyframes include SPS/PPS/IDR.
- GPU-copy timing remains unmeasured until D3D11 timestamp queries are implemented. Do not
  present `-1` as zero or assume the copy is free.
- M4 and M5 remain follow-up work. The structural checkpoint makes no performance or
  compatibility claim.
- Evidence from `acccc00` remains valid only for the POC tree it measured. New integration
  code invalidates it until the new path is re-measured.

---

## 9. What not to do

- Do not leave `PhoneSession` reading CPU frames while adding a dormant GPU library.
- Do not keep `poc-monitor` as the only place that enforces scale.
- Do not shell out to the POC EXE from the agent; that is a sidecar, not integration.
- Do not keep `windows/poc/...` and a copied production native tree as two implementations.
- Do not preserve old FFmpeg tests as a blocker for the structural checkpoint.
- Do not weaken the elevation gate or edit the installed driver ACL.
- Do not change physical displays, global DPI registry, or topology.
- Do not commit evidence, logs, profiles, serials, or secrets.
- Do not claim the integration is runnable, native-refresh complete, or performance-proven
  before the make-it-work milestone actually demonstrates it.
- Do not force-push, rebase, or squash the pushed POC history.

---

## 10. Review questions for the other agent

1. Is the native component a shared library loaded by the agent, or a helper process? This
   plan recommends the in-process DLL because it avoids a new IPC protocol and keeps the
   GPU texture path inside one process. If a helper is preferred, the plan's contracts still
   apply, but the transport/IPC must be designed before WP-1.
2. Should FFmpeg be deleted now or kept as an explicit fallback? This plan recommends
   deleting it from the default path and accepting temporary WGC+NVENC-only coverage. A
   fallback is allowed only as a named `IVideoPipeline` implementation, never as a silent
   default.
3. Should the first structural checkpoint be allowed to fail to compile on `main`? The owner
   has accepted this. If the reviewer requires a compiling checkpoint, keep the old path
   behind a temporary compile-time shim until the new path builds; do not call that shim the
   target architecture.
4. What is the color contract: full-range BT.709 (POC-proven) or limited-range BT.709
   (existing FFmpeg path)? Pick one, encode it in VUI, and test it. Do not mix.
5. Should `WorkspaceWindowHost` be raw Win32 or WinForms? This plan recommends a minimal
   dedicated host (Win32 or a purpose-built WinForms host), not `ProbeForm`; the probe window
   is diagnostic and should not become the product window.
6. Does the reviewer accept the temporary capability cut (WGC+NVENC only) until other GPU
   encoder backends are added? If not, define the fallback/backends before deleting FFmpeg.

---

## 11. Definition of done for this plan

This plan is done when the reviewer has either accepted it, or returned a concrete
alternative for the six decisions above. The plan does not itself merge anything, change
code, or claim the GPU path is complete. Its purpose is to make the integration target
unambiguous before the merge work starts.

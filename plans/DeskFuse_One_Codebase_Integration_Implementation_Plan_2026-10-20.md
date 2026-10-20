# DeskFuse — ONE CODEBASE production integration plan

**Status:** Implementation plan; replaces the limited POC-only merge strategy for product integration.  
**Decision:** The GPU/WGC POC is complete enough to become production source. Integrate it now into the **one maintained DeskFuse application codebase**, not a second long-lived runtime, separate POC application or parallel maintained product.  
**Prepared:** 2026-10-20. **Canonical destination:** main of braindamp/deskfuse.  
**Scope:** Main Windows host and Android client, GPU video, virtual display, Device Bridge, protocol, media, shipping, migration and removal of duplicate paths.

## 0. Definition of ONE CODEBASE (non-negotiable)

- One Git repository with **main as the sole enduring integration/source-of-truth branch**. Short-lived merge/integration branches are transaction safety mechanisms, not independently developed products. Do not leave the POC and Device Bridge branches as competing ongoing implementations. Archive/close superseded PRs after their changes have been reconciled into main.
- One Windows DeskFuse host product and one Android app. Native C++ GPU/D3D/WGC/NVENC modules and C# services are allowed: these are implementation modules compiled, versioned, packaged, launched and stopped **by DeskFuse**. A native DLL is a product dependency, **not a separate POC executable or separately installed application**.
- Exactly one authoritative owner of each monitor, capture source, encoder instance, authenticated session, input focus and media route. A Windows display must never be created or torn down independently by a second helper claiming the same device.
- The **main runtime path** uses the proven native WGC texture-to-encoder architecture on supported hardware. CPU desktop capture/FFmpeg is a deliberately bounded **compatibility backend only** when necessary; do not maintain a second independent session/product architecture. Log honest fallback and density/color limitations.
- Keep tests and reproducible evidence tools under the one repository, but do not ship or invoke a production-side standalone DeskFuse.GpuWorkspacePoc executable. Retire/relocate the test-window harness after the native backend is integrated; retain accepted evidence outside Git as reference, not runtime state.
- There is no permanent "later merge the POC" phase. The POC source becomes production source before the integration is declared complete. The previous POC-only merge plan is superseded as the **end state**; its provenance, clean-tree, safety, CI and rollback checks remain useful.

## 1. Existing verified baseline and how it differs from the final product

Baseline checked against remote on 2026-10-20 (re-read before any commit):
- main: dff5f1525eb33509b016a6e9907f945c1d11198a.
- poc/gpu-phone-workspace-wgc: acccc008ad1a3d781f777c1d5f3ce866eb3f4d80; 2 commits beyond the above main; CI workflow run 39343907786 passed.
- The user-provided MERGE_PLAN_poc-gpu-phone-workspace-wgc.md (2026-10-20) authorizes an evidence-preserving **POC-only merge**, not a production GPU runtime; it requires a human-coordinated merge window. This plan goes further.
- The stacked functionality branches exist through feature/audio-surround-negotiation (draft PRs #5-#10, with their dependencies). They are **not in main**, have not been validated as one built product, and must be reconciled rather than described as shipped.
- Experimental grid PR #4 was closed and must **not** be resurrected as the default interaction: it uses physical Windows HWND foregrounding.
- Today main has a C# PhoneSession with an IFrameSource copying CPU BGRA from the shared FocusIdd surface, FFmpeg H.264 via an IVideoEncoder.WriteFrameAsync(BGRA) interface, and Android MediaCodec surface decode. This is not a GPU-native WGC pipeline.
- The native POC has WGC HWND capture of its **own test window**, D3D11 texture slots, NVENC H.264 and BT.709 full-range color probes. The POC encoder writes Annex-B H.264 to a local file; it does **not** send authenticated DeskFuse video or handle arbitrary app-copy semantics yet.
- The POC demonstrates a 2100x3120@350% Windows backing display and 1440x3120 captured-window geometry for that evaluated phone; **not every phone profile or every Windows application is proven**. Keep phone-specific calibration/read-back and honest fallback.
- The installed FocusIdd device ACL currently forces elevation for POC monitor control; shipping a network-facing DeskFuse host as administrator is **not** an acceptable production permission solution.
- POC observed ~144 Hz WGC arrivals with a requested 60 fps encoder configuration. **Production pacing and latency accounting are not proven.**

## 1A. Exact POC code disposition — replace, consolidate, or retire

**The POC is not a shipping backend or a separately maintained implementation. All proven GPU/native-DPI/color technology migrates into DeskFuse.** The code inventory below resolves what "use the POC" means at the file level.

| Source from accepted POC | Exact product disposition | Legacy implementation it supersedes or complements |
| --- | --- | --- |
| \`windows/poc/gpu-phone-workspace/src/capture.h/.cpp\` (\`WgcCapture\`) | **PROMOTE** to the shipping native GPU engine; retain GPU texture slots, WGC session, exact-size validation, frame timestamps, lightweight capture callbacks, proper release. Replace the POC's HWND test source with a managed real-app source. | **REPLACES as primary** old \`IFrameSource\` / \`SurfaceFrameSource\` / \`PhoneSession.FrameLoopAsync\` CPU desktop-frame capture; old source can serve explicit unsupported-GPU compatibility only. |
| \`windows/poc/gpu-phone-workspace/src/nvenc_encoder.h/.cpp\` (\`NvEncoder\`) | **PROMOTE** NVENC's D3D11 interop, resource registration, low-latency encode and SDR BT.709 signaling. Refactor \`Encode\` to emit in-memory Annex-B access units with SPS/PPS, keyframe and timing metadata; **remove file output**. | **REPLACES as primary on NVIDIA** old \`FfmpegEncoder\`/FFmpeg child-process and stdin BGRA pipeline. Other GPU/codec backends must be selected by capabilities, not forced through a CPU readback contract. |
| \`windows/poc/gpu-phone-workspace/src/common.*\`, GPU/D3D helpers | **REUSE/EXTRACT** only native shared functionality required for WGC/NVENC, adapter matching, timing and error handling, under production namespace/module. | Existing C# session infrastructure remains; avoid duplicate monitor/session owners. |
| DPI/layout and stable monitor identity in \`windows/agent/FocusWindow.Agent/Display/PocMonitorHost.cs\` | **MIGRATE** verified profile identity, EDID match, requested-vs-actual DPI/scale checks and guarded teardown into the one production \`DisplayResourceManager\`, using current \`WindowsDisplayBackend\`/FocusIdd. Retire \`PocMonitorHost\` as an independent runtime. | **REPLACES** redundant/ineffective exact-1440 scale-calibration choices and competing monitor lifecycle paths. Retain useful physical-display guard, driver interop and heartbeat code rather than replacing the FocusIdd driver. |
| \`windows/poc/gpu-phone-workspace/src/test_window.*\` | **TEST-ONLY then retire from shipped runtime.** Keep ruler, colors and dimensions as automated diagnostic fixtures if useful. | Real DeskFuse source/window management supplies capture HWND and input mapping. |
| \`windows/poc/gpu-phone-workspace/src/main.cpp\` | **EXTRACT** useful capture orchestration and diagnostics into production GPU engine and nonshipping tests; **DELETE standalone POC program entry point** from product builds. | Existing \`Program.cs\` remains the one host entry point; it creates unified authenticated sessions rather than launching a parallel demo. |
| \`windows/poc/gpu-phone-workspace/CMakeLists.txt\` and native app manifest | **REWRITE** into one integrated native DLL build, versioned ABI, packaging, CI and matching x64 runtime. | Replace separately launched native EXE, manual POC build and independent deployment. |
| \`third_party/nvenc/nvEncodeAPI.h\` | **RETAIN where required** in native shipping component subject to license review; no duplicated copies. | Supports the new native encoder. |
| \`third_party/nlohmann/json.hpp\`, BMP/CSV/JSON evidence exporters | **RETAIN only if product needs them**; otherwise remove from runtime and reserve for nonshipping diagnostic tools. | Product runtime should use a typed/versioned ABI, not pass POC JSON evidence between native GPU engine and .NET. |
| \`tools/build-gpu-workspace-poc.ps1\`, \`tools/run-gpu-workspace-poc.ps1\`, \`poc-monitor\` CLI | **RETIRE from shipping** after incorporating useful safeguards into host startup and unified build/CI. Retain test-only scripts under diagnostic/test names where they still verify native hardware. | Single DeskFuse build/start flow; no second manual POC runner or release-flag handshake. |
| POC spec, architecture and historical evidence | **CONSOLIDATE** proven facts, limitations and reproducible acceptance references into the canonical project architecture; preserve old commit provenance in Git history. Evidence folder stays ignored. | No competing architecture specifications or current product status split between old POC and main. |

### Explicit legacy-deletion rule

Once the integrated GPU path has passed the **real app's** encoder/decoder, native DPI, color, touch and teardown gates:
1. The GPU path is the standard capture/encoding route for supported hardware and sources. **Do not keep the old CPU/FFmpeg pipeline as the hidden default.**
2. Remove the old verified-viewport **CPU cropping** from the main streaming path; its replacement is WGC's exact captured HWND pixel geometry.
3. Keep a **clearly named, isolated compatibility backend** for hardware that cannot use native WGC/NVENC *only while it is genuinely required*. Compatibility implementations must not duplicate PhoneSession, monitor ownership, device catalogs or transport semantics. Remove obsolete calibration algorithms when the replacement covers their supported use cases.
4. Remove the POC executable, native standalone main, POC-only \`Program.cs\` command, separate test-window launch and file-output path from shipping code and release artifacts.
5. No critical implementation may remain **only** in \`poc/gpu-phone-workspace-wgc\` after main integration. Historical commit pointers are retained for audit, **not an ongoing code dependency**.
6. The final source/build must use one GPU engine, one stream transport, one monitor/session owner, one Android receiver and the same peer/device bridge.

### Things the POC did NOT replace

Do not delete or rewrite the existing authenticated enrollment, AES-GCM framing, input authorization, Android \`MediaCodec\`, file/clipboard operations, Device Bridge, microphone/camera capability design, physical-display guard or FocusIdd driver **just because they are old main code**. Connect the new POC rendering engine to those services. FocusIdd was a dependency of the POC, **not a component superseded by it**.

**Known adaptation, not a rejection of POC technology:** NVENC's current \`std::ofstream\` output, the synthetic HWND, CLI release flag, POC client ID and diagnostic JSON cannot be shipped unchanged; replace each with production session access-unit callbacks, actual source ownership, authenticated enrollment ID and structured in-process runtime state. Keep the working WGC/D3D/NVENC/color/scale mechanisms.

## 2. Target production architecture

### Windows runtime in one application

- **SessionCoordinator**: authenticated enrollment, peer/session role, lifecycle, state transitions, grants, reconnect leases and bounded shutdown. Owns neither GPU textures nor Windows system settings directly.
- **DisplayResourceManager**: the sole virtual-monitor allocation authority, stable per-enrollment monitor identity, supported mode/scaling, verified DPI, physical-monitor snapshot/guard, normal release and crash cleanup. Present backend initially has exclusive monitor admission because its creation/removal API is host-global. **Architecture does not assume one device or one monitor** once the backend supports per-display lifetime.
- **SourceCatalog / WindowSource**: typed source identities and lifetimes, distinguishing managed virtual-desktop windows, existing physical HWND mirrors, future managed Chromium and extended-display surfaces. A source ID must not be a raw HWND presented as permanent identity. Binding source visibility and consent to enrollment is mandatory.
- **GpuCapturePipeline** (native DeskFuse module): WGC frame pool, texture-slot management, D3D adapter matching, source-relative crop/window geometry, GPU encoder and quality/latency telemetry. **Does not own the entire authenticated network session.**
- **VideoPipelineCoordinator**: chooses GPU/WGC/NVENC or supported explicit fallback according to capability and actual source. Owns bitrate, pacing, keyframe requests, frame-generation/version invalidation and recovery. Offers a common output of **encoded access units plus metadata**, not a common raw-BGRA input that would force readback.
- **TransportMultiplexer**: authenticated AES-GCM control/media routing. Maintains ordered record counters per transport lane; bounded queues and fair/priority scheduling. A future dedicated real-time media lane must be compatible with current enrollment and session security.
- **DeviceBridge**: each enrolled phone/tablet may independently contribute microphone, speakers, camera, touchpad, keyboard and sensors. Media-only/accessory peers cannot allocate monitors or invoke arbitrary display/input/file commands. Per-device camera/audio endpoint IDs and presence semantics must be stable.
- **PrivilegeBroker** (minimum narrow OS-facing component, if driver ACL requires): local authorized control for FocusIdd create/remove/heartbeat/scale and state repair. Privileged operations must never be triggered by arbitrary remote text or executed inside an elevated internet/LAN-facing host.

### Android (same application)

- Phone-native fullscreen focused view using current MediaCodec/Surface; adaptive session chrome, input geometry and quality/degradation indicators.
- Android Activity/SessionController manages session intent, runtime permissions and remembered per-host device choices; transport/readers and decoders are separate.
- Two distinct modes: **focused Windows activity** (default product direction) and **extended Windows monitor** (additional mode; hardware/backend readiness gated). Optional full-desktop fallback. Spatial/grid UI is deferred and must never change physical Windows layouts.
- Devices may combine functions: Phone A camera+mic, Tablet B extended display+speakers, Phone C touchpad/buttons. One-device UX first; never code a global singleton "active phone" that rules out the combination later.

### Ownership/data-flow diagram

Authenticated peer -> SessionCoordinator -> DisplayResourceManager -> WindowSource
                                       -> VideoPipelineCoordinator -> GpuCapturePipeline (WGC -> D3D11 -> NVENC)
                                                                  -> encoded H.264 access units -> TransportMultiplexer
                                       -> DeviceBridge audio/video/input tracks independent of display
Android transport -> target metadata + H.264 -> MediaCodec -> native surface
                  -> audio / mic / sensor tracks governed by capabilities and permissions

The POC test executable and the production Desktop CPU pipeline are **not parallel sources of product truth**.

## 3. Ordered implementation work packages

### I0 — Reconcile sources and prepare one short-lived integration transaction

1. Coordinate the same exclusive merge window required by the provided POC plan: all agents freeze main writes and overlapping status-doc edits. Confirm live origin refs, build provenance, accepted evidence, clean tree and a backup ref; do not assume the SHA above remains current.
2. Create **one temporary unified-integration branch** from current origin/main, strictly to assemble the one new main tree. Preserve the tested POC tip as a merge parent (no rebase/force-push). It is not a new maintained product branch.
3. Bring POC code and history into that integration tree, then **immediately migrate its native capture and encoding implementation into production paths** described in I1. Do not stop at a merged POC executable.
4. Bring code from draft PRs #5-#10 into this same tree in dependency order, reviewing every file. Merge/cherry-pick or reimplement individual changes as needed; never wholesale-copy Program.cs, PhoneSession.cs, HostConfig.cs, Protocol.kt or Android SessionController.kt from an outdated branch.
5. Publish **one canonical integration PR / final merge**, not a continuing stack of feature PRs. Once integrated, close/archive the superseded stacked PRs; delete temporary integration branches only after main and delivery verification. Development thereafter branches briefly from main and lands back in main promptly.

Completion: no active production source is maintained only under poc/ or feature/*; all retained features originate from the one main tree.

### I1 — Convert POC into a native production component, not another program

Move/refactor:
- windows/poc/gpu-phone-workspace/src/capture.* -> windows/native/DeskFuse.Capture/WgcCapture.* (reusable WGC source).
- windows/poc/gpu-phone-workspace/src/nvenc_encoder.* -> windows/native/DeskFuse.Capture/NvencEncoder.* (reusable encoder).
- Common, vendored header dependencies and D3D build rules -> native module CMake target. Preserve applicable third-party license notices.
- POC main.cpp, test_window.* and file-writing output -> a **non-shipping test/diagnostic harness** under windows/tests/gpu/ or retire after replacement by production validation. Remove their runtime coupling.
- Native library packaging -> Windows host publish/copy; one supported x64 build/release workflow. No manual second POC build required to get production DeskFuse running.

Provide a small versioned C ABI at the managed/native boundary (for example: create, capabilities, start-source, poll/callback encoded-packet, request-IDR, change bitrate/fps, stop, destroy). Clarify GPU-thread ownership, callback lifetime, cancellation, shutdown and native buffer ownership. Native code should return encoded Annex-B access units **in memory**, not write to an .h264 file. Avoid calling managed blocking operations from WGC callbacks.

Keep D3D11 resources on compatible adapters; verify that the NVENC adapter can register the captured textures. Invalid combinations get an explicit supported fallback. Preserve the proven GPU-only steady path, bounded texture slots, error propagation, BT.709 full-range metadata and no intermediate CPU BGRA readback on the GPU backend.

Completion: DeskFuse's shipping host loads this native module and can request encoded data for a production source; there is no shipping GPU POC executable.

### I2 — Replace the monolithic video path with one production pipeline coordinator

Refactor current windows/agent/FocusWindow.Core/Session/PhoneSession.cs:
- Move monitor creation/calibration/teardown to DisplayResourceManager; PhoneSession holds a display lease.
- Move source acquisition, WGC start/stop and frame scheduling to VideoPipelineCoordinator.
- Remove the assumption that every encoder accepts ReadOnlyMemory<byte> BGRA from the driver surface. Define the seam at **encoded H.264 access units plus capture timestamps** and source geometry.
- Keep current encoder/CPU surface reader temporarily as a named compatibility implementation, not a second PhoneSession and not default for supported WGC/NVENC.
- Map POC encoded packets into existing PhoneConnection.TrySendFrame (Annex-B, keyframe flag, targetVersion, monotonic PTS in microseconds, bounded AU size), including SPS/PPS/IDR availability on startup/resync.
- Implement in-place IDR request through the native encoder where supported rather than restarting FFmpeg; when frame drops invalidate H.264 prediction, stop delivery until a fresh valid IDR.
- Provide bounded startup/restart, timeout, cleanup and version-switch semantics. Failure of WGC or NVENC must not strand a virtual monitor or an authenticated session.

Completion: one authenticated Android client can decode the **production native WGC/NVENC output**, not a standalone file, at an exact negotiated resolution.

### I3 — Production virtual display, real DPI and source geometry

- Adapt the validated POC's per-device display identity/EDID/scale readback into DisplayResourceManager. Use **cryptographically bound enrolled device identity**, not the CLI POC test pseudonym or IP address. Maintain one profile per enrolled device and reverify effective DPI on every attach/reconnect, even when preferences are cached.
- Produce a stable 2100x3120 / 350% backing monitor only for profiles where the hardware/software capability and readback support it. Generalize search for other panels and orientations; never hardcode the demonstrated phone's numbers as a universal requirement.
- Create an owned target window within the virtual workspace at **exact phone pixel dimensions**. WGC can then capture exactly that HWND. Maintain the actual HWND/window bounds/client insets and DPI; do not assume it begins at monitor X=0 or that its frame equals the backing monitor frame.
- Correct InputGeometry using source client-to-screen/desktop transform and actual WGC target geometry, including rotation and pointer capture; the POC test window is centered horizontally, whereas today's viewport input path assumes a left-origin crop.
- Keep physical-monitor topology, primary flags, rectangles and DPI unchanged; never move, resize, minimize or foreground existing physical-desktop windows merely to maintain a phone grid.
- Explicit **source modes**: (a) DeskFuse-managed window launched/placed on its owned virtual monitor; (b) read-only or user-opted capture of an existing physical HWND, which is a shared view and **not a separate independently editable copy**; (c) optional desktop stream; (d) later extended-monitor and offscreen Chromium. Do not claim arbitrary existing app windows can be duplicated independently without proving their app-specific state and input isolation.
- Centralize virtual display close/release and physical topology guard; no POC helper separately creating or tearing down the same monitor. The existing exclusive resource admission remains while FocusIdd teardown is host-global.

Completion: a managed native-DPI focused window streams 1:1 to the phone with correct input coordinates and without altering the physical PC workspace.

### I4 — Native color fidelity and end-to-end metadata

- Preserve POC's correct BGRA/D3D capture and NVENC H.264 color signaling; report primaries, transfer, matrix, range, pixel format, encoder backend and capture resolution accurately. Do not label NVENC YUV420 as full-resolution RGB or promise 4:4:4 on Android's H.264 decoder.
- Update TargetRecord / negotiated optional capabilities conservatively. Preserve v3 framing and Android compatibility where possible; new message fields may be optional but an unsupported new wire record **must not be sent to an older client**.
- At source/encoder/Android decoder boundaries track the physical window density, logical DP canvas, one-to-one pixel geometry, crop and source-versus-target color semantics. On Android do not introduce an intermediate bitmap or rescaling layer on a native-matched surface.
- Define an explicit HDR/SDR policy: the measured POC is SDR BT.709 full-range; HDR and advanced color are **not silently certified** until source, encoding and Android decoding are all supported.
- Accept color only on decoded Android outputs (plus codec metadata), not merely POC source values. The POC's decoded ffmpeg delta <=2-code-value result is an input baseline, not universal Android decoder proof.

Completion: the same genuine native-resolution source is decoded on the phone with truthful color/density reporting.

### I5 — Latency, pacing and transport convergence (architectural requirement)

- WGC callbacks must remain nonblocking. Use a latest-frame / tiny bounded texture-slot policy; never encode stale backlog or blindly encode all ~144 Hz arrivals when the session negotiates 60 fps. Preserve the final frame after a burst, allow 0 fps on static windows, force an IDR as needed.
- Carry capture QPC, encoder submit/output timestamps and session PTS; measure capture->encoded AU->encrypted transport->Android decode->surface present. Map clock domains deliberately, rather than treating packet-arrival time as the only latency.
- Introduce bounded queues for video AUs, per-device microphone/audio and file traffic. Explicitly manage oversize H.264 AUs and backpressure; keep one H.264 resync coordinator.
- **Avoid permanent TCP head-of-line coupling of media and files.** The current control/video/audio/file shared reliable stream is a starting compatibility path. Plan an authenticated priority/separate real-time media lane with independent sequence/nonce space and congestion control, without weakening replay protection or duplicating pairing. Large file transfers must not starve control, mouse, mic or audio.
- Keep GPU-native capture and encoding, minimize buffer copies, avoid unnecessary device/color conversions, prefer low-latency hardware and stable frame timing. Expose counters for WGC backlog, NVENC queue/waits, dropped/resynced frames, encode times, device-to-device audio jitter and RTP-like arrival timing (transport choice still open).
- Establish hardware measured p50/p95 **budgets** per stage before declaring performance achieved; the plan does not claim unmeasured numbers. AV sync and channel-preserving 2.0/5.1/7.1 routing belong to the same performance model.

Completion: no unavoidable image readback, FIFO growth or file-induced audio/input starvation is designed into the main path.

### I6 — Consolidate functionality PRs #5-#10 into the new main runtime

Reconcile, do not import indiscriminately:
1. **#5** Windows mixed-output -> Android playback (WASAPI/AudioTrack) with explicit host and phone permissions. This is audio *mirroring*, not a true Windows per-phone speaker endpoint.
2. **#6** per-enrollment logical camera/mic/speaker IDs, reconnect leases and concurrent authenticated accessory peers.
3. **#7** composable capabilities (camera, mic, speakers, display, touchpad, keyboard, sensors) and replaceable display admission. The current backend is exclusive **by capability, not product requirement**.
4. **#8** remembered per-host opt-in for PC sound when a streaming session is restored; do not confuse a saved preference with an OS capture permission.
5. **#9** permissioned Android AudioRecord -> authenticated phone-mic PCM stream -> explicitly configured **temporary third-party virtual audio cable**. Treat it as a development adapter, not the shipped native Windows microphone endpoint.
6. **#10** channel-mask-aware 2.0/5.1/7.1 capture/playback negotiation, speaker-aware downmix and bounded audio packets. Validate the actual Android output hardware route; do not claim surround on stereo speakers.

Compile/resolve overlapping files **once**, especially Program.cs, HostServices/HostConfig.cs, PhoneSession.cs, SessionHost.cs, PhoneConnection.cs, Protocol.kt, LanTransport.kt, SessionController.kt, AndroidManifest.xml and SessionChrome.kt. Preserve consistent handshake roles, capability gates, stream ID lifetime, optional fields and encryption framing.

The *separate physical PnP* provider is still future product work: each trusted phone should eventually expose its own hotpluggable Windows microphone, camera and speaker with stable identity on reconnect (Option A). Do not silently leave a disconnected device ACTIVE and feed silence. Media-only devices must work without owning a monitor; actual Windows endpoint installation is a separate implementation milestone inside this **same repo and main application**.

Completion: one installed DeskFuse Windows/Android build contains all safe, reconciled and supported Device Bridge functionality from the draft PRs. Unfinished native PnP/hotplug remains explicitly marked unfinished, not shipped as a fake endpoint.

### I7 — Privileged display access and packaging

- Keep the normal paired Windows host least-privileged and interactive. If installed FocusIdd ACL requires elevation, design a **minimal local, authenticated Windows broker/service** with narrowly scoped display-control operations, strong peer validation, limits and crash repair. It is a bundled implementation module of **one DeskFuse product**, not another independent application/codebase.
- Never loosen the ACL blindly, never expose registry DPI writes to phone requests, and never run the LAN control host elevated as a workaround. Preserve physical-display snapshot/recovery and watchdog semantics.
- Build native library from the one Windows host build/release; ship the matching DLL next to the .NET host, with architecture/version/ABI checks. Enable Windows-native build in CI; avoid an external manual POC dependency.
- Replace the public poc-monitor command with properly scoped diagnostic/maintenance entry points or a non-shipping test. Do not include a second GPU demo binary in the consumer installer. Keep reproducible native diagnostic builds/tests in the repository where useful.
- Package versioned signed host and Android release artifacts together; document codec/license/vendored header notices and rollback/recovery.

Completion: installing, updating and launching **DeskFuse** is sufficient to run GPU/native focused window streaming on compatible hardware—no separate POC launcher, manual monitor helper or elevated network service.

### I8 — Product behavior, devices and feature completion

- **Default phone mode:** focused window at true phone pixels, full screen. Selection/switching affects DeskFuse-owned virtual app sources, not the physical desktop layout. Previous grid brainstorming is not a blocker.
- **Second display mode:** tablet/phone as actual Windows extended display, separately selectable when per-display allocation and teardown become safe. Do not couple all media capabilities to owning that monitor.
- **Input modes:** touchscreen mapped to captured window; standalone phone touchpad with mouse buttons, scrolling and safe target arbitration; keyboard/IME via Android input forwarding.
- **Device Bridge:** per-device mic/camera/speaker sources with genuine Windows presence and permissioned routes in both directions; persistent enrolled identities and preference restoration following genuine hotplug semantics.
- **Future options:** managed offscreen Chromium, source previews, efficient background feeds and possible paid/Pro background-mode use (screen off, mic/audio continuation) are future scope and subject to actual Android foreground-service/permission policy. Do not lock the transport and lifecycle against such capabilities.
- Continue optimizing functionality ahead of spatial grid/layout experiments.

Completion: product workflows compose sources and device capabilities rather than forcing a physical Windows desktop mirror.

## 4. Integration gates and acceptance criteria

These are **completion gates**, not a request to reopen the already accepted POC research campaign. Preserve original POC evidence unchanged; validate only changed integration behavior and unavoidable packaging/lifecycle paths.

**Gate A — source consolidation:**
- One host build includes the native GPU component and Android client; no production path invokes a standalone POC executable.
- The native capture/encoder production source exists once; test harness may link it but must not maintain a divergent copy.
- Full compilation succeeds for Windows .NET, native C++/CMake and Android. All agreed functional PR code is reconciled against the same main tree.
- No confidential hardware evidence/client identifiers/logs committed.

**Gate B — real main-app native session:**
- A paired phone starts a normal DeskFuse session and receives actual WGC/NVENC H.264 via AES-GCM framing on supported NVIDIA hardware.
- Proven native physical pixel size/DPI on the phone; correct WGC/window input transform including horizontal centering, window insets and rotation.
- H.264 SPS/PPS/IDR and decoder reset/resync work on connect, reconnect, bitrate/size change, transport congestion and window close.
- Android surface shows truthful stream codec/color/mode/degraded reason; does not silently upscale a smaller buffer while claiming native.

**Gate C — Windows safety:**
- DisplayResourceManager is the only display owner; physical Windows display positions, DPI, primary state and existing windows remain unchanged.
- Monitor release after clean disconnect/crash/timeout leaves FocusIdd monitor arrived=0 when owned by DeskFuse and physical topology unchanged.
- Normal network host is not elevated for access to installed driver; privilege resolution uses approved local-only boundaries.
- No POC-only registry rewrite, display-driver reinstall, ACL weakening or unowned monitor removal.

**Gate D — real functionality and performance:**
- Authorized sound and microphone toggles, disconnect cleanup, per-enrollment routing and basic accessory/display concurrency work in one build; no claim of a genuine PnP audio endpoint until such an endpoint exists.
- GPU queue bounded under >target-fps WGC arrivals; no extra CPU full-frame BGRA copy on native path.
- Frame lateness/jitter, dropped/IDR behavior, color fidelity, A/V/mic responsiveness and color-range handling are measurable end to end. Measure multichannel on truly capable Android output rather than a simulated stereo handset.
- Unsupported GPU/codec/density path fails explicitly to an honest compatibility mode or actionable error, never a false-native or blank stream.

**Gate E — one codebase end state:**
- main contains the final integrated production tree and the corresponding single build/installer pipeline.
- Old POC runtime, POC-only host command and unused duplicate source code are removed from shipping; any retained test harness is read-only test infrastructure.
- PRs #5-#10 are integrated/reconciled or each remaining scope is explicitly deferred; no long-lived competing feature branch is the only place where product functionality exists.
- The integration transaction branch can be deleted, tested POC tip retained as historical Git provenance, and all further work starts from current main.

## 5. Rollout, merge, and rollback rules

1. Work on **one short-lived unified integration branch** only while performing this merge transaction, to avoid committing an uncompilable intermediate state directly to main. This does **not** create a second product codebase. Keep main stable until the complete integrated product tree passes baseline software gates.
2. Merge the unchanged tested POC tip as an explicit ancestry parent (no rewriting proven history), **then immediately migrate its code into production** in that same transaction. Preserve accepted POC evidence and do not modify its remote branch.
3. Reconcile all Device Bridge functionality from the existing draft branches against this integrated tree. Close/retire duplicate draft PRs after successful integration. Respect an exclusive merge window when finalizing.
4. Add a single native+managed+Android CI build path. Fix defects in the integrated production files, not in two diverging native copies. Hardware acceptance focuses on the **integrated app**, not on re-proving a detached POC.
5. Merge one reviewed final integrated tree into main and then develop normally on **main as the sole source of truth**. Do not keep adding features only to old POC/device branches.
6. Use feature flags/capability selection for runtime fallback, not branches or parallel binaries. A feature flag is not permission to leave an unfinished GPU backend forever: default to validated native GPU path on qualified systems.
7. Rollback via normal revert of the integrated merge in Git; avoid force-push. Recover display ownership and driver state before reconnect. A product release can fall back to the integrated compatibility backend when native WGC fails.
8. **Stop** on unexpected driver/physical-monitor changes, new hidden driver privileges, incorrect GPU/color/DPI reporting, broken protocol compatibility, unclean display cleanup or unresolved security/identity binding—not merely because production integration requires more work than the original 22-file POC merge.

## 6. Near-term engineering order (not separate future projects)

1. **First:** land a single integrated buildable DeskFuse runtime with the native POC module moved into production files; no POC binary.
2. **Second:** wire GPU encoded access units to the current authenticated Android decoder; consolidate video ownership/session contracts.
3. **Third:** use native-scale virtual window/monitor provisioning and correct input mapping inside normal sessions; establish production permissions.
4. **Fourth:** reconcile and land Device Bridge PRs #5-#10 on the same main tree.
5. **Fifth:** finish channel-aware media/latency routing, per-device Windows audio/camera endpoint providers, independent tablet-display/touchpad modes and richer window-source workflows.

Items 1–4 are one integration effort with internal check-ins; **do not call the POC integrated after step 1 alone**. Item 5 is ongoing mainline product development against that integrated foundation.

## 7. Explicit non-goals of this integration

- No continued independent GPU POC application or separately maintained POC branch.
- No cosmetic grid implementation that manipulates physical Windows HWNDs.
- No assumption a WGC screenshot of a physical window is an independent interactive duplicate.
- No fake claims that an Android mic stream is already a genuine Windows PnP device, or that mixed-PC loopback is a per-phone Windows speaker endpoint.
- No desktop-admin elevation workaround, silent system-default device change or weakening of the installed display driver ACL.
- No promise that HDR, all GPUs, every phone's 350% DPI, full color correctness or end-to-end zero latency is solved without integrated device verification.

**Final success condition:** A user installs **one DeskFuse product** and, on supported hardware, gets full-screen focused Windows applications at true phone scale via the production GPU pipeline, with authenticated input and Device Bridge functionality in the same running experience. No detached POC program, no permanent experimental fork, and no second maintained Windows streaming stack.

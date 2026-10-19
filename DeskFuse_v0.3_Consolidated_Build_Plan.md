# DeskFuse — Consolidated Engineering Build Plan v0.3

**Project working version:** `v0.3.0-dev` (source candidate, NOT hardware-certified)  
**Canonical Windows working directory:** `D:\Coding\DeskFuse\v0.3`  
**Canonical source:** `DeskFuse_v0.3_FULL_SOURCE.zip`  
**Pi assignment:** `DeskFuse_v0.3_PI_HANDOVER.md`  
**Required return handshake:** `DeskFuse_v0.3_RETURN_HANDSHAKE.md`  

> **v0.3 scope clarification:** This retains the substantive P0–P5 architecture and feature requirements of the original plan. The current hardware **integration test environment** is Windows 10 Home build 19045; the plan's proposal to qualify later Windows 11 releases remains a future product platform decision, not permission for Pi to upgrade Windows. The application build labels are now v0.3, while installed known-good FocusIdd v0.2.29, driver INF, ABI v2, protocol v3 and guarded scripts are deliberately *not* relabeled or changed.

> **Pi gate:** Prioritize P0 Windows/Android compilation, P2 real authenticated phone connection, native video/input and reliable teardown. The separate 48-test v0.3 handshake determines whether those results can be accepted upstream. P1 true 350% DPI remains unresolved and cannot be represented as achieved just because P2 streams native pixels.

**Status:** Proposed plan for iteration — 18 October 2026  
**Objective:** Turn the original Desklink C and the two agent implementations into one dependable, secure Windows-to-Android workspace, using **Option A as the canonical implementation** and selectively incorporating improvements from Option B and Desklink C.

> **Current evidence boundary:** The delivered Windows code has not been proven with a complete WDK build and real Windows/Android hardware. Passing EDID/contract or simulated streaming tests does not prove the display driver installs, Windows applies target DPI, or the Android client successfully decrypts and renders actual frames. The plan below is designed to resolve that boundary first.

## A. Scope, product decisions, and non-goals

### Initial product contract (MVP)

- One Windows PC and one Android phone/tablet, connected over a trusted LAN, without an account or mandatory cloud service.
- One session-scoped **virtual monitor** created on demand, with the physical displays left untouched as far as supported by Windows.
- **Binding DPR-first, DPI-verified rule (the best of both worlds):** Android's effective DPR and current native pixels are the source of truth for the *desired* logical workspace; Windows' measured per-monitor-v2 DPI and the physical captured pixels are the source of truth for *actual* rendering. For a DPR-3.5, 1440×3120 phone, request 350%/336 effective Windows DPI, giving ≈411×891 DIPs, but never claim success without `GetDpiForWindow` and a 100-DIP → 350-pixel PMv2 ruler at native resolution. **Do not assume passing 350% to the console-session FocusIdd driver forces Windows to apply it.** Separate native-pixel from native-density status; reject silent video resizing or false DPI reporting. Treat Android font size/user ergonomics as additional adjustable preferences, not a substitute for honest DPR↔DPI verification. The 350% example is one device profile, not a hardcoded product scale.
- Phone-profile negotiation: physical pixels, DPR, physical panel dimensions, orientation and refresh support.
- **Hardware acceleration is a priority:** prefer actual NVIDIA NVENC, then usable Intel QSV/AMD AMF, with CPU encode fallback; select against the target phone's *real width/height*, not just a low-resolution capability claim. Prefer GPU-resident frame capture/encode and reduced CPU copies where proven feasible, with hardware Android MediaCodec decode and pixel integrity. The present FFmpeg BGRA pipe → NVENC path accelerates encoding but is **not** yet GPU zero-copy. Never claim NVENC or zero-copy verified without the real hardware run.
- Windows renders on the virtual monitor; stream native-size frames to a native Android H.264 decoder. The UI reports **pixel match** separately from **verified Windows DPI match**.
- QR pairing, human approval, authenticated encryption, remembered devices with revocation, and session reconnection.
- **Binding trusted-device file-access rule:** Once the Windows user explicitly approves pairing with the **full user file access** scope (the default trusted-device profile), the Android client can browse and manage all local drives, folders and accessible network shares that the *logged-in Windows user* can access: bidirectional upload/download, create folders, copy/move, rename, delete, and save/overwrite in arbitrary authorized destinations. No permanent `Incoming`-only sandbox or per-routine-operation approval. Offer an optional restricted profile and per-device revocation. All operations run under the user-session agent's normal user permissions, respect Windows filesystem/share ACLs, and do not silently inherit administrator or SYSTEM privileges from the installer/driver service. Explicitly warn/confirm destructive operations as appropriate.
- **Binding cross-device file-action rule:** DeskFuse must integrate with native file workflows on *both* platforms. On Android, expose "DeskFuse / Open on…" via supported Sharesheet/`ACTION_SEND` and DocumentsProvider/file-manager integration (third-party apps control their own long-press menus; universal menu injection is not possible). On Windows, expose a per-user Explorer "DeskFuse → [trusted device]" context-menu action. Selecting a file and target device retrieves the target OS's eligible actions/app associations, then invokes the **selected action on the target device**, preserving source-file ownership and location from the user's perspective through encrypted on-demand materialization or a hydrated, writable virtual-file provider where justified. Example: Android photo → Windows "Open with Photos" actually opens on the PC; Windows APK → Android "Install" opens Android's OS-controlled installer (user consent always required). Source does not move unless the user explicitly requests Move; temp cache is lifecycle-managed with writeback/conflict semantics for edits. No arbitrary remote shell commands or silent APK installation. Device chooser must handle multiple approved PCs/phones; use stable peer identities, routing and capability discovery, and anticipate phone↔phone via direct authenticated connection or an explicitly approved PC relay. Keep Android intent/URI grants and Windows user ACLs enforced. Treat real shell-extension handlers, external-storage availability, and disconnections as capability/compatibility constraints, not promises that every third-party action works.
- Touch, keyboard, explicit input modes, straightforward diagnostics and deterministic teardown.
- A **degraded mode** when Windows will not honor the requested DPI, disclosed to the user; no false “native-density” claim.
- Preserve the old Desklink window-focused workflow as a subsequent integrated mode.

### Proposed initial platform and feature limits (can be changed)

| Decision | Proposed v1 baseline | Reason / tradeoff |
|---|---|---|
| PC | Windows 11 x64; qualify at least 24H2 and 25H2 independently | Smaller driver compatibility matrix; Windows 10 can follow |
| Phone | Android 13+, ARM64 first | Smaller MediaCodec/input/permission matrix |
| Transport | LAN IPv4 first; E2E encrypted, authenticated | Explicitly avoids WAN, account, relay and NAT complexity in MVP |
| Session | One concurrent phone; one virtual monitor | Matches available driver/session model |
| Video | H.264, YUV420, 60 FPS target; hardware encoder when available, tested CPU fallback | Broad decoder support; do not promise 4:4:4 without verified decode |
| Frame rendering | 1:1 source-to-sink spatial dimensions; no silent resampling | Main readability goal; encoding artifacts still possible |
| DPI handling | EDID recommendation, verified window DPI; optional experimental Windows scale override **behind a flag** | The `DisplayConfig` -3/-4 method is undocumented and cannot be a default product guarantee |
| Existing apps | Start with desktop canvas; add window-targeted behavior after baseline proven | Option A explicitly does not implement clone/window occupant modes yet |
| Licensing | Keep core LAN/resolution/connection time unrestricted; monetization later | Directly addresses competitor complaints |

**Explicitly not in MVP:** unrestricted PC control of arbitrary Android apps; general WAN relays; multi-client conferencing; multiple simultaneous virtual monitors; HDR, 4:4:4, AV1, 120 FPS promises; pre-login/unattended secure-desktop support; an “installer-free” virtual display driver (a driver install requires privileges and signing).

## B. Single technical architecture

```text
Android (Kotlin/MediaCodec)
  ├─ QR pairing, secure key store, authorization UI
  ├─ device/profile + density contract and session state
  ├─ H.264 decoder → 1:1 render; touch/keyboard input
  └─ secure transport + reconnect
                ⇅ authenticated versioned protocol
Windows user-session host (Option A C#)
  ├─ SessionHost / PhoneSession / HostListener
  ├─ DensityNegotiator + trusted DPI/read-back and probe
  ├─ input sink, pairing store, physical-display journal
  ├─ pluggable video encoder (initially FFmpeg)
  └─ DriverDevice + validated shared-memory consumer
                ⇅ versioned IOCTL and memory ABI
FocusIdd (Option A C++ / IddCx / UMDF)
  ├─ virtual display arrival/departure, EDID and modes
  ├─ IddCx swapchain acquisition
  ├─ bounded BGRA surface + frame event
  └─ watchdog and error/status reporting
```

**Repository direction:** Retain Option A's `windows/driver/FocusIdd`, `windows/shared`, `windows/agent/FocusWindow.Core`, `FocusWindow.Agent`, `FocusWindow.Tests`, and `android/app`. Store the original `C.zip` host and `android.zip` separately as **reference sources** during integration, not concurrently running production servers. Retain Option B's C++ capture/encoder path only as a performance/diagnostic alternative. Do not try to combine its wire protocol with Option A's protocol 3.

**Core interfaces to freeze after the first verified build:**

1. **Driver ABI:** `FocusShared.h` ↔ `DriverContract.cs`, `IOCTL` fields, protocol version 2, surface layout and object ACLs. Regenerate tests from one contract definition rather than continuing independent handwritten copies.
2. **Phone wire protocol:** protocol version 3, authenticated hello, target/state/quality/status, framed video, input, keepalive and rejection/error messages. Kotlin protocol and C# parser require shared fixtures.
3. **Density state:** `requestedPixelMode`, `actualPixelMode`, `requestedScale`, `measuredScale`, `windowAwareness`, `effectiveDpi`, `pixelMatch`, `dpiMatch`, `reason`, and distinct `nativePixels`/`nativeDensity` flags.
4. **Session ownership:** user-session agent owns user/UI/input; driver owns indirect monitor and frame publication; one journal/guardian owns cleanup. No driver-side GUI operations.

## C. Delivery phases, dependencies, and gates

### Phase 0 — Canonical build baseline and security repair

**Goal:** Establish one reproducible source tree, and make every existing test or compile failure visible.

| ID | Engineering task | Direct code starting point | Completion evidence |
|---|---|---|---|
| P0.1 | Create canonical Git project from Option A Windows/Android; exclude generated frontend, build caches, credentials and experiments | Reconstructed Option A source | Clean checkout, reproducible file set |
| P0.2 | Reconcile inferred `FocusShared.h` v2 constants/structures with actual `DriverContract.cs` and C++ `contract_test.cpp`; test **Windows x64 ABI**, not just Linux shims | `windows/shared/FocusShared.h`, `.../Display/DriverContract.cs` | Native/C# field sizes, offsets, constants, magic, names and permissions all agree |
| P0.3 | Resolve all Windows agent .NET builds, warnings and tests, including final post-edit full suite | `FocusWindow.Core`, `FocusWindow.Agent`, `FocusWindow.Tests` | Clean Release build; full suite passes; no unreviewed suppressed failures |
| P0.4 | Reproducible Android build: Gradle wrapper, release/debug configurations, app signing kept outside repository, Android unit tests | `android/app`, `Protocol.kt`, `DensityContractTest.kt` | `:app:assembleDebug` and `:app:testDebugUnitTest` green |
| P0.5 | Lock pairing and shared-object ACLs to minimum privilege; eliminate sample keys and insecure stored-credential fallback | C# secure channel and DPAPI store; Kotlin `PairingRepository` | Security tests for reject/approve/revoke, corrupt storage, replay and unauthorized shared section access |
| P0.6 | Add CI lanes (Linux pure logic, Windows host, Windows WDK, Android); version stamps, test results | Existing tests | Commit fails CI if required gate fails |

**Gate G0:** Both applications compile; ABI verified on Windows; no default open/unapproved access; baseline tests green. If WDK is not available in CI, distinguish the manual WDK gate rather than marking it passed.

### Phase 1 — Windows-only driver + DPI proof (FIRST HARDWARE GATE)

**Goal:** Prove virtual-display reality before involving phone networking. **Do not write another driver from zero.** Use Option A's `FocusWindow.Agent density-probe` and `FocusIdd`.

| ID | Task | Measurement / acceptance criterion |
|---|---|---|
| P1.1 | Build the IddCx driver using matching VS/SDK/WDK in a disposable Windows test environment; resolve real driver-API errors | `FocusIdd.dll`, INF and signed catalog produce; test installation; driver status responds |
| P1.2 | Install, create and remove virtual monitor via `driver-status`, `SET_TARGET` and `REMOVE_MONITOR` | Windows enumerates monitor on demand; removes it, including crash/heartbeat scenarios |
| P1.3 | Run `density-probe` for 1080×2400@60 and 1440×3120@60 | Windows reports exact requested active resolution; no >2560-axis frame refusal; usable BGRA surface |
| P1.4 | Measure DPI using an actual per-monitor-v2 **probe window** and `GetDpiForWindow`; measure a rendered 100-DIP square in captured pixels | DPI read-back, expected square dimensions, and source frame dimensions recorded as separate facts |
| P1.5 | Correct DPI measurement semantics: `GetDpiForMonitor` should not be relied on directly from a per-monitor-aware thread; test explicit thread awareness/probe strategies | Effective window DPI reporting is internally consistent; DPI-unaware apps marked unsupported for this assertion |
| P1.6 | Test the *supported* EDID/recommended-scale behavior before attempting undocumented -3/-4 override; gate override behind explicit diagnostic opt-in | Requested/actual/recommended/capped scaling and error cause shown; no hidden Windows display mutation |
| **P1.6a** | **Arena DPI investigation (read-only first):** run `tools/run-density-readonly.cmd` from an x64 VS Native Tools prompt; compare the **640-DIP logical-width** and **recommended-scale-relative** explanations for the observed 1440×3120 / 100%-recommended / 225%-max readings. Existing FocusIdd already emits phone-derived physical EDID size; do **not** assume it is missing. Interpret `DisplayConfigGetDeviceInfo(-3)` as an undocumented read-only diagnostic; use `QDC_VIRTUAL_MODE_AWARE` and the correct CCD path union indexes; measure a real PMv2 `GetDpiForWindow` HWND on each display, with `GetDpiForMonitor` only as corroboration. | Record the readings for all active displays; derive a discriminating test before altering an EDID, mode or driver. Neither explanation is established by the existing 225% result. |
| **P1.6b** | **Conditional source-only lab candidate, with explicit hardware consent later:** if measurements favor a relative-cap/recommendation problem, investigate EDID recommendation using genuine reported physical size at native 1440×3120; if they favor a logical-width floor, investigate 2240×3120 plus a 1440×3120 native-pixel GPU crop and input offset. Do not build an old-resolution *fallback mode* in this work. | Explicit 336-DPI PMv2 window and measured 350-px/100-DIP ruler are required before success; oversized/cropped output is not automatically a solution. Keep v0.2.29 and its guarded teardown unchanged. |
| P1.7 | Add an independent physical-display journal (identity, mode, primary, orientation, bounds and scale), compare during and after; implement safe guided recovery, never blind stale-topology restoration | Before/after snapshots; no modification to physical monitors in pass path; clear recovery if drift occurs |
| P1.8 | Test safe driver teardown under disconnect, host kill, screen lock, sleep/resume and hotplug | The virtual display is removed/recoverable without stranding the user's primary display |

**Gate G1:** Driver installs and uninstalls; virtual monitor appears with correct pixel mode; pixel ruler is measurable from actual swapchain output; monitor cleanup works; DPI result is honestly classified as `verified`, `mismatch` or `unavailable`. **Exact 350% automation is a separate *experimental* gate, not a prerequisite for a useful monitor.**

**Mandatory DPI test cases:** 1440×3120 at DPR 3.5 (target 336 DPI, about 411×891 DIPs); 1080×2400 at DPR 3; foldable 1812×2176; landscape rotation; DPI-aware and legacy applications. If scale is not 350%, do not call the output native-DPI.

### Phase 2 — Secure end-to-end Android session

**Goal:** One real handset, one encrypted connection, actual frames and input.

| ID | Task | Completion evidence |
|---|---|---|
| P2.1 | Build protocol-3 test vectors for QR/ECDH identity binding, key confirmation, message sizes, sequence numbers, packet framing and replay rejection | Kotlin ↔ C# interoperability tests using the **same** vectors and cross-implementation calls |
| P2.2 | Pair by QR, verify fingerprint/host key, approve on Windows, persist identity securely, permit explicit revocation and per-device capability scopes | Approval clearly states default full **current-Windows-user** file access versus optional restricted access; wrong key, rejection and replay fail closed; reconnect does not silently expand granted permissions |
| P2.3 | Integrate agent shared-memory reader → FFmpeg H.264 → encrypted frame packets → Android `MediaCodec` | Real received and displayed virtual-monitor pixels, codec dimensions match source |
| P2.4 | Test end-to-end input mapping; absolute touch, trackpad alternative, right-click/drag, modifier keys and physical keyboard | Key and coordinate integration tests plus human acceptance checklist |
| P2.5 | Handle target refresh/rotation/orientation/bitrate changes with state versioning, IDR refresh, decoder reconfiguration | No permanent black screen; clear error on unsupported mode |
| P2.6 | Network handover, sleep/resume, dropped frames, duplicate packets and session termination | Authenticated resume or new secure handshake, bounded reconnect time, host cleanup |
| P2.7 | On-screen diagnostics: exact input/video dimensions, requested versus measured DPI, codec, FPS, encoder, latency and fallback reason | Distinguish `1:1 pixels` from `DPI verified`; never advertise unsupported 4:4:4 |

**Gate G2:** A real Android device securely pairs, streams desktop content and sends input for a sustained session, reconnects, and exits cleanly. Minimum first acceptance: 1080×2400@60 with no spatial downscale; 1440×3120 next. Measure latency rather than assume a target was met.

### Phase 3 — Desklink usability and productivity integration

**Goal:** Retain the strongest functionality from the original app without corrupting the new virtual-display foundation.

| ID | Task | Source and acceptance |
|---|---|---|
| P3.1 | Port Windows process/window enumeration, window picker, safe activation and region selection from `C.zip/host.py` | Works in new C# host; never selects another user's secret/secure desktop; manual choice is clear |
| P3.2 | Port window resize/restore and local-user stand-down from old Desklink; make it optional and transactional | App placements always restored when possible; local mouse/keyboard activity takes precedence |
| P3.3 | Port quick taskbar/window controls and keyboard UX from original `android.zip` and browser viewer | Android controls share same protocol; no second independent host listener |
| P3.4 | Add clipboard text first, then images; user-control toggles, size limits and sanitized logging | Bidirectional read/write with authorization and supported MIME types |
| **P3.5a — FIRST file-workflow priority** | Implement cross-device "Open with / Send to DeskFuse" actions: Android Share target/SAF selection → approved PC device chooser → Windows native eligible associations/context actions → Windows file materialization/hydration → chosen Windows app; Windows Explorer per-user context menu → approved Android device chooser → available Android MIME handlers, including APK install via system user approval. Support action requests/results, cancellation, temporary-file lifecycle, edit/writeback conflicts and per-peer capabilities over the authenticated protocol | Actual chosen app/action runs **on the target OS**, not a lookalike local action; source file remains at its original location, no implied permanent copy. No arbitrary command strings from peers; per-device authorization and OS permission checks; Android content URIs / URI grants honored, shell-extension safety respected. First SOURCE CANDIDATE now handles Android Sharesheet → explicit Open on PC with digest-verified document staging + Windows default file association; complete app chooser, virtual-display window placement, file hydration/writeback, context-menu extension and peer routing are NOT yet implemented. "Seamless location" may require a temporary staged file for read-only actions and a real hydrated provider for path-sensitive/editing applications. Do not claim universal Android long-press integration: Sharesheet is the supported cross-app entry point |
| P3.5b — immediate existing implementation | Build the full trusted-device file manager: browse authorized Windows drives/folders/network shares; upload **and** download; create, copy, move, rename, delete, overwrite and choose arbitrary permitted destinations | Default after explicit host pairing approval: full access **as the signed-in Windows user**, not as admin/SYSTEM; enforce OS ACLs and the device's granted scope for every operation, prevent path traversal/TOCTOU abuse, and provide progress/cancel, resumable/error-safe transfers and sensible destructive-action confirmation. The current fixed `Incoming` upload path is **interim only**, not the product permission model |
| P3.5c — multi-device foundation | Stable Windows/Android peer IDs, active-device selection and a device chooser; obtain per-device capabilities, MIME handlers and action labels from the target OS; route explicit actions to one authenticated peer | Multiple connected PCs/phones selectable; no action is silently sent to the wrong device. Design routing for eventual Android↔Android transfers without requiring a central account; a relay forwards encrypted authorized requests rather than becoming a universal untrusted filesystem server |
| P3.6 | Deliver **PC → Android audio playback** first (e.g. Windows WASAPI loopback → secure audio stream → Android playback); add optional **Android mic → PC** later with explicit microphone consent; separate audio transport, Android audio focus and mute controls | Playback works while remote video/input operate; A/V sync, stream latency, permissions, mute, audio-focus changes, disconnect/reconnect and impact on the physical PC user's audio are verified |
| P3.7 | Implement `canvas`, `quick view`, `window` UI honestly; do **not** advertise clone/window isolation before proven | Occupant states match actual host capabilities; unsupported modes visibly disabled |
| P3.8 — future opt-in automatic camera backup (not P2 work) | On Android, **save each photo/video locally first**, wait until its MediaStore item is finalized, then queue an encrypted transfer to the selected trusted PC destination. WorkManager/MediaStore observation can resume uploads when connectivity returns; respect Android's scoped media permissions and background-execution restrictions | Local original stays safe when PC is offline; receiver writes to a temporary file, verifies digest and size, atomically commits, then acknowledges. Deduplicate retries and preserve local media unless the user explicitly requests deletion. Multiple destination PCs are explicitly selected; no automatic sharing to an untrusted device. This is a later feature, not a P2 acceptance gate |

**Gate G3:** Can complete everyday workflow (open app, type, scroll, copy, use a native Android share action to open a phone file with an approved PC app, use Explorer DeskFuse actions to open a PC file with an approved phone app, browse/upload/download across user-authorized folders, hear PC audio on Android, disconnect, restore) without disrupting the physical PC user's ongoing activity. File permissions must reflect explicit trusted-device approval and normal Windows user access, never installer privilege.

### Phase 4 — Performance, reliability, security hardening

| ID | Task | Acceptance |
|---|---|---|
| P4.1 | Benchmark Option A IddCx-shared frame pipeline and Option B Desktop Duplication/encoder approach on identical hardware; cherry-pick B only if it improves a proven metric | Comparative data: encode p50/p95, FPS, latency, CPU/GPU load, memory, pixel integrity |
| P4.2 | Replace or optimize FFmpeg child-process path with Media Foundation/hardware MFT if necessary; retain software fallback | No mandatory GPL-encumbered binary unless the licensing/distribution obligations are explicitly accepted |
| P4.3 | Adaptive frame pacing (idle static frame budget, 30/60/90/120 device modes), bitrate and encoder settings | Measured thermals/power, responsiveness and dropped-frame rates by device |
| P4.4 | Reconcile video/chroma limitations: 4:2:0 H.264 initially; optional HEVC/AV1/10-bit only after hardware decode and text-fidelity tests | UI accurately reports codec and chroma; no promise of lossless text from lossy encoding |
| P4.5 | Threat-model LAN discovery, pairing, key rotation, event injection, clipboard, **full-user filesystem operations and destructive file actions**, process privileges and updates | Independent test matrix and documented mitigations; per-device revocation and authorization checks; never exceed the logged-in Windows user's rights |
| P4.6 | Fault injection: sleep, GPU reset, Windows Update, user logout, agent crash, phone rotation, renderer crash, router restart | Deterministic recoverable state, no persistent phantom display or stuck input |

**Gate G4:** Reliability and security criteria tested across a Windows/GPU/Android device matrix. No uninvestigated critical vulnerability; measured stable session metrics published alongside build.

### Phase 5 — Packaging and product release

| ID | Task | Acceptance |
|---|---|---|
| P5.1 | Signed driver and Windows installer/service packaging, firewall rules, uninstall and rollback | Install/uninstall on a fresh Windows PC with a documented rollback path |
| P5.2 | Background service only where justified; use user-session agent for UI/Media APIs, not an always-admin GUI | Appropriate Windows permissions and logon behavior |
| P5.3 | Driver update safety (version pin, staged rollout, prior-known-good) and paired client compatibility | Safe incompatible-version messaging and rollback |
| P5.4 | Stable Android packaging, Play policy review, limited analytics/diagnostics consent | No AccessibilityService/ADB dependency for Windows desktop use |
| P5.5 | One-page promise: accountless LAN, unlimited sessions, full panel resolution, clearly scoped advanced capabilities | No misleading price, compatibility or “true DPI everywhere” claims |

**Gate G5:** Fresh-machine installation, recovery and repeated end-to-end acceptance passes. Pilot ready; not a claim of universal hardware compatibility.

## D. Technical risks and explicit decisions

| Risk / decision | Impact | Required strategy |
|---|---|---|
| Console-session IddCx cannot reliably force monitor scale through supported API | **Critical for exact-DPI marketing** | Verify EDID recommendation/read-back; experimental opt-in undocumented method; assisted manual scaling fallback; never claim enforced 350% without evidence |
| Current A driver shared contract reconstructed from partial scrape | **Critical for build/security** | One verified ABI on real Windows and ACL audit before installation |
| Physical-display changes are only detected, not restored | High user harm risk | Journal and safe recovery UX; compare topology and avoid blind restoration |
| `GetDpiForMonitor` in per-monitor-aware execution context | Wrong result risk | Use window-probe/appropriate DPI awareness context; validate physical ruler |
| Driver signing and admin rights | Adoption friction | Signed install and rollback, explicit admin requirement; no “portable no-admin driver” claim |
| A has desktop-canvas mode, not true isolated window clone | UX feature gap | Keep canvas MVP; port/window mode later with documented limits |
| FFmpeg BGRA→YUV420 pipeline | Soft color/text and copying overhead | Test text fidelity, tune bitrates, later GPU-native encoder; honor codec licenses |
| Option B binary-v3 Windows protocol incompatible with Android JSON protocol | High integration risk | Reuse algorithms only, not Option B host/protocol |
| Original C.zip includes secret/device credentials and open-access settings | Security concern | NEVER ship/import these credentials/configurations; rotate and create new identities |
| Phone-control (PC→Android apps) without privileged API | Potentially infeasible | Separate R&D; not part of core roadmap or initial marketing |

## E. Verification matrix and measurable release evidence

**Device matrix:** At minimum Windows 11 24H2/25H2 with two distinct GPU vendors; one 1080×2400-class Android device, one 1440×3120-class device, and a foldable or tablet when available. Add multi-monitor, primary-monitor-off, locked session and laptop sleep cases.

**Each acceptance run must emit:** build hash, OS/build, driver version, monitor EDID hash, requested/actual mode/refresh, requested/actual effective DPI, DPI-awareness mode, measured 100-DIP ruler size, source/decoded frame sizes, codec/chroma, cryptographic channel status, stream drops, teardown result, and physical-monitor before/after comparison.

**Quality targets (proposed objectives, not claims):** encrypted authenticated session; no unrequested physical-display changes; no silent up/downscale; zero persistent phantom monitors after clean disconnect; 60 FPS capability on supported hardware; stable 30-minute session. Latency, text readability and thermal targets will be set from the first real baseline measurements rather than invented benchmarks.

**Definition of done per feature:** source merged, tests automated, logs understandable, negative path tested, Android/Windows interop verified when applicable, user visible status accurate, rollback possible for host/driver changes. “Has a class/function” alone is **not done**.

## F. Implementation order and branch policy

**Sequence:** P0 → P1/G1 → P2/G2 → P3/G3 → P4/G4 → P5/G5. P2 protocol fixtures and Android build can run in parallel with P1 Windows driver work; user-facing new features must not obstruct G1.

- `main`: tested, known-good integration only.
- `feature/driver-abi`, `feature/density-proof`, `feature/android-interop`, `feature/desklink-ux`, `feature/perf`, `feature/installer`.
- `experiment/windows-scale-override`: unsupported Windows -3/-4 path isolated; default **off**, can be removed without touching session protocol.
- `reference/option-b`: kept as read-only implementation reference; no duplicate runtime.

**First deliverable to implement next:** `Desklink Windows Proof v0.1` consisting of a corrected driver/build configuration, `driver-status`, hardened `density-probe` + read-back/100-DIP ruler, non-destructive physical-display snapshot, test/install/uninstall scripts, machine-readable evidence and one unambiguous Windows test guide. **The proof must run without Android or networking.** It should emit PASS / DEGRADED / FAIL, never a fake success on missing hardware or driver.

## G. Iteration decisions to settle

These defaults allow engineering to start; they are not blocking questions:

1. **Primary promise:** “Phone-native Windows workspace when the host confirms DPI; otherwise explicitly degraded” — recommended, rather than a guarantee of automatic 350% on every Windows build.
2. **Release boundaries:** Windows 11 first, Android 13+ first, LAN-only MVP — recommended.
3. **Modes:** virtual desktop canvas MVP; Quick View/window workflow next; genuine extended display after that. Remote control of Android remains a research feature.
4. **Product priorities:** reliability/security and 1:1 readable text outrank maximum FPS, codecs and multi-device support.
5. **Windows test environment:** Disposable secondary test machine or VM with tested graphics/IddCx support and ability to recover from driver faults; never first install an unverified display driver on the only production PC.

### Source anchors

- **Option A:** `windows/agent/FocusWindow.Agent/Program.cs` (`density-probe`, `driver-status`); `.../Display/DisplayConfigApi.cs` (undocumented DPI packet); `.../Display/ProbeWindow.cs` (per-monitor ruler); `windows/agent/FocusWindow.Core/Display/DensityNegotiator.cs`; `.../Session/SessionHost.cs`; `.../Media/Encoder.cs`; `windows/driver/FocusIdd/Driver.cpp`; `windows/shared/FocusShared.h`; Android `LanTransport.kt`, `VideoDecoder.kt`, `Protocol.kt`.
- **Option B:** `windows/agent/CaptureEncoder.cpp`, `SessionHost.cpp`, `DpiScale.cpp`, `windows/INTEGRATION.md` (protocol incompatibility).
- **Original Desklink:** `C.zip/host.py`, `devices.py`, `viewer/viewer.js`, and `android.zip/.../dev/desklink/c/` (window management, keyboard UX, files, approvals).
- **Microsoft boundaries:** [IddCxAdapterDisplayConfigUpdate](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/nf-iddcx-iddcxadapterdisplayconfigupdate) (remote session driver); [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor) (not for per-monitor-aware caller); [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow); [IddSample](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/indirect-display-driver-sample/).


## v0.3 handoff addendum — P2 field integration (19 October 2026)

- **Application source release candidate:** v0.3.0-dev, not a finished application or verified installer. Normalized .NET and Android application version labels; original protocol and driver identifiers unchanged.
- **Authorized root:** `D:\Coding\DeskFuse\v0.3`. Do not extract over `D:\Coding\DeskFuse\DeskFuse_Phase_0_1_BuildFix_v0.2.1` or make changes to the verified driver installation.
- **Pi task:** Reproduce the Windows .NET Release build, Windows xUnit tests, Android debug APK and JVM tests; connect authorized ADB test handset via real authenticated LAN; prove native H.264 video and input; exercise disconnect/reconnection and physical-display safety. Fix non-protected P2 defects with regression tests.
- **Acceptance:** The 48 tests A01–E10 and S01–S06 supplementary checks in `DeskFuse_v0.3_PI_HANDOVER.md` are formally received under `DeskFuse_v0.3_RETURN_HANDSHAKE.md`. Full P2 G2 requires **48/48 evidenced PASS**, otherwise accept only justified partial fixes and keep G2 OPEN. Do not claim missing hardware tests passed.
- **Explicit no-touch:** `windows/driver/**`, `windows/shared/**`, historic driver/safety scripts, installed FocusIdd, driver version/INF, ABI v2, protocol-v3 wire contract, Windows physical-display modes/scales, system DPI or unsupported DPI writes.
- **Deferred but retained requirements:** True 350%/336-DPI PMv2 gate; P3 window focus, clipboard, file manager, native cross-device actions, PC audio, optional automatic camera backup (local save FIRST, then secure reliable PC transfer); P4 performance/security; P5 installer/signing.
- **Known evidence boundary:** Earlier native EDID/protocol/media logic tests passed in a non-Windows environment, but newly added C# session lifecycle tests, Windows Release compilation, Android APK build and actual phone session are **not verified by this repackaging**.

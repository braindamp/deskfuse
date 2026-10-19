# DeskFuse v0.3 — Pi CLI hardware integration assignment

**Document:** DF-PI-P2-HANDOVER-v0.3  
**Companion acceptance contract:** `DeskFuse_v0.3_RETURN_HANDSHAKE.md`  
**Windows working directory:** `D:\Coding\DeskFuse\v0.3`  
**Starting source:** `DeskFuse_v0.3_FULL_SOURCE.zip`  
**Target:** Windows host + one Android phone connected by authorized USB debugging / Android Debug Bridge (ADB), with actual DeskFuse communications over an authenticated LAN connection  
**Role:** Pi, the Windows CLI coding agent  
**Primary and overriding objective:** **Return source and reproducible evidence meeting the handshake's 48-test acceptance criteria.**

## 1. Mission and success definition

Complete the **P0 build gates and P2 real Windows ↔ Android integration gate** from the consolidated plan, making necessary narrow bug fixes in the authorized application code. The upstream engineer does not have access to the PC or phone. Your main product is a reproducible *code-and-evidence handoff*, not a persuasive progress summary.

Required real path: **existing installed FocusIdd → Windows C# frame acquisition → H.264 encoder → protocol-v3 authenticated encryption → LAN → Kotlin client → MediaCodec → visible content**, plus authenticated Android input in the other direction and safe teardown. The real session must not interfere with either physical Windows monitor.

Work autonomously inside your permitted scope. Identify defects, fix them, add regression tests, run tests again and document before/after results. **Never mark a test passed based only on source inspection, intended behavior, or another agent's assertion.** If blocked by equipment, owner approval or protected code, record the blocker and return the verified partial result.

### Firm product contract

- DeskFuse is a **mobile-first Windows productivity application**, not just a screen mirror. The broad P3–P5 feature roadmap is preserved but *not Pi's assignment*.
- Native display **pixels** and actual **Windows DPI** are distinct requirements. For the reference phone 1440×3120 / DPR 3.5, target Windows 350% / 336 DPI and a measured 350×350 px square for 100 DIPs, but the currently installed driver has **not** demonstrated that DPI. Do not conceal the mismatch or alter the driver to fix it.
- Phone-native streaming means **source and decoded spatial dimensions agree exactly** for the negotiated full-resolution mode; no silent rescaling.
- Real LAN encryption and explicit pairing are mandatory. ADB provides installation, debugging and automation only, not an acceptance substitute for authenticated Wi-Fi/LAN transport.
- Protocol version **3** and driver/shared-memory ABI version **2** are frozen engineering interfaces. They are not application release version numbers.

## 2. Inputs, folder layout and authority

Place and extract the latest source ZIP **directly** into `D:\Coding\DeskFuse\v0.3` (so `windows\`, `android\`, `tools\`, `plans\` are direct children of that folder). Do not create `v0.3\v0.3` accidentally. The ZIP is source only, not an installed release.

Read these files in order:

1. `README_V0.3.md` from the ZIP root, and `DeskFuse_v0.3_Consolidated_Build_Plan.md`.
2. `DeskFuse_v0.3_RETURN_HANDSHAKE.md` — **binding return requirements**.
3. `plans\DeskFuse_Consolidated_Build_Plan_v0.3.md` within the extracted tree.
4. Relevant `android\README.md`, `windows\driver\README.md`, actual C#/.NET and Gradle projects, and tests.

**Canonical source:** `DeskFuse_v0.3_FULL_SOURCE.zip`. **Historical diff only:** `DeskFuse_v0.3_P2_PATCH.zip`. Never use the patch as an alternative source tree or apply it on top of an already complete v0.3 source directory.

**Important version distinction:** `v0.3.0-dev` is the *application candidate label*. It does not attest hardware success. The formerly verified FocusIdd **v0.2.29** identity and its guard scripts deliberately retain their historical names/expectations and must **not** be rebadged to v0.3. The driver INF's version field is also not being reissued.

Prior verified driver baseline at `D:\Coding\DeskFuse\DeskFuse_Phase_0_1_BuildFix_v0.2.1`, identity `DeskFuse-HoldUntilOK-0.2.29`, is **read-only and separate**. No work in that directory.

### Local source management

1. Record `Get-FileHash` SHA-256 for the incoming ZIP before extracting.
2. Make an immutable archive or Git baseline commit of the *extracted v0.3 source* before editing.
3. For protected paths, record baseline hashes in `evidence\protected-baseline.sha256`.
4. Commit only reviewed application/test/build changes; keep logs and personal-device evidence outside source until sanitized.
5. Use a separate `evidence\` directory. Do not commit developer secrets, phone identifiers, screenshots of private content, build intermediates or test credentials.

## 3. ABSOLUTE no-touch restrictions

**Do not edit, rebuild, replace, install, uninstall, reconfigure, reset or delete the installed FocusIdd driver or protected source.** The no-touch files include:

- `windows\driver\**`, `windows\shared\**`, driver INF/cat, shared IOCTL numbers, published ABI constants, EDID and monitor modes.
- `tools\driver-install-testpc.ps1`, `tools\rollback-focusidd.ps1`, `tools\safe-driver-proof.ps1`, `tools\safety-boot-recovery.ps1`, `tools\safety-watchdog.ps1`, `tools\run-proof.ps1`, historical `tools\build-host-clean-verify.ps1`, and any related guarded safety/expiry/confirmation script.
- Installed driver service configuration, PnP device state except normal use through the existing host/session flow; Windows test-signing policy; driver certificates; physical-display topology, global DPI and per-monitor undocumented DPI setter.
- Protocol-v3 *on-the-wire* packet types, encryption algorithms, key derivation semantics, framing, sequence/replay model and negotiated identity fields; driver ABI v2.
- Normal Windows-account permissions, pairing approval requirements and security defaults.

**Forbidden operations:** `pnputil`/`devcon` to replace/remove the driver, `sc delete` of display services, changing test-signing status, Windows display/graphics driver reinstall, arbitrary `SetDisplayConfig` rearrangement of physical monitors, `ClipCursor` restricting the local PC operator, disabling encryption, no-auth server, automatic insecure pairing (`--auto-approve`) for acceptance, saving pairing keys in plaintext, ADB-root/bootloader unlock/root, or arbitrary Windows SYSTEM/admin privilege escalation.

The *existing* application may attach and remove a virtual monitor using its normal documented host API **only after** a read-only physical-monitor inventory and a successful safety/guardian readiness check. If the installed driver is missing or mismatched, **stop the live-driver test and report a blocker**; do not install it.

**Do not migrate the interface to protocol 4, change ABI v2, or introduce a different architecture.** If a P2 defect truly requires a protected modification, produce a change proposal with evidence and continue independent tasks. Do not make that change.

## 4. Permitted code fixes

Within the isolated v0.3 source tree, you may modify:

- `windows\agent\FocusWindow.Agent\` non-driver host/UI/diagnostic/input logic.
- `windows\agent\FocusWindow.Core\` protocol-v3 implementation (frozen wire semantics), session ownership, encoder, media, pairing, transport, teardown, diagnostic state.
- `windows\agent\FocusWindow.Tests\` plus isolated protocol conformance tests.
- `android\app\` pairing, transport, decoding/rendering, input, lifecycle and test code.
- Build and test configuration, **excluding protected driver/safety scripts**. Official toolchain installation is allowed; do not install/reconfigure system display drivers.

Allowed bugs include: mismatched C#↔Kotlin serialization, C# tests not building, Android build setup, missing Gradle wrapper or documented Gradle invocation, input coordinates, MediaCodec failure, frame limits, session cancellation races, reliable keyframe recovery and truthful telemetry. Fix the cause, not just the failing assertion. Prefer small, test-backed diffs.

**Not assigned:** DPI experimentation, EDID edits, gutter/cropping driver experiments, redesign of video architecture, P3 file management/context menus, automatic camera backup, audio, multidevice routing, WAN/relay/cloud, 90/120 FPS features, P4 benchmark optimizations and P5 installer/release work. Keep their plan entries intact.

## 5. Working procedure (execute in this order)

### Stage 0 — Record environment and prove safety

Capture: Windows edition/build, PC GPU and codec capability, .NET SDK, JDK, Gradle, Android SDK, MSVC/WDK availability, Android model/API/firmware, ADB version and authorized device presence, USB debug authorization, Wi-Fi and PC LAN IPs (redacted for return), original display identities/modes/scales/primary/orientation/bounds, and currently installed driver identity.

Suggested non-destructive commands:

```powershell
Set-Location 'D:\Coding\DeskFuse\v0.3'
dotnet --info
adb version
adb devices -l
adb shell getprop ro.build.version.sdk
adb shell wm size
adb shell wm density
adb shell dumpsys display
Get-FileHash '.\DeskFuse_v0.3_FULL_SOURCE.zip' -Algorithm SHA256  # only if ZIP is stored here
```

ADB commands are examples; use the actual SDK location or run via the resolved executable. **Do not record an unredacted Android serial or credential in deliverables.** Use established read-only display-config tooling; no display mutation to collect evidence.

### Stage 1 — P0 builds and repeatable tests

The archive has `.csproj` and `android\settings.gradle.kts`, but **the legacy snapshot may not contain a Gradle wrapper**. Inspect first. If missing, generate/restore an official pinned Gradle wrapper in the *authorized Android source* or use an installed compatible Gradle, documenting the version and reproducibility. Do not claim a wrapper exists before checking.

Suggested commands (actual paths and exit codes must be recorded):

```powershell
Set-Location 'D:\Coding\DeskFuse\v0.3'
dotnet build .\windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj -c Release
dotnet test  .\windows\agent\FocusWindow.Tests\FocusWindow.Tests.csproj -c Release
Set-Location .\android
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:testDebugUnitTest
```

Run the existing native source/EDID and Kotlin protocol/media checks using supported Windows tools where feasible. Repair existing test breakage without modifying protected driver code. Compile and execute the new C# session lifecycle tests (not yet executed in earlier development).

### Stage 2 — Cross-language protocol interoperability

Generate **one shared test set** and run it through actual C# and Kotlin serializers/cryptography implementations, both ways. Cover: authenticated pairing, identity binding, key confirmation, message frames/limits, sequence numbers, replay, tampered ciphertext, malformed payloads, reconnect keys and rejection. A Python or JavaScript fixture alone is not proof of the real C# ↔ Kotlin implementation. No protocol-3 schema changes.

### Stage 3 — Real pairing and video

Install the debug Android app via ADB (Android platform installer only). Use the normal Windows signed-in user-session host, visible QR scan and Windows approval; do **not** use `--auto-approve`. Establish transport over the actual trusted LAN. Debugging through USB is fine but **ADB reverse/forward is not the accepted network path**.

Observe the full path from virtual Windows display into MediaCodec and onto the phone. Prove a generated moving test pattern/window on the virtual monitor is visible on Android and matches frame dimensions. Do not capture personal/private content. Report actual encoder (e.g., NVENC only if truly used), decoder, dimensions, FPS, latency methodology, frame losses and security status. Do not label Windows DPI verified unless measured appropriately.

### Stage 4 — Real input and rotation

Test tap, double tap, right-click, drag, scroll, trackpad mode, ASCII/Unicode text, IME and modifiers, hardware-style key event, screen edges and mapping after orientation change. Use a simple synthetic Windows input target/text document rather than altering user files or interacting with physical-monitor windows. Record the target application result for each case, not merely send success.

### Stage 5 — Disconnect, reconnect, teardown and repeat

Test normal Goodbye, USB debug disconnection while Wi-Fi session continues, Wi-Fi drop/return (restore connectivity), Android app force stop, test host failure **without killing the driver/guardian**, phone screen off/on, and 10 successive connection cycles. Observe normal cleanup and after-snapshots. **Stop any repeated fault test immediately if it disturbs the local PC or a phantom monitor persists.** Supervised host sleep/lock is a *conditional test*, not a requirement for unattended execution.

### Stage 6 — Compile complete return package

Review source diff, rerun all affected tests, match each of the **48 IDs** to recorded evidence, produce complete archives and SHA-256 manifest. No user sign-off is required for normal source bug fixes. Do not block progress on optional features or protected driver work.

## 6. Forty-eight mandatory acceptance tests

Report **PASS / FAIL / BLOCKED / NOT_RUN** for *every* ID; use the handshake's evidence schema. **48/48 PASS is the full G2 target**, not a claim that every test has already been run.

### A — Baseline/build/ADB (8)

| ID | Required evidence / passing condition |
|---|---|
| A01 | Input ZIP SHA-256, working-tree baseline and protected file hashes captured before edits |
| A02 | Actual Windows/.NET/Android/JDK/Gradle/ADB environment and authorized test phone inventoried |
| A03 | Windows Agent/Core Release build exits successfully with no unexplained errors |
| A04 | Windows test suite passes, including session lifetime and encrypted frame bound tests |
| A05 | Android debug APK builds from delivered source; hash recorded |
| A06 | Android unit-test suite passes; explicit result count and exit status |
| A07 | Actual C#↔Kotlin protocol fixture interoperability passes in both directions |
| A08 | APK installs and starts on actual authorized USB-debug phone without fatal startup error |

### B — Pairing/security (10)

| ID | Passing condition |
|---|---|
| B01 | Unpaired phone denied privileged session |
| B02 | QR normal pairing, correct host fingerprint and visible Windows authorization bind correct phone |
| B03 | Explicit denial leaves phone unauthorized |
| B04 | Wrong host identity/public key rejected; no insecure fallback |
| B05 | Duplicate/replayed authenticated record rejected |
| B06 | Tampered ciphertext/authentication tag rejected |
| B07 | Android keystore failure never persists plaintext secrets |
| B08 | Valid remembered reconnect retains identity and granted scope; no silent widening |
| B09 | Oversized encrypted packet/media frame blocked before unsafe queue/allocation |
| B10 | Revocation prevents subsequent authentication with removed phone credential |

### C — Native video (10)

| ID | Passing condition |
|---|---|
| C01 | Normal host connects to existing FocusIdd virtual display without any driver reinstall |
| C02 | Real phone pixels/DPR/orientation/refresh reported and negotiated accurately |
| C03 | Active virtual-monitor and BGRA frame dimensions match negotiated source mode |
| C04 | Real moving Windows content passes through encrypted LAN/H.264/MediaCodec and is visibly presented |
| C05 | Decoder/display maintain native 1:1 spatial sizes; no hidden downscale/stretch |
| C06 | 10-minute sustained live stream measured for FPS, drops, latency, interruptions |
| C07 | Supported decoder fallback works or hardware capability exception is accurately recorded; actual decoder named |
| C08 | Oversized/broken frame produces recovered IDR/keyframe synchronization rather than permanent black display |
| C09 | Supported bitrate/quality adjustment works or is explicitly rejected without terminal stream loss |
| C10 | Orientation/mode change is correctly reconfigured, or unsupported transition rejected cleanly |

### D — Touch/keyboard/control (10)

| ID | Passing condition |
|---|---|
| D01 | Android single tap activates specific synthetic Windows target |
| D02 | Double tap/primary double-click performs intended action |
| D03 | Long press/explicit secondary click produces Windows context click |
| D04 | Drag across a visible test target works; button/touch release guaranteed |
| D05 | Scroll reaches correct window, direction and magnitude sane |
| D06 | Trackpad-relative pointer mode works without absolute-coordinate jump |
| D07 | ASCII + Unicode + normal IME composition arrives correctly in Windows test editor |
| D08 | Ctrl/Shift/Alt combinations accepted by intended Windows app |
| D09 | Android hardware-style key events handled; distinguish synthesized from real external keyboard |
| D10 | Edge and orientation coordinates stay on virtual display and do not affect physical monitor app |

### E — Recovery/safety/diagnostics (10)

| ID | Passing condition |
|---|---|
| E01 | Authenticated Goodbye terminates without deadlock and closes worker/transport |
| E02 | ADB debugging connection ending does not drop independent Wi-Fi/LAN session |
| E03 | Controlled phone Wi-Fi loss yields bounded failure and cleanup, then restores Wi-Fi |
| E04 | New reconnect uses authenticated fresh session state; no stale record replay |
| E05 | Force-stop Android app triggers Windows end-of-session cleanup |
| E06 | Kill **only** test Windows user agent and verify existing guarded cleanup, without touching driver/guardian |
| E07 | Android screen-off/resume yields working session or clear recoverable renegotiation |
| E08 | Ten successful connect → stream/input → disconnect cycles leave no leaked monitors/sessions |
| E09 | Diagnostics accurately identify pixels, actual codec/encoder, actual vs requested DPI, FPS/latency and failure cause |
| E10 | Final physical-monitor snapshot byte/field comparison matches baseline; virtual monitor properly removed |

**Supplementary, tracked separately:** S01 alternate 1080×2400 phone; S02 reference 1440×3120/DPR3.5 ruler; S03 real external keyboard; S04 actual PC HW encoder observation; S05 physical USB unplug; S06 supervised Windows sleep/lock. S01–S06 cannot compensate for a missing required test; mark unavailable where hardware absent.

## 7. Evidence format and grading

Every test's record must contain: ID, UTC time, commit/tree hash, PC/Android build identifiers, sanitized device identifier, setup and exact commands/user actions, expected outcome, observed outcome, status, artifact path, and any bug/commit that changed its outcome.

- Source inspection is **not** equivalent to a compiled test.
- Independent Kotlin crypto vectors are **not** equivalent to C#↔Kotlin interoperability.
- An FFmpeg sample is **not** a real Windows virtual-display-to-phone video test.
- A network socket is **not** proof of authenticated protocol 3.
- A debug APK launch is **not** proof of real frame decoding.
- A 1440×3120 frame is **not** proof of 350% / 336-DPI Windows rendering.
- A synthetic tap packet is **not** proof that a Windows application reacted correctly.
- A screen capture must show only synthetic DeskFuse test content; no private PC data.

Maintain sanitized full logs with build/test exit codes, representative sample snapshots, codec reports and bounded timing. Never include private keys, QR payload secrets, ADB full serial, arbitrary user files, network credentials or signing keys. Account for **all 48** outcomes; `PASS` requires evidence. A dangerous or physically blocked test must be recorded `BLOCKED`, not forced.

## 8. Return package (mandatory)

Return three compact archives and a top-level summary:

- `DeskFuse_v0.3_PI_FULL_SOURCE_RETURN.zip` — **complete** changed source with build and test configuration, preserving original layout.
- `DeskFuse_v0.3_PI_SOURCE_PATCH.zip` — clean Git diff, changed-file list, and relevant changed/new files, relative to **this** v0.3 source ZIP.
- `DeskFuse_v0.3_PI_EVIDENCE.zip` — `RETURN_SUMMARY.md`, `TEST_MATRIX.csv`, `TEST_REPORT.md`, `ENVIRONMENT.md`, `PROTOCOL_INTEROP_REPORT.md`, `DISPLAY_SAFETY_REPORT.md`, `PROTECTED_FILE_AUDIT.txt`, `SESSION_METRICS.csv`, `CHANGELOG_P2.md`, `UNRESOLVED_BLOCKERS.md`, sanitized `LOGS/`, `MANIFEST_SHA256.txt`.

The full-source return MUST be enough to build without applying patches. Keep original source file paths. Do not bundle driver DLLs, personal credentials, build caches, `.vs`, Gradle caches or private screenshots. Debug APK optional and separately hashed.

Use a documented SHA-256 checksum for each delivered file and include both baseline and final Git/source hashes. If one archive is needed, it must unambiguously contain the same three sections.

## 9. Stop conditions and ownership transfer

**Completion target:** 48/48 required PASS plus honest supplementary statuses, no critical security defect, no protected modification, real phone session, native video/input, repeatable disconnect and display isolation. Meet the **handshake document** before declaring P2 gate success.

**Stop just the unsafe scenario** if a test damages monitor state, driver/guardian seems unstable, or protected changes appear necessary. Preserve reproducible evidence and continue independent safe P2 checks. If overall work is blocked, submit partial code and all observations; the receiving engineer may accept safe improvements while keeping P2 G2 open.

**Final instruction:** Fix what real testing reveals, not what an LLM predicts. Ship evidence and a minimally changed source tree that the upstream GPT-6 engineer can audit and continue. Never claim full DeskFuse release or true 350% DPI success from a P2 handshake.

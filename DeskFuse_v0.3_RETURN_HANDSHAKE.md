# DeskFuse v0.3 — Pi return handshake and receiving-engineer acceptance contract

**Handshake:** `DF-P2-RETURN-HANDSHAKE-v0.3`  
**Sender:** Pi CLI coding agent on Windows with ADB-connected Android handset  
**Receiver:** upstream GPT-6 DeskFuse development session  
**Authorized base:** `DeskFuse_v0.3_FULL_SOURCE.zip`  
**Canonical working root:** `D:\Coding\DeskFuse\v0.3`  
**48 mandatory test IDs:** A01–A08, B01–B10, C01–C10, D01–D10, E01–E10  
**Purpose:** Formally determine whether Pi's code changes are mergeable and whether P2's tested-device hardware gate has been achieved.

## 1. Contract: acceptance depends on evidence, not a successful-looking demonstration

I will audit the actual returned source and artifacts **before merging**. Pi must demonstrate: source provenance; protected-code integrity; successful builds and tests; C#↔Kotlin protocol compatibility; real approved Wi-Fi/LAN phone pairing; actual FocusIdd frames decoded on the handset at native pixels; real input actions; safe disconnect and reconnect; unchanged physical displays; and honest DPI reporting. A confident text report alone is no evidence.

**Application version v0.3 does NOT change or authorize modifying** FocusIdd's historically verified v0.2.29 driver, ABI v2, wire protocol v3 or guarded safety scripts. P2 may succeed while Windows true 350% rendering remains a separately tracked, non-negotiable project requirement.

## 2. Required submitted artifacts

1. `DeskFuse_v0.3_PI_FULL_SOURCE_RETURN.zip`: buildable, complete Windows/Android source tree, retaining the original relative directory structure. No patching required to compile it.
2. `DeskFuse_v0.3_PI_SOURCE_PATCH.zip`: `changes.patch` or equivalent, changed/added/deleted file list, rationale and test coverage, *relative to the immutable `DeskFuse_v0.3_FULL_SOURCE.zip` input baseline*.
3. `DeskFuse_v0.3_PI_EVIDENCE.zip`: all required documents and sanitized logs below.
4. `RETURN_SUMMARY.md`: accessible from evidence archive and briefly included in Pi's final message.

Evidence archive mandatory members:

| Path | Required content |
|---|---|
| `RETURN_SUMMARY.md` | Completed metadata block; main observed functionality and remaining blockers |
| `TEST_MATRIX.csv` | Exactly 48 unique test ID rows; status and artifact path for each |
| `TEST_REPORT.md` | Commands/actions, expected/actual, results, first-failure and fixes |
| `ENVIRONMENT.md` | Windows version, toolchains, display identities, installed FocusIdd identity, handset and LAN method |
| `CHANGELOG_P2.md` | Every changed module, purpose, risk and regression test |
| `PROTECTED_FILE_AUDIT.txt` | Baseline/return SHA-256 match for protected driver/shared/safety files |
| `DISPLAY_SAFETY_REPORT.md` | Before and after all *physical* monitor properties, virtual monitor lifecycle |
| `PROTOCOL_INTEROP_REPORT.md` | Both C# and Kotlin implementations actually exercised against identical protocol-v3 fixtures |
| `SESSION_METRICS.csv` | 10-minute run, native pixel sizes, FPS/drops, measured latency, codec/device, session epochs |
| `UNRESOLVED_BLOCKERS.md` | All FAIL/BLOCKED/NOT_RUN items with cause and safe next action |
| `LOGS/` | Sanitized build/tests, Windows host, ADB/logcat and test instrumentation |
| `MANIFEST_SHA256.txt` | Artifact integrity, including logs and important screenshots/test recordings |

No private QR secret, pairing key, full Android device serial, passwords, installer signing key, private desktop screenshot, personal file, cache/binary output from protected driver, or undisclosed executable installation instructions. Reject archive path traversal and unexplained installer scripts. Source must be independent of Pi's local machine.

## 3. Exact mandatory return metadata

Place this block at the beginning of `RETURN_SUMMARY.md`, replacing all bracketed markers with measured values. `NO`, `YES`, `UNKNOWN` and `NOT_AVAILABLE` are literal states; no optimistic defaults.

```text
HANDSHAKE_ID: DF-P2-RETURN-HANDSHAKE-v0.3
SUBMISSION_STATUS: COMPLETE | PARTIAL | BLOCKED
WORK_DIRECTORY: D:\Coding\DeskFuse\v0.3
BASELINE_SOURCE_ZIP: DeskFuse_v0.3_FULL_SOURCE.zip
BASELINE_SOURCE_SHA256: <actual hex>
RETURN_SOURCE_SHA256: <actual hex>
RETURN_PATCH_SHA256: <actual hex>
RETURN_EVIDENCE_SHA256: <actual hex>
BASELINE_SOURCE_TREE_SHA256: <actual or NOT_AVAILABLE>
RETURN_SOURCE_TREE_SHA256: <actual>
BASELINE_GIT_COMMIT: <hash or NOT_AVAILABLE>
RETURN_GIT_COMMIT: <hash or NOT_AVAILABLE>
WINDOWS_BUILD: <observed>
DOTNET_SDK: <observed>
ANDROID_API_LEVEL: <observed>
ANDROID_MODEL: <observed>
ADB_DEVICE_ID_REDACTED: <observed>
FOCUSIDD_INSTALLED_IDENTITY: <observed or UNKNOWN>
DRIVER_SOURCE_CHANGED: NO | YES
DRIVER_ABI_CHANGED: NO | YES
PROTECTED_SAFETY_FILES_CHANGED: NO | YES
PROTOCOL_3_ON_WIRE_CHANGED: NO | YES
SECURITY_DOWNGRADE: NO | YES
USB_ADB_TESTED: YES | NO
REAL_AUTHENTICATED_LAN_TESTED: YES | NO
PHYSICAL_DISPLAY_CHANGE_DETECTED: NO | YES | UNKNOWN
REQUIRED_TOTAL: 48
REQUIRED_PASS: <0..48>
REQUIRED_FAIL: <0..48>
REQUIRED_BLOCKED: <0..48>
REQUIRED_NOT_RUN: <0..48>
SUPPLEMENTARY_TESTS: <S01..S06 summary>
CRITICAL_OPEN_ISSUES: <count>
```

**The four required outcome counts must sum to 48.** The ZIP hash must be for the exact bytes submitted, not an earlier artifact. If a field is unknowable, disclose it; do not supply guessed values.

## 4. 48-case contract and mandatory evidence thresholds

The test definitions, expected results and recommended execution order are in `DeskFuse_v0.3_PI_HANDOVER.md` §6. Required accounting:

| Class | IDs | Count | Minimum evidence |
|---|---|---:|---|
| A: Baseline/build/ADB | A01–A08 | 8 | E2 build/unit tests, E3 for phone installation |
| B: Pairing/security | B01–B10 | 10 | E2 cryptographic negative cases, E3 real pairing/revocation |
| C: Video | C01–C10 | 10 | E3 real native host→encrypted LAN→MediaCodec→screen pipeline |
| D: Input | D01–D10 | 10 | E3 verified Windows app action, not merely packet send |
| E: Lifecycle/safety | E01–E10 | 10 | E3 failure/recovery and display-snapshot comparison |
| **TOTAL** | | **48** | **48 demonstrated passes for full hardware gate** |

Per-test record fields: `test_id, status, executed_utc, source_commit, device_id_redacted, host_build, android_build, setup, exact_command_or_action, expected, observed, evidence_paths, related_fix_commit`. CSV should contain an `evidence_paths` field. Each hardware PASS must link at least one contemporaneous real-device log/measurement/observation artifact. A dry-run, static assertion or synthetic fixture does not fulfill a hardware requirement.

**Conditional supplementary tests S01–S06**: alternate phone resolution; reference DPR3.5 physical square; real external Android keyboard; observed NVENC/QSV/AMF hardware encoding; physical USB unplug; *explicitly approved, supervised* Windows sleep/lock. Report them, but do not count them among 48 or require dangerous unapproved experiments to satisfy G2.

### Evidence quality taxonomy

- **E0:** assertion by agent only — worthless for acceptance.
- **E1:** manual source review and reasoned claim — useful for review, not runtime proof.
- **E2:** reproducible executed compiler/unit/integration test — software gate evidence.
- **E3:** real PC+handset, actual LAN, logs + observed application/device result — hardware gate evidence.

Unverifiable video screenshots, pre-recorded H.264 samples, Android emulator success, ADB port-forwarded sessions and unpaired TCP connections do **not** satisfy C04, C05 or E3 connection gates.

## 5. Receiving engineer review gates (must proceed in order)

### H0 — Source provenance & reconstruction

- Verify ZIP integrity and SHA-256s; check no unsafe paths or bundled credentials.
- Verify the input baseline identity against our preserved `DeskFuse_v0.3_FULL_SOURCE.zip`.
- Reconstruct returned candidate from baseline plus patch and compare its resulting source-tree hashes with full returned source.
- Account for each addition/change/deletion. Investigate any hidden generated artifact or unexplained binary.

**Fail H0** if baseline unknown, archives inconsistent or source cannot be reconstructed.

### H1 — Protected/no-touch audit

Hash-compare to original v0.3 archive:

- `windows/driver/**`, `windows/shared/**`.
- Driver install/rollback, safety watcher/guardian/expiry, physical-monitor recovery and old verified host gate scripts.
- Driver ABI/version/layout, EDID modes/INF/cat and driver binaries.
- Protocol-v3 packet shapes and identity/security contract.

Inspect diffs for introduction of unapproved `DisplayConfig` changes, global/system DPI setter, aggressive cursor clipping, silent frame downscaling, insecure fallback, new elevated service assumptions or `--auto-approve` reliance. The host can call the existing normal virtual-monitor creation/teardown methods; it cannot change the verified driver or its safety system.

**Fail H1** on unexplained protected changes, weakened security or potentially destructive physical display actions. A protected change *proposed but not made* is not a violation.

### H2 — Build reproducibility and regressions

Build both .NET projects and Android APK from returned source. Run Windows xUnit suite, Android JVM unit tests and protocol cross-language vectors. Run applicable existing Kotlin/native read-only test scripts. Confirm the reported **session lifecycle, frame boundary and decoder recovery tests** are present and actually executed. Record warnings/failures honestly. Inspect Gradle wrapper/toolchain pinning when missing in original source.

A binary run on Pi's PC without build logs and matching source is **not** reproducible evidence.

### H3 — Security/authorization interoperability

Verify the ten B-series outcomes, especially:

- Actual Windows-approved QR pairing binds host key and phone identity.
- Wrong key, denial, tampered ciphertext and replay fail closed.
- No persistent plaintext key on keystore error.
- Valid reconnect never broadens filesystem/control scope.
- Revoked device cannot connect.
- Full C#↔Kotlin byte-framing and crypto coverage, not just independent language tests.
- No acceptance run uses insecure auto-approval or unencrypted media.

**Any real authentication bypass or plaintext pairing-secret persistence is a critical issue and prohibits Decision A.**

### H4 — Real complete video path

Establish this evidenced chain:

```text
Installed known-good FocusIdd
  → Windows BGRA frame acquisition
  → real H.264 encoding
  → encrypted/authenticated protocol-v3 media record
  → real LAN socket
  → Android authenticated Kotlin receiver
  → actual Android MediaCodec decoder
  → visible live synthetic Windows content at native spatial dimensions
```

Cross-correlate host and Android per-frame/session identifiers and timestamps where available. Confirm native source and decoded dimensions. Note **encoder selection vs actual encoder used** and distinguish spatial pixel matching from DPI. No claim of zero-copy or 336 DPI without the corresponding measurement.

### H5 — Actual input and local-user isolation

For each D-case inspect the real Windows target application's state change, security scope, touch coordinate mapping and keyboard/IME behavior. Ensure signed-in physical PC user's screens and active applications are not hijacked. Stop accepting authenticated input immediately after teardown. Protocol packets alone are insufficient.

### H6 — Session failure, reconnect & teardown

Independently inspect cancellation, Goodbye completion, size-bound rejection, keyframe recovery, decoder reset, reconnect state, 10 repeated sessions, Android force stop and Windows user-agent termination cleanup. The normal host must dispose of authenticated transport before virtual-display removal to avoid late requests. Do not punish missing supervised sleep/lock tests, but do not claim they passed.

### H7 — Physical display invariants & DPI truthfulness

Diff before/after **each physical screen**'s identity, resolution, refresh, orientation, primary flag, position and scaling. The temporary virtual display is permitted to appear/disappear; it must not strand a phantom after normal teardown. Check diagnostic separation: requested vs active display pixels; Android DPR; host effective PMv2 DPI; source vs sink pixel sizes; `pixelMatch` vs `dpiMatch`; encoder, decoder and FPS.

**A functioning 1440×3120 session at 225% is a P2 pixel result, NOT a native-DPI success.** Do not penalize Pi for leaving P1 DPI unchanged within its constrained assignment.

### H8 — Evidence and return quality

- Exactly 48 unique ID rows with status sum 48; no silent omissions.
- All PASS rows include evidence; blocked rows include clear reproduction and reason.
- Required 10-minute run has actual times, FPS/drops, network, decoder and latency methodology.
- Source diff covers all modifications; tests protect regression fixes.
- No unrelated P3/P4/P5 feature sprawl.
- Logs sanitized; source and evidence package reasonably small.
- Return summary uses this handshake ID and source identity.

## 6. Specific past bug regressions to check

Review whether changed-code tests address these known problem categories (an existing proven test can satisfy the case):

1. Goodbye deadlocking on a completion task rather than canceling worker tasks.
2. Socket failure not propagating cancellation to media and input workers.
3. Late control/file messages racing virtual-monitor teardown.
4. Oversized H.264 access unit entering encryption/queue with invalid record length.
5. Encoder keyframe/IDR recovery after rejected oversized/damaged frame.
6. Vendor MediaCodec rejecting optional low-latency features.
7. Compatible decoder fallback with truthful hardware/software label.
8. Android keystore failure causing a persistent unencrypted credential.
9. Reconnect restoring too many capabilities or accepting stale sessions.
10. Native-pixel request silently replaced by smaller display mode.
11. Invalid replay and late encrypted records after new handshake.
12. Cleanup failure after terminating the normal Windows user agent.

These are engineering review subjects, not permission to modify driver/ABI or broaden scope.

## 7. Formal acceptance decision

**A — ACCEPTED: P2 G2 hardware gate for tested configuration**

Requires **H0–H8 all pass; 48/48 mandatory tests PASS with adequate E2/E3 evidence; no critical security flaw; no unauthorized protected changes; reproducible .NET and Android builds; secure real LAN pairing; real native-pixel video and input; reconnection and cleanup; physical displays unchanged**. This is *not* a whole-product release and does not certify unresolved 350% DPI, P3–P5 or other devices.

**B — ACCEPTED FOR CONTINUED ENGINEERING ONLY (G2 OPEN)**

Allowed if baseline, protected changes and source diffs are trustworthy, no critical security regression, and useful fixes have supporting tests; some mandatory hardware tests fail/are blocked. Merge or cherry-pick only defensible modifications. Preserve blockers and remaining hardware gate evidence. **Never call this G2 complete.**

**C — REJECTED / QUARANTINED**

Use if source provenance lost, patch/full archives inconsistent, protected driver/ABI/safety changed without authority, insecure authentication or plaintext secrets introduced, evidence fabricated, physical-monitor state endangered, or uncontrolled unrelated rewrite. Recover diagnostics safely, but do not merge returned candidate wholesale.

If any critical safety/security issue is found, reject regardless of claimed PASS count.

## 8. Blocker record schema

Every FAIL/BLOCKED/NOT_RUN must supply:

```text
ISSUE_ID:
SEVERITY: CRITICAL | HIGH | MEDIUM | LOW
TEST_ID:
MODULE:
ENVIRONMENT:
EXPECTED:
ACTUAL:
REPRODUCTION_STEPS:
EVIDENCE_LOG_PATH:
ROOT_CAUSE: KNOWN <details> | UNKNOWN
SOURCE_FILES:
SAFEST_NEXT_STEP:
PROTECTED_CHANGE_REQUIRED: YES | NO
OWNER_AUTHORIZATION_REQUIRED: YES | NO
```

Potential legitimate blockers include no installed matching driver, absent SDK, broken USB authorization, unavailable LAN, unhandled decoder profile, security permission requiring unavailable human input, or a fix requiring protected ABI/driver changes. A guessed root cause is not a verified root cause.

## 9. Continuation after receipt

Receiver sequence: extract to a **new review directory**, verify hashes/source reconstruction, inspect protected changes, run available unit/logic tests, review E3 hardware artifacts, make an explicit A/B/C decision, merge only accepted code, update consolidated P2 test status and unresolved-risk register. Do not install returned drivers or replace the physical PC configuration.

If **A**, move to the guarded true-DPR investigation and P3 window/clipboard/file/audio workflow following the v0.3 plan, then P4/P5. If **B**, close specific P2 blockers first and reuse already valid evidence. If **C**, restore the trustworthy v0.3 candidate and reconsider only isolated proven fixes.

## 10. Required final message from Pi

```text
DESKFUSE v0.3 P2 HANDSHAKE RETURN
HANDSHAKE: DF-P2-RETURN-HANDSHAKE-v0.3
BASELINE_SHA256:
RETURN_SOURCE_SHA256:
DRIVER_SHARED_SAFETY_UNCHANGED: YES/NO
PROTOCOL_V3_WIRE_UNCHANGED: YES/NO
SECURITY_DOWNGRADE: YES/NO
WINDOWS_RELEASE_BUILD: PASS/FAIL/BLOCKED
WINDOWS_TESTS: PASS/FAIL/BLOCKED
ANDROID_DEBUG_BUILD: PASS/FAIL/BLOCKED
ANDROID_UNIT_TESTS: PASS/FAIL/BLOCKED
CSHARP_KOTLIN_INTEROP: PASS/FAIL/BLOCKED
REAL_AUTHENTICATED_LAN: PASS/FAIL/BLOCKED
REAL_NATIVE_VIDEO: PASS/FAIL/BLOCKED
REAL_ANDROID_INPUT: PASS/FAIL/BLOCKED
RECOVERY_AND_CLEANUP: PASS/FAIL/BLOCKED
PHYSICAL_MONITORS_PRESERVED: PASS/FAIL/BLOCKED
REQUIRED_PASS: __/48
REQUIRED_FAIL:
REQUIRED_BLOCKED:
REQUIRED_NOT_RUN:
CRITICAL_OPEN_ISSUES:
CHANGED_MODULES:
FULL_SOURCE_RETURN_ZIP:
PATCH_ZIP:
EVIDENCE_ZIP:
```

**Binding rule:** Tests, code and measured evidence outrank confidence. Pi must not lower a gate to get a green checkbox. GPT-6 receives and audits the code rather than believing it. A partial honest return is acceptable for continued development; a false complete return is not.

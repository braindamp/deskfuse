# Architecture v0.6 implementation status

Authoritative design: `DeskFuse_Architecture_v0.6_and_Implementation_Programme.md`,
delivered 27 October 2026. Implementation baseline:
`experiment/vnext-audio-latency` at `2f181cf2da9c4efdde3013b115108ecac650d1ab`.

**Current: source implementation continues under the Ultra High handoff.**
Signing policy is settled. Protected preparation APIs and coordinated prepared-run
scripts are implemented and tested offline. Installed trusted-installer/service
binding, actual SYSTEM role-host qualification, signed-package admission and
physical gates remain open. Earlier entries are chronological evidence, not the
current stopping state.

## T01 / F0a — source truth and CI

Implemented source changes:

- Unified and native-audio CI admits active-branch pushes and matching PRs.
  Driver/shared/safety changes reach the unified checks; committed-source
  dependencies also reach the QUIC checks.
- Unified CI runs source, provenance and existing safety checks without devices.
  It builds the actual MSVC x64 driver/host ABI fixture and supplies that fixture
  to the host tests. GPU ABI tests remain a distinct check.
- `tools/build-receipt.py` records pre-build source, executed commands/toolchain
  reports, exact logs and output hashes. Checks bind to before/after source
  identities. Missing/changed evidence, source drift and failed commands cannot
  produce a passing receipt. Dirty source and untracked source contents are
  identified, rather than silently described as a clean commit.
- Windows/Android CI emits source-bound build evidence, including partial/failing
  evidence when available. These records do not replace Audio Lab's existing
  admission receipt or assert installed driver identity/hardware qualification.

Baseline checks executed before runtime changes:

| Check | Result |
|---|---|
| .NET Release tests | 210 passed, 10 skipped, 0 failed; .NET SDK 9.0.319 targeting net8.0 |
| Actual MSVC x64 driver/host ABI fixture | Passed; 41 constants |
| Existing source gates | 10 passed |
| Existing safety text checks | 12 passed, 2 failed, reproducing the documented three-checkpoint mismatch |

New provenance behavior tests cover command failure, missing/tampered evidence,
artifact hashes, staged/unstaged/untracked content, source changes during the
build interval and attempted reuse of checks from a different source snapshot.
All 7 new tests passed. The Windows host and bundled native GPU DLL also built
successfully with zero warnings/errors, and the hardware-free GPU contract test
passed (1/1). Host tests were repeated using the actual MSVC ABI fixture: 210
passed, 10 skipped, 0 failed.
CI edits require real CI execution before being described as CI-qualified.

## Original F0b preflight blocker — resolved by the continuation below

Both unchanged baseline driver projects were copied with their shared headers
into a fresh ignored staging directory. Actual MSVC x64/WDK 10.0.26100.0 builds
used `SignMode=Off`: both drivers compiled and catalogs were generated, with no
signing, certificate creation or installation. The original source/INF files
were not stamped or modified.

Direct x64 InfVerif (10.0.26100.6584), `/u /v`, then found:

- **FocusIdd:** valid, with warning 2084 concerning the `WUDFRd.sys` service binary.
- **DeskFuseAudio:** **not valid**. Error 1289 at staged INF line 77:
  `Missing directive 'KmdfLibraryVersion' under [DeskFuseAudio_Service_Install] section.`
  It also reports unreferenced WDF and speaker-interface sections (warning 2083).

Compilation/catalog generation therefore do not clear package verification.
The existing Audio project skips package verification; the new direct check
exposes a concrete packaging blocker. Determining the correct INF section and
framework/interface references is deferred to Ultra; no proposed fix is assumed.

Evidence is in ignored `out/provenance/t01/`:

- `audio-inf.log` / `audio-inf.json`: exact failing command, output and source IDs.
- `focusidd-inf.log`, both driver build reports/logs, host/ABI/GPU reports/logs.
- `driver-stage-path.txt`: exact isolated staging location.
- `build-receipt.json`: hashes for actual staged driver packages and host/native
  outputs. `recorded_checks_passed=false`; the failed check is
  `audio-inf-universal`. Installed/hardware verification remains false.

The receipt captures the source before this status-only update; no runtime
source changes followed it. T02 recovery scripts remain unchanged, including
the two previously documented safety failures. T03 and later work has not begun.
No driver, PnP, default audio route, physical display, Android installation,
test-signing or other machine-configuration operations were performed.

## Continuation — INF corrected; recovery implementation stopped

Applied the two Ultra-adjudicated functional INF corrections:

1. Point `KmdfService` to the existing `DeskFuseAudio_Service_Install.Wdf`
   section containing `KmdfLibraryVersion=1.27`.
2. Move all five unchanged `AddInterface` directives into
   `DeskFuseAudio_Device.NT.Interfaces`.

Corrected the nearby INF/project comments about KMDF compatibility; retained
the 1.27 target, service, hardware IDs, interface GUIDs/reference strings,
permissions, audio modes and driver source behavior.

A fresh isolated Release x64 build with WDK 10.0.26100.0 and `SignMode=Off`
compiled the driver and regenerated its catalog. Direct x64 InfVerif
10.0.26100.6584 `/u /v` returned **INF is VALID**, with no errors or warnings.
Evidence is preserved under `out/provenance/t02-inf/`; its `build-receipt.json`
records both executed checks and staged package hashes, with
`recorded_checks_passed=true`, installed/hardware verification false.
No signing, certificate creation, installation or hardware operation occurred.

### New stop: recovery completion and ownership

Read-only inspection before changing the checkpoint schedule found:

- `safe-driver-proof.ps1` persists only `Id` and `StartTick` in `state.json`.
  It does not persist installer completion or an owned device incarnation.
- `Try-Cleanup` writes `done.flag` after `rollback-focusidd.ps1` reports no
  matching devnodes. Its caller can then unregister both recovery tasks.
- The watchdog treats `done.flag` as completion and otherwise repeats
  hardware-ID-scoped removal. Its own comment explicitly anticipates an
  installation finishing after an earlier removal.

These observations are a missing implementation contract, not a reproduced
physical failure. The v0.6 delayed-installation/crash requirements need an
explicit rule for proving installation is finished, declaring final absence,
retiring recovery tasks and retaining the intended device ownership when
completion is uncertain. A snapshot absence check or the checkpoint schedule
alone does not establish that rule. The existing text-only tests do not prove
the lifecycle behavior.

Per the user's stop-on-new-design-decision instruction, **no safety/recovery
script changes, replacement lifecycle protocol or workaround was attempted**.
The next Ultra pass should resolve this narrow contract and specify its mocked
failure tests, then hand it back for implementation. T03 and later remain
unstarted. The INF receipt predates this status-only continuation record.

## Latest T02 increment — state core and exact-instance adapter

Implemented the adjudicated recovery contract from
`DeskFuse_T02_Recovery_Contract_and_High_Handoff.md` (beside the v0.6 architecture
artifact), without enabling a live installation path:

- Added `tools/driver-test-policy.json` with the reviewed three checkpoints,
  readiness/Closing/cleanup observation bounds and heartbeat/retry policies.
- Added `windows/tools/DeskFuse.DriverTestOwner.Core/`: immutable run identity,
  source-bound policy, flushed sequence/hash journal, stable short state lock,
  one-shot creation admission, irreversible fences and expected incomplete states.
- Added final-cleanup-after-last-creation fencing, exact worker incarnations,
  replay/expiry checks, reboot/ownership-conflict latches and fresh absence checks.
- Added durable maintenance admission and compare-and-clear release, preserving
  retired tombstones and preventing delayed run A cleanup from clearing run B.
- Added the internal exact-instance Windows SetupAPI adapter and real x64 SDK
  ABI fixture. The helper project is still a **library**, with no live CLI.
- Extended native-contract generation and CI/test wiring for the new SDK check.

Verification completed:

| Check | Result |
|---|---|
| Targeted recovery, registry and ABI checks | 35 passed, 0 skipped/failed |
| Full host regression suite | 245 passed, 10 skipped, 0 failed |
| Real MSVC x64 fixture | Original 41 constants plus the new SetupAPI size/offset/constant fixture passed |

A restricted-sandbox full run encountered Windows CNG key-import errors. The
unchanged suite was repeated with the required existing user-profile/crypto
access and passed; no cryptographic code was changed. Final TRX, log, executed
command, source snapshot and DLL/ABI hashes are in `out/provenance/t02-core/`.
The receipt represents source before this status/README-only update.

**Historical stop, resolved by the later package-admission handoff:** the recovery handoff says to validate signatures appropriate to
the installation mode, but does not define the trusted signer/catalog policy
or the authorization binding for test versus release packages. Current receipts
are hashes/build evidence (`attestation=false`), and the available staged package
was deliberately unsigned. They cannot be treated as installation approval.

Ultra needs to specify: admissible signer identity, catalog membership and
chain/verification requirements, and test/release approval relative to actual
Windows policy. No weaker verification mode, certificate installation,
test-signing change or loader-only fallback has been assumed.

## T02 package-admission increment

The subsequent `DeskFuse_T02_Package_Admission_and_Signing_Handoff.md` resolves
the signing policy. The source now defines explicit profiles, a protected-approval
record, fixed FocusIdd package identity and v2 manifest binding. The identity
uses a canonical, length-delimited file list and includes the final CAT. The
coordinator denies new creation without a v2 binding and a verifier; it checks
again before stage and install, and closes creation on verification failure.
Recovery/removal does not require the package verifier. These are production
core changes, not a working Windows trust or protected-approval backend.

Focused driver-owner tests: 45 passed with the rebuilt real SDK ABI fixture.
Full host suite with the existing native ABI fixture: 255 passed, 10 skipped.
The skipped tests need unavailable runtime/physical integration prerequisites.
The native fixture now also compares the WinTrust data, catalog, file and
signature-settings layouts and selected policy constants against the SDK.

Outstanding before any live helper: implement and qualify Windows SYSTEM
catalog/member/signer verification, protected approval and atomic one-run
binding, locked-package and ACL checks, worker roles and coordinated recovery
script migration. The present verifier interface has only test implementations;
no live entry point or device operation is enabled by this increment.

`safe-driver-proof.ps1`, watchdog, boot recovery and rollback scripts remain
unchanged, including their two known static timing failures. Native device APIs
have not been executed; executable roles, protected live preflight, coordinated
script migration and mocked PowerShell integration remain unfinished. No
installation, task registration, machine-configuration or hardware operation
was performed. T03 and later remain unstarted.

## Ultra implementation increment — 27 October

- T02 now has one-run approval binding, read-locked package custody, a short
  revocation/admission fence, Windows catalog/member and signer verification,
  actual SetupAPI catalog selection, and NTFS alternate-stream inspection.
  These parts are not yet composed into an authorized live helper. The protected
  root must have verifiable secure creation and handle provenance, and approval,
  run identity and actual verification evidence must be bound together. A valid
  signed package under SYSTEM remains a separate qualification gate. A file's
  unnamed-stream lock does not prevent creation of another NTFS stream.
- T03 has a bounded, one-use P-256 proof component with canonical context and
  replay tests. It is **not wired into the live broker**: protected enrollment,
  trusted worker/logon registration and Android Keystore integration remain open.
- T04 adds in-memory Preparing/Closing/Quarantined/Retired endpoint states,
  deny-on-uncertain retirement, and no unauthenticated cross-pipe takeover or
  automatic adoption of an unowned existing node. A failed cleanup retains its
  identity for retry while the broker lives. Durable recovery after broker exit
  and the independently authenticated ownership protocol remain open. Broker
  requests are bounded before JSON parsing.
- T05 introduces a host-session input ledger, tracks uncertain partial native
  injection and avoids unqualified global release. It is not wire-generation
  fencing, hardware attribution or recipient-isolated input. The practical
  modifier and local recovery limitations are recorded in
  `plans/Input_Safety_Interim.md`.
- T06 transfers owned encoded buffers into the ordered encrypted writer and
  retains them through write/cancellation. One in-flight unit plus one GPU-side
  pending unit are bounded; the consumer waits before advancing the encoder
  enumerator. A synthetic encrypted loopback comparison showed reduced managed
  allocation; no physical video performance conclusion follows from it.

The current broker and the Windows host plus native GPU library have compiled
against the existing toolchain. The combined host test suite passed 338 tests,
with 10 integration/FFmpeg tests skipped. The real SDK ABI fixture was included.
Physical device, signed SYSTEM, audio category, and end-to-end latency
qualifications are not implied by source or unit tests.
No device installation or machine-configuration change occurred in this pass.

## Core closure source pass — 28 October

The earlier missing protected-root and composition decisions are now implemented
in source. `DeskFuse.WindowsSecurity` owns one fixed maintenance registry root and
one separate broker-authority root under the trusted OS installation anchor.
Objects are private at creation, population copies verified bytes into new files,
and the product broker holds an exclusive owner lease. The shared component has
no runtime dependency on driver test tools or audio code.

`PackageAdmissionOwner` composes canonical run/approval-bound verification records,
fresh Windows trust, exact helper/package sets and retained protected custody.
`RunProvisioner`, `RunRecovery` and `WorkerProcessMonitor` supply execution and
real process-incarnation observation primitives without enabling an installer.

The broker now has a concrete protected enrollment store and a concrete resource,
scope, generation and retained-worker registry. Proof dispatch rechecks current
authority. Caller-bound sessions compare the actual local pipe process object;
registration IDs are not bearer credentials. The local-only pipe excludes
client server-creation rights and cannot reuse protocol I/O after disconnect.
Trusted worker source uses verified SYSTEM-owned process/thread security, an
atomic kill-on-close job at creation, no child processes, creation mitigations,
sanitized environment and exact approved logon. The pipe namespace is claimed
before resume. Android has its separate host/enrollment-scoped Keystore adapter
and expected-context proof signing source.

These are core source components, not an activated service/installer. The ordered
High integration and real qualification gates are specified in
`plans/Ultra_Core_High_Handoff.md`. Existing broker v1 remains a development path;
the independently authenticated v2 guarantee must not be advertised through it.
The complete source/check/artifact receipt for this pass is under
`out/provenance/ultra-core-closure/`.

Build environment note: an isolated SDK-profile invocation emitted the SDK's
first-run development-certificate message. Read-only inspection of the normal
user store found only the preexisting 2024/2025 localhost certificates, with no
new certificate dated this pass. Further builds use the normal initialized SDK
profile and explicitly disable development-certificate generation. No trust,
import or certificate cleanup command was performed.

## High continuation — standalone owner source

The maintenance helper now has executable typed roles, protected per-run code and
policy composition, independently checked scheduled-task definitions, separate
creator/removal processes and runner/watchdog control records. The actual density
probe has an optional guarded-start path: no driver access until the watchdog
registers that exact probe and publishes its process-bound permission. Its proven
density/ruler negotiation remains unchanged after admission.

Native worker observation retains the original launched process object, including
fast-exit children. Cleanup scheduling waits for actual process/native facts,
prevents piling up workers behind unknown removal, and repeats successful
emergency cleanup after a changed creation/quiescence frontier. Third-checkpoint
and probe-rundown behavior is exercised through the production coordinator.

Self-contained win-x64 publication, host/native GPU compilation and the offline
suite have been executed. The latest detailed evidence is under
`out/provenance/high-owner-roles/`. An in-memory Windows Scheduler XML parse is
included; no task is registered or started. Installed state and hardware remain
unqualified. The full actual role-host/UI fault harness is still pending.

**Open source integration:** trusted setup/approval publication and the real
interactive runner plus coordinated watchdog/boot/rollback wrapper migration.
The old wrappers are unchanged; `tools/test-safety-gates.py` still reports its two
known failures for three-checkpoint UI/expiry timing. These remain visible and
must be replaced by passing behavioral checks before activating the new flow.
No architecture blocker requiring Ultra has been identified in this increment.

## High continuation — protected preparation and coordinated runner

`TrustedMaintenanceSetup` consumes independent protected helper/probe/runtime and
package approvals. It copies verified bytes into new private files, verifies the
candidate using composed Windows trust and burns the one-run authorization through
the production registry. Task publication follows complete bundle/journal/policy
and control-file publication. Neither a build receipt nor an exposed directory is
promoted to authority. Failed reservations remain unresolved. Native preflight
source rejects existing known FocusIdd/legacy tasks and root devices; inventory,
SYSTEM trust and Scheduler registration were mocked, not executed here.

The runner, watchdog request, boot request, rollback and compatibility scripts
were migrated together. Only prepared namespace/run identities are accepted; an
INF path no longer starts installation. The interactive process must match its
approved PID/creation time. Explicit local risk confirmation precedes watchdog
start. The timer/dialog keeps heartbeats alive through all three checkpoints and
requests only exact prepared recovery after watchdog loss. Earlier acknowledgements
never disable later checks. Release flags and device absence cannot certify
retirement. Recovery reports expose actual owner snapshots to the UI; the journal
predicate alone authorizes task deletion and registry release.

Readiness starts as an empty SYSTEM-owned file; nonempty probe output is required.
Graceful release uses the existing ten-second policy window before terminating
only the retained exact probe. Remove-only recovery can attempt the same rundown
after watchdog loss. Missing/reused process identity stays unconfirmed.

The immutable manifest has a separate bounded 1 MiB budget for the complete
self-contained dependency closure. Journal transition limits are unchanged;
oversized manifests are rejected before allocation/publication. Packaging includes
the seven runner/module scripts and hashes every nested bundle file.

The replacement safety gate exercises production coordinator/setup decisions and
shipping Windows PowerShell 5.1 UI event handlers/entrypoints against mocked
machine, UI, process and clock boundaries. It covers all three missing checkpoint
deadlines, cancellation, wrong-run data, lost progress, changed package/code and
failed preparation. The previous two timing-string failures are superseded by
behavioral checks. Portable static checks remain supplementary. Windows CI runs
the actual suite and builds the helper/runner without activation.

Evidence: `out/provenance/high-prepared-maintenance/`. No installed task, driver,
certificate, machine setting, phone or speaker changed. The actual trusted local
installer/service entrypoint still needs to call these APIs through its protected
activation path. Full SYSTEM role-host/launch, signed-package and physical
qualifications remain open; this source pass does not establish them.

## Receipt use

Use ignored `out/` paths. `source` does not take an atomic filesystem snapshot;
run against a stable checkout. Records are evidence with hashes, not signed
attestations, and arbitrary commands are executed only when explicitly supplied
to `check` by the developer/CI. The tool never discovers or contacts devices.

```powershell
python -B tools/build-receipt.py source --output out/provenance/source.json
python -B tools/build-receipt.py check --name dotnet --kind toolchain --report out/provenance/dotnet.json --log out/provenance/dotnet.log -- dotnet --info
python -B tools/build-receipt.py check --name host-tests --kind test --report out/provenance/tests.json --log out/provenance/tests.log -- dotnet test windows/agent/FocusWindow.Tests/FocusWindow.Tests.csproj -c Release --verbosity minimal
python -B tools/build-receipt.py create --baseline out/provenance/source.json --component host-tests --profile Release --architecture x64 --check-report out/provenance/dotnet.json --check-report out/provenance/tests.json --output out/provenance/receipt.json
```

For a build, add its executed build report and `--artifact` files or
`--artifact-dir` staged bundles. Profile/architecture fields are declared build
intent; executed commands/logs remain the evidence. Missing expected reports
remain failures even if other reports passed. Keep the whole evidence directory.

## Next gates

- **T02:** implement the selected structured three-checkpoint recovery policy and
  behavioral/fault tests; pin/build/package/verify both drivers in staging.
  Existing safety failures remain visible until their behavior is corrected.
- **T03 onward:** no broker, input, audio-owner, media or driver runtime change is
  included in T01. F0b and physical qualification are still open.
- No driver installation, PnP changes, default-route changes, test-signing changes
  or device/hardware operations are authorized by the build tooling changes.

## Subsequent audio continuation — 29 October

The historical gates above describe that earlier tranche. Current T03/T09 audio
work is recorded in
[Audio_N2_Bounded_Request_Implementation_Status.md](Audio_N2_Bounded_Request_Implementation_Status.md).
N1, the focused N2 request-retention source and independent N5 capture foundations
are implemented/offline checked. Next is N3 native TLS/control on High, then
actual source/egress/rundown, N5 wiring and N6 renewal/recovery. Production stays
disabled; installed and physical gates remain open. This continues the original
v0.6 programme and does not reopen completed media optimizations.

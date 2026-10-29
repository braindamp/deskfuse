# Driver test owner

This source implements T02's exact-run recovery and package admission components.
The project now builds as a standalone x64 executable with typed `status`,
`control`, `provision`, `watch` and remove-only `recover` roles. Compiling or
publishing does not prepare a privileged namespace, register/start tasks or
install a driver. Trusted installer/service activation and SYSTEM/physical
qualification are still required before live activation.

The signing policy is settled by
`DeskFuse_T02_Package_Admission_and_Signing_Handoff.md` beside the architecture
artifact. Do not reopen that policy merely because artifacts are unsigned or
machine trust is not provisioned. Build-only, local-test and release are explicit
profiles; source/build receipts never authorize installation.

- The core owns one-shot creation, durable operation intents/results,
  Closing/Retired fences, approval binding/revocation and exact-run recovery.
- Windows verification binds the exact INF-selected CAT, member proofs and
  Windows-selected signer to approved package bytes. Final verification requires
  SYSTEM machine trust and cache-only behavior.
- Protected custody keeps package and helper identities stable through admitted
  work. Arbitrary existing directories cannot be promoted by an ACL snapshot;
  secure initial creation and retained directory/file identities are required.
- `RunProvisioner` checks cancellation between native operations without
  manufacturing completion. `RunRecovery` performs one exact cleanup attempt;
  only the full coordinator predicate can certify retirement.
- `WorkerProcessMonitor` observes a retained process incarnation and its real
  signal state. Native return, permit relinquishment and process exit are separate.
  For children launched by the watchdog it duplicates the original process
  object, preserving observation even if the child exits before the next poll.

## Executable integration

`OwnerRunContext` requires SYSTEM, the fixed namespace identity, the immutable
manifest/policy and the exact per-run executable/dependency closure. Cleanup
checks helper custody independently of candidate package/probe availability.
Runtime data binds the actual runner process, distinct role nonces and the probe
profile. CLI roles accept neither arbitrary device targets nor executable paths.

`WindowsWatchRole` verifies exact SYSTEM task definitions and the bound runner
before arming. Provisioning and removal use separate native worker processes;
the watch loop handles clock policy, mailbox liveness, process observations,
checkpoint requests and scoped cleanup. Unknown removal intents prevent more
automatic remove workers. Successful emergency cleanup is repeated when actual
creation/quiescence changes, rather than continuously polling native removal.
At 350 seconds status classifies overdue cleanup separately; the 20-minute retry
window never certifies an unresolved operation or removes retained recovery tasks.

The coordinated runner must launch the approved probe with all three
`--owner-start-gate`, `--owner-run` and `--owner-nonce` bindings. That source path
parks before any driver access and fails after ten seconds without an exact
PID/creation-time/run/nonce permission. The watchdog retains approved probe code,
registers its exact process and then publishes its one-shot start permission.
Closing requires real probe exit; the release flag is only a request. These
arguments are for the new coordinated runner, not an activation of the old scripts.

Trusted setup must create `runner-control.json` and `watch-status.json` as private
files before starting tasks. Their stable file objects serialize bounded writes;
runtime code never adopts a missing mailbox. Runner revision changes renew
liveness using watchdog time; repeated/replayed records cannot renew it. The
surviving runner must independently observe watchdog progress and invoke only
the prepared remove-only task if the watchdog is lost. Shipping runner event
handlers and entrypoints are exercised with mocked Windows PowerShell 5.1 platform
boundaries. Actual SYSTEM role-host and desktop/hardware behavior remain separate.

Task XML is checked against the Windows in-memory parser without registering a
task. SYSTEM service-account registration uses COM `TASK_LOGON_SERVICE_ACCOUNT`
(5); `ServiceAccount` is not an XML `logonType` value in the
[Microsoft scheduler schema](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-schema).
Actual registered task identity/security is checked separately before arming.

`tools/build-driver-test-owner.ps1` publishes a fresh self-contained win-x64
directory using cached runtime packs. It runs only `--version`, checks dependency
presence/runtime configuration and records hashes outside the executable bundle.
Those hashes are build evidence, never privileged helper approval.

`WindowsPnpAdapter` is an internal adapter, not authorization. Its constructor
and disposal perform no device calls. Recovery does not need the candidate
package to remain present. New creation must go through composed package admission
and independently confirmed recovery.

Native layouts/constants are compared against actual MSVC x64 SDK output from
`tools/driver-test-abi.cpp`. Pure and fault tests inject platform boundaries; they
do not qualify signed SYSTEM behavior, PnP execution, display safety or physical
device compatibility. Those remain explicit later gates with identified artifacts.

`TrustedMaintenanceSetup` implements independent protected code/runtime/package
approval publication and preparation. Its internal API is for trusted local
installer composition, with no network/legacy-agent nomination route or live
bootstrap CLI. Partial preparation and existing legacy tasks/devices require
explicit reconciliation; they are not swept away.

The six operator/compatibility scripts now request prepared exact-run tasks.
`safe-driver-proof.ps1 -DescribeRunner` reports the current process incarnation
without authority. Trusted setup must approve that actual process before the same
PowerShell process starts the proof with `-NamespaceId`, `-RunId` and
`-IUnderstandDriverRisk`. Passing an INF no longer installs a driver. The approved
bundle contains `runner/` scripts and the self-contained runtime. Old installed
legacy tasks/bundles are not adopted or altered by this source migration.

`tools/test-safety-gates.py` requires the native SDK fixture and runs coordinator/
setup fault tests and Windows PowerShell 5.1 mocked UI/entrypoint behavior.
`--static-only` is a portable supplement, not a behavioral safety pass. Tests do
not call PnP, register tasks or display the proof UI. Device absence, release flags
and UI reports cannot override unresolved native history. Latest source results
are in `plans/Architecture_v0.6_Implementation_Status.md`.

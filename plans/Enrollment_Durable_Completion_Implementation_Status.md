# Enrollment durability implementation — current continuation

29 October 2026. High continuation under
`Enrollment_Durable_Completion_High_Handoff.md`; preserve all existing uncommitted
work at `experiment/vnext-audio-latency`, HEAD `2f181cf2da9c4efdde3013b115108ecac650d1ab`.

**Focused thread-security decision accepted and implemented in source.**
[Native_Worker_Thread_Launch_High_Decision.md](Native_Worker_Thread_Launch_High_Decision.md)
records the private duplicated-token/default-ACL policy, diagnostics setting,
actual suspended-child verification and executed checks. N1's fixed Rust worker,
process-bound control bootstrap and local build/staging are implemented; they do
not grant audio authority. Continue N2–N6. Installed qualification remains open.

## Current source work

- D1: enrollment-only direct private creation / existing exclusive writable
  custody is implemented in `EnrollmentPrivateRecord.cs` and
  `SecurePrivateStorageRoot.cs`. Completion reasserts only exact validated
  positive bytes, explicitly checks `FlushFileBuffers`, retains parent/file
  custody, and latches reported errors. Local NTFS prerequisite is explicit.
  Other private storage/maintenance callers remain on their existing APIs.
- D2: `TrustedBrokerEnrollmentStore` uses retained records through the small
  `EnrollmentRecordFiles` adapter. Startup checks negative artifacts first,
  validates both positive artifacts before evidence-then-marker completion,
  never manufactures a missing marker, and separates immediate denial from
  confirmed revocation. Receipt recovery preserves exact signed bytes and its
  quarantine does not invent commit revocation. Anonymous old staging blocks
  new receipt signing where the previous publication is ambiguous.
- D3: `EnrollmentPersistenceOwner` now supplies bounded, retained cold operations,
  joined exact commit retries, cancel-after-admission revocation, status-response
  revision checks and actual-I/O shutdown. `BrokerEnrollmentRundown` retains
  registered native owners and refuses replacement when actual owner custody or
  rundown is missing. Its concrete native resources will be wired by E6/N1–N6.
  Ceremony completion is typed; late admitted success is historical durable
  evidence and cannot reopen denied admission. Queued revocation may complete
  during shutdown while new positive admission is closed.
- D4: Android immutable enrollment files now use private direct creation,
  retained descriptors, exact-byte reassertion and checked file/directory/root
  sync. `AtomicFile` no longer controls this store. Legacy residue is preserved
  and denied/quarantined. Repository recovery completes Prepared/Active before
  exposing capabilities; proof-current checks consult memory. A raw parsed
  Prepared transcript cannot authorize final approval signing.
- D5 source composition is now implemented in `BrokerSetupCoordinator`: actual
  elevated local selection (paged protected enrollment list and scope subset),
  fixed high UI/medium transport launch and observed pipes, frozen transcript,
  both consent channels, retained asynchronous commit/cancel/status/receipt work,
  tagged transport-write acknowledgements and joined actor/control I/O rundown.
  Bounded output owners prevent slow pipe/TLS writes from blocking UI cancellation.
  The result UI remains alive until the operator closes it. Network input cannot
  supply PC confirmation or unauthenticated post-commit cancellation.
- Windows setup now offers protected New/Replace/scope selection and displays
  the public setup address/port. The medium transport owns optional rate-limited
  discovery on UDP 47813; failure retains the manual locator path. Android has
  an explicit trusted setup dialog alongside the preserved legacy picker, with
  independent code verification, foreground final consent and saved recovery.
  No legacy secret is promoted and successful enrollment does not auto-start a
  legacy display/audio session.
- Initial E6 hardening: public legacy `signExisting` now rejects authority v2.
  V2 goes through completed enrollment, root-authenticated challenge and exact
  scope/context checks, with current-state recheck after signing. Bounded exact
  signature retry caching retains key-availability and local-denial checks.
  The shipping Kotlin interoperability fixture now exercises completed recovery,
  no disk access in proof-current checks, raw-Prepared/v1-entry rejection,
  authenticated challenge/signature retries and local denial.

## Checks actually run in this continuation

- Windows security library build passed, zero warnings/errors.
- Broker plus its real native source-control DLL build passed, zero warnings/errors.
- Android `:protocol3:interopJar :app:compileDebugKotlin` passed offline after
  correcting use of a nonpublic SDK `O_DIRECTORY` constant to opened-directory
  type validation. No app installation occurred.
- Latest focused C# run: 18 passed, 0 failed, 0 skipped, including four combined
  durability faults, canonical C#/shipping Kotlin interoperability and current
  authority-state checks and protected selection/direction bounds. Repeated after
  the new shipping Kotlin guard/recovery fixture and passed without warnings.
- Broker/native DLL, protected setup UI and medium enrollment worker builds all
  passed with zero warnings/errors. Android compile and interoperability JAR
  passed again with the real trusted setup dialog and v2 signing guard.

Build environment: Windows-targeted MSBuild needs the installed SDK outside the
sandbox. Import Visual Studio's `Microsoft.VisualStudio.DevShell.dll` and enter
the x64 dev shell so CMake/Ninja/MSVC are on PATH. Android uses the existing
`C:\Users\Arne\.gradle` cache and
`D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1`, with `--offline`.
The interoperability JAR is `android/build/protocol3/deskfuse-protocol-v3-kotlin.jar`;
set `DESKFUSE_PROTOCOL3_KOTLIN_JAR` for that focused C# test.

These are source/offline checks, not installed SYSTEM, power-loss, phone, PCM or
native process-isolation qualification. The test result interrupted in the
previous conversation remains historically unconfirmed; the 18 checks above are
newly executed results.

## Remaining active work

0. Thread decision and N1 source are implemented. Latest focused checks: 25 C#,
   three Rust control tests, Windows builds, actual SDK ABI and native Debug/
   optimized x64 Release staging passed. Continue concrete audio authority in N2;
   production remains
   blocked until the complete chain and installed qualification pass.
1. Finish installed-service composition/entry binding after the full native chain
   is present. `BrokerSetupCoordinator` is source-wired internally but is not
   yet called by an installed service host. `Program.cs` still refuses production
   authority. Do not mark E2–E6 integrated or physically qualified from these builds.
2. Review the coordinator and phone-flow changes for additional lifetime races;
   use focused behavior checks, not a broad new framework. Actual protected UI/
   token/Keystore/storage/discovery behavior remains an installed/device gate.
3. Continue E6/N1–N6 in the native ownership handoff: fixed Rust worker/pipe,
   typed source/delivery authority and deadline, in-memory native TLS, committed
   source integration/cutoff, in-process WASAPI queue and renewal/recovery.
4. Re-run affected focused checks only after new changes; record results here.
   Update this document and its `state_saving` copy before any final handover.

Production authority remains blocked by `Program.cs`. No service publication,
persistent authority-key provisioning, installation, driver, route, PnP or
hardware operation was performed.
The worker-created-thread design stop is resolved by the accepted decision above.
Its source implementation does not claim installed isolation qualification.

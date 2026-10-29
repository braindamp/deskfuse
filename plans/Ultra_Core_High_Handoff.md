# Core security decisions and High implementation handoff

28 October 2026. Continues Architecture v0.6 and its T02 recovery/signing
handoffs. The scope is source implementation and offline verification. Installed
drivers, speakers, phone, Windows service configuration and production trust
have not been qualified by these tests.

## Decisions now implemented in core source

1. **One private storage mechanism, two real owners.**
   `windows/security/DeskFuse.WindowsSecurity` provides fixed Maintenance and
   BrokerAuthority profiles under the OS-resolved default x64 Program Files
   anchor. It accepts no arbitrary privileged root. The supported premise is an
   intact trusted OS/installer boundary; administrator/kernel compromise is
   excluded. Current permissions are not treated as proof of arbitrary history.
   Objects are born private; imports copy verified bytes into new private files.
   Existing exposed files/directories are never moved, linked or tightened into
   trust. Directory/file identity, ACLs, reparses and streams remain checked and
   held through use. Failed bootstrap is retained and denies reuse.
2. **Global maintenance and product owner serialization.**
   A fixed maintenance root contains the permanent run registry. New GUIDs cannot
   select parallel roots or bypass unresolved runs. The separate fixed broker
   root has an exclusive service-owner lease. Restart never restores active
   worker registrations or resource authority from stored enrollment alone.
3. **Composable driver admission.**
   `PackageAdmissionPreparation`, `PackageAdmissionOwner` and
   `SecurePackageAdmissionStore` bind the approved package, actual Windows
   verification, run, protected evidence and complete helper bundle. Signer and
   exact INF-selected catalog/member proofs are checked anew before creation.
   Approval/revoke and journal admission use a short gate; file custody survives
   native use. Cleanup does not depend on candidate/signing trust. The fixed
   helper profile includes its shared security dependency; a package-only manifest
   is insufficient. Helper hashes prove consistency, not independent approval of
   arbitrary privileged code.
4. **Recovery facts remain separate.**
   `RunProvisioner`, `RunRecovery` and `WorkerProcessMonitor` distinguish native
   return, relinquished creation permission and retained process-object exit.
   Missing results stay UnknownInFlight; observed device absence alone cannot
   retire the run. Recovery construction does not require source package files.
5. **Independent device proof and concrete broker state.**
   `BrokerAuthority`, `BrokerAuthorityState` and `ProtectedBrokerEnrollments`
   bind protected immutable enrollment, delegated resource/scope, exact worker,
   owner generation, logon and one-use challenge. Keys rotate through a new
   explicit enrollment identity, never automatic legacy trust upgrade. Durable
   revoke markers deny reuse; persistence failure closes current in-memory
   authority and is returned as failure. Signature verification is outside the
   short gate. A held proof is checked again at actual dispatch, consumed once,
   and cannot execute after changed authority. Native resource serialization and
   confirmed rundown remain the native owner's separate responsibility.
6. **Actual local caller identity.**
   `BrokerLocalPipe` reserves a local-only, per-worker namespace before worker
   execution. Client rights exclude server-instance creation; generic client
   write permissions are not granted. `BrokerCallerBinding` retains the actual
   kernel-observed client process and compares it to the service-created worker,
   including process incarnation, binary logon identity and Windows session.
   Production authority sessions are caller-bound. A registration GUID is an
   identifier, never a bearer credential. Protocol I/O makes disconnect terminal
   so old asynchronous I/O cannot attach to a new client.
7. **Contained native worker launch.**
   `BrokerWorkerLauncher` selects the approved WTS logon, requires medium and
   non-elevated token facts, and creates the fixed protected native image suspended.
   Process/thread objects have verified SYSTEM-owned security. Creation-time job
   membership closes broker-death orphaning even before registration; child
   processes are prohibited for this native source-worker profile. Extension-point
   and image-load mitigations, sanitized environment, no inherited handles, exact
   creation identity and a fresh logon check precede resume. A cleanup failure
   retains the exact worker for quarantine/retry. This profile must never own or
   kill ordinary document/application processes.
8. **Android's independent signing identity.**
   The shipping Kotlin proof parser/signer and Android Keystore adapter use
   separate host/enrollment-scoped P-256 keys. Signing accepts only a bounded,
   parsed, expected-context transcript and the approved key fingerprint. Key loss
   or change requires explicit enrollment; no ECDH reuse or runtime rotation.
   Hardware protection is reported, not assumed. The JVM harness exercises the
   actual shipping parser and JCA code; hardware Keystore behavior is a later gate.

## Precise next High work

**29 October planning resolution:** items 2–5 now use the concrete contracts in
[Trusted_Enrollment_Bootstrap_High_Handoff.md](Trusted_Enrollment_Bootstrap_High_Handoff.md).
This resolves the enrollment-bootstrap stop; it is a plan, not deployed authority.
Implement its E1–E6 increments under the same stopping/qualification rules.

The subsequent focused native-worker stop is resolved in
[Native_Audio_Owner_Boundary_High_Handoff.md](Native_Audio_Owner_Boundary_High_Handoff.md).
It fixes the E6 audio executable/ownership and renewal contract without changing
the no-children profile or routing PCM through managed/SYSTEM control. E1/E2 work
already written is partial implementation, not activated v2 authority.

These are integration/build steps under settled contracts, not permission to
install or silently activate an incomplete security profile.

1. Finish the standalone driver's typed executable roles and self-contained x64
   packaging. Implement the independently verified scheduler/process/UI bindings
   and migrate runner/watchdog/boot/rollback together. Use the production clock,
   executor, journal and exact process/device identity. Keep the legacy timing
   test failures visible until behavioral replacements pass. Never use wildcard
   recovery, done.flag certification, an installation replay or automatic reboot.
2. Connect trusted installer setup to the fixed private namespaces, explicit
   helper/package approvals and service configuration containing their expected
   namespace IDs. Write records with CreateNew-copy APIs; do not invent directory
   adoption or trust from a source receipt. Keep exact rollback artifacts.
3. Implement the local protected enrollment ceremony and user-facing trust
   upgrade. It must independently verify the phone signing key and selected host
   identity; a key nominated by the old same-user host or old pairing secret is
   insufficient. Only trusted local setup calls AddFromTrustedSetup. Network
   requests cannot write enrollments, register processes or nominate installed
   worker images.
4. Wire the service to the fixed installed native-worker RX profile and dependency
   custody, trusted launch, concrete enrollment/registration state and typed local
   control channel. The native worker accepts only the generated registration
   bootstrap, opens the reserved pipe with explicit ClientAccessMask, validates
   the actual SYSTEM-owned pipe descriptor and absence of untrusted server-create
   rights, binds its server to the trusted service instance, and stops on broken
   established control. The mask includes READ_CONTROL for this inspection;
   checking owner alone without the server-create restriction is insufficient.
   Initial PIPE_BUSY retry may be bounded; reconnection after broken established
   control requires a fresh trusted launch. No arbitrary command/environment or
   PID registration is accepted.
5. Activate the versioned broker protocol only after that full chain works.
   Existing v1 is a development path and is not independently authenticated.
   Never advertise the new guarantee while dispatching through v1, and never
   automatically fall back to it on v2 mismatch. WTS lock/logoff/switch events close
   interactive admission; already admitted native work must be joined or kept
   quarantined before replacement ownership. Service shutdown retires owners
   before disposing authority storage/worker custody.
6. Continue v0.6's independent video, audio renewal, input wire-generation/local
   reconciliation, terminal and product slices. The interim input restrictions
   in Input_Safety_Interim.md remain visible until qualified replacements exist.
   No additional signature check, policy IPC or registry lookup belongs on the
   per-frame/sample/input hot path.

## Focused media performance continuation

**Latest execution:** the media continuation below is implemented in source and
built, with its physical gates still open. The trusted enrollment bootstrap is
now specified in
[Trusted_Enrollment_Bootstrap_High_Handoff.md](Trusted_Enrollment_Bootstrap_High_Handoff.md)
for High implementation. No production authority is inferred from that plan,
the media builds or old pairing credentials.

The 28 October read-only review retained the recent video/audio copy removals.
The remaining implementation plan for those affected paths is now specified in
[Media_Performance_High_Handoff.md](Media_Performance_High_Handoff.md): M0 corrects
measurement boundaries; M1 separates crypto directions and removes Android
ciphertext concatenation; M2 confines codec work to one owner with bounded
submission; M3 transfers owned PCM to quiche and trims reactor bookkeeping; M4
uses encoder metadata and fits pool/retry storage; M5 qualifies exact-alias video
sealing. All are planned, not implemented by that document update.

Use its explicit ordering, lifetime/failure contracts, comparison gates and
rollback boundaries for High implementation. It refines T06/T07/native audio
egress independently of the privileged installation work above. It does not
supersede the broader v0.6 programme or authorize builds, device operations or
machine changes in this planning turn. Keep its expected qualification failures
separate from contradictions that require stopping for Ultra.

## Qualification and stopping rule

High continuation source increment: executable role parsing, protected per-run
composition, task XML/COM verification, watch-loop process separation, bounded
runner/watch status mailboxes and self-contained x64 publication are implemented.
The approved probe has a parked-start path bound to its exact process incarnation;
this prevents an unregistered late launch from beginning driver work through that
path. Original launched worker handles survive fast child exit. The source
recovery schedule does not accumulate workers behind unknown remove intents.

This does **not** complete item 1: trusted setup, the real interactive runner and
the four coordinated PowerShell replacements still need wiring and full mocked
role/UI fault tests. Old scripts remain unchanged with their two known timing
failures. Do not activate this role host through them or treat pure/in-memory XML
tests as SYSTEM scheduler, process-containment, PnP or display qualification.
Continue on High under the same stopping rule; no architecture change is implied.

Latest source/build evidence for this continuation is recorded separately under
`out/provenance/high-owner-roles/`; the earlier Ultra receipt remains historical.

Subsequent High source pass: protected local setup approval, package/code copying,
complete-bundle task publication and the interactive three-checkpoint runner are
wired. All six operator/compatibility scripts use prepared exact identities. The
old timing-string failures are replaced by passing coordinator/setup and Windows
PowerShell 5.1 mocked event-handler/entrypoint behavior. Packaging includes the
approved runner scripts. Evidence: `out/provenance/high-prepared-maintenance/`.

The earlier paragraph describes the prior increment. Actual trusted installer/
service bootstrap and expected namespace configuration still need binding; full
SYSTEM role-host/launch faults, signed-package and physical qualifications remain
open. Continue remaining High work under the same stopping rule. Enrollment must
independently verify phone and host identity; maintenance APIs/build receipts do
not provide that trust upgrade.

Before live activation, run the bounded SYSTEM storage/launch adversarial harness
against a separately approved installation profile: rogue same-user/cross-logon
clients, VM/APC/thread/hook injection, handle theft, DACL changes, stale logon/PID,
broker death and dependency replacement. An OS API returning success does not by
itself prove the promised isolation. Source ABI and fake-boundary tests stay
separate from these results. Launch containment does not prove driver/GPU/PCM
rundown.

Then identify and sign the exact final driver package for the chosen local-test
or release profile. Validate it under actual SYSTEM machine trust before any
separately scoped physical operation. Do not import certificates, weaken boot or
code-integrity policy, or reinterpret an unsigned build as an install approval.
Preserve the DPI/ruler and seven audio-category qualification requirements.

On High, stop only for a real contract contradiction, an unexpected ownership or
security/performance tradeoff, or a qualification failure requiring new design.
Expected missing prerequisites and expected UnknownInFlight outcomes are explicit
gate results, not grounds to redesign or weaken the architecture.

Final executed source/build/test evidence for this pass is recorded under
`out/provenance/ultra-core-closure/`; it makes no installed-state or physical
performance claim.

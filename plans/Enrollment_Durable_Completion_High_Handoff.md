# Enrollment durable completion — focused Ultra decision and High handoff

**Execution update:** source work and newly executed checks are recorded in
[Enrollment_Durable_Completion_Implementation_Status.md](Enrollment_Durable_Completion_Implementation_Status.md).
The separate worker-created-thread design stop has been resolved and implemented
in source; follow that status document for the current continuation point.

**29 October 2026. Planning only.** This closes the persistence-completion
decision that interrupted E5 integration. It specifies source work for High;
it does not implement, build, test, install or activate anything. Production
authority remains blocked by `DeskFuse.AudioBroker/Program.cs:32–36`.

Verified checkout: `D:\Coding\DeskFuse\vnext-audio-latency`, branch
`experiment/vnext-audio-latency`, HEAD
`2f181cf2da9c4efdde3013b115108ecac650d1ab`, **plus extensive existing uncommitted
work**. Preserve that work. The earlier interrupted test command has no recovered
result and must not be reported as passed.

Read this with [Trusted_Enrollment_Bootstrap_High_Handoff.md](Trusted_Enrollment_Bootstrap_High_Handoff.md)
and [Native_Audio_Owner_Boundary_High_Handoff.md](Native_Audio_Owner_Boundary_High_Handoff.md).
Architecture v0.6, E1–E6, the Rust/native audio ownership decision and completed
media optimizations remain in force. The saved `durability_adversary.md` supplied
review findings; this document supplies the final decisions, including the
Android correction and a stronger exact-byte recovery step.

## 1. Decisions

| Question | Selected contract |
|---|---|
| Publication | For enrollment artifacts, create the fixed final file directly, born private and exclusively owned. Use Windows `CREATE_NEW`; Android `O_CREAT \| O_EXCL`. No staging/rename in this new path. |
| What proves completion? | Exact bounded bytes, validated object/context, successful writes and explicit platform persistence barriers under retained write-capable custody. Record completion in memory only afterward. |
| Existing positive record | Fully validate before writing; if this owner has no completed snapshot, reassert **the exact stored bytes** through the same existing object, then complete the barriers. No truncation, resizing, replacement, reserialization or new signature. |
| Authorization | Keep separate prepared evidence and semantic commit marker. Only the admitted operation may create a marker. Startup can complete an existing valid marker; it cannot invent a missing or damaged one. |
| Revocation | Presence immediately denies, including empty/torn tombstones. Durable denial is a separate fact, acknowledged only after persistence completion. |
| Ownership | One concrete cold enrollment persistence owner retains operation identity, private-file custody and the service-owner lease until actual I/O completion. Readers receive immutable snapshots. |
| Receipt | Complete and return an existing valid signed envelope byte-for-byte. Losing an acknowledgement is not permission to sign a replacement. |
| Android | Replace `AtomicFile` for these immutable records with direct private creation, checked writes, file sync and directory sync. Recover before exposing Prepared/Active capabilities. |
| Runtime cost | No persistence, hashing, signature verification or new IPC on media paths or inside short authority/state gates. Completed snapshots avoid repeated disk work. |

Direct publication is appropriate because these records already have semantic
validation and fail-closed partial-state rules. A half-written positive file
never grants authority; a half-written negative tombstone already denies. This
choice removes a namespace operation and its custody transition. Do not add a
"completion completed" sidecar, a multi-record pointer engine or a general lease
framework.

**Exact-byte recovery is an explicit availability tradeoff.** Reasserting a valid
record can tear it if the process or machine fails during that write. Such a
record becomes inactive/quarantined. This is accepted for these small cold records
to establish a fresh write-and-barrier attempt instead of inferring repair from
cached readability and a later flush. It never permits rewriting an already
damaged positive record from a guessed reconstruction. Logical values, signed
bytes, file identity and length remain immutable.

## 2. Source evidence and platform basis

### Current source that must change

Paths below are relative to the verified checkout; line numbers describe the
pre-implementation source inspected for this decision.

| Source | Finding |
|---|---|
| `windows/audio/DeskFuse.AudioBroker/TrustedBrokerEnrollmentStore.cs:83–95,198–205` | `Revoke` returns on an existing tombstone; `SaveOnce` returns on matching bytes. Neither completes an uncertain earlier publication. |
| Same file, `:98–114,139–170` | Receipt recovery returns validated bytes; `LoadCommitted` activates readable evidence/marker. Both need completion-aware recovery. |
| Same file, `:15–22,125–135,165–166` | Retained streams are read-only; one `revoked` set also drives signed status; cache publication removes quarantine indiscriminately. |
| Same file, `:62,100,125,226–233` | Commit/status reopen records; receipt captures active state before its persistence gate; disposal must retain ownership through real completion. |
| `windows/security/DeskFuse.WindowsSecurity/SecurePrivateStorageRoot.cs:198–218,236–257,413–422` | Copy closes write custody before opening read custody; publication then renames. Read opens share read only, so they block a later writer. |
| `windows/audio/DeskFuse.AudioBroker/EnrollmentCeremony.cs:139–176` | `CommitAdmitted` is already the authorization boundary. `CompleteCommit` must not be called for a timeout and then called again for late I/O. |
| `windows/audio/DeskFuse.AudioBroker/BrokerEnrollmentState.cs:293–310` | Immediate admission denial precedes disk I/O. It does not certify native resource rundown. |
| `android/.../transport/AndroidTrustedEnrollmentStore.kt:35–46` | Matching immutable bytes bypass persistence. One catch also calls `failWrite` after possible publication and directory-sync failure. |
| `android/.../transport/TrustedEnrollmentState.kt:78–123` | Recovery exposes readable Prepared/Active records; current availability checks also read storage on proof checks. |

The Android prefix is
`android/app/src/main/java/com/focuswindow/client`. Live coordinator calls to the
new Windows ceremony/store methods were not found; definitions alone do not mean
E3–E6 are integrated. The production service entry remains explicitly refused.

### Verified platform facts and their limits

- Microsoft documents creation-time security, exclusive creation and incompatible
  sharing in [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
  Its caching discussion explicitly includes cached metadata when creating an
  empty file and directs callers to flush it. [File Caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)
  distinguishes cached reads from persisted storage and requires metadata flushing.
- [FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)
  requires `GENERIC_WRITE` and has a checked result. Closing a file is not a barrier.
  In the repository's .NET 8 family, [OSFileStreamStrategy](https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/OSFileStreamStrategy.cs)
  invokes its disk flush only when the stream can write. Adding `Flush(true)` to
  today's read stream is therefore not a fix.
- [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
  describes write-through movement, but its detailed flush guarantee discusses
  copy/delete. This decision avoids needing to resolve that paragraph into a new
  same-volume rename recovery contract; it does not declare all renames unsafe.
- Android's [AtomicFile API](https://developer.android.com/reference/android/util/AtomicFile)
  supplies no locking. The [Android 15 AOSP implementation](https://raw.githubusercontent.com/aosp-mirror/platform_frameworks_base/android-15.0.0_r1/core/java/android/util/AtomicFile.java)
  logs sync/close/rename failures, and `openRead` may rename a backup or delete a
  staging file. A normal `finishWrite` return is not a checked completion result.
- Android [Os](https://developer.android.com/reference/android/system/Os)
  exposes checked file operations. [fsync semantics](https://man7.org/linux/man-pages/man2/fsync.2.html)
  require a separate directory sync for the containing name. The [Linux writeback
  error contract](https://docs.kernel.org/6.4/filesystems/vfs.html#handling-errors-during-writeback)
  allows a later sync to return zero after a prior error was reported. Microsoft
  also documents [lost delayed-write data](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/troubleshoot-event-id-50-error).
  Exact-byte reassertion is this plan's conservative application decision, not a
  claim that either platform automatically heals a failed write.
- Windows [I/O cancellation](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
  is a request and does not wait for completion. A managed timeout or canceled
  caller likewise cannot certify that persistence stopped.

The first Windows contract is **qualified local NTFS, an established private
directory chain, and a storage stack that honors successful write/flush requests**.
Checking persistent ACL capability alone is insufficient. Do not invent a Windows
parent-directory `fsync` sequence, require a volume-wide flush per enrollment, or
claim support for arbitrary remote/cloud filesystems. E2 must establish the fixed
namespace chain through trusted bootstrap; a readable directory is not evidence
of completed bootstrap. Bootstrap/host-key/SCM qualification remains its separate
gate, not something this enrollment helper may infer or repair.

Android uses its app-private internal storage under `noBackupFilesDir`; actual
file and directory sync support remains a device gate. Neither protocol proves
power-loss behavior of dishonest hardware or repairs filesystem corruption.
Known hard write/flush errors quarantine the current attempt/owner; there is no
retry-until-success healing loop. Distinguish an outstanding/unobserved completion
from a terminal reported I/O failure: timeouts retain the operation; a reported
hard error blocks that owner's positive completion/activation until explicit
repair, rather than letting its next sync clear the failure. After process death,
memory of an old error may be lost: a new owner establishes a **new** validated
write-and-barrier completion,
not proof that the earlier attempt succeeded. No extra sidecar can reliably record
a permanent failure promise on the same storage that just failed.

## 3. Windows file custody and completion

Add a small completion capability in `DeskFuse.WindowsSecurity`, used by the
concrete enrollment store. Preserve the existing fixed root, SYSTEM requirement,
exclusive broker-owner lease, retained directory chain, file identity/ACL/reparse
inspection, single unnamed stream requirement, bounded records and no inherited
handles. Do not relax the general read API or change maintenance callers globally.

The capability owns a synchronous read/write file handle, retained parent and
immutable exact-byte snapshot internally. Use zero sharing for these enrollment
handles. Expose typed completion results and snapshots, not a mutable stream or
an arbitrary-data SYSTEM write endpoint. Ordinary readers must not reopen these
files behind the owner. In particular, migrate `Commit`, `StatusFor`, receipt
lookup and `LoadCommitted` to the owner's custody/snapshots.

### New positive artifact

1. Build and validate the complete bounded artifact in memory under its typed
   operation. Evidence is at most 16 KiB; marker/receipt at most 4 KiB. Keep the
   existing wire/transcript limits and canonical verification.
2. Inspect/retain the exact protected parent; create the fixed final name with
   `CREATE_NEW`, private security supplied at creation, `GENERIC_READ |
   GENERIC_WRITE` plus required inspection rights, no inheritance, no delete or
   write sharing. A collision branches to exact-existing recovery, not overwrite.
3. Complete checked writes; drain managed output, verify exact length/bytes through
   the same object, then call checked native `FlushFileBuffers` on that writable
   handle. A read/hash or managed buffer flush is not this barrier.
4. Recheck identity/length and publish completion only for the still-current
   persistence operation. Retain custody while the store relies on that snapshot.

### Existing positive artifact / new owner

1. Reuse retained custody if available. Otherwise open the exact final object with
   `OPEN_EXISTING`, read/write inspection rights, zero sharing, no truncation or
   creation. Apply all protected-read inspections to this same handle before use.
2. Bound and fully validate existing bytes, canonical encoding, signatures and
   applicable host/namespace/enrollment/record bindings. Where a live operation
   supplies expected bytes, require exact equality. Save the exact byte sequence;
   do not parse and reserialize it as the write source.
3. If this owner already recorded successful completion for that same held object
   and byte sequence, reuse it. Otherwise reassert every byte at fixed offsets,
   preserving identity and length; check complete writes, verify, then perform
   the explicit file barrier. No `SetLength`, append, replace or new signature.
4. Return a completed snapshot only after the final state/revision/denial check.
   Torn, foreign, oversized or otherwise invalid positive files remain quarantined;
   recovery does not reconstruct them from other records.

A cold transition from old read custody must first withdraw the affected cached
admission, exclusively serialize the transition, close the owner's old read
handles and acquire validated write custody. Failure leaves it unavailable. Do
not add `FILE_SHARE_WRITE` merely to make a second open succeed. This source is
not active production authority, so do not build a hot upgrade framework for it.

Tombstones use a different completion operation: a validated protected regular
file at the fixed enrollment revoke name immediately sets denial, even if empty
or torn. Complete its file/metadata barrier under write custody; its payload need
not become valid JSON. Preserve contents instead of rewriting negative evidence
into a positive canonical record. Invalid object identity, inaccessible storage
or unsupported file shape leaves denial/quarantine and unconfirmed persistence.

Do not repurpose `CopyProtectedFile` globally. Leave other subsystems under their
existing contracts. Enrollment stops calling `PublishProtectedFile`; preserve
old `pending-*.bin` residue. Startup never promotes such anonymous staging files
to a commit/receipt. Ambiguous residue affecting an attempted recovery stays
quarantined for explicit reconciliation, rather than authorizing a new signature.

## 4. Store states, startup, acknowledgements and receipts

Keep four independent facts: (a) live admission eligibility/denial, (b) artifact
persistence completion, (c) outstanding operation terminal/in-flight, and (d)
native-resource rundown/isolation. Do not encode all four in `revoked` or one
quarantine bit. A storage completion can resolve its own uncertainty, but cannot
erase a deny fence, shutdown state or native-owner quarantine.

Use concrete per-enrollment state and an operation identity tied to the immutable
enrollment/ticket/evidence hash and owner incarnation. Keep a monotonic local
revision/deny fence. Exact retries join/reuse that operation and its frozen bytes;
they do not race another writer or signature. Bound recovery enumeration to the
existing registry limit. Inspect fixed-name negative artifacts before positive
activation; include tombstone-only enrollments in negative recovery.

| Observed state | Permitted result |
|---|---|
| Evidence only; no complete final marker | Inactive. Startup never creates a marker. Uncertain historical admission gives `Unknown`, not invented `Aborted`. |
| Complete matching evidence and existing final marker | Validate both first, complete evidence before marker, then publish an immutable active snapshot if no denial/shutdown applies. No workers/grants are restored. |
| Torn/foreign evidence or marker | Inactive/quarantined; no automatic reconstruction, deletion, truncation or new ceremony identity hidden inside retry. |
| Tombstone present, persistence not confirmed | Immediately denied. Signed status is `Unknown`; UI may explain denial with persistence unconfirmed. |
| Tombstone completed under this owner | Denied; signed `Revoked` is permitted. This is not a native-rundown acknowledgement. |
| Valid existing receipt | Validate against complete commit, complete its exact bytes, return that envelope. No new signature. |
| Torn/foreign receipt | Quarantine receipt recovery; do not revoke a separately proven commit by inference, and do not invent a new receipt signature. |
| No receipt, no prior unresolved publication and completed commit | One retained operation may sign once, freeze the result, create/complete it and only then return it. |

`Stage` completes prepared evidence before returning. That does not admit commit.
`Commit` accepts only the closed `VerifiedEnrollmentCommit` from the same live
ceremony, verifies staged equality, and creates its marker only after
`CommitAdmitted`. If completion becomes uncertain, retain evidence and deny
activation; do not claim the disk was rolled back. Startup may recover a complete
valid final marker because marker creation was restricted to an admitted
operation, even if the previous reply was lost.

Keep active enrollment and receipt completion separate. If the commit is complete
but receipt publication is uncertain, retain the commit, Prepared/key and exact
receipt attempt; status/receipt recovery must not claim successful receipt
delivery. A valid receipt is historical commit evidence, never current resource
authority or proof of nonrevocation.

Reserve `Committed` for a complete current commit with no denial and `Revoked`
for a completed tombstone. Use existing `Unknown` for persistence uncertainty or
unresolved quarantine; `Pending` names an actual live pending ceremony. Sign
typed status only after authenticating the request/context with the pending key.
Build/sign outside the short gate, then recheck revision/denial at response
admission. A raced receipt/status snapshot cannot override a newer denial; do
not present historical receipt recovery as a new `Committed` acknowledgement.
As with any response, a revoke can occur after response admission.

`IBrokerEnrollments.Revoke` may remain `void`: a **normal return means completed
durable tombstone**, while an exception means denial remains active but persistence
is unconfirmed. Retrying against an existing tombstone must complete it, not merely
find it. Cached completion under the same retained owner can avoid redundant I/O.

If no tombstone reached storage before a failed revoke/process loss, this protocol
cannot promise that the denial survives restart. It must never acknowledge that
failure as durable revocation or allow replacement to proceed. Android preserves
its own local denial and pending key; a historical commit receipt cannot heal or
override either side's denial.

## 5. Admission, cancellation, shutdown and replacement

Keep the existing `CommitAdmitted` authorization point. Stage I/O and signature
work are outside the ceremony/authority gate. Immediately before admission,
recheck the exact approvals, transcript, UI/logon facts, revision and deadline.
Loss before admission aborts. Loss after admission does not retroactively undo
consent or cancel the authorized persistent write; interactive resource admission
still closes under the existing logon policy.

Use one cold persistence owner for these small setup operations. Synchronous
writes/flushes can execute there without making the UI or broker control loop
wait under a short state gate. The operation retains buffers, handles and owner
lease until the native call is terminal. A caller timeout/disconnect changes
what can be reported; it does not free this owner or start an overlapping writer.
Do not use `Task.WhenAny`/cancellation as a fictitious I/O completion barrier.

After admission, explicit authenticated Cancel sets live denial and a newer deny
fence immediately, then serializes tombstone completion behind any outstanding
write. A commit that finishes late can be recorded as historical committed
evidence; it cannot publish live authority, clear denial/quarantine or make a
replacement active. If no marker write began, cancellation need not manufacture
one. In either case, complete revocation before acknowledging canceled authority.

Complete the ceremony once, from the actual terminal I/O result. In particular,
do not call `CompleteCommit(false)` for a timeout and then attempt a second
completion when the same write returns. Refine the bool completion API into a
small typed result if needed; no general transaction framework is required.
Current service/persistence owner and denial revision matter at final publication;
the vanished UI is not asked to reapprove an already admitted operation.

Shutdown first closes new admission and marks shutdown under its short gate.
Then drain/join the cold owner outside that gate, retain exact native-resource
custody/rundown, and only afterward release record handles and the owner lease.
If I/O is stuck, remain unavailable with retained custody; do not start another
service owner or free objects still used by native calls. Process death releases
OS handles; its successor performs the startup protocol, not in-memory replay.

For Replace, use this order:

1. Finish new frozen consent and complete its staged evidence. Retain the old
   protected peer association; labels/preferences supply no authority.
2. Close old live admission, complete the old tombstone, and obtain its durable
   denial result. A failed barrier blocks replacement.
3. Complete the old owner's existing native rundown/isolation barrier. Retained
   quarantine with possible native work is not success and blocks replacement.
4. Recheck the new ceremony's current approvals/UI/logon/revision/deadline and
   enter its `CommitAdmitted`. Never restore the durably revoked old key if this
   check fails; a short availability gap is accepted.
5. Complete the new marker, publish its eligible snapshot, then complete/return
   its receipt. No late completion from the old operation can activate it.

Do not confuse `BrokerAuthorityState` removing registrations with joined native
work, nor a `Quarantined` enum assignment with established isolation. The native
audio handoff's retained process/job, PCM revoke and capture rundown contracts
still determine that barrier.

## 6. Android immutable-record correction

Keep one main-process cold writer and existing host/enrollment-scoped Keystore
identities. Replace only `AndroidTrustedEnrollmentStore`'s immutable enrollment
file publication; no general Android storage rewrite or new multiprocess writer.
Use platform `Os` APIs already available below the repository's minimum API 30.

1. Resolve only the existing app-private `noBackupFilesDir` root. Create the one
   fixed `trusted-enrollments` child with private permissions if absent. Retain
   and validate parent/root directory descriptors; sync the child and then its
   parent before treating root creation as complete. Repeat root completion on
   a new owner; `mkdirs()` success alone does not certify it.
2. Use the existing hashed host/enrollment names and typed suffix allowlist.
   Create a final regular file with `O_CREAT | O_EXCL | O_RDWR | O_CLOEXEC |
   O_NOFOLLOW`, mode `0600`; no truncation, append or replace. Retain its descriptor
   and validate type, identity, private ownership/permissions and exact bounds.
   Keep all path resolution in the validated app-private parent, with no caller
   supplied paths. An existing-file branch uses `O_RDWR | O_CLOEXEC | O_NOFOLLOW`,
   without create, exclusive-create, truncate or append flags.
3. Write with checked bounded loops, verify exact bytes/length, call checked
   `Os.fsync(file)`, then `Os.fsync(rootDirectory)`. On a new/unconfirmed owner,
   fully validate existing positive bytes before reasserting their exact stored
   sequence through that descriptor; then use the same barriers. Handle short
   writes/EINTR without treating a hard error as a retryable success. Never
   truncate or resize an existing record. Publish completion only afterward.
4. A revoke filename's presence denies before parsing content, including zero
   bytes. A nonregular object or inspection/I/O error is also a denial/quarantine,
   not absence. Complete its file and directory barriers separately from live
   denial. No successful `remove` result until those barriers succeed.
5. Distinguish missing (`ENOENT`) from denial/I/O failure. Preserve records and
   keys on all uncertain outcomes. A failure after name visibility or sync cannot
   mean rollback succeeded; there is no `failWrite`/delete cleanup in this path.
6. Preserve/quarantine legacy `.new`/`.bak` residue per enrollment. Do not call
   `AtomicFile.openRead` to mutate it, silently adopt it, or delete it as cleanup.
   Revocation residue also denies. A clean fully valid legacy final file may
   undergo the new completion protocol; ambiguous positive residue requires
   explicit recovery, not reconstructed consent or a new signature.

The repository must separate raw bounded bytes from completed capabilities.
`prepare()` and recovered `prepared()` return a usable Prepared capability only
after completion; final phone approval follows durable Prepared plus a fresh
foreground consent/attempt check. Do not infer new user consent from a saved
frozen transcript. Prepared still grants no operational proof signing.

`activate()` validates the exact host receipt and completes Active storage before
returning. `committed()` on a new owner completes validated Prepared first, then
the existing valid Active receipt; it never synthesizes Active from Prepared or
trusts a lone receipt without the required local binding/key. Missing/damaged
positive state remains unavailable with recovery material preserved.

After recovery, return immutable cached snapshots plus a shared current-owner/
local-denial revision. `active.requireCurrent()` checks memory, not disk on every
proof. Denial is published before remove I/O and survives its failure within the
owner. A new owner rechecks negative artifacts before publishing capabilities.
Coroutine/UI cancellation does not release writer serialization while sync calls
are still running. Keep all persistence and Keystore work off AAudio/codec owners.

## 7. Ordered High implementation and focused verification

| Increment | Work and exit condition |
|---|---|
| D1 — Windows completion custody | Add narrow born-private direct creation and validated existing writable custody. Checked exact writes/file barrier, immutable snapshots, no sharing relaxation; qualified local-NTFS prerequisite explicit. Preserve other storage callers. |
| D2 — Windows recovery/store | Migrate Stage/Commit/LoadCommitted/GetReceipt/Revoke and competing reads. Separate deny/completion/quarantine causes. Complete existing valid artifacts in order; missing/damaged marker is never fabricated. Exact receipt has no extra signing call. |
| D3 — operation/coordinator lifetime | Integrate retained operation identity, real terminal completion, response revision checks and shutdown. Apply cancel-after-admission and revoke-old/rundown-before-new order. Store helper never clears native quarantine. |
| D4 — Android completion/recovery | Direct private immutable files, exact-byte reassertion, checked file/directory/root barriers, residue preservation and negative presence semantics. Prepared/Active capabilities require completion; proof-current checks use snapshots. |
| D5 — close the E5 integration gap | Wire the concrete protected setup coordinator and signed status/receipt recovery using these results. Recheck unfinished E2–E4 integration against their handoff; component files/builds do not imply completion. |
| D6 — return to E6/N1–N6 | Continue v2 subject/challenge binding and fixed native audio owner programme under its existing order. Keep production authority blocked until the full launch/control/rundown chain and required qualification gates are complete. |

Use existing fixtures and small fault seams around the changed I/O owner. Prioritize
these combined behaviors rather than a new framework or broad test-count campaign:

- New and existing evidence/marker/receipt interrupted before write, during short
  write, before/after barrier and before acknowledgement; retry and fresh-owner
  startup. Assert full write/barrier order, not merely a matching readable hash.
- Startup never creates a missing marker or promotes staging residue. Reassertion
  receives only fully validated exact bytes; an interruption leaves torn state
  inactive. Test one reported hard writeback error followed by sync success:
  current owner remains quarantined, and fresh recovery is a new full assertion.
- Empty/torn tombstone denies immediately; failed completion gives unconfirmed
  status, completed tombstone permits `Revoked`; failed revoke cannot activate
  replacement. Test native rundown independently blocking replacement.
- Valid receipt is returned byte-for-byte without a second signing call; torn or
  foreign receipt is preserved/quarantined. A newer deny beats delayed status,
  commit, receipt and cache publication.
- Cancel on each side of `CommitAdmitted`; timeout with actual I/O still running;
  shutdown/dispose during completion; late success never frees the lease early,
  clears denial or resurrects authority.
- Android file/parent/root sync order, existing-byte recovery, legacy residue,
  consent loss during persistence and Prepared/Active recovery. No raw-read
  capability or runtime storage check bypasses the completed snapshot.

Run affected Windows/Android builds and focused existing offline checks during
High implementation, once each changed slice is ready. No such checks ran in
this planning pass. Offline fault injection proves ordering/state behavior, not
actual physical durability, SYSTEM isolation or device performance.

Installed SYSTEM private creation, retained-handle operations under actual ACLs,
root/host-key/SCM protection, and real crash/power-loss/device qualification remain
separately scoped gates. Missing prerequisites keep the gate open; they do not
authorize enabling authority, weakening private creation, or falling back to v1.

All new work remains setup/control work. A snapshot hit performs no new disk read,
hash, signature or broker round trip; no media forwarding stage, per-sample gate
or alteration to source/delivery/QUIC ownership is introduced. The additional
recovery write is bounded and cold, never part of renew's normal media path.

Rollback disables the new setup/capability, retains all uncertain records/keys and
performs confirmed owned rundown. It never deletes a tombstone, reconstructs
positive authority, returns to legacy trust or adopts old endpoints.

**High stopping rule:** implement these choices. Stop for a failed required
private-creation/persistence boundary, a source contradiction or a new ownership/
security tradeoff. Do not reinterpret a failed flush, readable file, timeout or
quarantine as success. The focused design is complete; this planning pass ends
without implementation.

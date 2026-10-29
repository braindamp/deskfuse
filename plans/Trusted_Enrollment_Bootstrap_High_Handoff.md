# Trusted enrollment bootstrap — settled design and High implementation plan

**Focused E5 durability resolution, 29 October 2026:**
[Enrollment_Durable_Completion_High_Handoff.md](Enrollment_Durable_Completion_High_Handoff.md)
settles direct private publication, exact-byte retry/startup completion, retained
write custody, revocation acknowledgement, cancellation/replacement ordering and
the Android persistence correction. Implement it before live E5 integration;
this original document remains the enrollment/consent contract. Production
authority is still blocked and E1–E6 are not declared complete.

**29 October 2026. Planning only.** No application/source implementation, build,
test, installation, key creation, service configuration or hardware operation was
performed for this document. It resolves `Trusted_Setup_Ultra_Decision.md` and
refines items 2–5 of `Ultra_Core_High_Handoff.md`. Architecture v0.6 and the
completed media work remain in force.

Reference: `experiment/vnext-audio-latency`, HEAD
`2f181cf2da9c4efdde3013b115108ecac650d1ab` **plus the existing uncommitted source**.
Preserve that work. This document is the execution contract for the bootstrap;
High should implement it rather than select another pairing protocol.

**Focused E6 ownership resolution, 29 October:**
[Native_Audio_Owner_Boundary_High_Handoff.md](Native_Audio_Owner_Boundary_High_Handoff.md)
selects the actual Rust worker, in-process WASAPI fallback, source/delivery lease
relationship, native TLS identity and cutoff/renewal contracts. Read it with this
document before resuming E2 packaging or E6 integration. The E1 protocol slice and
part of E2 are implemented; this resolution does not mark E3–E6 complete.

## 1. Decisions and security boundary

| Decision | Selected contract |
|---|---|
| PC authority identity | A new protected P-256 signing key, independent of legacy transport ECDH/pairing secrets. Host identity is SHA-256 of its canonical public SPKI. |
| Phone identity | Retain the existing separate host/enrollment-scoped Android Keystore P-256 signer. Enrollment adds narrowly typed proposal/approval signing; it never exposes this key to TLS. |
| Network handling | A contained medium-integrity setup worker owns TLS and network framing. SYSTEM handles bounded typed local records, state and signatures. |
| Human confirmation | One frozen transcript. The genuine PC setup UI displays its full SHA-256 digest as QR, or 52 grouped Crockford characters for camera-free entry on Android. Both approvals bind that exact transcript. |
| Windows consent | A distinct protected high-integrity Setup UI, entered through the installed signed UAC launcher. Keep the existing native/network worker medium-only checks intact. |
| Account scope | Enroll for the intended Windows subject user SID; record an approving administrator separately. A pairing is not machine-wide authorization for every Windows user. |
| Activation | Broker durable commit is authoritative; Android activates on its signed receipt. Proposed/prepared records confer no operational authority. |
| Runtime proof | Version the signed context to include host identity, authority namespace and enrollment ID, and enforce the enrolled subject SID. Authenticate issued challenges to the phone. |
| Performance | Everything here is setup or acquire/renew/revoke control work. No frame/sample signature, media forwarding process or additional payload-stage IPC. |

The supported premise remains an intact OS, trusted installer/approved installed
code, and genuine PC/Android setup interfaces operated by the user. Ordinary
same-user Windows code must not inject approval, replace records/keys or supply
privileged caller facts. Administrator/kernel or authorized-endpoint compromise
is not solved by this ceremony.

**Do not claim an unspoofable Windows desktop.** High integrity/UIPI helps prevent
ordinary lower-integrity input injection; it does not make displayed codes secret
or defeat all overlays, phishing or compromised UIAccess software. The user must
operate the genuine installed setup UI and perform the cross-device check.
Use no screenshot secrecy assumption and no medium-process `approved=true`.
[SendInput/UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput),
[mandatory integrity control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control).

## 2. Identity definitions

- `AuthorityNamespace`: the existing protected BrokerAuthority namespace UUID.
- `HostSigningSpki`: canonical uncompressed P-256 DER SPKI, exactly 91 bytes.
  `HostIdentitySha256 = SHA256(HostSigningSpki)`, exactly 32 bytes. It identifies
  this installed authority, not its hostname, IP address or legacy host secret.
- `PeerIdentity`: service-generated random 32-byte stable product-device identity
  within the protected authority. It is not a key fingerprint or a claimed phone
  label. A trusted replacement may retain it; a new pairing receives a new one.
- `EnrollmentId`: fresh nonzero UUID for each explicitly enrolled signing key
  and consent set. `PhoneSigningSpkiSha256` is independently derived from the key.
- `SubjectUserSid`: stable binary Windows user SID permitted to own the resources.
  Current logon SID/LUID/session are fresh execution bindings, not permanent IDs.
- `ApproverUserSid`: actual approving elevated administrator; it can differ from
  the subject during over-the-shoulder UAC. SYSTEM remains the protected writer.
- `TicketId`, `SetupBootNonce`, `HostNonce`, `PhoneNonce`, `FreezeNonce`: fresh
  per-ceremony/session values. UUIDs use 16 network-order bytes; nonces are 32 bytes.

Legacy phone IDs/labels can be explicit preference/data migration links. They
cannot nominate these protected identities or prove that a replacement key belongs
to a previously enrolled phone. Never derive `PeerIdentity` from an attacker-
editable label, a path or the old pairing secret.

## 3. Trusted installation and host-key bootstrap

Use the two existing fixed SYSTEM-private Maintenance/BrokerAuthority roots.
Extend the authority profile with fixed `bootstrap` and `ceremonies` directories
through its private-at-creation APIs. Do not add arbitrary privileged roots.

Cold trusted installer order:

1. Verify the approved installed code/dependency bundle and exact intended
   deployment. Populate a **new** fixed RX deployment directory below the trusted
   Program Files anchor. Public code is SYSTEM/admin writable and ordinary-user
   read/execute; no per-logon ACL rewrites. Secrets/approvals/journals stay outside
   that RX directory. Retain existing `IInstalledBrokerWorkerImage` custody rules.
2. Create fresh private roots, or open exact independently configured namespace
   IDs for an authorized schema upgrade. Existing exposed directories/services
   are not adopted or tightened into trust. Preserve failed reservations.
3. Reserve the host-key creation intent in the authority root. Generate P-256
   using Microsoft Software KSP, machine scope, signing-only usage, no private
   export and a SYSTEM-only owner/DACL. Key name:
   `DeskFuse.Authority.<AuthorityNamespace:N>.Host.v1`. No overwrite flag,
   provider fallback, imported legacy private key or mandatory TPM dependency.
4. Qualify private creation, set required properties before publication, and
   verify actual key security/usage/export policy and canonical public key.
   Write/flush an immutable `HostIdentityRecord` containing namespace, exact
   provider/key name, SPKI/hash, policy version and deployment identity.
5. Persist the setup/configuration records, then publish the exact SCM-controlled
   service startup configuration with nonsecret expected Maintenance/Authority
   namespace IDs and deployment ID. Service startup opens those expected IDs;
   it never discovers a directory and declares it trusted.

CNG key creation/property work is not performed in ServiceMain/StartService;
Microsoft warns of deadlock there. Only the service keeps the opened signing
handle. UI/transport workers receive public data. The signer exposes typed
`SignOffer`, `SignFrozenTranscript`, `SignCommitReceipt`, `SignStatus` and
`SignIssuedChallenge` operations constructed from its own current records—never
an arbitrary signing oracle. Cache an issued signature for exact retransmission.
[CNG creation](https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptcreatepersistedkey),
[key usage/export/security properties](https://learn.microsoft.com/en-us/windows/win32/seccng/key-storage-property-identifiers).

Exact Software KSP private-creation behavior is a qualification gate. If the
provider cannot establish the required protection at creation, stop that path;
do not create exposed key material and tighten it afterward. A pre-existing key
without the matching complete creation record is not adopted. Missing key,
partial bootstrap, unexpected provider/ACL/SPKI or configuration mismatch leaves
authority unavailable for explicit repair. Software updates retain the identity;
losing/replacing it requires explicit trust upgrade and new enrollments.

## 4. Component ownership and entrypoints

| Component | Authority and permitted work |
|---|---|
| Trusted installer | Approved deployment, private roots, host key and SCM publication; no phone/network-supplied approvals. |
| Signed UAC launcher | Operator-initiated `BeginSetup` only. No silent approval, public-key import, arbitrary executable/path/PID or external command arguments. |
| SYSTEM setup coordinator | One active ceremony globally, observed caller/account facts, ticket/freeze/approval/commit/revoke state, key signatures and private records. No socket/TLS parser. |
| Protected high-integrity Setup UI | Displays coordinator-owned subject/scopes/transcript; sends deliberate Confirm/Cancel for that digest through its retained private channel. No authority/key storage. |
| Protected medium enrollment worker | Temporary TLS key/certificate, sockets, bounded canonical framing, forwarding signed records; no enrollment-store write or human-confirm capability. |
| Android enrollment coordinator | User-initiated pending attempt, independent key, transcript check, consent, Prepared/Active state and recovery. |

The service activation pipe captures the actual UAC launcher's retained process,
creation time, approved installed image/dependencies, primary elevated high token,
enabled administrator identity and session. It then creates the **fixed Setup UI**
suspended under that approved elevated token with SYSTEM-owned process/thread/job
security and sanitized environment, claims its private control pipe, validates
facts, and resumes. This is a separate cold profile; do not weaken
`BrokerWorkerLauncher.ReadLogon` or `BrokerCallerBinding`'s medium-only rules.

Bind UI confirmation to that exact retained UI process and channel incarnation.
Its pipe grants the approver logon the necessary explicit client mask, excluding
server-instance creation. The medium worker pipe instead binds the subject's
medium logon. Reuse the existing local-only/first-instance/disconnect-terminal
patterns and server descriptor/process validation. Losing established control
requires fresh launch; a registration UUID is never a bearer credential.

Derive the **subject** from SYSTEM-observed WTS/token facts for the intended
interactive session, and the **approver** from the actual elevated token. Show
both if different. Neither comes from a network SID/PID string. Lock, logoff,
switch or changed incarnation closes an unfinished ceremony. A standard user
can be enrolled with another administrator's approval without granting that
administrator's SID as the resource-owning subject.

## 5. One frozen-transcript ceremony

### 5.1 Transport and initial quarantine

The user starts trusted setup on the PC and chooses Pair/Replace plus the subject
and maximum scopes. The service creates a ticket, new enrollment ID and (for New)
peer ID, with a five-minute monotonic deadline and fresh boot/host nonces. Start
one contained medium worker; its ephemeral TLS leaf/key stay in that process.
The coordinator signs an offer binding its leaf-certificate hash and worker
registration to this ticket/host/subject/allowed scopes.

Discovery is a locator only. Reuse existing discovery/picker affordances; it
advertises no pairing secret, enrolled-device list or capability. The phone's
explicit Pair action opens a **quarantined enrollment connection**, not an
ordinary media/session connection.

Use platform TLS with TLS 1.2 ECDHE+AEAD on the Windows 10 reference profile;
allow TLS 1.3 where supported. Reject obsolete/static-key/CBC negotiation; do
not change global Schannel policy. Windows 10 Schannel does not support TLS 1.3.
[Microsoft support table](https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-).

Before first trust, the dedicated per-connection verifier may accept only a
bounded canonical temporary self-signed P-256 leaf as an **untrusted candidate**.
The received signed offer must match the actual TLS leaf hash and offered root
SPKI/hash. Its root is still provisional until the cross-device check. Never
install a CA, use a global trust-all client, or pass this connection to normal
resource APIs. Send only public enrollment fields/PoP; no reusable credentials,
native session secrets, files, clipboard or media. Certificate/signing keys for
TLS are distinct from phone/broker identity keys. No TLS exporter is required.

### 5.2 Proposal, freeze and cross-device check

1. Android fixes the offered ticket/root for this attempt, creates the independent
   host/enrollment-scoped signing key through `prepareEnrollment`, and contributes
   a fresh phone nonce and a locally selected subset of the offered scopes.
   It signs a narrowly parsed enrollment proposal proving possession.
2. The coordinator validates proposal fields/PoP outside its short gate, rechecks
   current worker/ticket/UI/logon, and freezes **one exact candidate**. Generate a
   fresh `FreezeNonce` only after selection. Issue one root-signed frozen transcript
   `T`. A changed proposal after selection aborts; exact retries return the same
   result. No parallel candidates, hidden reselection or key/nonce grinding loop.
3. The genuine PC Setup UI displays the selected phone hint, intended Windows
   user, permissions and `H = SHA256(T)`. QR payload is exactly
   `DESKFUSE-ENROLL-VERIFY-V1:<ticket-N-hex>:<base64url(H), no padding>`.
   It contains no secret or bearer authorization.
4. The phone checks the signed frozen transcript against its own key/nonce,
   provisional host/namespace/ticket, subject and selected scopes. The user scans
   the PC verification QR. Camera-free mode displays **all 256 digest bits** as
   52 Crockford Base32 characters, grouped as 13 groups of four, for entry on
   Android from the genuine PC UI. Compare all decoded bytes in constant time.
   An untrusted legacy clipboard/host cannot supply this independent check.
5. After that match, Android shows the verified PC/account/permissions and requires
   explicit foreground consent. Persist Prepared before producing the signed
   final PhoneApproval. The PC operator separately confirms the same frozen
   digest in the retained Setup UI, checking the selected phone's verification
   state. Neither a network statement nor receipt of PhoneApproval is PC consent.
6. The coordinator accepts only these two approvals for the same `H`, current
   ticket/revision/UI/logon and deadline, then performs the durable commit below.
   Only the signed committed receipt activates the Android enrollment.

The full digest avoids a new SAS/PAKE protocol and weaker short-hash collision
claims. Camera-free entry is deliberately a one-time strong check; QR is the
quick path. Do not silently substitute the old six-digit code, truncate to 26
characters or automatically confirm on a matching phone name. A later shorter
code is a separately reviewed convenience protocol, not part of this work.

## 6. Exact protocol/record contracts

### 6.1 Encoding

Use existing P-256/SHA-256 and canonical DER signatures (8–72 bytes), canonical
91-byte SPKIs, big-endian integers and RFC 4122/network-order UUIDs. Domains are
ASCII plus a terminating zero. All fields have fixed lengths or explicit bounded
lengths; reject unknown enums, duplicates, trailing bytes and alternate encodings.
No JSON reserialization serves as a cryptographic transcript.

Setup frames are `u32 length | u8 kind | canonical body`, at most 8 KiB, over the
dedicated TLS/local typed channels. `T` is at most 2 KiB. SID fields are
`u8 length | binary SID` (user 8–68 bytes; logon SID exactly the existing valid
20-byte form). Scopes are `u8 count | ascending unique u8 enums`, count 1–8.
Names are bounded display hints, never identifiers; quote/sanitize control and
bidirectional text in UI. Do not fetch URLs or execute QR content.

Base32 alphabet: `0123456789ABCDEFGHJKMNPQRSTVWXYZ`, MSB first, four zero padding
bits at the end. Input accepts ASCII case and formatting spaces/hyphens only;
requires exactly 52 symbols and zero padding. Shared vectors: 32 zero bytes →
52 `0`s; 32 `FF` bytes → 51 `Z`s followed by `G`. Encode/decode identity is not
permission; only comparison with this attempt's `H` verifies the UI code.

### 6.2 Canonical enrollment values, in order

`Offer`, domain `DeskFuse.Enrollment.Offer.v1\0`:

1. Host SPKI(91), authority namespace(16), ticket(16), enrollment(16), peer(32).
2. Action byte New/Replace; old enrollment(16, zero only for New).
3. Setup boot nonce(32), host nonce(32), enrollment-worker registration(16),
   exact temporary TLS leaf DER SHA-256(32).
4. Subject user SID, current subject logon SID, LUID(u64), session(u32).
5. Approver user SID; approved Setup UI registration(16), PID(u32), creation
   FILETIME(u64); offered scope list; expiry in broker monotonic ms(u64).

The root signs these exact Offer bytes. The expiry is opaque on Android; only
the broker decides host-clock expiry. Android also has a bounded local UI timeout.

`Proposal`, domain `DeskFuse.Enrollment.Proposal.v1\0`:
`SHA256(Offer) | phone SPKI(91) | phone nonce(32) | selected scopes`.
The phone signs these exact bytes with its pending enrollment key. The coordinator
must verify that selected scopes are a subset and every offer fact is still current.

`T`, domain `DeskFuse.Enrollment.Transcript.v1\0`:
`u16 Offer length | Offer | u16 Proposal length | Proposal | FreezeNonce(32)`.
All embedded domains/lengths are parsed and exact; proposal's Offer hash must
match. Sign/verify `DeskFuse.Enrollment.Frozen.v1\0 | H` with the host root.
The UI receives only coordinator-owned `T/H`, never a worker-supplied digest.

`PhoneApproval`, domain `DeskFuse.Enrollment.PhoneApproval.v1\0`:
`HostIdentitySha256 | authority namespace | ticket | enrollment | H`.
The phone signs only after the OOB match, explicit consent and durable Prepared
state. It does not expose a generic arbitrary-data signer. The PC's local
confirmation references the same values, accepted only from the retained UI.

`CommitReceipt`, domain `DeskFuse.Enrollment.Committed.v1\0`:
`HostIdentitySha256 | namespace | ticket | enrollment | peer | subject user SID |
selected scopes | phone SPKI SHA256 | H | immutable record SHA256`.
The root signs only after a valid durable commit. The protected record contains
the complete bounded `T`, required signatures, observed UI confirmation identity
and writer=SYSTEM, not just a caller-provided approved hash.

`Status`, domain `DeskFuse.Enrollment.Status.v1\0`, binds host/namespace,
ticket/enrollment/H, fresh phone status nonce and a typed outcome
Committed/Pending/Aborted/Unknown/Revoked. Authentication requires PoP from the
existing pending key. A signed old commit receipt is historical evidence, not
a bearer grant or proof that revocation has not occurred.

## 7. Durable state, cancellation and recovery

Windows states:
`Offered → CandidateFrozen → ApprovalsComplete → CommitAdmitted → Committed`;
pre-commit terminal outcomes are Aborted/Expired. Uncertain storage/native
completion is Quarantined, not success. Pending in-memory tickets abort on
service restart; committed enrollments do not.

- Phone prepares its key and immutable local Prepared record (root, namespace,
  identities, subject, scopes, `T/H`, key fingerprint) before final approval.
  Prepared never permits `signExisting` for operational proofs.
- The broker writes/flushes the immutable prepared record and complete evidence.
  Under a short admission gate it rechecks approvals, deadline, UI/logon and
  revision, consumes them and enters CommitAdmitted. Serialize its final commit
  and cancellation/revocation using the existing cold store owner; do not put
  file flush or signature verification inside a media/resource hot lock.
- Publish a separate immutable, validated commit marker binding the record/H.
  `ReadActive` requires that marker, complete matching record and no revoke
  marker. Files existing alone, partial JSON, an arbitrary digest or a receipt
  created before durable commit are insufficient.
- CommitAdmitted is the authorization linearization point. UI/logon loss before
  it aborts. After it, loss does not undo already-approved durable enrollment;
  interactive resource admission still closes. Explicit cancellation after this
  point is revocation, not an assumption that no commit happened.
- The service returns a signed receipt after commit. Android validates it against
  Prepared and its existing key, durably records Active, then enables runtime
  proof signing. One Android enrollment coordinator writes the private store;
  other product processes read immutable validated records/snapshots. No
  multi-process writable SharedPreferences or per-sample Binder/storage access.
- Lost receipt: keep Prepared/key, authenticate the pinned root and a fresh
  status response, prove the same key, and recover the exact stored receipt.
  No new key/scopes/identity or restored worker registration enters repair.
- Missing/torn commit is inactive/quarantined. Do not report Aborted/NotCommitted
  while an admitted filesystem write can still finish. Unknown completion is
  retained for explicit recovery. An observed revoke marker denies even if torn;
  storage failure closes live authority and reports persistence unconfirmed,
  rather than promising durable revocation that never reached storage.

Phone key loss/changed key, host-key loss, new authority namespace, changed subject
or added scopes requires a new explicit ceremony/enrollment identity. Preserve a
possibly committed Prepared key until status/revocation resolves it; no automatic
cleanup of an uncertain committed identity. Before final phone approval, canceled
pending keys can be deleted safely. Bound phone pending attempts to explicit user
gestures; discovery cannot fill the Keystore with identities.

For Replace, select the old protected peer/enrollment locally in trusted setup.
Complete new consent, durably revoke the old enrollment, close/join or quarantine
its grants, then activate the new one. A short availability gap is preferable to
a new multi-record atomic pointer framework. Failure never restores a durably
revoked key. Preserve preferences/files; existing PnP instances and Windows routes
are not adopted or changed by enrollment.

## 8. Record v2, runtime proof v2 and activation boundary

`BrokerEnrollmentRecord v2` adds the authorized subject SID, key fingerprint,
complete evidence/transaction binding, approver identity and validated commit
reference. Replace the raw `approvedTranscriptSha256` entrypoint with a closed
`VerifiedEnrollmentCommit` minted only by the coordinator. Keep SYSTEM as writer.

At launch, registration, authority-facts lookup and generation transition, compare
the actual worker user SID with the enrollment subject; keep today's fresh
logon/session/process/owner-generation checks. Existing source checks scopes but
has no enrolled subject field: fix this explicitly, not by name matching.

Select `DeskFuse.BrokerProof.v2\0`. Keep the current layout, adding after nonce:
`HostIdentitySha256(32) | authority namespace(16) | enrollment ID(16)`.
Then retain peer/resource, operation/scope, registration, user/logon SIDs,
LUID/session/PID/creation, generation and expiry. User SID must equal the enrolled
subject; do not duplicate it with a second contradictory field. Set a 384-byte
maximum and parse exact field lengths; do not copy v1's approximate minimum bound.

The service authenticates each issued v2 challenge with
`SignHost(DeskFuse.BrokerChallenge.v1\0 | SHA256(v2 transcript))`.
Android verifies that envelope with the committed root, then compares host,
namespace, enrollment, subject, peer, resource, operation/scope and generation
against committed enrollment and the current authorized task **before** signing.
Extend `BrokerProofApproval` and its parser accordingly. Alias scoping alone does
not bind those fields into signed bytes. This is context hardening, not a claim
that v1's unpredictable one-use challenges had a demonstrated replay bypass.

Keep verification outside the short authority gate and the final current-state/
revision check at dispatch. Reuse the signature for exact challenge retries.
These signatures happen only on acquire/renew/supersede control operations; no
media payload gets a new signature or SYSTEM round trip.

Production requires committed Record v2, Proof v2 and the complete trusted launch/
caller/native-owner chain together. Legacy records are migration data, not trusted
v2 records. No v1 downgrade on mismatch. Enrollment does not authorize arbitrary
worker command lines, DLL paths, media destinations or resource parameters: the
existing typed resource/generation/installed-profile bindings must cover them.
It does not authenticate old transport keys merely by copying their metadata.

## 9. Ordered High programme and minimal qualification

| Increment | Concrete work / source anchors | Exit and rollback |
|---|---|---|
| E1: canonical contracts | Shared bounded Offer/Proposal/T/receipt/status encodings; extend `BrokerDeviceSigner` with typed enrollment signing; Record v2/Proof v2 vectors. | Cross-language byte agreement, substitutions/unknown fields rejected. No live authority enabled. |
| E2: host identity/bootstrap | `DeskFuse.WindowsSecurity` fixed directories; new cold host-key owner and immutable configuration; trusted installer/SCM binding; installed RX custody. | Missing/partial/foreign identity denies. No automatic key/root replacement or service activation from a receipt. |
| E3: protected setup entry | UAC launcher, distinct high Setup UI/caller binding and contained medium enrollment worker; bounded typed IPC and signed offer. | Actual caller/token/subject facts; no network Confirm or arbitrary signing. Can disable setup independently. |
| E4: phone ceremony | Dedicated quarantined TLS adapter, picker/QR/manual verification, Prepared/Active repository and foreground approval. Reuse existing UI affordances, not v1 trust semantics. | Mismatch/abort preserves no authority; no normal session use before signed commit. |
| E5: coordinator/store | One-ticket state machine, durable evidence/commit/revoke, `VerifiedEnrollmentCommit`, receipt recovery and explicit replacement. | Crash/retry never broadens trust or creates authority from partial state. Preserve uncertainty, not blind replay. |
| E6: v2 operational binding | `BrokerEnrollmentState`, `BrokerDeviceProof`, `BrokerAuthority`, Android approval parser; subject checks; signed challenge envelope; wire the existing fixed native-worker chain. | Enable each qualified scope only when its actual owner/control/rundown chain is complete. No false v2 claim through v1. |

E1 precedes all wire integration. Windows E2/E3 and Android E4 can proceed in
parallel once E1 is fixed. E5 requires both; E6 follows committed enrollment.
This does not delay unrelated media, terminal, file or audio-owner workstreams.

Keep verification focused, consistent with the reduced test-work instruction.
Reuse current fixtures; no new general framework or repeated broad suite merely
to accumulate counts. Essential groups are:

1. C#/Kotlin canonical vectors, full-digest QR/manual match, cross-role/domain,
   key/host/namespace/subject/scope/ticket substitution and downgrade rejection.
2. Frozen candidate/retry semantics, both approvals, expired/stale UI/logon and
   phone background/cancel; root/phone keys never serve as TLS or arbitrary signers.
3. Commit crash points, lost receipt/status freshness, unknown write completion,
   revocation failure and revoke-old-before-new replacement.
4. Runtime v2 subject/caller/context/generation rechecks, using the existing
   authority/rundown fixtures. No per-frame/sample checks introduced.

Separately authorized installed-SYSTEM qualification must demonstrate actual
key isolation/private creation; service/config/RX protection; rogue same-user and
cross-logon denial; high UI injection/caller/pipe-squatting resistance; standard
user plus different-admin subject separation; process death and dependency
replacement. It must not claim that UIPI makes the ordinary desktop unspoofable.
Device/media/driver gates remain separate. No such operation is authorized by
this plan.

Measure cold ceremony/control costs only where needed: worker startup, root
sign/verify and acquire/renew tails. Preserve cached-key/cached-retransmission
paths and keep all such work off media owners. No arbitrary latency target
justifies reduced cryptographic or identity binding.

**High stopping rule:** implement these choices. Stop for an actual contradiction,
failed private-creation/process boundary, or new security/ownership tradeoff;
do not substitute legacy trust, an unsafe UI assumption or a weaker code. Expected
unavailable hardware/installation prerequisites leave their gate open and do not
reopen the architecture. This planning pass stops here; no implementation follows.

# N2 bounded request retention — Ultra decision and High handoff

**Subsequent handover review:**
[Audio_Handover_Reviewed_High_Decision.md](Audio_Handover_Reviewed_High_Decision.md)
preserves this positive protocol and clarifies exact applied-result recovery on
the newly current target. A surviving Supersede Pending matching authenticated
current target/source/boot must reach exact cached receipt recovery before generic
resync retirement on an empty active slot. The status itself grants no window;
original identity/signature/first-send anchor and ingress rules remain mandatory.
This is selected source work for High, not an implemented N2 result.

**High execution update, 29 October:** the focused protocol is implemented and
offline checked. See [Audio_N2_Bounded_Request_Implementation_Status.md](Audio_N2_Bounded_Request_Implementation_Status.md).
Continue N3 on High. The decision-pass scope/evidence below is historical and
does not claim production activation.

29 October 2026. **Decision selected on Ultra; implemented afterward on High.**
This resolves the focused stop in `Audio_N2_Request_Retention_Ultra_Stop.md`.
Architecture v0.6, the native audio ownership handoff and completed media work
remain in force. This pass changes planning/state documents only. Preserve all
uncommitted source on `experiment/vnext-audio-latency`, committed base
`09fd7a86b3c674fb1580705b734e64086a5b4e1a`. Production remains disabled.

## 1. Selected protocol

Use **one source-scoped strict sequence, one pending control operation and an
eight-entry applied-result cache**, with actual native-operation custody held
separately. Keep the existing 32-byte phone request field:

`RequestId = uint64-big-endian sequence || 24 fresh CSPRNG bytes`.

Sequence starts at 1; the random suffix must not be all zero. Keep an in-memory
`issuedThrough` u64, initially 0, for this exact boot/worker/source grant and
source-owner generation. Allocate only its exact successor, never an arbitrary
larger value. Reserve/burn the sequence before issuing/signing a challenge.
Failure, abandonment, expiry and cache eviction never make that identity fresh
again. Counters never wrap; exhaustion requires normal retirement/fresh source.

The phone normally has one operation in flight and retries its exact identity.
There is no per-renewal disk ledger, extra request signature, healthy-path round
trip, new process, media copy or packet-path sequence lookup. All changes are
bounded setup/control work. Continuous healthy renewal no longer hits a lifetime
operation count. Eight cached results are a small implementation capacity, not
eight renewals or a new audio buffering policy.

## 2. Bind the request before the phone signs

The draft's generic `BrokerProof.v2` transcript does not contain the phone
request identity, exact audio subtype, proposed cutoff or expected audio
revision. A later receipt cannot repair that missing proof binding. Adopt a
small **audio-specific proof transcript**, reusing the canonical v2 facts:

| Order | Canonical audio proof content |
|---|---|
| 1 | ASCII `DeskFuse.Audio.Proof.v1\0` |
| 2 | u16 big-endian length, then the exact canonical `DeskFuse.BrokerProof.v2\0` core, including broker nonce, protected identities, resource, generic operation, Speaker scope, owner generation and proof expiry |
| 3 | Service boot nonce, 32 bytes |
| 4 | SourceGrantId, 16-byte canonical UUID; SourceOwnerGeneration, u64; Source digest, 32 bytes |
| 5 | Exact AudioOperationKind, u8; full RequestId, 32 bytes |
| 6 | Expected audio ControlRevision, u64 (0 is valid before first Acquire); frozen ProposedUntil in Windows boot-clock 100-ns units, nonzero u64 |

Integers/UUIDs follow the existing network canonical encoding. Reject trailing
bytes, wrong domains, inconsistent duplicated facts and unbounded core lengths.
Bound the audio proof to 1024 bytes; retain the current bounded canonical v2 core
limit. Acquire's core resource/generation must be this source's; other operations
must name the exact current/candidate Delivery digest/generation. The Delivery
digest already commits its source binding. Renew keeps that digest unchanged.
Audio kind distinguishes Source Acquire from Delivery Attach even though both
map to the generic Acquire operation. All bytes are snapshotted once.

The host signs `DeskFuse.Audio.Challenge.v1\0 || SHA256(audioProof)`. The phone
validates that root signature and all typed approval/actual-TLS facts, then signs
the **whole audioProof** with the existing enrolled P-256 key. Preserve the
existing challenge/device-proof/receipt signature counts; do not add a second
phone pre-signature or nested root signature. Generic v1/v2 proofs for unrelated
paths remain unchanged. A bare generic Speaker proof is not accepted by this
audio owner, even if correctly signed. No downgrade on parse/version mismatch.

Build/verify this transcript only through narrow typed audio overloads in the
existing challenge/admission machinery. The binding comes from the retained
owner reservation, never a fresh set of parameters supplied with proof dispatch.
Existing protected-context/key/revision checks before and after verification and
the short `CommitOnce` publication stay mandatory. Keep root signing constrained
to broker-owned typed records; do not introduce an arbitrary-data signing API.

Freeze ProposedUntil at reservation as today. Also freeze the absolute proof
expiry, bounded by the existing challenge ceiling, ProposedUntil, and the current
parent cutoff where one exists. Convert to proof milliseconds conservatively;
never round past a cutoff or recalculate an expiry after signing. The receipt's
existing ProofSha256 becomes SHA256(audioProof), RequestNonce contains the full
structured RequestId, and AuthorityUntil must equal that proof's ProposedUntil.
Its applied revision is the checked successor of the proof's expected revision.
Source/Delivery descriptors, the media wire, native Control grant and ALPN need
no format change for this decision.

## 3. Reservation, retries and retirement

Check current ingress eligibility, enrollment/worker/parent and terminal state
before mutating sequence/cache state. Lookup retained identity before applying
new-operation phase rules, so a completed Acquire retry is not mistaken for a
second Acquire. Identity matching includes full RequestId, kind, selected
resource/generation, parent context and candidate descriptor. Changing challenge
lifetime/options on a duplicate cannot change its frozen challenge or cutoff.

| Input/state | Required result |
|---|---|
| Exact retained pending request | Join the same retained phase completion or report bounded InProgress; once issued, return identical transcript, nonce and host signature. Never start another signing/verification operation. |
| Exact retained applied request/proof | Return the same committed identity/exact signed receipt while its binding and authority remain current. A changed proof signature is not an exact retry. |
| Retained sequence with different entropy, kind or parameters | Conflict; no mutation or replacement. |
| Unknown sequence at/below issuedThrough | Stale forever; no challenge, new cutoff, second signature or dispatch. |
| Sequence above exact successor | OutOfOrder; no floor jump or allocation. |
| Exact successor while the operation slot is occupied | Busy, without burning it. The caller retains that proposed identity for a later first admission. |
| Exact successor, free slot, valid operation/origin | Atomically reserve its immutable tuple and frozen times; advance issuedThrough; sign outside short gates. |
| Pre-admission proof/challenge completion after retirement/revision change/expiry/denial | Reject publication; release only that operation's completed custody. Never clear a newer fence or alter another request. |

The one slot covers challenge issuance, awaiting proof, verification, admitted
control result and receipt signing. A caller timeout does not release it while
signing/verification is actually running. Exact duplicates join that operation;
ordinary waits do not hold the owner/authority gate or the native media owner.
Awaiting-proof reservations retire on frozen proof expiry, authoritative state
invalidation, or abandonment by their authenticated current delivery owner.
Candidate disconnect may retire its pre-admission reservation; it cannot retire
another connection's request. Retirement does not roll back a burned sequence.
An admitted mutation is not undone by abandoning its reply: post-admission Stop,
revoke, detach and rundown follow their existing contracts.

Retirement immediately closes dispatch for that reservation and removes its
exact nonce from **BrokerProofChallenges as well as the audio indexes**. Host
challenge-signing failure must remove a nonce already allocated underneath it.
Record that allocation before starting host signing, or retain it inside the
issuer's guaranteed cleanup scope, so failure cannot lose its nonce identity.
If cryptographic work is still running, keep the slot/record pinned until its
real completion; that completion cannot republish it. A new operation may then
reserve the next identity while the original parent is still live. Failed or
unproved terminal reservations need no growing tombstone map: issuedThrough
already makes them stale. Restrictive Stop/expiry/revoke/detach/output fences
never wait for the positive-operation slot.

## 4. Protect healthy playback from candidate churn

Exact-next prevents a single MAX-value poisoning request. It is not permission
for a public candidate to occupy a healthy owner's next slot. Enforce this
concrete ingress rule in N2's typed boundary and N3's actual connection routing:

- **Current delivery present:** Renew and any live Supersede intent must arrive
  through that exact approved authenticated TLS delivery. A live handoff can
  nominate the worker-observed new candidate connection; the new Delivery proof
  still binds that candidate's actual keys, nonces and generation. Public/new
  candidates may obtain bounded public metadata but cannot reserve, abandon or
  advance this source's operation lane.
- **Idle/initial Acquire, first Attach, or after actual Detach:** allow only the
  corresponding bounded worker-observed setup/recovery candidate operations.
  Supersede remains limited by the original parent/grace cutoff. Issuance and
  resync never extend it. A request from a new connection cannot itself force
  Detach; native observation/authorized lifecycle controls that transition.
- Do not accept a network/C# boolean asserting authenticated/current status.
  Bind the origin to the retained worker's observed connection incarnation and
  compare it with the broker's approved delivery facts. This is routing/admission
  eligibility, not a substitute for the fresh device proof that renews authority.

This preserves the existing quarantine-to-authorized TLS design and permits a
live handoff without restarting the healthy source. Before an approved delivery
or after real loss, setup/recovery availability still uses bounded candidate,
frame and issuance limits. There is no promise of recovery under an active setup
denial-of-service attacker. Do not pay another Keystore signature on every
healthy renewal to solve that separate availability issue. A future measured
need can strengthen candidate admission without changing the media path.

## 5. Applied reply cache and actual custody are different

Retain the eight most recent **applied** operation records for exact replies,
including a latched receipt-signing failure. Unproved candidate failures cannot
evict the last successful receipt. Each applied record owns its frozen proof,
exact phone signature, applied revision, immutable receipt bytes, signing attempt
and exact signed result. Sign once; uncertain signing never generates another
randomized signature for that request. A cache hit does not resample remaining
validity. Expired/denied/replaced bindings return terminal/stale instead of a
success acknowledgement, regardless of cache contents.
An already-applied exact receipt may remain replyable across benign later
revisions while its binding, cutoff and admission remain current; an output-only
revision change does not retroactively invalidate that committed operation.

Evict only records whose control/verification/signature work is terminal. Native
setup/rundown may still be alive through independent custody; it does not require
keeping that operation's reply-cache entry resident. Keep
the source acquisition/preparation record and any actual native setup/rundown
custody through explicit owner references, independently of this cache. In
particular, remove `Committed.RequireAcquisition`'s dependence on an eternal
`applied` dictionary entry: it must validate the exact retained source owner and
acquisition record/claim. Cache eviction must neither invalidate a live source
nor allow a second preparation. It does not authorize a replacement or certify
cleanup. An evicted reply cannot be re-signed through an old retained capability.

The positive control slot and existing one-source/one-delivery/rundown rules bound
active records; do not create an unbounded 'pinned' overflow collection. If actual
custody is occupied or uncertain, retain it and reject/defer conflicting work.
Prune all secondary indexes; deleting one dictionary while leaving another or
the generic challenge store growing does not satisfy this decision.

## 6. Phone ownership, resync and reset

The independent Android audio owner retains one Pending operation containing the
full request identity, typed intent/descriptor snapshots, **original first
Issue-send elapsedRealtimeNanos anchor**, accepted audio proof/challenge and exact
phone signature. The signer's shared 64-entry cache is not this custody. Retry
uses those bytes/anchor, including after delivery-only reconnect. A local signing
call also remains owned until its real completion. An uncertain outcome cannot
cause a second signature for that same pending request.
Preservation is outcome/custody tracking, not rebinding: actual Detach invalidates
operations bound to the old delivery. Supersede uses a new successor identity,
new proof and its own first-send anchor after old pending work is terminal.

Keep the phone's confirmed issuance floor separate from its one proposed Pending
sequence. Merely allocating/sending a proposed identity does not prove it was
burned at the broker. A verified challenge/receipt or authenticated status can
raise the confirmed floor. Busy retains the same proposed identity and does not
advance that floor. If resync reports the proposal has not yet been reserved,
retain/retry it; do not skip to a larger number or lower a confirmed floor.

Receipt acceptance takes that retained Pending object, rather than caller-chosen
sent-time/revision values. Check root, full source/boot/context, current pending
identity, proof hash, frozen cutoff, applied revision and current local admission.
Exact duplicate acceptance returns the existing live window; it never restarts
its timer. Older/abandoned receipts cannot replace a newer window. Reduce the
authenticated remaining validity by the existing qualified clock-rate bound and
anchor it to the original first send. A genuinely new successor operation gets
its own first-send anchor and fresh proof. No late result revives a terminal one.

Provide a narrow **read-only request-status/resync** operation for lost/stale
issuance replies or initial/recovery sequence drift. It echoes a fresh 32-byte
query nonce and returns broker-owned host/enrollment/boot/worker/source context,
phase, current binding/delivery, control revision, cutoff and issuedThrough.
Include the active RequestId/stage/frozen proof expiry, or an explicit empty
slot, so a surviving owner can distinguish an exact retry from a retired request.
Use a strict canonical `DeskFuse.Audio.RequestStatus.v1\0` record, at most
1024 bytes, signed
by the installed root and correlated to that query. Bound/serialize this cold
signing work; it changes no floor, slot, generation, deadline or source state.
It does not acquire the positive-operation slot: permit one bounded read-only
status job while a proof is awaited, rather than deadlocking resync behind it.
Apply the same origin eligibility to new status-signing work: while a current
delivery exists, public candidates cannot bypass its protected renewal lane by
flooding root-signed queries. Public metadata does not require fresh root signing.
Do not retain a lifetime query history or derive a grant from this response.

Status field order is: domain, QueryNonce[32], Host[32], Namespace UUID,
Enrollment UUID, Boot[32], Worker UUID, SourceGrant UUID, SourceGeneration u64,
SourceDigest[32], Phase u8, BindingDigest[32], DeliveryGeneration u64,
DeliveryDigest[32], ControlRevision u64, AuthorityUntil u64, IssuedThrough u64,
ActiveRequestId[32], ActiveStage u8, ActiveProofExpiryMilliseconds u64 and
ActiveAudioOperation u8. Use the existing phase values Idle=0, Preparing=1,
Prepared=2, Closed=3; report expired authority as effectively Closed. Stages are
Empty=0, Issuing=1, AwaitingProof=2, Verifying=3, Applied=4, ReceiptSigning=5,
Retiring=6. Empty requires zero active identity/expiry/operation. Binding is zero
before preparation; delivery digest is zero exactly when delivery generation is
zero. Reject inconsistent context, enums, zero/nonzero rules and trailing bytes.
All integer/UUID encodings match the proof. This is an actual current-state
snapshot, not caller-nominated data for root signing.

The surviving phone owner verifies it against its committed root and exact source
context, only raises its RAM counter, and may retire its old terminal Pending and
create a **different** successor identity with a new first-send anchor. It cannot
discard still-running local signing or re-anchor the old request. An expired or
closed broker parent, expired local window or terminal phone owner stays terminal
after resync. Healthy exact retries do not need this extra exchange.

| Event | Request history / runtime behavior |
|---|---|
| Healthy renew, output recreation or workspace UI change | Same source counter and owner; no source/transport/renderer restart for renew. |
| Transport-only loss/rebind, Android audio owner survives | Keep counter, Pending/proof/anchor and source identity; discard old delivery data, obey existing grace, use a fresh delivery generation/keys. |
| Fresh source/backend incarnation | New broker SourceGrantId/owner context; sequence starts at 1 only after required old acquisition rundown/quarantine resolution. |
| Service or protected native worker death/restart | Old runtime grant terminal under existing job/rundown rules; no counter/window restored from enrollment or receipt. |
| Android audio owner/process loss | Cold recovery: stop/retire the old source under existing grace/rundown rules, then fresh Acquire. Do not reconstruct an old runtime window/counter from saved records. A surviving independent audio owner through UI/workspace changes is not this case. |

This deliberately keeps ordinary reconnect fast while making process-death
recovery unambiguous. Remembered enrollment still authorizes obtaining fresh
proofs; it is not a saved running audio grant.

## 7. Ordered High implementation and acceptance

1. Add typed RequestId/audio-proof/status codecs and shared C#/Kotlin vectors.
   Preserve generic proofs and native media/control formats. Wire the dedicated
   root challenge signer and typed phone signing/verification; reject the old
   generic audio proof. No arbitrary signer interface or custom cryptography.
2. Replace lifetime request/applied maps with issuedThrough, one retained pending
   slot and eight applied replies. Implement exact cancellation of underlying
   broker nonces, joined phase completion and native-acquisition custody separate
   from response lookup. Keep verification/signing outside short gates.
3. Require typed issue origin and prepare the exact N3 ingress contract above.
   Add phone Pending custody and read-only resync. Actual TLS routing and the
   independent phone owner compose in N3/N6; do not claim those are already wired.
4. Run meaningful offline checks, then continue N3, N4, prepared N5 integration
   and N6 in the established order. Product lease/grace/rate margins remain
   explicit qualified policy inputs, not new defaults selected here.

Essential checks (combine behaviors; no new general test framework):

- More than 4096 healthy renewals (e.g. 5000) through actual proof admission with
  fake time: bounded indexes/cache, unchanged source/connection/output ownership,
  no cutoff refresh on retries. Verify boundary sequences above signed-long range
  and exhaustion without wrap/truncation.
- Wrong request entropy/kind/boot/source/revision/cutoff, skipped/MAX sequence,
  bare generic Speaker proof and cross-operation substitution all fail without
  advancing the floor or media authority.
- Huge/quarantined candidate churn cannot occupy a healthy Renew slot or affect
  its counter; live handoff requires approved ingress, and post-Detach recovery
  cannot run past grace. Busy does not burn a proposed next request.
- Lost issuance/proof/receipt replies, in-progress duplicate joins, cache eviction,
  signature failure, invalidation and late proofs/completions preserve exact
  bytes/first-send time or return stale. Underlying generic pending counts stay
  bounded even when host challenge signing fails before returning a response.
- Evict old acquisition replies while its actual source/preparation remains held;
  late setup, failed Stop/rundown and replacement still obey the original custody
  contract. No new source can appear merely because history was pruned.
- Resync is root/query/context bound and read-only. Receipt duplicates and delayed
  old receipts cannot re-anchor or resurrect a local window; reconnect preserves
  surviving Pending state, whereas process/service/source reset does not adopt it.

## 8. Review evidence and pass scope

Reviewed the actual N2 owner/proof/signing/descriptor implementations, Android
proof/window code, native control guard, focused tests, N2 stop, independent High
status, native ownership handoff and original Architecture v0.6 B4/T09 contracts.
Two independent read-only reviews checked replay/custody and phone/receipt cases.
No runtime source was edited and no build/test/install/device operation was run
in this decision pass. Earlier source/offline results remain in their status
files; they do not test this newly selected protocol. Return to High now for the
implementation above; N2 is not yet an implemented exit gate.

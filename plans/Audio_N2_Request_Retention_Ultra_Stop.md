# N2 High draft — bounded request history decision for Ultra

29 October 2026. **Resolved by the focused Ultra decision:**
[Audio_N2_Bounded_Request_Retention_High_Decision.md](Audio_N2_Bounded_Request_Retention_High_Decision.md).
The focused High implementation is now recorded in
[Audio_N2_Bounded_Request_Implementation_Status.md](Audio_N2_Bounded_Request_Implementation_Status.md).
Continue N3 on High; this is not production qualification.
The original draft/problem/verification below is retained as background;
the new decision supersedes its provisional lifetime cap and open choices.
The independent High continuation is saved in
[Audio_Independent_High_Continuation_Status.md](Audio_Independent_High_Continuation_Status.md);
its N5 groundwork does not change this pending decision. Preserve
all N2 uncommitted source in `D:\Coding\DeskFuse\vnext-audio-latency`, branch
`experiment/vnext-audio-latency`, base `09fd7a86b3c674fb1580705b734e64086a5b4e1a`.
No source commit/push, service activation, installed probe or device operation
has been performed during N2. **N2 is a draft, not a completed exit gate.**

## What is implemented in the draft

- `AudioAuthorityProtocol.cs`: canonical source, observed binding, delivery,
  typed signed receipt and native-control projection. The installed deployment
  fingerprints its held code/role closure; source identity is taken from the
  actual worker and committed v2 enrollment. Descriptor inputs are snapshotted.
- `BrokerAudioOwner.cs`: one source parent/one delivery child, immutable published
  authority view, explicit lease/grace policy, frozen request/cutoff at challenge
  issuance, one-use proof mapping, exact operation/receipt retries, preparation
  claim and late-completion guards, output state, disconnect grace and expiry.
  A short memory-only `BrokerOperationAdmission.CommitOnce` publication is
  serialized with the registry's current enrollment/worker authority. Signature
  verification, signing, persistence, control I/O and joins stay outside it.
- Speaker association: a private durable record in the existing bootstrap
  directory maps namespace/host/peer/subject to a separately allocated 16-byte
  PnP identifier. It reuses checked positive-record completion and retained
  custody under the enrollment store's cold writer/owner lease. Readable existing
  bytes are re-completed; torn records are not reallocated. Enrollment denial
  cannot poison already completed identifier history. The identifier is not a
  bearer credential and confers no runtime authority or legacy-devnode adoption.
- `AudioAuthority.kt`: shipping typed descriptor checks before v2 Speaker proof
  signing, exact worker/logon/process facts, actual TLS-hash comparison inputs,
  root-signed receipt checks and a terminal conservative BOOTTIME window anchored
  to the original request-send time. Generic v2 Speaker signing is refused.
  N3/N6 still have to wire actual TLS observations and the independent phone owner.
- `audio_authority.rs`: concrete native context/revision/deadline guard. Source,
  approved TLS/delivery and output observations are separately required before
  media permission. Identical control retries do not refresh time; suspend-time
  expiry and stale source/delivery/generation/revision fail closed. The fixed
  worker accepts bounded SYSTEM control projections and observes the boot cutoff.
  N3/N4 still supply the real TLS/source observations and media dispatch path.
- `AudioWorkerControl.cs`: retained serialized publication/acknowledgement and
  joined stop. An old but correctly observed projection returns superseded rather
  than restarting a healthy source. An acknowledgement only confirms authorization
  state observation; it does not certify physical setup, egress or cleanup.

Production remains blocked in `DeskFuse.AudioBroker/Program.cs`. Physical native
custody/rundown, source/backend replacement wiring, transport, renderer and service
composition remain the existing N3–N6/integration work. No cleanup success is
inferred from an authorization-state flag or a control acknowledgement.

## Decision that must be settled

The draft `BrokerAudioOwner.Issue` retains every fresh phone request in `requests`
and every applied operation/receipt in `applied`. A lifetime bound of **4,096
requests per source owner** fails closed but eventually stops otherwise healthy
renewals. This is a provisional implementation bound, not accepted product policy.

Keeping all identities forever is unbounded. Simply evicting old request identities
can cause an old request nonce to be treated as a new request: a new broker
challenge and `now + duration` cutoff would be created, rather than an exact retry
or a stale result. Existing one-use broker proof nonces alone do not define this
fresh-request versus retry distinction after request metadata is forgotten.

Choose the smallest bounded protocol that preserves continuous healthy renewal,
exact retry semantics and the original frozen cutoff. Recommended direction:

1. A monotonic request sequence scoped to the current source grant, a retained
   high-water mark, a small bounded pending set and an exact-response retry cache.
2. An unknown/evicted old sequence is stale and never issues another challenge or
   obtains a new cutoff. A retained duplicate must match its full frozen request.
3. Decide the representation/binding: a structured existing 32-byte phone request
   nonce (sequence plus adequate random entropy), or an explicit sequence field.
   Preserve the unchanged Delivery digest for Renew and explain the binding to
   the one-use broker challenge, phone proof and signed receipt. No format choice
   has been accepted in this High draft.
4. Specify sequence/reset behavior across delivery reconnect, source replacement,
   service restart and phone-owner/process loss. Remembered enrollment/receipt
   data must not restore a terminal runtime grant.
5. Separate evictable response data from actual operation identity and native I/O
   custody. Do not forget a pending/uncertain setup, allow a second owner, regenerate
   an uncertain randomized signature, or claim rundown because a cache entry aged.

Keep this a focused control-plane decision: no universal lease/replay framework,
new persistence on renewals, media copies, extra process or per-packet crypto.
Do not reopen the Rust ownership, in-process capture, single queue or existing
durability design. Lease duration, reconnect grace and clock-rate margins remain
explicit policy inputs requiring qualification; test values are not production
defaults.

## Verification

Before this stopping point, Windows broker/setup/transport builds, Android Kotlin
and native receiver build, two native authority tests and optimized x64 worker/
DLL staging passed. Android native compilation reports the six existing lab-path
unused/dead-code warnings. The earlier C# run passed its selected cases with one
old Kotlin harness environment skip; the final run will set both harness variables.

Final draft verification: **42 focused C# checks passed, zero failed/skipped**,
including actual shipping Kotlin source/delivery proof and receipt interoperability,
nonce-mutation isolation, association completion/denial, frozen deadlines and
revocation/signing races. TRX: `out/n2-checks/n2-focused.trx`. Both native authority
tests and all three existing native control tests passed. The latest broker build
passed without warnings/errors, and the final Android Kotlin/JVM harness build
passed offline. No installed or hardware qualification is implied.

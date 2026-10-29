# Audio Stop, Game mode and handover — focused Ultra design

29 October 2026. **Design only.** Checkout `experiment/vnext-audio-latency`, source
baseline `7859ad6`. No runtime source was changed, built or tested in this pass.
Architecture v0.6, native ownership, N2 positive request retention and original
media timing remain in force. Production authority stays disabled.

| Topic | Decision / readiness |
| --- | --- |
| 1. Wi-Fi/cellular preparation | Research requested by the user; **pending Arena review**. [Question, verified prerequisites and provisional baseline](Audio_Handover_Arena_Research_Question.md). Final switch/readiness protocol and High handover steps follow that review. |
| 2. Source Stop before Attach / after loss | **Selected below:** separate negative-only admission, direct current-delivery Stop and a connection-bound enrolled signature for recovery. Ready for focused High implementation after the design pass. |
| 3. DI-001 modes | **Selected below:** existing source v1 means Game; setup-only mode admission prepares a versioned future buffered path. Interactive/Streaming remain unsupported. |

This resolves the decision in
[Audio_N3_Source_Release_Ultra_Stop.md](Audio_N3_Source_Release_Ultra_Stop.md).
Its factual implementation stop remains historical until High implements this
handoff. Topic 1 remains deliberately provisional; it must not hold up documenting
the two settled decisions or silently rewrite N2 to park a standby proof.

## 1. Source Stop is separate negative authority

Use a **source-incarnation-scoped, irreversible Stop**, outside N2's positive
sequence/pending slot and eight grant-reply entries. No authority extension,
fresh Acquire/Attach or positive grant receipt is involved.

| Ingress | Authentication / admission |
| --- | --- |
| Exact current approved delivery | A fast Stop names its source grant/generation/digest and fresh StopIntentId. Match the retained approved origin and source under the short authority gate, then deny immediately. No new Keystore or root signature. |
| Before first Attach; replacement/new connection after loss; cold phone cleanup | The enrolled phone signs the typed negative transcript below. Broker verifies it against its own enrollment/source/actual connection facts; an ephemeral TLS certificate alone is insufficient. |
| Internal expiry, revoke, logon/worker/service failure | Existing independent restrictive fences remain authoritative. Network Stop availability cannot delay them. |

### Typed proof and freshness

Add a dedicated canonical `DeskFuse.Audio.SourceStop.v1\0` transcript. All UUIDs
and integers use the existing network canonical encoding. Field order:

`domain, HostHash[32], Namespace UUID, Enrollment UUID, SpeakerScope u8,
Boot[32], Worker UUID, SourceGrant UUID, SourceGeneration u64,
SourceDigest[32], DeviceKey UUID, StopIntentId[32], ConnectionIncarnation UUID,
NativeCertificateHash[32], PhoneCertificateHash[32], NativeNonce[32]`.

Require nonzero IDs/hashes/nonces/generation, exact Speaker scope and no trailing
bytes. Bound transcript to 1024 bytes and use the existing P-256/SHA-256 DER
signature validation (8..72 bytes). Signed-Stop frame body is `u16 transcript
length || transcript || u8 signature length || signature`; total remains below
the existing 8 KiB frame ceiling. Use a distinct typed network operation and
broker method; do not append Stop to `AudioOperationKind` or reinterpret its
positive proof/grant-receipt codec.

The fresh connection's native incarnation/nonce and both actual TLS certificate
hashes bind this signature to its observed channel. The random phone StopIntentId
identifies local cancellation; it is not N2's sequence, a bearer credential or
permission to select a different source. No host-root challenge/signing job or
separate unauthenticated challenge reservation is needed before denial. A Stop
signature is deliberately valid only to close its exact source; it never
authorizes continuing or restoring it.

Do **not** require equality with a positive ControlRevision or a lease proposal.
Renewal/output/delivery changes must not defeat a Stop aimed at the same source
incarnation. Recheck the exact source and protected identities instead. A source
that accepted Stop never resumes; a later Start requires a new source after
confirmed old rundown. There is therefore no meaningful later live incarnation
of this same source to which a delayed valid Stop could grant access.

### Phone intent and cold recovery

The independent phone owner first latches a local Stop, closes local audio/output
admission for its affected audio owner, stops renewal and retires positive
requests. It retains actual signing/I/O custody until completion. A peer message
never creates this local intent or automatically elicits a Stop signature.

A surviving owner names its exact known source. A cold owner may explicitly
clean up the **selected trusted enrollment's Speaker association** before a new
Acquire. Bounded public discovery can supply canonical source bytes as an
untrusted locator. Phone checks the committed host/namespace/enrollment,
peer/subject/Speaker scope, recomputes the source digest and uses native-owned
actual TLS facts. Broker independently matches the exact retained source and
its authoritative Speaker association/DeviceKey; it never trusts the locator.
The current association is keyed by host/namespace/peer/subject in
`TrustedBrokerEnrollmentStore.GetSpeakerAssociation`. If future multiple Speaker
targets exist, cold intent must select one from trusted local selection; a
locator cannot choose an unrelated endpoint.

This exception is safe specifically because the signature grants **only denial**:
a terminating relay changes the certificate/nonce binding and cannot transplant
it into the actual worker connection. A transparent relay gains no new rights.
The local intent plus broker-owned exact association confines the possible
target. No unsigned metadata grants source access or authorizes positive signing.
Existing authentic cached proofs may narrow the source identity after expiry,
but no saved runtime window/counter/receipt is adopted. No new persistence.

### Short admission, bounded work and races

1. Check framing, retained worker observation, exact source and protected
   enrollment/key/subject/association before reserving one negative verification
   job. Snapshot owned bytes; verify outside authority and phone lifecycle locks.
   Keep this job independent of positive issuance, receipt signing and resync.
   Apply existing global crypto, candidate/control budgets and configured rate limits;
   unverified traffic cannot publish a Stop record or evict an approved delivery.
2. Under the short owner/registry gate, recheck source incarnation, enrollment/key
   revision, subject, worker and actual captured observation binding. Then set
   irreversible source denial, advance the restrictive revision, invalidate
   positive work and publish/wake the native terminal fence. No native join,
   CNG/Keystore operation, storage access or resource setup occurs under that gate.
3. Pin a legitimate observation captured at ingress while verification completes.
   Losing that connection afterwards does not by itself cancel this negative
   operation; it may still deny the same current protected source. A submission
   initially arriving on a different connection must bind that connection's own
   facts. That pinned observation counts toward the fixed custody bound after
   leaving the live connection map. A new source/key/enrollment/worker context
   cannot be substituted.
4. Keep one source termination/rundown record, independent of the positive reply
   cache. Exact in-progress attempts join; competing unverified work returns
   bounded Busy. Successful repeats join the already terminal source operation.
   No lifetime per-attempt ledger. A new connection uses a fresh connection-bound
   signature under the frozen local source Stop intent. Retired old crypto/I/O
   stays owned until its actual completion, which can only reinforce denial.
5. Stop versus an unadmitted Acquire closes that proposed source and prevents
   late admission. Stop after admitted Acquire applies source revocation even
   if its positive receipt was lost. Fast Stop from an already superseded old
   delivery fails origin checks; retain its local intent and fall back to a fresh
   enrolled signature for the same source. Hardware signing/network failure can
   delay remote Stop, so local denial is immediate and existing remote expiry
   remains the fallback.

Reserve one negative command capacity in addition to regular control RPC limits
(currently eight on the native wire), **at every hop**: phone enqueue, native
pending accounting, broker dispatch/verification and native fence publication.
A full regular pending map cannot reject or deadlock a Stop. Central readers
must continue bounded parsing/demultiplexing while crypto/native jobs run.
Allocate transport correlation IDs in actual writer admission order; prioritize
Stop at the next eligible scheduling point before numbering, never by reordering
already numbered/submitted frames. Preserve actual outstanding-write custody
and fail closed on counter exhaustion. Stop may still wait for bounded prior
transport bytes/network delivery, but not for a positive RPC's signing/completion.
The existing native terminal fence takes priority when read; late positive
completion cannot overwrite/clear it. No extra media queue or QUIC stream.

The phone's current `BrokerDeviceSigner` holds a global positive-signing gate.
Refactor ownership so a negative request does not queue behind that software
gate; snapshot/recheck key/enrollment outside actual crypto and retain each real
job. The hardware provider may itself serialize or stall. Never fabricate
completion, replace an uncertain signature, or promise instantaneous remote Stop.

### Acknowledgement follows facts

Separate `Denied`, `RundownComplete`, `Quarantined` and `Unknown` outcomes.
Denied confirms the broker fence, not physical cleanup. RundownComplete requires
the actual native owner barrier, export revocation/capture join or exact-worker
termination/join, and existing endpoint cleanup obligations. Uncertain setup,
revocation, I/O or join retains custody/Quarantined and blocks replacement.

Return a **dedicated root-signed, query-bound SourceStopStatus**, never a positive
grant receipt. Canonical order is `DeskFuse.Audio.SourceStopStatus.v1\0`, fresh
QueryNonce[32], the source identity fields from HostHash through DeviceKey above
(including fixed Speaker scope), TerminationRevision u64 and State u8.
State is Unknown=0, Denied=1, RundownComplete=2, Quarantined=3; revision is zero
only for Unknown, nonzero otherwise. Query nonce/source context must match the
phone's outstanding query. Bounds/encoding match the 1024-byte typed codecs;
there is no cutoff/remaining-validity field. Query/result state comes from
broker-owned source/termination records, not caller-nominated claims. New status
revision cannot make a terminal source live; an old/unknown reply grants nothing.

Allow one bounded status signing job independently of positive work; retain its
exact frozen reply/signature for retries while owned. Root signing is **after
denial** and never gates denial/rundown. If it blocks or the reply is lost, the
phone shows unconfirmed remote cleanup. Keep the terminal record with actual
rundown custody and at most the latest completed record per Speaker association;
drop old reply history when safely replaced, preserving separately pinned actual
work. An old source then returns Unknown rather than invented cleanup success.

Normal source cleanup can keep control alive long enough for its result. Exact
worker termination may destroy that channel. Provide typed read-only broker
status lookup through the existing public-metadata/locator relay composition,
or a newly established control connection, backed by retained broker records.
It has no action authority and cannot require the dead source's live registration.
Fresh source acquisition always checks broker rundown custody; a phone-held
status is never a replacement permission. Service death retains the established
job/recovery rules and cannot be called confirmed cleanup merely from silence.
The read-only terminal query never creates a source/worker or restores a dead
registration. Unknown asserts neither existence nor completed cleanup.

## 2. Current source contract is Game; prepare two later modes at setup

Define setup-only `AudioDeliveryMode`: **Game=1, Interactive=2, Streaming=3**.
The canonical **Source.v1 contract means Game only**. Its digest already commits
the source policy inherited by all deliveries. Preserve its bytes, Delivery.v1,
N2 proof/receipt/control formats, ALPN and PCM packet layout. Neither an opaque
profile hash nor an optional unsigned field selects a different interpretation.

Advertise only Game in the new authority path. Reject Interactive/Streaming and
unknown values with UnsupportedMode **before** worker/source/preparation or
positive-request reservation. Source mode is immutable; renewal and network
handover preserve it. It belongs to a source/explicitly associated A/V group,
not the whole phone or an unrelated app's video. The optional setup preference
does not authorize media and cannot weaken authenticated Source.v1 semantics.

Construct the existing Game owner once. Add no per-packet mode field/branch,
virtual policy framework, queue/copy or retention. Keep committed source age
and presentation/commit checks, the 60 ms gate, old ciphertext retirement and
original timestamps. Naming this policy does not claim that unfinished generic
video/H.264 recovery or physically synchronized A/V Game support is qualified.

Future Interactive/Streaming require a **new canonical source version** binding
mode, retention/recovery policy and associated-track scope into its source digest,
plus a separately selected buffered playback owner at setup. Explicit negotiation
must reject unsupported source versions; never append optional fields to v1 or
raise/relabel Game freshness to replay old content. Switching mode requires new
mode-bound source authority and old-owner retirement, not an ordinary Supersede.
Shared immutable sample/PTS identity and authority/rundown remain reusable.

DI-001 retains its requested future semantics: Interactive preserves bounded
content while observing and returns A/V to live on mouse activity with an input
fence; Streaming preserves bounded/recoverable content and Live accelerates both
tracks with pitch-preserving audio. Storage, recovery transport, retention limits,
input catch-up and time-stretch DSP remain that later design/implementation slice.
Neither mode is advertised or implemented by this High handoff.

## 3. Ordered High implementation

1. Add Game-only setup admission and distinct source-Stop/status codecs in
   C#/Kotlin/Rust. Preserve existing canonical vectors byte-for-byte; add shared
   negative vectors and explicit unsupported-mode checks. Reuse installed
   enrollment cryptography and strict parsers; no new crypto primitive.
2. Add dedicated registry negative-facts snapshot/final-publication methods and
   retained termination state. Existing `WithAudioCurrent` routes through positive
   `ResourceForAuthority` and live/revision checks; it must not be bypassed by a
   generic boolean or reused unchanged for negative/terminal lookup.
3. Add direct current-delivery Stop and signed-source Stop to `BrokerAudioOwner`;
   implement independent verification and post-fence status custody. Source setup,
   renewal, output and candidate completions must recheck terminal denial.
4. Finish the central bounded broker-pipe reader/demultiplexer before unsolicited
   N3 observations. Replace `PublishCore`'s direct competing read. Preserve one
   pipe reader/writer owner, correlated bounded jobs and a restrictive terminal
   fence that cannot wait behind positive signing or an unbounded media drain.
5. Extend unpublished network framing with distinct signed-source Stop and status
   operations. The current 40-byte STOP body/quarantine rejection is only a
   foundation placeholder: fast Stop now carries SourceGrant UUID, Generation
   u64, Digest[32], StopIntentId[32]; signed Stop is allowed only in the negative
   verification lane. Bind every observation to the retained registered worker.
6. Wire the independent Android owner's local Stop and narrow negative signer,
   exact cached attempts, cold cleanup selection and status verification. Actual
   native teardown is N4/N5/N6 integration; expose no false Complete result while
   only an N3 fence/authorization acknowledgement exists.
7. Implement handover only after topic 1's reviewed selection is appended here.
   Preserve source wire position, packet/accepted/presented floors and the healthy
   AAudio/ASRC owner across delivery replacement. `ReceiveFrontier` is currently
   connection-scoped; moving to a new TLS connection must not rewind its source
   floor. Rebind receiver readiness to the new delivery/output context.

Meaningful acceptance cases: Stop before/after Acquire admission with a lost
receipt; pending renewal/output/handover; blocked positive/root/phone signing;
foreign source/enrollment/association/cert/nonce; cold cleanup without runtime
adoption; connection closure during verification; fresh-connection retry; revoke
during verification; late native completion, failed revoke/join and Quarantined;
query/substitution rejection and Unknown after history retirement. Unsupported
modes allocate no source/job/request, and Game's original timing/wire behavior
remains unchanged. Handover cases/measurements are finalized with Arena input.

## Evidence and scope of this pass

Read the focused N3 stop, DI-001, accepted N2/native ownership decisions,
`BrokerAudioOwner`, `BrokerAuthority`, `BrokerEnrollmentState`, host signer,
Speaker association store, Android proof/request/signer and native TLS/control/
authority/freshness boundaries. Independent read-only reviews covered Stop,
handover platform facts and mode compatibility. Official Android/quiche sources
and unresolved handover limits are linked in the research question. No runtime
source edits, builds/tests, key/service/deployment/device operations or physical
performance claims were made. Finish Arena review on Ultra before treating the
three-topic handoff as complete, then return to High implementation.

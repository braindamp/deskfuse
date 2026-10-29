# N3 source release without an approved delivery — focused Ultra handoff

**Subsequent Ultra design, 29 October:** the negative-only source Stop decision
is selected in [the focused High handoff](Audio_Stop_Game_And_Handover_High_Handoff.md).
It is not implemented. That handoff also defines Game-only current mode semantics;
its handover topic awaits the user's Arena review. The unresolved wording below
records the historical High stop, not a second open Stop decision.

29 October 2026. **Decision required; no release protocol selected on High.**
Architecture v0.6 and the accepted native audio/N2 decisions remain in force.
This is a narrow control-authority question, not a new audio architecture.
Production authority remains disabled.

## Exact stopping point

The completed N2 and independent N5 foundations were committed and pushed as
`d57d7344f8535f4768b1894a672b290f5a805bc2` on
`experiment/vnext-audio-latency`. The subsequent N3 foundation adds in-memory
mutual TLS, quarantined connection facts, bounded control framing and exact
descriptor checks. Its current scope/results are in
[Audio_N3_TLS_Control_Implementation_Status.md](Audio_N3_TLS_Control_Implementation_Status.md).
It does **not** connect a production listener, broker dispatcher or source.

Before wiring network Stop/source lifecycle dispatch, settle this question:
**How does the enrolled phone authenticate an immediate source Stop when that
source has no approved delivery connection?** This includes the interval after
source Acquire and before first Attach, connection loss during bounded grace,
and a cold phone owner recovering while a source grant remains live.

## Evidence and the missing contract

- `windows/audio/DeskFuse.AudioBroker/AudioAuthorityProtocol.cs` defines only
  AcquireSource, AttachDelivery, Renew and SupersedeDelivery. These are positive
  audio operations. Its receipt rules require a live remaining window and use
  zero delivery only for Acquire; they do not define cleanup acknowledgement.
- `BrokerProofKinds.cs` has generic Release, but this audio owner deliberately
  rejects bare generic Speaker proofs. No accepted typed audio source-release
  transcript, approval, retained operation or phone implementation maps to it.
- `BrokerAudioOwner.RequireOrigin` requires a retained native observation and,
  while a delivery exists, its exact approved connection. Without a delivery,
  observed candidates can enter specified proof/status operations; that does
  not itself authorize source Stop. An ephemeral TLS certificate is not an
  enrolled device identity.
- `BrokerAudioOwner.Abandon` retires only the current delivery's pending,
  unadmitted request. It cannot undo an admitted source Acquire. Internal
  `CloseAdmission` applies immediate denial but authenticates no network caller
  and asserts no physical cleanup. `ConnectionClosed` detaches current delivery
  and retains only the already-approved bounded grace/cutoff.
- Native ownership handoff sections 4, 5 and 7 require surviving source during
  permitted reconnect, immediate denial on Stop/revoke, and actual native
  rundown before cleanup/replacement acknowledgement. N2 retains real operation
  custody independently of its eight cached positive replies.

An unapproved candidate must never stop another connection's healthy source
merely by naming its public descriptor, receipt, source ID or request nonce.
Requiring positive Attach solely to stop, silently waiting for lease expiry, or
inventing a negative proof format each selects behavior absent from the accepted
contract. That choice is why High stops here.

## Focused decision requested

Select the smallest adequate authenticated release rule. A fresh **source-scoped
typed device proof** is one candidate, reusing enrollment identity, canonical
challenge/proof machinery and source incarnation. It is **not an accepted
decision**. Compare it with retaining a narrowly scoped authenticated control
capability; account explicitly for cold phone-owner recovery and healthy-source
denial of service. Avoid persistent per-renewal state or changes to media latency.

Specify only what High needs to implement:

1. Exact enrolled/source/boot/worker/revision and actual connection bindings;
   proof domain and fresh challenge/request rules; what current approved
   delivery can already request without extra positive proof.
2. How authenticated negative admission bypasses a blocked positive signing job
   without weakening enrollment checks, making stale sources current or renewing
   `AuthorityUntil`. Decide whether it shares N2 sequencing or needs a separate
   bounded negative lane; do not assume either choice.
3. Immediate denial versus retained asynchronous rundown, lost connection and
   late completion; exact retry/status/terminal acknowledgement semantics and
   bounded retention. No positive grant receipt may imply cleanup completion.
4. Phone cancellation before versus after positive admission, and how that maps
   to source release when no delivery exists. Preserve actual I/O/signing custody.
5. Tests and an ordered High implementation handoff, including initial Acquire,
   first Attach failure, disconnect/cold owner, concurrent positive work, stale
   source/connection, revocation and failed rundown/quarantine.

Keep the settled source/delivery split, fresh per-delivery native keys, exact
certificate/hash/nonces, bounded request history, fixed destination policy,
queued-ciphertext retirement, native ownership and existing latency work. No
extra process, PCM queue/copy, periodic disk ledger, general lease framework or
root arbitrary-data signer is needed for this decision.

## High work after the decision

Implement that narrow negative path and its behavior tests. Finish the central
bounded broker-pipe reader/demultiplexer before adding unsolicited N3 messages:
`BrokerAudioWorkerSession.PublishCore` currently writes a grant and reads its
acknowledgement directly, so another reader would race it. Add native setup/
connection custody and bounded/rate-limited candidates off the active media
service loop; report actual TLS facts over the process-bound SYSTEM channel.
Route typed descriptors/challenges/proofs/status and approve only their exact
retained observation. Keep source/output readiness independent. Then complete
N4 source/egress/rundown, N5 adapter integration and N6 independent Android owner.

No service, deployment, driver/PnP, endpoint/default route, hardware or persistent
host/enrollment key operation was performed by this N3 foundation iteration.
Its tests generate disposable TLS keys in memory and exchange packets in memory.

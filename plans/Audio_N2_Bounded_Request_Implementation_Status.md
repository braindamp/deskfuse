# N2 bounded requests — High implementation status

29 October 2026. The focused implementation of
[Audio_N2_Bounded_Request_Retention_High_Decision.md](Audio_N2_Bounded_Request_Retention_High_Decision.md)
is implemented in source and checked offline. No further design stop was found.
Continue **N3 native TLS/control on High** under the existing native ownership
handoff. This is not installed/live E6 qualification or production activation.

Checkout: `D:\Coding\DeskFuse\vnext-audio-latency`, branch
`experiment/vnext-audio-latency`, committed base
`09fd7a86b3c674fb1580705b734e64086a5b4e1a`. The N2 draft, independent High/N5
foundation and this continuation remain uncommitted/unpushed. Preserve all work.

## Implemented

- `AudioRequestProtocol.cs`: canonical structured RequestId (u64 sequence plus
  24 random bytes), frozen audio proof wrapper, domain-separated root challenge
  message and strict query-bound request-status codec. Full unsigned sequence
  encoding is retained. Generic proof formats and native control/media formats
  are unchanged; Renew still names the same Delivery digest.
- `BrokerProofChallenges` / `BrokerAuthority`: typed audio issue/admit paths use
  the full wrapped transcript. Absolute proof expiry is frozen within policy and
  parent bounds. Verification remains outside authority locks; current protected
  facts/key/revision are rechecked at final admission. Exact retirement removes
  underlying nonces, including failure after allocation but before host signing
  returns. Generic issuance also releases its nonce on host-signing failure.
- `BrokerAudioOwner`: issued-through scalar, one real pending control slot and
  eight applied replies replace lifetime request/operation dictionaries. Exact
  successor only; Busy/InProgress do not allocate another operation. Failed or
  unproved attempts stay stale via the floor without accumulating tombstones.
  Signing/verification stays retained until actual terminal completion.
- Acquisition custody is independent of reply-cache residency. Evicting an old
  acquisition reply cannot invalidate the source's preparation claim or permit
  another preparation. Exact commit capabilities check owned records. One-use
  receipt-signing material prevents an old capability from bypassing the signing
  attempt/cache rules. Failed signing is latched; late denial cannot acknowledge
  live authority. Benign output-only revisions do not invalidate applied receipts.
- Retained `Connection` observations bind the actual worker and connection
  incarnation/certificate hashes/native nonce. Current-delivery control is
  accepted only through its approved observation. Candidates cannot reserve or
  root-sign status through the healthy lane. Live handoff is nominated by the
  old approved connection and binds the new observed one. Candidate closure and
  state invalidation retire pending work; old connection closure cannot detach a
  replacement. Observation capacity is bounded at 16. These are internal typed
  boundaries; N3 still supplies the real TLS observations and frame routing.
- `BrokerHostIdentity`: dedicated audio challenge and typed request-status signing
  validate installed host/namespace context. Audio receipts use the committed
  record's single-use signing material. No network arbitrary-data signer exists.
- Shipping Android `AudioRequests.kt`: one independent control-state owner holds
  the proposed identity separately from confirmed issuance, first-send BOOTTIME
  anchor, exact challenge and exact phone signature. Busy retains its proposal;
  failed signing cannot be retried as another randomized signature. Cancellation
  retains actual signing/verification custody. Generic signature-cache eviction
  does not own audio retries. Cached audio signatures still check current
  enrollment, key availability and exact key identity.
- Typed audio approval/root verification signs the complete audio proof. Receipt
  acceptance uses retained Pending state, requires exact frozen cutoff and applied
  revision, and rejects superseded/abandoned/terminal work. Duplicate receipt
  returns the existing window without re-anchoring it. Cold phone-owner recovery
  cannot adopt a saved runtime receipt.
- Root/query/source-bound resync is read-only and bounded. It reports the issued
  floor plus active request/stage. An unreserved proposal is retained; a burned,
  retired request gets a different successor identity. New status signing has its
  own one-job bound and cannot deadlock behind an awaited phone proof. Resync does
  not change authority, restore a terminal window or discard still-running work.

Crypto, dictionaries and storage remain setup/control work. No native media
source, PCM queue, source timestamp, transport packet or renderer path changed.
The 16 native source files in the previous High source receipt matched their
hashes in this pass. No measured latency/CPU/power improvement is claimed here.

## Verification actually run

- **75 focused C# checks passed, zero failed/skipped**, including the updated
  shipping Kotlin exchange through real JCA P-256 signing and C# verification.
  Receipt: `out/n2-bounded-checks/n2-bounded-focused.trx`.
- A 5000-renewal fake-clock behavior check exercises actual phone proof admission
  and signed receipts. The source/delivery/output remain stable, the applied
  cache stays at eight, and pending broker nonce count returns to zero. Old
  acquisition custody survives reply eviction.
- Cases include unsigned-long boundaries, skipped/MAX injection, bare generic
  audio proof and boot/source/request/revision/cutoff substitution; candidate
  churn, live handoff/closure, Busy, blocked issuance and 33 consecutive failed
  challenge signatures without leaking the underlying nonce registry; denied or
  uncertain receipt signing; and pre-admission versus applied output revisions.
- The shipping Kotlin harness checks request-correlated signed resync, wrong
  source/query/signature rejection, unreserved versus burned request behavior,
  fresh successor anchors, exact duplicate windows, cold-owner rejection, key
  loss and local denial on cached signatures, latched signing failure and retained
  custody through blocked signing. Its normal generic broker proof check passed.
- Broker build including its actual native DLL target passed: **0 warnings,
  0 errors**. Android `:protocol3:interopJar :protocol3:checkBrokerProof
  :app:compileDebugKotlin --offline` passed. The existing ARM64 native library was
  up to date; this continuation did not require rebuilding native media code.
- Two Rust audio-authority and three Rust broker-control checks passed. Their
  unchanged wire projection and terminal/deadline rules remain in force.
- Command-local CRLF-aware diff whitespace check passed. Source/artifact hashes
  and result paths are saved in `out/n2-bounded-checks/source-receipt.json`.

All qualification above is source/offline. No production host key, service,
installation, driver/PnP, endpoint/route, device or phone operation was performed.
`Program.cs` still refuses production authority. The installed protected worker,
actual TLS identity/connection, COM/device, power/resume, endpoint/driver and
performance gates remain their existing independent qualification work.

## Next High step

N3 supplies per-delivery in-memory keys and actual TLS/quarantined connection
ownership, routes bounded typed public controls over the registered worker pipe,
uses the approved observation for healthy control and nominates observed new
connections for handoff. Neither a network authentication boolean nor a generic
Speaker proof may enter the new owner. Root status is not a grant.

Then N4 connects actual source/egress/cutoff/rundown, the prepared N5 native
capture adapter joins that source owner, and N6 wires `AudioPhoneRequests` into
the independent Android owner and real renewal/recovery. Preserve original source
age/fence rules and the explicit policy inputs. Installed service entry remains
blocked until this whole chain and required qualification are ready.

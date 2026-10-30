# Stop/Game/handover — High implementation status

30 October 2026. **Implementation in progress; production authority disabled.**
Plan: [combined handoff](Audio_Stop_Game_And_Handover_High_Handoff.md) and
[reviewed handover decision](Audio_Handover_Reviewed_High_Decision.md).
Baseline `f00c912`, branch `experiment/vnext-audio-latency`.

## Iteration 1 — shared control and exact outcome recovery

Implemented in source:

- Game-only setup mode validation in C#/Kotlin/Rust, including pre-launch and
  pre-association checks. Source.v1/Delivery.v1/proof/receipt bytes remain as
  before; existing canonical vectors pass. No per-packet mode dispatch.
- Strict SourceStop/status/query codecs and bounded signed envelope, shared
  C#/Rust vector and shipping phone encoding. Native framing now distinguishes
  signed negative Stop, read-only Stop status and applied-result queries.
- Broker negative-facts capture/recheck excludes positive audio revision/cutoff
  equality while retaining actual worker/enrollment/key/subject/source checks.
  Enrolled-signature Stop and exact current-delivery fast Stop deny immediately
  and invalidate positive work. Captured negative ingress remains owned through
  connection closure and counts against the fixed observation budget.
- One independent verification slot and one exact-query status signing slot.
  Status reports Unknown/Denied from actual broker state; **no physical rundown
  success is exposed by this iteration**. Complete/Quarantined wire values are
  defined for subsequent actual owner integration. Terminal broker lookup/index
  and post-worker status routing still need wiring.
- Phone local Stop intent closes positive admission first, retains the same
  negative intent, signs only its typed connection-bound transcript and caches
  the exact result. Negative signing has its own bounded job and bypasses the
  positive software signing gate. Key availability/enrollment are rechecked;
  uncertain signing is latched. Native/Android physical Stop is not yet wired.
- Read-only `BrokerAudioOwner.GetAppliedResult` returns only the exact retained
  applied operation to eligible current ingress. It never issues/admit-dispatches
  a miss or advances N2 history. Phone signed resync preserves the surviving
  verified Supersede matching the current target before generic pending retirement.
  The exact recovery body keeps its RequestId, broker nonce and phone signature;
  cached receipt acceptance keeps the original send-time anchor.
- Native control queues/pending accounting reserve one negative command beyond
  eight regular slots, including an error response to that negative request.
  Unverified negative framing never grants media authority. End-to-end broker
  pipe/dispatch/fence capacity remains the next integration step.

All work above is setup/control. Capture, committed source age, sample/packet
timelines, PCM storage/assembly and AAudio/ASRC processing were not changed.

## Verification actually run

- **41 focused C# checks passed**, no failure/skip, including source Stop before
  Acquire/after lost admission receipt, blocked positive signing, wrong signed
  source/association/TLS facts, closure during final verification, query retry
  and signer-failure latching, exact applied-result recovery and existing N2
  authority cases. `out/control-handover-checks/control-authority.trx`.
- Actual shipping Kotlin code performs real JCA P-256 signing accepted by C#
  for positive and negative proofs. The exchange includes Supersede committed
  with an empty active slot/lost reply, exact retrieval bytes, duplicate receipt
  deadline stability, local Stop, signed Denied status, key loss/local denial,
  and a blocked positive software gate while a negative signature completes.
- **10 focused native checks passed**, including actual mutual TLS/control,
  the full eight-regular-plus-one-negative command/response exchange, strict
  shared Stop vectors and original authority/freshness/reactor behavior.
- Broker build plus native DLL target passed with zero warnings/errors. Fixed
  worker x64 Release/DLL staging passed. Android ARM64 native rebuild and
  `:app:compileDebugKotlin` passed; six pre-existing lab/conditional warnings
  remain. JVM interoperability jar and broker-proof check passed.
- No installed service, host/enrollment key creation, driver/PnP/route, phone
  deployment or physical audio/network operation was run. No latency/power
  improvement or gapless handover is claimed by offline checks.

## Iteration 2 — sole pipe reader and retained shutdown

Implemented the post-bootstrap control pump with one reader, exact correlated
authority acknowledgements, one regular projection and a separate restrictive
projection. Unsolicited native events are demultiplexed without a competing
reader. Stop submission does not wait for a positive acknowledgement. Invalid
acknowledgements fail the retained channel; it never reconnects in place.

Caller cancellation fences authority but retains actual I/O. Shutdown joins the
reader, outstanding writes/projections and typed event-owner work before releasing
the process-bound caller handles. Native event frame kinds are reserved on both
sides; listener/request/rundown handlers remain the next integration step.

Seven focused control-pump checks passed (including interleaved events, exact
and conflicting acknowledgements, separate restrictive capacity, and delayed
actual write completion after cancellation). Broker/native DLL build passed
with zero warnings/errors after loading the Visual Studio build environment.
The three native broker-control checks also passed. No media processing changed.

## Iteration 3 — bounded broker request routing and captured negative ingress

Implemented a typed dispatcher for actual-worker connection observations,
source/binding metadata, current-ingress delivery nomination, challenge/proof,
exact applied-result lookup, output/detach, Stop and query-bound status. It owns
eight regular jobs, a separate negative job, one bounded nomination and actual
job/reply completion across TLS closure. Source preparation/publication are
mandatory resource-owner dependencies; fixtures do not stand in for production
native composition. The exact unpublished encoding is recorded in
[Audio_Native_Control_RPC.md](Audio_Native_Control_RPC.md).

Signed Stop is captured/pinned on trusted ingress before asynchronous dispatch;
it survives queued connection closure and rechecks protected registration and
enrollment after crypto. Fast Stop fences at ingress without crypto. Actual
pipe completions are retained even when their TLS route closes. The pump has
eight regular reply writes and a separate negative reply slot. Ordinary source
projections coalesce through exact ACKs, with publication completion serialized
against new coalescing callers; restrictive projection remains independent.

Added matching strict C#/Rust RPC codecs/shared vectors and native worker-wide
forwarding credits. Native TLS validates the same bodies on send and receive.
The forwarding component is source checked; connecting it to the configured
listener/one-reader native runtime is still open. No active PCM queue/copy,
capture behavior, sample timeline, freshness policy or renderer was changed.

**54 focused C# checks passed**, including real-signature routed Acquire/Attach,
exact read-only recovery, negative Stop with eight retained regular jobs and a
blocked positive signer, pinned-ingress capacity, revoked registration, and the
reserved negative pipe reply. **12 native checks passed**, including real TLS,
shared malformed vectors and worker-wide regular/negative forwarding capacity.
Broker/native DLL build passed with zero warnings/errors. Evidence:
`out/control-handover-checks/control-dispatch.trx`. No physical operations ran.

## Continue on High

The single broker-pipe reader/demultiplexer and bounded broker observation/request
routing are implemented. Finish concrete native handlers and resource-owner
composition, including listener configuration and real source preparation. Wire
negative control and real termination custody/lookup. Then implement native
candidate setup/transfer/old-egress barrier and phone receipt-based approval,
followed by the Network-handle-aware coordinator and cellular lease lifecycle
(reviewed H2–H4), preserving source/renderer/floors. H1 broker/phone primitives
are source checked; native request routing remains part of that integration.

Source Stop physical cleanup and successful terminal acknowledgement must follow
actual N4/N5 resource rundown. Keep Game-only admission, unchanged timing and the
reserved negative lane throughout. Installed/physical H5 qualification remains
separately authorized work. The task is ongoing; this checkpoint is not N3 or
handover completion.

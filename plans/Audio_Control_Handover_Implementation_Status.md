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

## Continue on High

Finish the single broker-pipe reader/demultiplexer and bounded actual observation/
request/fence routing; replace direct competing acknowledgement reads. Wire
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

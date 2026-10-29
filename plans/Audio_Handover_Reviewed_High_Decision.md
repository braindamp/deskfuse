# Wi-Fi/cellular audio handover — reviewed Ultra decision and High plan

29 October 2026. **Selected design, ready for High implementation. No runtime
implementation or qualification is claimed.** Source baseline `7df5216` on
`experiment/vnext-audio-latency`. This closes topic 1 of the
[three-topic handoff](Audio_Stop_Game_And_Handover_High_Handoff.md); its Stop and
Game decisions remain in force. Architecture v0.6, N2, N3/N4/N5 ownership and
native source/receiver timing remain authoritative. Production remains disabled.

Reviewed input: the user's
[Arena aggregate, preserved unchanged](Audio_Handover_Arena_Candidate_2026-10-29.md),
provided as `972c81f7-7521-431f-87c5-71813faeec2b/Pasted text.txt`.
The input is a candidate, not evidence of implemented behavior. Its external
"A3" migration label has no supplied companion artifact; this decision relies
on the stated proposal and actual code, not an assumed additional design.
Input SHA-256: `9deae7524d40ebc649e34e37a49abc3fb448a327fd45dec4bce023cf6d5e9625`.

## 1. Selected architecture and review adjustments

Use **one active authorized media delivery plus at most one automatic,
short-lived, quarantined replacement candidate**. Warm network reachability,
fresh native-owned TLS and bounded public control while the incumbent plays.
Authorize exactly one replacement just in time through existing Supersede.
No pre-authorized standby, dual PCM, extra playback buffer or simultaneous
positive authorization requests. This is Game's live-first handover.

| Candidate point | Reviewed disposition |
| --- | --- |
| Fresh TLS, selected-socket cellular routing, control-only preparation | Accepted. Preparation has no media/readiness/renewal authority. |
| One positive slot; live nomination through the current approved delivery | Accepted. Candidate preparation never reserves that slot. |
| "Never extend the original source authority deadline" | Clarified: preparation, callbacks, retries and status never extend it. The accepted N2/native contract explicitly permits a **fresh admitted Supersede proof** to renew the shared cutoff under its bounded policy. Preserve that behavior; do not invent a second lease or silently change Supersede. Issuance/admission must occur before the then-current source/grace cutoff. |
| "Atomic" commit/old egress retirement | A short broker authority publication is the logical commit. Actual native retirement is a separately observed serialized barrier, completed before any replacement PCM is sent. It is not established by a broker acknowledgement alone. |
| Cellular request transfer after success | Pin it before a possibly committing request. Promote the same owned lease at committed installation; do not release it while outcome is unknown or while the active path depends on it. |
| Lost acknowledgement / unchanged renderer | Requires the concrete recovery and phone-approval work below. Current foundations do not already supply all of it. |
| QUIC migration | Deferred. Keep fresh delivery keys and fixed destination policy. Quiche capability alone does not authorize a route change. |

The objective is no added healthy-path processing stage, PCM copy/queue, lock,
poll or duplicate media. Additional setup CPU, cellular traffic and radio power
exist and must be bounded/measured; zero audible gap or zero resource cost is
not a documented platform guarantee.

## 2. Owners, eligibility and Android networking

The independent phone audio owner holds one event-driven handover coordinator,
the active delivery and optional candidate. It survives workspace UI changes.
The candidate owns its network request/callback, Network handle, socket, fresh
TLS identity, observed native connection, attempt identity and setup deadline.
Use generation-tagged callbacks; stale acquisition/DNS/TLS completion cannot
change a later candidate. No new persistent process, capture queue or PCM relay.

Add `CHANGE_NETWORK_STATE`. Use one owned
`ConnectivityManager.requestNetwork` request with CELLULAR transport and INTERNET
capability, bounded acquisition and explicit unregister/release. Do not request
mutable VALIDATED as a request capability; assess capability/link/blocking updates
from callback snapshots. Bind **only the candidate UDP socket before connect**
using `Network.bindSocket(fd)` or `android_setsocknetwork`, and use that network's
DNS. Never bind the process or rebind the incumbent socket.
[Android requestNetwork](https://developer.android.com/reference/android/net/ConnectivityManager#requestNetwork(android.net.NetworkRequest,%20android.net.ConnectivityManager.NetworkCallback)),
[Network socket binding](https://developer.android.com/reference/android/net/Network#bindSocket(java.io.FileDescriptor)),
[NDK networking](https://developer.android.com/ndk/reference/group/networking#android_setsocknetwork).

Respect mobile-data consent, metering/roaming policy, Data Saver, blocked-network
callbacks and VPN/lockdown routing. Socket binding is not permission to bypass
VPN policy. Failure to obtain or use the approved route leaves healthy Wi-Fi
running. A LAN-only route can work without public Internet validation; do not
apply cellular Internet heuristics as a new requirement for healthy LAN playback.

The cellular target must be an already approved, reachable host/relay/VPN route
for the same enrolled host. A private Wi-Fi IP is insufficient. A locator/DNS
result is a routing hint, never host authority: actual TLS facts plus the root-
authenticated device-proof exchange must still match. No new relay/NAT service,
public firewall exposure or provider selection is part of this implementation.
If no approved cellular route exists, report handover unavailable and preserve
the available path; do not fabricate WAN support.

### Preparation and switching policy

Listen passively while the independent session is active. Preparation is a
coalesced reaction to an actual route/degradation event or explicit handover
intent, subject to consent and cooldown. Use `onLosing` only as a hint; abrupt
loss can have no warning. Use existing transport progress/closure and source/
receiver observations; neither RSSI, default-route change, UDP send acceptance
nor lack of audible sound proves peer delivery or authorized Detach.
[Android callbacks](https://developer.android.com/reference/android/net/ConnectivityManager.NetworkCallback#onLosing(android.net.Network,%20int)).

Use one explicit setup policy carrying acquisition deadline, warm-candidate TTL,
setup bytes/messages/work budget, bounded retry/backoff, switch hysteresis and
cooldown. Values are bounded configuration inputs to qualify; they are not new
media latency targets or authority lengths. Require them at feature admission
and leave automatic preparation disabled when consent/route/policy is absent.
No continuous RSSI polling, periodic PCM-path probe or permanent cellular standby.
Reuse native QUIC timers for a short candidate; do not add a keepalive service.
Stop candidate work at its budget and ignore obsolete completions with custody.

Reverse cellular-to-Wi-Fi handover uses the same replacement logic and stable-
path hysteresis. Do not repeatedly switch merely because the system default
changes; the actual active Network/socket may remain usable. Release an unused
cellular request after the active path and all dependent candidate I/O retire.

## 3. State and lease transitions

These are coordinator states, not new authority/grant states. Existing source
cutoff, Stop and revocation dominate every row.

| State | Allowed work / transition |
| --- | --- |
| ACTIVE | Existing media and normal renewal; no candidate work by default. A permitted trigger creates one attempt. |
| ACQUIRING | Request/resolve the selected network within its acquisition budget. Old delivery continues. Failure/cancellation releases only actually retired candidate resources. |
| WARMING | Bind the new socket, establish fresh TLS and bounded public observation. No positive request/PCM/readiness. |
| READY | Candidate is only transport-ready. Old delivery continues renewal; no parked Supersede proof. TTL/degradation may discard this candidate without changing source authority. |
| SUPERSEDING | Pin candidate/network custody and one retained N2 operation. Start one eligible Supersede, retaining source/renderer. TTL/callback cancellation now requests reconciliation; it cannot assume rollback or release a possibly committed path. |
| RECONCILING | Join/retrieve the exact retained result or wait for actual Detach/recovery eligibility. Keep first-send anchor, signature and actual I/O ownership. No competing positive operation. |
| COMMITTED | Broker selected the target. Transfer the candidate's same network lease and sole native connection custody to the active-delivery lifecycle, even while readiness/receipt recovery finishes. Media starts only after section 4's gates. Return to ACTIVE with cooldown. |
| RECOVERING | After actual authoritative Detach, retain only approved source/grace time, consume/discard undeliverable source material, and use the eligible fresh recovery successor when prior work is terminal. |
| CANCELLED / COOLDOWN | Retire unused setup work and suppress repeated preparation. These do not cancel committed authority or acknowledge cleanup. |

The cellular callback/request lives **with the active path**, not with a temporary
preparation coroutine. Acquisition and warm TTL timers must never unregister a
promoted or possibly committed lease. Preserve it through ordinary reconciliation.
If local authority expires or the whole phone owner becomes terminal, apply
local Stop, close/join dependent socket/native I/O and then release the lease;
retain any unknown remote outcome/custody and rely on source Stop/remote expiry.
Resource timeout may end the local session, but never assert remote rollback.

## 4. Just-in-time commit and native retirement barrier

1. On a warranted switch, recheck candidate/socket observations, policy/route,
   source/parent validity and N2 slot availability. Complete an incumbent renewal
   already in flight; do not burn a second request or cancel it based on a hint.
   If an incumbent renewal is due and feasible, service it before optional
   switching. Output/Stop/expiry fences remain immediate and can stale a proof.
2. Through the **current approved delivery**, nominate the exact worker-observed
   candidate. Obtain the native setup owner's bounded pin before issuance, so
   warm TTL cannot independently free it during a potentially committing request.
   A pin is custody, not authority, and cannot revive a closed candidate. Then
   issue one Supersede with the exact-next N2 RequestId. Freeze its
   normal revision, proof expiry and proposed cutoff. Phone checks actual target
   hashes/nonces/source descriptor and signs once. No proof is held while merely
   READY. No candidate can force old-path Detach to enter the recovery lane.
3. Broker admission atomically publishes its new delivery/revision/cutoff and
   retains the result. New logical authority does not mean native I/O has finished.
   Publish the target to the same source's native owner; do not reconstruct source
   authority or change source binding to fit the new delivery.
4. On a serialized native owner turn, apply the valid newer grant, close old
   delivery media admission, discard partial old-delivery assembly/ledger, old
   QUIC/Egress and pending paced/WouldBlock ciphertext. Do **not** drain old PCM
   or sealed packets. Complete actual setup/I/O custody transfer before permitting
   the new egress. A previously kernel-admitted send cannot be recalled.
5. Install the grant on the **new candidate connection object**, initially with
   media/readiness false. Obtain the exact root-signed grant receipt on the target
   and accept it against the surviving phone Pending. Then a narrow phone-native
   install binds that receipt/window to its actual candidate/source/TLS facts.
   Its local cutoff is the conservative Android BOOTTIME window anchored to the
   original send, never a Windows absolute cutoff compared to an Android clock.
   Use a retained verified owner capability, never a caller's approval boolean.
   Emit authenticated OutputReady for that new delivery and the still-valid
   physical renderer incarnation. Broker/native observations and cutoff must all
   agree before new media is admitted. A preflight renderer check grants nothing.
6. Preserve capture/endpoint/period, true source position/sequence and physical
   AAudio/ASRC/output clock. Rebind delivery-scoped readiness without automatically
   reopening the renderer. If the actual output route changed or failed, use the
   existing output-recovery lifecycle; do not claim that a failed renderer stayed
   healthy. Game resumes at timely source material with existing discontinuity/
   fresh-anchor rules; original commit/presentation timestamps remain unchanged.

Windows may share one listener UDP socket between both QUIC connections. Keep
**one socket ingress/readiness owner**. Serve active packets inline; hand off
only candidate encrypted datagrams/control to a bounded setup owner, with a
fixed queue/byte/work budget and overload drop. Never introduce another reader
racing `recv_from`, nor a new active-media queue/copy. Candidate quiche objects
discard unauthorized media DATAGRAMs within that budget; preparation never
parses/admits them as PCM. Candidate objects
are touched by one owner at a time. At the transfer barrier, stop setup service,
join actual candidate work and transfer ownership exactly once. Retiring the
old logical connection must not close the shared listener needed by the target.
Use the accepted protected-thread launch policy and existing bounded event wait.

### Source-binding review: existing Rust rules are correct

`AudioAuthority::apply_inner` already accepts a newer nonzero delivery generation/
digest for the same source and unchanged nonzero source binding. It retains
`source_observed` for an unchanged binding and clears only delivery/output
observations. It rejects older revisions, source substitution and generation
rollback. Keep those checks. `ControlConnection::install_native` intentionally
rejects changing an approved connection's full binding; use the new object.
The missing work is actor/barrier/phone installation and observation routing,
not weakening either guard.

## 5. Lost commit replies and failure recovery

The existing retained-result path supports this design, but the Android resync
rule must be corrected before handover implementation:

- `BrokerAudioOwner.Admit` checks the applied cache before requiring the original
  uncommitted origin. After commit, the **exact new current target** can replay
  the original broker nonce and exact phone signature to obtain/join the same
  committed receipt. Factor those checks into a narrow **GetAppliedResult**
  broker/control operation for recovery; it may only look up/join an applied
  record, never fall through to Issue or uncommitted Admit. Its bounded request
  is `RequestId[32] || BrokerNonce[32] || u8 signature length || exact original
  phone signature`. All three must match the retained record and current
  source/target ingress. The reply is the existing exact signed receipt, bounded
  InProgress or Unavailable. A read-only miss cannot burn a sequence or prove
  rollback. This adds a small N3 control operation, not a new proof format,
  signature, grant or positive authorization attempt.
- `Issue`/`Admit`/`SignedRequestStatus` still require legitimate current-origin or
  actual post-Detach recovery eligibility. While the old delivery is current,
  an arbitrary candidate cannot use this path to reserve, sign status, force
  Detach or advance the sequence. An ineligible/failed reply is not proof of
  rollback. Do not duplicate issuance over both connections.
- Current `AudioPhoneRequests.resync` retires Pending when the broker reports no
  active RequestId. A successfully applied/signature-completed Supersede also
  leaves an empty active slot. Therefore, **before** generic retirement, recognize
  a live retained verified Supersede whose exact target digest/generation,
  source and boot match root-signed current status, with an applied-or-later
  revision and cutoff sufficient for its frozen operation. Preserve that Pending
  and
  fetch the exact receipt through the now-current target. Try the applied-only
  exact-result path directly after ambiguous commit; use bounded status/resync
  when needed. A read-only status is never a grant/window or permission to send.
- Preserve original RequestId, verified proof, phone signature, first-send anchor,
  minimum accepted revision and uncertain actual work. A status revision does
  not become an accepted grant revision. Never re-anchor the receipt, lower a
  floor, regenerate its signature or reconstruct a cold runtime owner. If the
  local owner/window has expired, receipt recovery cannot revive it.
- If old-path failure occurs **before** commit, wait for actual native/authority
  Detach. Retire the old-bound request through real completion, reconcile its
  sequence and create a fresh eligible successor bound to the candidate under
  the surviving source/grace limit. A phone network callback alone is not Detach.
  A blackhole may require the existing bounded native transport/liveness timeout;
  source expiry wins if recovery is too late.
- If commit occurred, old-connection closure must not detach the new target.
  Failure of the committed target follows its own actual Detach/grace rules;
  there is no rollback to old keys/egress. Receipt signing failure, an unavailable
  exact result or expired/terminal state fails closed using Stop/expiry and
  retained rundown. Do not manufacture success or an alternate grant receipt.

Fresh TLS does not reset a surviving source's receiver rejection frontier.
Retain accepted/presented packet and source-frame floors plus valid playback
state in the independent owner. Clear old delivery transport/reorder residue and
reject retired connection callbacks/media. Existing source freshness/fence logic
still decides whether material can be played; handover adds no replay buffer,
WSOLA, playout inflation or timestamp-split double egress.

## 6. High implementation order and acceptance

Integrate with the Stop/Game steps in the parent handoff. Use small source
iterations with relevant checks and optional commit/push checkpoints:

| Order | Concrete work / exit evidence |
| --- | --- |
| H1 — exact outcome recovery | In `AudioRequests.kt`, preserve the exact target-matching Supersede Pending before generic resync retirement. Add bounded read-only GetAppliedResult, reusing broker applied-cache checks and receipt custody, through the current target with native response correlation. Check lost reply, empty active slot, original anchor, stale target/status and latched signing failure. Preserve N2 proof/receipt/status formats. |
| H2 — native lifecycle | Finish single broker-pipe reader/demux, actual observation routing, one UDP reader, bounded candidate setup and connection ownership transfer. Implement old-egress barrier and narrow phone receipt-based approval/readiness. Check stable source binding, newer generation, paced/WouldBlock retirement, old close after replacement and failed transfer. |
| H3 — independent phone coordinator | Add Network-handle-aware observations, consent/route/policy admission, selected-socket binding and one lease/callback lifecycle. The new authority path must not inherit `SpeakerController.onDefaultNetworkChanged`'s unconditional `nativeAudio.stop()` / `output.stop()` behavior. Keep legacy development paths distinct. Check lease pin/promotion/cancel/unknown outcome and stale callbacks with deterministic adapters. |
| H4 — integration | Wire current-delivery nomination, one just-in-time Supersede, preserved renderer/source floors and negative Stop preemption. Use existing fake-clock/crypto/native fixtures to exercise current and post-Detach lanes. No relay service, driver ABI change or future buffered mode is needed. |
| H5 — authorized qualification | Installed/device/network work remains separately authorized. Measure actual make-before-break and abrupt/blackhole behavior with identified source/build/artifacts, latency distributions and radio cost before production approval. |

Policy values are explicit bounded inputs; High can implement admission/timeout
mechanics and deterministic boundary checks without selecting unsupported product
defaults. Release qualification must supply and measure their chosen profile.

Essential combined scenarios: gradual degradation, AP loss and silent blackhole;
renewal/output revision during switch; commit with lost receipt; detach before
proof; candidate TTL/failure and flapping; callback/lease transfer races; Stop,
revoke and source/local expiry at each phase; output failure without source
restart; VPN/lockdown, Data Saver/roaming/mobile consent; screen-off/OEM behavior;
Wi-Fi return; IPv4/IPv6/DNS route mismatch and unavailable WAN endpoint.

Measure preparation/TLS, proof/authorization, native barrier, receipt/readiness,
first valid replacement media and **actual audible interruption** separately.
Record source/renderer identity, source-to-speaker latency distributions, packet/
source gaps and frontier continuity, CPU/wakes/allocations, cellular bytes and
battery/radio cost against matched healthy playback. Do not infer audible
continuity from a successful send, READY callback or unchanged object identity.

## 7. Evidence and return point

Reviewed the Arena input, accepted N2/native/Stop/Game plans and actual broker
Issue/Admit/receipt/status/Detach code; Rust authority, connection and Egress
guards; Android Pending/resync/receipt/network/controller paths and receive
frontier. Platform statements use the primary Android/pinned quiche sources in
this decision and the original research question. The review selected the lean
candidate, corrected its lifecycle details and identified necessary source work.
No implementation code, build, runtime test, installation, network/device change
or physical measurement was performed. **The three-topic design is complete;
return to High for the ordered implementation.**

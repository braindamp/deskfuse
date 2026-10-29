# DeskFuse — Wi-Fi → Cellular Audio Handover
**Final Design Candidate for Codex Review**

**Status:** Provisional architecture. Review against the current repository, finalize and save the engineering decision, then implement only after approval.

## 1. Recommended Architecture

Use **one active authorized audio delivery and at most one short-lived, quarantined cellular candidate**.

Prepare the candidate while Wi-Fi remains active:

- Request cellular connectivity and bind only the candidate UDP socket to it.
- Resolve the approved cellular-reachable endpoint using network-specific DNS.
- Establish a new QUIC/TLS connection with fresh, memory-only credentials.
- Complete TLS identity and candidate observation checks.
- **Do not authorize the candidate or transmit PCM during preparation.**

When switching becomes necessary, execute **one just-in-time Supersede authorization**. Commit the new delivery and retire the old media egress atomically under the existing authority rules.

Do **not** implement a pre-authorized standby, dual media, bicasting, or duplicate authorization requests over both connections.

## 2. Preserve Existing Security Contracts

The current N2/N3 architecture remains authoritative:

- Exactly one positive authorization operation in flight.
- Proof binds the actual candidate certificates, nonces, source context, expected revision and frozen authority cutoff.
- Live Supersede originates through the **currently approved delivery**, not through an arbitrary quarantined candidate.
- After actual incumbent detachment, use only the existing bounded recovery eligibility.
- Never skip request sequences, locally assume remote cancellation, or discard uncertain signing/commit custody.
- Reuse N2's retained-request and authenticated status/resync mechanisms.
- Never extend the original source authority deadline or reconnect grace.

**Codex must verify the actual replacement transition**, including the `AudioAuthority::apply_inner()` binding rules, before relying on Supersede. Do not relax source-binding or generation checks merely to make handover work.

A quarantined connection may participate in recovery only when the authoritative lifecycle explicitly permits it.

## 3. Android Network Ownership

Add the manifest permission `CHANGE_NETWORK_STATE`.

Use `ConnectivityManager.requestNetwork(TRANSPORT_CELLULAR + INTERNET)` with bounded acquisition time and a single owned callback.

Bind the candidate socket using `Network.bindSocket()` or native `android_setsocknetwork()` **before connecting**. Never bind the process globally.

Requirements:

- Cellular endpoint must already be reachable and explicitly approved; private Wi-Fi LAN addresses cannot be assumed reachable.
- Respect VPN/lockdown behavior, Data Saver, blocked networks, roaming and explicit mobile-data consent.
- Treat network callbacks and RSSI as advisory; use actual transport and audio delivery observations for switching decisions.
- Support abrupt Wi-Fi failure without advance warning.
- Use bounded candidate TTL, cooldown and hysteresis. No permanent cellular standby or continuous polling.
- **After successful cellular commit, transfer ownership of the cellular network request to the active delivery.** Do not unregister it while the active media path still depends on it.

## 4. Audio Continuity

Preserve the existing Windows capture source, encoder, source timeline and Android native renderer across handover.

A new delivery generation requires its proper authority/readiness observations, but **must not automatically recreate AAudio, restart the output clock or reset valid playback state**.

Retain original source timestamps and monotonic playback floors. Continue the live-first policy: discard late data rather than accumulate a handover backlog.

No extra healthy-path PCM copies, queues, locks, polling or additional media streams.

Do not introduce timestamp-split dual-egress draining, WSOLA, or temporary playback-buffer inflation in the initial design.

## 5. State Machine and Failure Handling

Recommended conceptual lifecycle:

`ACTIVE → ACQUIRING → WARMING → READY → SUPERSEDING → COMMITTED`

With bounded `CANCELLED`, `RECOVERING` and `COOLDOWN` exits.

While `READY`, the candidate is transport-capable but completely unauthorized for media. Active Wi-Fi renewals continue normally.

At switch:

1. Verify the candidate, current authority and single-flight availability.
2. Run one eligible Supersede operation.
3. Apply the verified replacement authority and required readiness observations.
4. Retire the old media egress with actual native custody.
5. Begin new-path media and preserve renderer/source continuity.
6. Transfer the cellular network lease.

If the old path disappears first, follow actual Detach and bounded recovery rules. If authorization outcome is unknown, reconcile it—never assume rollback.

Stale callbacks, proofs and media from retired generations must not revive old state.

## 6. QUIC Migration Decision

**Do not implement QUIC path migration in the initial version.**

Pinned `quiche = 0.30.0` supports path migration, but DeskFuse currently disables it and enforces fixed routes. Migration also requires the same approved peer endpoint across both networks, plus changes to socket routing, path validation, CID handling and security policy.

Retain A3's migration proposal as a separate future optimization if real measurements justify it.

## 7. Required Verification

Before production approval, test:

- Gradual Wi-Fi degradation, instantaneous AP loss and silent Wi-Fi blackholes.
- Renewal in flight when Wi-Fi fails.
- Supersede committed remotely but acknowledgement lost.
- Candidate failure, TTL expiry, stale callbacks and network flapping.
- VPN/lockdown, Data Saver, roaming, screen-off playback and OEM restrictions.
- Renderer identity, source continuity, delivery-generation gates and retired-egress custody.
- No healthy-path audio performance regression.

Measure network preparation, authorization critical path, first valid new-path media, **actual audible interruption**, source-to-speaker latency, cellular bytes and battery/radio cost.

Do not promise gapless audio or fixed millisecond bounds without physical-device measurements.

## 8. Instructions to Codex

Independently inspect the current implementation and N2/N3/N4/N5 contracts. Challenge this candidate wherever the repository contradicts it.

Produce the **smallest correct implementation design**, identifying any genuinely necessary protocol changes rather than assuming they already exist.

Preserve existing completed work and security invariants.

**Finalize and save the reviewed design decision before writing implementation code.**

The objective is the lowest practical interruption with unchanged healthy-path playback, minimal new complexity and no compromise of authorization correctness.
# DeskFuse — Wi-Fi → Cellular Handover Design

Branch: `arena/e6085c00-deskfuse` (from `experiment/vnext-audio-latency`, commit `7df5216`)
Reference repo: https://github.com/braindamp/deskfuse
Status: **Design only.** No implementation, build, test, or device/network operation is authorized by this document. It answers Arena research topic 1 and records the recommendation to be saved by review; it does not select implementation yet.

Conventions used throughout:
- `[G]` documented guarantee — official Android documentation, QUIC RFC, or pinned quiche source in this repo.
- `[I]` engineering inference — not a documented guarantee; treat as a design assumption to validate.
- `[E]` device experiment required — cannot be guaranteed from documentation or unit tests alone.

## 0. Scope and the hard prerequisite

Cellular use requires a **reachable remote Windows endpoint or an approved relay/VPN route over the cellular path**. That is a prerequisite this design assumes; it does not design NAT traversal, relays, or a VPN platform.

LAN addresses (e.g. the lab Windows `192.168.2.107`) and the scoped Windows firewall rule (Private-profile UDP `48235` to the phone's current LAN address) **cannot be reused from cellular**: the cellular path observes different addresses, carrier NAT, and possible carrier filtering. Do not expand this into a general NAT/relay-platform design.

Non-goals: permanent dual media, process-wide network rebinding, QUIC migration under the pinned policy, general WAN/relay design, any change to the Windows trusted base.

## 1. Android APIs: request and retain cellular alongside Wi-Fi; bind only the candidate socket

### 1.1 Requesting an additional network (`[G]`)

- `ConnectivityManager.requestNetwork(NetworkRequest, NetworkCallback)` with a `NetworkRequest.Builder()` adding `NET_CAPABILITY_INTERNET` and `TRANSPORT_CELLULAR` requests an additional cellular network **while Wi-Fi remains the default network**. The two can coexist; the app never asks cellular to become the default.
- Required permission: `android.permission.INTERNET` only (already held in the manifest). `CHANGE_NETWORK_STATE` is **not** required for `requestNetwork`, and we intentionally never change the default network, so we never need that permission.
- The request/callback needs explicit ownership: the cellular network stays retained only while the `NetworkCallback` is registered. Release it with `unregisterNetworkCallback` when the candidate is torn down, committed, or failed. A passive callback alone does not retain the network.
- All of the above is available at minSdk 30.

### 1.2 Binding only the candidate socket (`[G]`)

- In `NetworkCallback.onAvailable(network)`, create the QUIC/UDP socket, call `network.bindSocket(socket.fileDescriptor)` **before** `connect()`, and resolve names through that network (e.g. `network.getAllByName()`, or the network-specific `DnsResolver` via `network.getRegistrar()` at API 30+). The NDK equivalent is `android_setsocknetwork`.
- Do not rebind any other socket, and never call `setProcessDefaultNetwork`: process-wide rebinding would affect other traffic and is explicitly not the design.
- Candidate-only binding means the active Wi-Fi media path and any other app traffic are untouched by candidate preparation.

### 1.3 VPN, OS/OEM, and background-execution limits

- `NET_CAPABILITY_NOT_VPN` can exclude VPN interfaces from the candidate. If an always-on or per-app VPN is active, every interface may be under it and the app cannot override VPN policy; cellular reachability then still depends on the VPN/relay route being reachable — a prerequisite matter, not something `requestNetwork` fixes (`[G]` + `[I]`).
- OEM battery optimization (e.g. Xiaomi MIUI restricted background) can delay or suppress callbacks and socket activity when the app is backgrounded. Mitigation within our control: keep candidate work inside the existing foreground-service lifecycle (`FOREGROUND_SERVICE_MEDIA_PLAYBACK` / `dataSync` are already declared). We cannot force whitelisting by API; the user-directed ignore-battery-optimizations setting is the only OEM escape hatch (`[I]`).
- If the app process is killed, callback registration and candidate ownership are lost: there is no persisted `requestNetwork` across process death (`[I]`).

### 1.4 Deliberately not used

`CHANGE_NETWORK_STATE` (not held, not needed), making cellular the default network, `NetworkFactory`, VPN APIs, or permanent dual-network residency.

## 2. Signals: what triggers preparation and switching

### 2.1 Useful early warnings

- `NetworkCallback.onAvailable(cellular)` plus `onCapabilitiesChanged` (`hasTransport(TRANSPORT_CELLULAR)`, `NET_CAPABILITY_VALIDATED`, metered flag): the candidate network exists and the candidate socket can be prepared while Wi-Fi is still healthy (`[G]`).
- Telephony radio/signal state (e.g. `TelephonyManager` data and signal tracking): a hint that cellular may soon be needed. Treat as a hint only — never as a handover commit authority (`[I]`).
- Local active-path health: QUIC idle/keepalive timeouts, missing PING/ack, media-arrival gap versus the playout floor. These detect Wi-Fi degradation locally and can start preparation before loss is certain (`[I]`).

### 2.2 Callbacks not guaranteed before Wi-Fi fails

- `onLosing(network, maxMsToLive)` is advisory; **a prior callback is not guaranteed on abrupt loss** (AP deauthentication, out of range, driver crash, Wi-Fi radio off) (`[G]`).
- `onLost` fires after the loss, never before (`[G]`).
- Consequence: preparation starts on early warnings, not on `onLosing`; switching must survive abrupt Wi-Fi loss through bounded source-side grace recovery.

### 2.3 Abrupt loss, reverse handover, flapping

- Abrupt Wi-Fi loss: the old path dies mid-playback. If a candidate is already `READY`, it commits; if none is ready, the source uses its bounded reconnect grace and re-prepares on whichever network returns. Playback continues within the grace; source and renderer survive (`[I]`).
- Reverse handover (cellular → Wi-Fi): fully symmetric — prepare a Wi-Fi candidate while cellular is active; whichever candidate reaches `READY` first and stays stable commits. No permanent preference for either bearer (`[I]`).
- Flapping: anti-flap hysteresis — require candidate stability (available + validated TLS) for a stability window before commit, plus a commit cooldown; brief dips never tear the active path down (`[I]`).

## 3. Fresh TLS/control warm-up vs QUIC path migration (pinned quiche 0.30)

### 3.1 What the pinned code does today

- `vnext/m2a/quiche-lab/Cargo.toml` pins `quiche = "=0.30.0"` and `Cargo.lock` resolves it (`[G]`, repo source).
- `audio_tls.rs`: `config.set_disable_active_migration(true)`. Both lab binaries do the same (`[G]`, repo source).
- `audio_transport.rs` `receive()` ignores any packet where `info.from != self.peer || info.to != self.local` — "Destination policy 1 never authorizes migration or NAT rebinding". Wrong-path packets are dropped without killing a healthy delivery (`[G]`, repo source).
- quiche 0.30 exposes `Connection::probe_path()` (RFC 9000 path validation), but with active migration disabled, probing does not move traffic (`[G]`, docs.rs 0.30).

### 3.2 Option A — QUIC path migration

Would require, on both peers: enabling active migration (`set_disable_active_migration(false)`), accepting changed peer/local addresses in `receive()`, re-anchoring the "approved destination" (`ControlConnection` pins `local` + `peer` `SocketAddr`), and making trust/authorization tolerate the new path. Under migration the connection ID and TLS session stay the same, so `Observation.connection` and certificate hashes remain valid, but the peer address the server sees changes across carrier NAT — the pinned destination policy would have to change, and path validation on a strict-freshness media path adds unbudgeted state (`[I]`).

### 3.3 Option B — Warm a fresh TLS/control connection on cellular, authorize just before switching

- New quiche `ConnectionId`, fresh ephemeral in-memory TLS certificates (bounded lifetime; `Identity::generate` issues ≈30-minute profile certificates), new handshake over the socket bound to the cellular network (`[G]`, repo source: `audio_tls.rs`).
- Candidate stays **quarantined** until an enrolled-device proof (`AudioProofApproval.delivery`, operation `SUPERSEDE_DELIVERY`) binds it to the actual certificate hashes, nonces, and source context. The Android-side delivery proof already rejects a changed TLS identity ("Actual TLS identity changed"), and `AudioAuthority` requires `delivery_observed` plus TLS-hash equality before media is allowed (`[G]`, repo source).
- The grant freezes revision and cutoff: `revision` increases by exactly one, `until` (the deadline) stays frozen; the old delivery remains active until commit (`[G]`, repo source: `audio_authority.rs`, `AudioGrantWindow`).

### 3.4 Trust/authorization changes per option

| | Option B: fresh TLS warm (recommended) | Option A: QUIC path migration |
|---|---|---|
| quiche config | unchanged (migration stays disabled) | `set_disable_active_migration(false)` both peers |
| Wrong-path acceptance | unchanged (rejected) | must accept changed peer/local addresses |
| `DeliveryBinding` / `Observation` | new connection ID + new cert hashes bound by the grant | connection ID unchanged, but address anchoring must change |
| Authorization model | no change; Supersede grant applies to the new connection | grant approval must survive a path change; re-approval needed |
| One-flight constraint | start Supersede only when switch warranted; bounded window | same, plus migration state held across the flight |

Recommendation: **Option B with quiche 0.30 left unchanged**. Migration stays out of scope unless the trusted base is explicitly re-approved.

## 4. Transport warm-up alone vs a separately authorized standby

**Answer:** warming transport alone is **not sufficient** for a smooth handover. `media_allowed` requires an approved grant on that connection (`source_observed && delivery_observed && ready && delivery_generation != 0 && output_generation != 0 && output_observed == grant.output_generation`) — authorization is the gate to media (`[G]`, repo source: `audio_authority.rs`). A warm-but-unauthorized candidate cannot play, and starting authorization only after Wi-Fi dies leaves an unavoidable audible gap.

The smallest robust design therefore adds a **short-lived, separately authorized standby**: candidate TLS + control handshake complete, grant received and applied (approved but not committed), old delivery still active. Commit retires the old delivery and rebinds readiness. This matches the provisional baseline but makes the standby explicit, bounded, and short-lived so the single in-flight authorization cannot block renewal or go stale.

### 4.1 Smallest candidate state machine (event-driven, no polling)

1. `IDLE` — no candidate.
2. `PREPARING` — `requestNetwork(cellular)` outstanding; socket bound only to the cellular network; QUIC handshake in flight.
3. `READY` — TLS established; facts/observation captured; grant received and applied (candidate approved); old delivery still active.
4. `COMMITTING` — Supersede proof sent over the old (still-live) connection nominating the candidate; old retirement in flight.
5. `COMMITTED` — old retired; receiver readiness rebound to the new delivery generation; candidate becomes the active delivery.
6. `FAILED` / `EXPIRED` — candidate torn down; active path untouched; return to `IDLE`.

Transitions fire on events only: `onAvailable`, handshake complete, grant applied, commit decision, commit acknowledged or timed out, `onLost` of the active network, media-arrival stall (`[I]`).

### 4.2 Readiness barrier

- Receiver output readiness is rebound when the delivery generation changes; on commit it must point at the **new** delivery/output generation, never the old one (`[I]` — existing behavior to preserve).
- Source/renderer identity and playback floors are preserved across delivery replacement: the new delivery continues at the current source age/generation with original source timestamps (`[I]`).
- `ReceiveFrontier`-style source floor is connection-scoped today; moving to the new TLS connection must **not rewind the source floor** (`[I]`, repo note in `Audio_Stop_Game_And_Handover_High_Handoff.md`).
- Healthy playback gains no extra PCM copies, queues, locks, polling, or duplicated media (`[I]`, product constraint).

### 4.3 Expiry and cancellation

- Candidate TTL is bounded by the remaining authority deadline minus a safety margin, with a hard cap (minutes). Expired candidates are torn down; this never blocks renewal because the Supersede flight is short and is started only when switching is warranted (`[I]`).
- QUIC idle timeout applies to the candidate; the candidate is cancelled when Wi-Fi recovers and is stable before commit, or on flap (`[I]`).
- One candidate at a time; an unfinished replacement authorization is not parked long (`[I]`, per the one-flight constraint).

### 4.4 Failure fallback

- The old path keeps playing until commit succeeds (`[I]`).
- If the old path detaches first: bounded source/grace recovery; attempt commit over any remaining path, otherwise re-prepare on the network that returns; retire old-bound pending work with actual custody (no orphan grants) (`[I]`).

## 5. Candidate lifetime, probing, retry, anti-flapping — and honest cost

- **Lifetime:** the candidate exists only while Wi-Fi is healthy and a switch is plausible; max TTL as in §4.3; torn down on commit, expiry, or failure. No permanent dual media; old egress is retired at commit (`[I]`).
- **Probing:** reachability plus control-plane TLS only — no media on the candidate, bounded datagrams, event-driven, no polling of the active path (`[I]`).
- **Retry:** bounded exponential backoff; distinct outcomes — cellular unavailable, consent denied, blocked (VPN/enterprise) — fail closed without endless retry; candidate retries never disturb the active path (`[I]`).
- **Anti-flapping:** stability window (candidate validated stable for tens of seconds) before commit, commit cooldown, one candidate at a time, no re-prepare while pending (`[I]`).
- **Mobile-data consent:** infer metered/cellular from `NetworkCapabilities`; honor an in-app mobile-data consent toggle. Reading the system-wide mobile-data on/off state reliably needs telephony permissions we do not hold, so "available but not consented" is candidate-blocked, not active-path failure (`[I]`).
- **Blocked/unavailable networks:** candidate preparation fails closed; Wi-Fi continues uninterrupted (`[I]`).
- **Cost honesty:** cellular radio promotion (`RRC_IDLE → RRC_CONNECTED`) costs latency and power; keepalives and the handshake consume battery and data. `"Zero impact" must not be assumed`; these costs are stated, not hidden, and measured in §6 (`[E]`).

## 6. Measurements demonstrating preserved latency and minimal interruption

| Area | Metric | How measured | Device experiment? |
|---|---|---|---|
| Preparation | `requestNetwork` → `onAvailable`; candidate TLS handshake completion; control-plane bytes | Android-side timestamps around candidate setup | Partial (handshake timing no; radio power yes) |
| Radio / power | mA delta during candidate warm-up; radio-promotion latency | BatteryStats / powermonitor on the device | **Yes** `[E]` |
| Authorization | Supersede proof RTT over the old connection; grant-apply latency | Instrument the control owner | No (unit/service level) |
| Source continuity | source age monotonic; original timestamps preserved; frames missed; source floor not rewound | Native owner counters | Partial (real drop conditions need devices) |
| Receiver continuity | readiness rebind latency on generation change; `PcmSeamGuard` events; AAudio xruns; decoder discontinuities | Android media-owner traces | Partial |
| Audible interruption | gap duration (ms) at output; end-to-end latency pre/post handover | Loopback / timestamp comparison on device | **Yes** `[E]` |
| Flapping / OEM behavior | callback suppression under doze; weak-signal handover success; VPN kill-switch interaction | Device experiments on target models | **Yes** `[E]` |

Guarantees requiring device experiments: actual audible gap, radio-promotion power/latency, OEM background/callback behavior, VPN/always-on-VPN interaction, flapping in weak signal. All authorization and state-machine correctness can be unit-tested against the existing Rust grant authority (`audio_authority.rs`, `audio_transport.rs`) and the Kotlin transport fakes.

## 7. Recommendation (decision to be saved by review)

Adopt Option B: one active media delivery plus one short-lived, control-only candidate prepared on the bounded setup owner. Warm reachability/TLS/public observations on the candidate; leave renewals unblocked. Start the fresh Supersede proof only when switching is warranted, nominating the observed candidate through the approved old connection while it remains available. Preserve source/renderer identity and playback floors, then retire old egress and rebind readiness at commit. If the old path actually detaches first, use bounded source/grace recovery and retire old-bound pending work with actual custody.

Why this over the alternatives:
- QUIC path migration is rejected under pinned quiche 0.30 (migration disabled, route rejection, pinned destination policy) without a trusted-base change.
- Transport warm-up alone is rejected because authorization is the media gate; a separately authorized standby is the smallest addition that closes the gap.
- The standby is deliberately short-lived and single-flight so it cannot block renewal or become stale.
- No permanent dual media; no process-wide rebinding; no zero-cost assumptions.

Open items before production integration: the device experiments in §6, and the reachable-endpoint/relay prerequisite over cellular (§0).

## Primary sources

- Android `ConnectivityManager#requestNetwork`: https://developer.android.com/reference/android/net/ConnectivityManager#requestNetwork(android.net.NetworkRequest,%20android.net.ConnectivityManager.NetworkCallback)
- Android `Network#bindSocket`: https://developer.android.com/reference/android/net/Network#bindSocket(java.io.FileDescriptor)
- NDK `android_setsocknetwork`: https://developer.android.com/ndk/reference/group/networking#android_setsocknetwork
- Android `NetworkCallback#onLosing`: https://developer.android.com/reference/android/net/ConnectivityManager.NetworkCallback#onLosing(android.net.Network,%20int)
- Android `NetworkCallback#onAvailable` / `#onLost`: https://developer.android.com/reference/android/net/ConnectivityManager.NetworkCallback
- Android `NetworkCapabilities`: https://developer.android.com/reference/android/net/NetworkCapabilities
- RFC 9000 §9 (path migration) and §9.1 (path validation): https://www.rfc-editor.org/rfc/rfc9000.html#section-9
- quiche 0.30.0 `Connection` (`probe_path`, `set_disable_active_migration`): https://docs.rs/quiche/0.30.0/quiche/struct.Connection.html
- Repo sources: `vnext/m2a/quiche-lab/src/{audio_tls.rs, audio_transport.rs, audio_authority.rs, audio_control.rs}`; `vnext/m2a/quiche-lab/Cargo.toml` (`quiche = "=0.30.0"`); Android `transport/DefaultNetworkHandoff.kt`, `transport/LanTransport.kt`, `transport/AudioAuthority.kt`, `media/NativeOwnerCoordinator.kt`; `plans/Audio_Handover_Arena_Research_Question.md`; `plans/Audio_Stop_Game_And_Handover_High_Handoff.md`.

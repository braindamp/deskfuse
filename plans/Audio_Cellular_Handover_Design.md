# DeskFuse — Wi-Fi → Cellular Handover Design (corrected, single-flight)

Branch: `arena/e6085c00-deskfuse` (from `experiment/vnext-audio-latency`, commit `7df5216`)
Reference repo: https://github.com/braindamp/deskfuse
Status: **Design only.** No implementation, build, test, or device/network operation is authorized. Supersedes the earlier draft on this branch; fixes the one-positive-authorization-in-flight constraint and the `CHANGE_NETWORK_STATE` statement.

Conventions:
- `[G]` documented guarantee — official Android documentation, QUIC RFC, or pinned quiche source in this repo.
- `[I]` engineering inference — not a documented guarantee; validate before relying on it.
- `[E]` device experiment required — cannot be established from documentation or unit tests.

## 0. Scope and hard prerequisite

Cellular use requires a **reachable remote Windows endpoint or an approved relay/VPN route over the cellular path**. This design assumes that prerequisite; it does not design NAT traversal, relays, or a VPN platform.

LAN addresses (lab Windows `192.168.2.107`) and the scoped Windows firewall rule (Private-profile UDP `48235` to the phone's current LAN address) **cannot be reused from cellular**: the cellular path observes different addresses, carrier NAT, and possible carrier filtering. No general NAT/relay-platform design.

Non-goals: permanent dual media, process-wide network rebinding, QUIC migration under the pinned policy, any change to the Windows trusted base.

## 1. Android APIs: request and retain cellular alongside Wi-Fi; bind only the candidate socket

### 1.1 Requesting an additional network (`[G]`)

- `ConnectivityManager.requestNetwork(NetworkRequest, NetworkCallback)` with a `NetworkRequest.Builder()` adding `NET_CAPABILITY_INTERNET` and `TRANSPORT_CELLULAR` requests an additional cellular network **while Wi-Fi remains the default network**. The two bearers coexist; the app never asks cellular to become the default.
- Required permission: `android.permission.INTERNET` only (already held in the manifest).
- **`CHANGE_NETWORK_STATE` correction:** the manifest does **not** hold `CHANGE_NETWORK_STATE`, we do **not** request it, and it is **not needed**. `requestNetwork` requires only `INTERNET`; we never call `setProcessDefaultNetwork` or otherwise change the default network, which is the operation that requires `CHANGE_NETWORK_STATE`. Binding a socket to a chosen network via `Network.bindSocket` also does not require it.
- The request/callback needs explicit ownership: the cellular network stays retained only while the `NetworkCallback` is registered. Release it with `unregisterNetworkCallback` when the candidate is torn down, committed, or failed. A passive callback alone does not retain the network (`[G]`).
- All of the above is available at minSdk 30.

### 1.2 Binding only the candidate socket (`[G]`)

- In `NetworkCallback.onAvailable(network)`, create the QUIC/UDP socket, call `network.bindSocket(socket.fileDescriptor)` **before** `connect()`, and resolve names through that network (network-specific `DnsResolver` / `getAllByName`; NDK `android_setsocknetwork`). Do not rebind any other socket; never call `setProcessDefaultNetwork` — process-wide rebinding would affect other traffic and is explicitly not the design.
- Candidate-only binding leaves the active Wi-Fi media path and all other app traffic untouched during candidate preparation.

### 1.3 VPN, OS/OEM, and background-execution limits

- `NET_CAPABILITY_NOT_VPN` can exclude VPN interfaces from the candidate. If an always-on or per-app VPN is active, every interface may be under it and the app cannot override VPN policy; cellular reachability then still depends on the VPN/relay route being reachable — a prerequisite matter, not something `requestNetwork` fixes (`[G]` + `[I]`).
- OEM battery optimization (e.g. Xiaomi MIUI restricted background) can delay or suppress callbacks and socket activity when backgrounded. Mitigation within our control: keep candidate work inside the existing foreground-service lifecycle (`FOREGROUND_SERVICE_MEDIA_PLAYBACK` / `dataSync` already declared). We cannot force whitelisting by API; the user-directed ignore-battery-optimizations setting is the only OEM escape hatch (`[I]`).
- If the app process is killed, callback registration and candidate ownership are lost: there is no persisted `requestNetwork` across process death (`[I]`).

### 1.4 Deliberately not used

`CHANGE_NETWORK_STATE` (not held, not needed), making cellular the default network, `NetworkFactory`, VPN APIs, permanent dual-network residency.

## 2. Signals: what triggers preparation and switching

### 2.1 Useful early warnings

- `NetworkCallback.onAvailable(cellular)` plus `onCapabilitiesChanged` (`hasTransport(TRANSPORT_CELLULAR)`, `NET_CAPABILITY_VALIDATED`, metered flag): the candidate network exists and the candidate socket can be prepared while Wi-Fi is still healthy (`[G]`).
- Telephony radio/signal state (e.g. `TelephonyManager` data/signal tracking): a hint that cellular may soon be needed. Treat as a hint only — never as a handover commit authority (`[I]`).
- Local active-path health: QUIC idle/keepalive timeouts, missing PING/acks, media-arrival gap versus the playout floor. These detect Wi-Fi degradation locally and can start preparation before loss is certain (`[I]`).

### 2.2 Callbacks not guaranteed before Wi-Fi fails

- `onLosing(network, maxMsToLive)` is advisory; **a prior callback is not guaranteed on abrupt loss** (AP deauth, out of range, driver crash, Wi-Fi radio off) (`[G]`).
- `onLost` fires after the loss, never before (`[G]`).
- Consequence: preparation starts on early warnings, not on `onLosing`; switching must survive abrupt Wi-Fi loss through bounded source-side grace recovery.

### 2.3 Abrupt loss, reverse handover, flapping

- Abrupt Wi-Fi loss: the old path dies mid-playback. If a candidate is warmed and authorized it commits; if none is ready, the source uses its bounded reconnect grace and re-prepares on whichever network returns. Playback continues within grace; source and renderer survive (`[I]`).
- Reverse handover (cellular → Wi-Fi): fully symmetric — prepare a Wi-Fi candidate while cellular is active; whichever candidate is authorized and stable first commits. No permanent bearer preference (`[I]`).
- Flapping: anti-flap hysteresis — require candidate stability (available + TLS complete) for a stability window before starting the single authorization, plus a commit cooldown; brief dips never tear the active path down (`[I]`).

## 3. Fresh TLS/control warm-up vs QUIC path migration (pinned quiche 0.30)

### 3.1 What the pinned code does today

- `vnext/m2a/quiche-lab/Cargo.toml` pins `quiche = "=0.30.0"`; `Cargo.lock` resolves it (`[G]`, repo source).
- `audio_tls.rs`: `config.set_disable_active_migration(true)`; both lab binaries do the same (`[G]`, repo source).
- `audio_transport.rs` `receive()` ignores any packet where `info.from != self.peer || info.to != self.local` — "Destination policy 1 never authorizes migration or NAT rebinding". Wrong-path packets are dropped without killing a healthy delivery (`[G]`, repo source).
- quiche 0.30 exposes `Connection::probe_path()` (RFC 9000 path validation), but with active migration disabled, probing does not move traffic (`[G]`, docs.rs 0.30).

### 3.2 Option A — QUIC path migration (rejected under pinned policy)

Requires, on both peers: enabling active migration (`set_disable_active_migration(false)`), accepting changed peer/local addresses in `receive()`, and re-anchoring the pinned "approved destination" (`ControlConnection` pins `local` + `peer` `SocketAddr`). Trust/authorization would have to tolerate a new path, and the peer address the server sees changes across carrier NAT. Path validation on a strict-freshness media path adds unbudgeted state (`[I]`). Rejected unless the trusted base is explicitly re-approved.

### 3.3 Option B — Warm a fresh TLS/control connection, authorize once just before switching (recommended)

- New quiche `ConnectionId`, fresh ephemeral in-memory TLS certificates (bounded lifetime; `Identity::generate` issues ≈30-minute profile certificates), new handshake over the socket bound to the cellular network (`[G]`, repo source: `audio_tls.rs`).
- Candidate stays **quarantined and control-only** until the **single** Supersede authorization grants it. No grant, no media, on the candidate before that flight (`[I]`, consistent with `media_allowed` requiring an approved grant on that connection — `audio_authority.rs`).
- The grant freezes revision (old + 1) and cutoff (`until` unchanged); the old delivery remains active until commit (`[G]`, repo source: `audio_authority.rs`, `AudioGrantWindow`).

### 3.4 Trust/authorization changes per option

| | Option B: fresh TLS warm + single Supersede | Option A: QUIC path migration |
|---|---|---|
| quiche config | unchanged (migration stays disabled) | `set_disable_active_migration(false)` both peers |
| Wrong-path acceptance | unchanged (rejected) | must accept changed peer/local addresses |
| `DeliveryBinding`/`Observation` | new connection ID + new cert hashes bound by the single Supersede grant | connection ID unchanged, but address anchoring must change |
| Authorization model | **one** Supersede flight moves authority old→new; revision+1, `until` frozen | grant approval must survive a path change; re-approval needed |
| Renewal interaction | Supersede and RENEW both claim revision+1 → only one in flight; Supersede must be short or it blocks renewal | same, plus migration state held across the flight |

## 4. Transport warm-up alone vs a single authorized standby

**Answer:** warming transport alone is **not sufficient** for a smooth handover. `media_allowed` requires an approved grant on that connection (`source_observed && delivery_observed && ready && delivery_generation != 0 && output_generation != 0 && output_observed == grant.output_generation`) — authorization is the gate to media (`[G]`, repo source). A warm-but-unauthorized candidate cannot play, and starting authorization only after Wi-Fi dies leaves an unavoidable audible gap.

The smallest robust design therefore uses a **short-lived, control-only candidate**, authorized **exactly once** by the single in-flight Supersede authorization just before switching. The candidate is NOT pre-authorized (that would be a second authorization). This matches the provisional baseline but makes the single-flight boundary explicit so the authorization cannot block renewal or go stale.

### 4.1 Exact grant/proof/commit sequence (one positive authorization in flight)

Notation: old delivery = connection `C_old`, grant `g` (revision `R`, until `U`, generation `G`, binding `b_old`); candidate = connection `C_new` (fresh ConnectionId, fresh ephemeral certs), quarantined, TLS established, `Observation` captured (`connection = C_new.id`, `native`, `phone`, `nonce`), control-only, **no grant**, `media_allowed == false`.

1. **Warm (no authorization).** `requestNetwork(cellular)` → bind socket only to cellular → QUIC handshake → `require_established` → capture `TlsFacts` + `Observation`. `C_new` stays quarantined. `g` still governs `C_old`; renewals on `C_old` proceed normally (revision `R`, until `U`). **Zero authorizations in flight.**

2. **Decide to switch** (Wi-Fi degrading / adverse metrics / proactive cellular preference). `C_new` must be stable and validated. Still **zero authorizations in flight**.

3. **Single Supersede flight — THE one positive authorization.** Over `C_old` (still live and able to carry it), send `wire::SUPERSEDE` (`AudioOperation.SUPERSEDE_DELIVERY`):
   - `DeliveryBinding` for `C_new`: commits `C_new`'s native + phone cert hashes, nonces, source scope/generation/digest, ALPN `df-audio-authority-2`, and a **fresh request nonce** (bound into the delivery digest).
   - Source context (`SourceScope`) unchanged — same source incarnation survives handover.
   - Expected new revision `R+1`; cutoff `until` stays `U` (frozen, not extended).
   - One flight only; no parallel grant on `C_new`.

   This proof is **in-flight authorization**. It is **not** an accepted grant yet: the broker must verify enrollment/source/binding and sign the grant receipt.

4. **Grant received and applied.** Broker signs grant receipt: revision `R+1`, until `U` (frozen), delivery = `C_new` binding, generation `G+1`. Phone applies it to `C_new`. `C_new` becomes approved at generation `G+1`; `C_old` is still live but will be retired at commit. **Exactly one authorization (the Supersede) has moved authority; `g` is being retired, not supplemented.** The in-flight authorization is now consumed; none remains parked.

   Model note: `AudioAuthority::apply_inner` rejects a nonzero→nonzero binding change while the old binding is current (`old.binding != [0;32] && old.binding != grant.binding` → `Err`). The supersede therefore retires/releases the old binding atomically as part of the same commit so the authority holds exactly one grant at a time. This binding-transition rule is an identified refinement to the authorization model (question 3); `ControlConnection::install_native_inner` per-connection imposes no problem on the fresh `C_new` (`self.approved` is `None` there).

5. **Commit.** Retire `C_old` (`retire()` closes the QUIC connection, clears `approved`, clears writes/pending/egress — old-bound pending work retired with actual custody). `C_new` becomes the active delivery at generation `G+1`. **Rebind receiver readiness to the new delivery generation `G+1`.** Source/renderer identity and playback floors are preserved; the source floor is **not rewound** across the TLS connection change; original source timestamps continue.

6. **Renewals unblocked.** While step 3–4 is in flight, `g` (rev `R`, until `U`) still governs `C_old`, so a `RENEW` on the old path is not pre-empted. But both `RENEW` and `SUPERSEDE` claim revision `R+1`; only one can hold it. Therefore:
   - Start Supersede only when switching is warranted.
   - Keep the flight short (RTT-bound) and cancel/expire it if Wi-Fi recovers, the candidate fails, or the stability window lapses — releasing `R+1` so a `RENEW` can proceed.
   - If Supersede does not complete before `U`, `g` expires → bounded grace → recovery; the parked authorization is retired, never allowed to go stale (`[I]`, per the one-flight constraint: "parking … could block renewal or become stale").

### 4.2 Smallest state machine (event-driven, no polling)

1. `IDLE` — no candidate.
2. `PREPARING` — `requestNetwork` outstanding; socket bound only to cellular; QUIC handshake in flight; TLS complete; `Observation` captured. Candidate quarantined, control-only, **no grant**.
3. `READY` — candidate warmed (TLS + observations complete), **still quarantined / no grant**. Decision point; switch only from here.
4. `AUTHORIZING` — the single Supersede flight in flight over `C_old`; grant frozen (revision `R+1`, until `U`). **This is the one in-flight positive authorization.**
5. `COMMITTING` — grant applied to `C_new`; old retirement in flight.
6. `COMMITTED` — `C_old` retired; `C_new` active at generation `G+1`; readiness rebound to `G+1`.
7. `FAILED` / `EXPIRED` — candidate torn down; active path untouched; return to `IDLE`, releasing `R+1`.

Transitions fire on events only: `onAvailable`, TLS complete, switch decision, Supersede sent/acknowledged, grant applied, commit acknowledged or timed out, `onLost` of the active network, media-arrival stall. No polling of the active path (`[I]`).

### 4.3 Readiness barrier

- Receiver output readiness is rebound when the delivery generation changes; on commit it points at the **new** delivery/output generation `G+1`, never the old one (`[I]` — existing behavior to preserve).
- Source/renderer identity and playback floors preserved across delivery replacement; original source timestamps continue (`[I]`).
- `ReceiveFrontier`-style source floor is connection-scoped today; moving to `C_new` must not rewind the source floor (`[I]`, repo note).
- Healthy playback gains **no** extra PCM copies, queues, locks, polling, or duplicated media (`[I]`, product constraint).

### 4.4 Expiry/cancellation and failure fallback

- Candidate TTL bounded by remaining authority deadline `U` minus a safety margin, hard cap minutes. Expired candidates torn down; this never blocks renewal because the Supersede flight is short and started only when warranted (`[I]`).
- QUIC idle timeout applies to the candidate; cancelled when Wi-Fi recovers and is stable before commit, or on flap (`[I]`).
- One candidate at a time; an unfinished replacement authorization is not parked long (`[I]`).
- Fallback: old path keeps playing until commit succeeds (`[I]`). If old detaches first: bounded source/grace recovery; attempt commit over any remaining path, else re-prepare on the network that returns; retire old-bound pending work with actual custody (`[I]`).

## 5. Candidate lifetime, probing, retry, anti-flapping — honest cost

- **Lifetime:** candidate exists only while Wi-Fi is healthy and a switch is plausible; max TTL as in §4.4; torn down on commit, expiry, or failure. No permanent dual media; old egress retired at commit (`[I]`).
- **Probing:** reachability + control-plane TLS only — **no media on the candidate**, bounded datagrams, event-driven, no polling of the active path (`[I]`).
- **Retry:** bounded exponential backoff; distinct outcomes — cellular unavailable, consent denied, blocked (VPN/enterprise) — fail closed without endless retry; candidate retries never disturb the active path (`[I]`).
- **Anti-flapping:** stability window (candidate validated stable) before starting the single authorization; commit cooldown; one candidate at a time; no re-prepare while pending (`[I]`).
- **Mobile-data consent:** infer metered/cellular from `NetworkCapabilities`; honor an in-app mobile-data consent toggle. Reading system-wide mobile-data on/off reliably needs telephony permissions we do not hold, so "available but not consented" is candidate-blocked, not active-path failure (`[I]`).
- **Blocked/unavailable networks:** candidate preparation fails closed; Wi-Fi continues uninterrupted (`[I]`).
- **Cost honesty:** cellular radio promotion (`RRC_IDLE → RRC_CONNECTED`) costs latency and power; keepalives and the handshake consume battery and data. `"Zero impact" must not be assumed`; these costs are stated, not hidden, and measured in §6 (`[E]`).

## 6. Measurements demonstrating preserved latency and minimal interruption

| Area | Metric | How measured | Device experiment? |
|---|---|---|---|
| Preparation | `requestNetwork` → `onAvailable`; candidate TLS handshake completion; control-plane bytes | Android-side timestamps around candidate setup | Partial (handshake timing no; radio power yes) |
| Radio / power | mA delta during candidate warm-up; radio-promotion latency | BatteryStats / powermonitor on device | **Yes** `[E]` |
| Authorization | Supersede proof RTT over old connection; grant-apply latency; revision `R→R+1` transition | Instrument the control owner | No (unit/service level) |
| Source continuity | source age monotonic; original timestamps preserved; frames missed; source floor not rewound | Native owner counters | Partial (real drop conditions need devices) |
| Receiver continuity | readiness rebind latency on generation change; `PcmSeamGuard` events; AAudio xruns; decoder discontinuities | Android media-owner traces | Partial |
| Audible interruption | gap duration (ms) at output; E2E latency pre/post handover | Loopback / timestamp comparison on device | **Yes** `[E]` |
| Flapping / OEM behavior | callback suppression under doze; weak-signal handover success; VPN kill-switch interaction | Device experiments on target models | **Yes** `[E]` |

Guarantees requiring device experiments: actual audible gap, radio-promotion power/latency, OEM background/callback behavior, VPN/always-on-VPN interaction, flapping in weak signal. Authorization and state-machine correctness can be unit-tested against the existing Rust grant authority (`audio_authority.rs`, `audio_transport.rs`) and the Kotlin transport fakes.

## 7. Recommendation (decision to be saved by review)

Adopt Option B: one active media delivery plus one short-lived, control-only candidate prepared on the bounded setup owner. Warm reachability/TLS/public observations on the candidate; leave renewals unblocked. Start the fresh Supersede proof only when switching is warranted, nominating the observed candidate through the approved old connection while it remains available. Preserve source/renderer identity and playback floors, then retire old egress and rebind readiness at commit. If the old path actually detaches first, use bounded source/grace recovery and retire old-bound pending work with actual custody.

Why this over the alternatives:
- QUIC path migration is rejected under pinned quiche 0.30 (migration disabled, route rejection, pinned destination policy) without a trusted-base change.
- Transport warm-up alone is rejected because authorization is the media gate; the candidate stays control-only until the single Supersede flight authorizes it — never two authorizations in flight.
- The single-flight Supersede is short and cancellable so it cannot block renewal (`RENEW` also claims revision `R+1`) or become stale.
- No permanent dual media; no process-wide rebinding; no zero-cost assumptions; `CHANGE_NETWORK_STATE` is neither held nor needed.

Open items before production integration: the device experiments in §6, the `AudioAuthority::apply` binding-transition refinement (§4.1), and the reachable-endpoint/relay prerequisite over cellular (§0).

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

# Arena research: lean Wi-Fi/cellular handover

29 October 2026. **Historical research request; review complete.** The user
provided the [returned candidate](Audio_Handover_Arena_Candidate_2026-10-29.md).
The [reviewed Ultra decision](Audio_Handover_Reviewed_High_Decision.md) now selects
topic 1 and is incorporated in [the focused High handoff](Audio_Stop_Game_And_Handover_High_Handoff.md).
The question/provisional baseline below records the inputs to that review.
No implementation, build, test or device/network operation was run by this
design pass.

## Question to paste into Arena.ai

We are designing DeskFuse, a Windows-to-Android real-time streaming application.
Windows uses a protected native Rust owner with **quiche 0.30.0 / BoringSSL**;
Android has an independent native audio owner. The current delivery policy
prioritizes staying live: late content is discarded, original source timestamps
are preserved, and the source/renderer should survive a network handover.

Each delivery uses fresh, memory-only TLS keys. A new connection starts
quarantined and requires an enrolled-device proof bound to its actual
certificates, nonces and source context. Each connection currently has a fixed
approved destination. The source has a bounded authority deadline and reconnect
grace. Production integration is unfinished; this is a design question.

**Important constraint:** there is one positive authorization operation in flight.
Its proof freezes the expected revision and cutoff. Committing a replacement
delivery retires the old delivery. Parking an unfinished replacement authorization
for a long time could therefore block renewal or become stale.

**Goal:** prepare cellular connectivity while Wi-Fi still works, then switch with
the smallest possible interruption. Keep healthy playback free of additional
PCM copies, queues, locks, polling or duplicated media. Background work must be
bounded and event driven. Cellular radio activation, battery and data costs must
be stated honestly; "zero impact" must not be assumed.

Please recommend the smallest robust design and address:

1. Which Android APIs can request and retain cellular connectivity alongside
   Wi-Fi and bind **only the candidate socket** to it? Explain API-level, VPN,
   OS/OEM and background-execution limits. The app currently has minSdk 30 and
   INTERNET/ACCESS_NETWORK_STATE, but no CHANGE_NETWORK_STATE permission.
2. Which signals can trigger preparation or switching? Distinguish useful early
   warnings from callbacks not guaranteed before Wi-Fi fails. Address abrupt
   loss, reverse handover and flapping.
3. Compare warming a fresh TLS/control connection and authorizing it just before
   switching against QUIC path migration. Check the **pinned quiche version**
   and identify changes each approach requires to our trust/authorization model.
4. Can warming transport alone give sufficiently smooth handover, or is a
   separately authorized standby necessary? Specify the smallest state machine,
   readiness barrier, expiry/cancellation rules and failure fallback. Receiver
   output readiness is currently rebound when a delivery generation changes.
5. How should candidate lifetime, probing, retry and anti-flapping policies limit
   power/data cost without disturbing the active path? Account for mobile-data
   consent and blocked/unavailable networks. Do not propose permanent dual media.
6. Specify measurements demonstrating preserved latency and minimal handover
   interruption. Separate preparation, authorization, source continuity,
   receiver continuity and actual audible interruption. State which guarantees
   require device experiments.

Assume a reachable remote Windows endpoint or approved relay/VPN route must
exist for cellular use. Identify that prerequisite; **do not expand this into a
general NAT/relay-platform design**. LAN addresses and private firewall rules
cannot simply be reused from cellular.

Use official Android documentation, QUIC standards and relevant quiche source.
Separate documented guarantees, engineering inferences and device experiments.

## Primary-source checks already made

These findings guide review; they do not preselect the handover solution.

- Android `requestNetwork` can request an additional cellular network and warns
  that doing so can increase radio power. Its request/callback needs explicit
  ownership and release; a passive callback alone does not retain that network.
  [ConnectivityManager](https://developer.android.com/reference/android/net/ConnectivityManager#requestNetwork(android.net.NetworkRequest,%20android.net.ConnectivityManager.NetworkCallback)).
- Bind the replacement socket to its chosen network before connect and use
  network-specific DNS. Process-wide rebinding would affect other traffic.
  [Network.bindSocket](https://developer.android.com/reference/android/net/Network#bindSocket(java.io.FileDescriptor)),
  [NDK android_setsocknetwork](https://developer.android.com/ndk/reference/group/networking#android_setsocknetwork).
- `onLosing` is advisory; a prior callback is not guaranteed on abrupt loss.
  [NetworkCallback](https://developer.android.com/reference/android/net/ConnectivityManager.NetworkCallback#onLosing(android.net.Network,%20int)).
- quiche 0.30 exposes probing/migration, but DeskFuse's `audio_tls.rs` disables
  migration and `audio_transport.rs` rejects changed routes. API availability
  alone cannot override its approved destination policy.
  [Pinned connection API](https://docs.rs/quiche/0.30.0/quiche/struct.Connection.html#method.probe_path).

## Provisional baseline to compare

One active media delivery plus one short-lived, control-only candidate, prepared
on the bounded setup owner. Warm reachability/TLS/public observations; leave
renewals unblocked. Start the existing fresh Supersede proof only when switching
is warranted, nominating the observed candidate through the approved old
connection while it remains available. Preserve source/renderer identity and
playback floors, then retire old egress and rebind readiness at commit. If the
old path actually detaches first, use bounded source/grace recovery and retire
old-bound pending work with actual custody.

This baseline reduces work on the active path, but cannot by itself promise
zero audible gap. Arena should examine whether a stronger ready-standby design
is justified, and identify its additional state/proof/cost explicitly. **High
must not implement this provisional choice until the reviewed decision is saved.**

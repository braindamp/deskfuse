# DeskFuse design improvements backlog

29 October 2026. Product requirements and proposals for later design/implementation.
Entries here do not imply shipped features or silently amend the accepted
[v0.6 implementation programme](../../v0.4-architecture-design/DeskFuse_Architecture_v0.6_and_Implementation_Programme.md).
Keep the current native audio implementation focused; resolve changes to its
timing/retention contracts explicitly before implementation.

## DI-001 — Game, Interactive and Streaming delivery modes

**Requested by the user, 29 October 2026; recorded, not implemented.**

**Subsequent focused Ultra design:**
[current source v1 is Game-only](Audio_Stop_Game_And_Handover_High_Handoff.md#2-current-source-contract-is-game-prepare-two-later-modes-at-setup),
with setup admission reserving the two future mode names and rejecting them
until supported. Future buffered modes require a new authenticated source
version and separately selected owner. No buffering/runtime implementation was
performed. Handover remains pending [Arena research](Audio_Handover_Arena_Research_Question.md).
Let the user choose whether a session prioritizes staying live, preserving
content while idle, or steady buffered playback. These are delivery/playback
policies, independent of focused-app versus extended-display mode and of which
device supplies speakers, microphone or input.

The historical [A/V design](../docs/av-sync-design.md) sketches Media and Gameplay
tradeoffs. It does not specify this three-mode behavior, retained outage content
or the requested Live catch-up control. Current v0.6/native audio favors bounded
freshness and explicitly forbids delayed backlog replay. Interactive/Streaming
therefore require an explicit, separately designed buffered playback path.

| Mode | User intent and requested behavior | During Wi-Fi/cellular handover or a stall |
| --- | --- | --- |
| **Game** | Stay as close to the current source as possible. Ruthlessly discard packets/frames that miss their useful playback deadline; resume from the newest usable, synchronized state. | Keep the authorized session/source alive within its approved reconnect grace, then resume live on the newly authorized delivery. Show/conceal gaps honestly; avoid replaying the outage as accumulating delay. |
| **Interactive** | Favor a complete, smooth stream while the user is observing. Allow bounded delay to accumulate; **skip forward to catch up when mouse activity occurs**, so interaction returns to the current state. | Preserve/recover available content within an explicit retention limit. When interaction resumes, bring picture and sound to the live position together. Mouse activity is the requested initial trigger; keyboard/touch/controller equivalents remain a design question. |
| **Streaming** | Favor steady, complete, synchronized A/V delivery using a bounded playback buffer. Allow the viewer to remain behind live. Provide a visible **Live** button that plays the retained audio **and** video faster until caught up, then returns to normal speed. | Use retained content to bridge a recoverable outage and refill the buffer. Do not automatically discard it merely to return live. Expose buffering and the current delay behind live; make irrecoverable gaps explicit. |

### Playback behavior to preserve in the eventual design

- Live catch-up means accelerated playback of retained content, not an automatic
  jump over it. Specify a comfortable bounded speed, synchronized video cadence
  and pitch-preserving audio treatment, then qualify quality and device cost.
  Any optional instant jump should be a separately named action.
- Keep original capture/sample/presentation timestamps and one coherent playback
  timeline for the associated A/V. Measure source age, playback delay, buffering
  and handover interruption separately. Never relabel old samples as fresh to
  bypass today's admission guards. Mixed endpoint audio cannot automatically be
  synchronized to an unrelated app's video; select the actual associated tracks.
- "Complete" is a delivery goal within declared retention/capacity and reachable
  network limits. Recovering missing content requires an explicit sender-side
  retention/recovery mechanism; enlarging the phone's jitter buffer cannot
  recover packets it never received. Define duration/byte limits, overflow and
  unavailable-content behavior. Avoid unbounded memory/disk growth.
- Video skips must preserve decoder reference correctness and recover from a
  usable reference/keyframe when required. Move the associated audio position
  coherently; avoid independently skipping one side and leaving A/V misaligned.
- In Interactive mode, decide how the catch-up transition relates to input
  dispatch. A click on a delayed picture must not silently target an unrelated
  current UI state. Preserve the existing target/geometry/input-generation
  fences; specify whether input waits for a fresh presentation or another
  explicit interaction rule. Mouse movement, sustained activity and returning
  to idle need clear transition rules.
- Display the selected mode and meaningful live/behind-live/buffering state.
  Decide safe mode transitions and whether preferences belong to a task,
  associated track group or enrolled device; avoid a global toggle that changes
  unrelated endpoints. Interactive/Streaming buffering must not introduce queues
  or copies into Game's native path.

### Handover and authority boundaries

Support automatic Wi-Fi-to-cellular and reverse handover while preserving the
session/source timeline where its authorization remains valid. Explore preparing
a replacement path before the old one disappears, when the OS and connectivity
allow it. Measure interruption and cellular/battery cost; uninterrupted or lossless
delivery is not guaranteed by the existence of two network types. A reachable
WAN/relay path remains a separate connectivity prerequisite.

The current N3 foundation uses fixed observed destinations and fresh approved
replacement deliveries. Seamless overlap/migration, content recovery and any
retention across delivery changes remain future decisions. Old connection keys,
queued ciphertext, receipts or IP addresses never authorize the replacement.
All modes retain explicit Stop/revoke/expiry, bounded reconnect authority and
actual rundown rules. Buffered playback does not authorize indefinite disconnected
capture; define revocation's effect on retained content before enabling it.

### Next design work and evidence

Keep the focused [N3 source-release decision](Audio_N3_Source_Release_Ultra_Stop.md)
separate so it can finish without a broad playback redesign. Before implementing
DI-001, use a focused design pass to choose retention location/format, recovery
transport, limits, input catch-up rules, accelerated A/V playout and mode changes.
Extend the accepted freshness contract explicitly for delayed playback; keep
Game's existing original-source-age and native cutoff guarantees intact.

Qualify steady traffic, recoverable loss, an outage beyond retention, Wi-Fi to
cellular and back, mouse-triggered catch-up, Live acceleration, decoder recovery,
mode switches and Stop/revocation with buffered content. Report live delay,
missing/recovered content, catch-up duration, physical A/V offset, input-to-photon,
memory, bandwidth, CPU and battery for each mode. Values remain to be selected
and measured; no new numeric product targets are invented here.

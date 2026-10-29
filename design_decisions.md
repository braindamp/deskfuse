# DeskFuse Design Decisions

Decisions that shape the product architecture. Status values: `accepted`, `superseded`, `open`.
New entries get the next ID; never rewrite history — supersede instead.

## DD-001 — FocusIdd device ACL allows interactive users (no elevation)

- Date: 2026-10-19
- Status: accepted

**Context.** The driver's device security descriptor only allowed SYSTEM, Administrators
and UMDF hosts (`D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;UD)`). A normal-user agent could not
open the display-control interface (`Access is denied`), so every session would have had to
run elevated — UAC friction per run, plus a high-privilege process that can see the screen,
control input and touch user files.

**Decision.** Add `(A;;GA;;;IU)` (interactive users) to the device SDDL in
`windows/driver/FocusIdd/FocusIdd.inf`. The agent runs unelevated.

**Consequences / accepted trade-offs.**
- Any local user process may open the display-control interface and attach virtual displays.
  Accepted ("who cares about more virtual displays created"); the interface creates displays,
  nothing more.
- The security gate for phone access is the on-console approval prompt, not the OS ACL.
- Revisit if the threat model changes (multi-user machines, remote logon, or the interface
  grows privileged operations). The broker design — a small privileged component owning the
  driver handle, narrow IPC for the agent — remains the eventual target if needed.

## DD-002 — Wi-Fi/cellular handover: fresh TLS candidate plus short-lived authorized standby (proposed)

- Date: 2026-10-29
- Status: open (proposed; pending Arena review, not approved for implementation)

**Context.** DeskFuse streams Windows audio to Android over a pinned quiche 0.30.0 QUIC connection with active migration disabled and a fixed approved destination. The goal is to prepare cellular while Wi-Fi still works and switch with the smallest interruption, without permanent dual media, extra PCM copies/queues/locks/polling, or unbounded background work. There is one positive authorization operation in flight whose proof freezes revision and cutoff, so a parked replacement authorization must stay short-lived.

**Decision.** Prepare one short-lived, control-only candidate on cellular (`ConnectivityManager.requestNetwork`, `TRANSPORT_CELLULAR`, `NET_CAPABILITY_INTERNET`, socket bound only to that network via `Network.bindSocket`), complete TLS + control handshake, receive and apply the Supersede grant (approved but uncommitted), then commit over the still-live old connection: retire old egress, rebind receiver readiness to the new delivery generation. QUIC path migration is out of scope under the pinned policy; transport warm-up alone is insufficient because authorization is the media gate. Costs (radio promotion, keepalives, handshake data) are stated honestly; the candidate is bounded by TTL, stability window, cooldown, and fail-closed retry. Full rationale: `plans/Audio_Cellular_Handover_Design.md`.

**Consequences / accepted trade-offs.** Adds one authorized standby and its state machine/proof/cost; cannot by itself promise zero audible gap — actual gap, radio-promotion power, OEM background behavior, and VPN interaction require device experiments. No implementation is authorized until this decision is saved by review.

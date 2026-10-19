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

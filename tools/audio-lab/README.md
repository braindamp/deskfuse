
---

## v0.5 — Configuration Atlas (NEW)

This version adds a full searchable, responsive **Configuration Atlas: 155 options/readouts across 12 subsystems**, rather than restricting the Lab to today's convenient physical controls. The original physical audio bridge remains unchanged except for adding three atlas UI assets to its explicit static allow-list.

The atlas filters by **scenario** (local, cellular/WAN, audio/video, duplex, reverse and peer-to-peer), **change boundary**, **impact metric** and **implementation readiness**. Change-boundary color identifies potential live updates, re-prime, media-stream reopen, session restart, rebuild, and future architecture work. Impact tags indicate *which property could change* (latency, stability, fidelity, power, bandwidth, etc.), **not** unmeasured guarantees.

A profile planner can export/import a JSON draft and preserves local values across sessions. **The draft is never automatically applied to actual DeskFuse software**. Four existing physically supported parameter families can be transferred into the v0.4 physical test selector **only when exactly one differs from the tested reference**. Even this only stages the selector—the user must separately approve and start playback. All other settings are clearly marked fixed-in-code, observed/readout or proposed.

For the catalog's design principles and rules for adding settings, see **`CONFIGURATION_ATLAS_GUIDE.md`**. The canonical options live in **`atlas-controls.json`**, which generates **`atlas-data.js`** through `py -3 sync_atlas.py` (standalone directory only).

A standalone website preview can still be opened from `index.html`. For a physical test, launch the local bridge using the unchanged `start-lab.ps1` instructions above. No native host code is modified by the laboratory.


## v0.6 — Windows integration checkpoint

This is a **standalone Lab** with an optional localhost bridge. It can be stored in a clearly isolated `tools/audio-lab/` repository directory without becoming a native audio component. The source selector shows MM-1, the dedicated endpoint and future direct export. Dedicated endpoint launch is deliberately **blocked** until the product enrollment identity is cryptographically bound to the separately authenticated QUIC client certificate; a 32-character hexadecimal string is not authentication.

The bridge provides `GET /api/windows-integration`: a **read-only** named-pipe reachability probe (no broker commands), role+SHA256+Release+ABI/capability receipt inspection, and source capability metadata. The runner is still the final authority for checking artifact hashes. The browser cannot read keys or select a peer hash. `-WindowsCaptureExecutable`, `-BuildReceipt`, `-WindowsEnginePeriod` and `-WindowsCapturePolicy` are sent only for admitted MM-1 physical tests; one factor may differ from the reference.

No driver install, endpoint creation, broker launch, default device restoration, audio route change, product enrollment logic or WaveRT work is part of this Lab update. Windows preflight and Xiaomi listening were **not** physically tested in the development environment.

Ultra handoff: implement the enrollment→mTLS certificate binding on the **production control plane**; expose a documented, narrowly scoped, authenticated identity-selection capability to the lab rather than accepting a manually supplied peer hash. Only after contract review should the blocked dedicated launch be enabled. Preserve the broker's generation ownership and the existing native runner.

Read `CODEX_ULTRA_HANDOFF.md` before enabling dedicated endpoint launch.

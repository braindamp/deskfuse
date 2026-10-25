# DeskFuse Configuration Atlas — control taxonomy and expansion guide

Version 0.5. Standalone experimental UI; no integration into DeskFuse GitHub or its installed apps.

## Why this exists

DeskFuse should support per-user, per-device, per-route configuration across local and remote access, cellular networks, audio/video sessions, full-duplex talkback, reverse streaming, and future peer-to-peer/device-to-device media. The registry **must not be artificially capped** for convenience. Filters, collapsible subsystem groups, scenario views and search scale the presentation instead.

The atlas currently contains **155 distinct controls/readouts in 12 systems**, including controls already implemented, fixed-source features, observed telemetry, and proposals. This is a planning reference, **not a claim that all 155 settings can configure today's native receiver**.

## Two independent taxonomies

Every option has **change boundary** (`apply`) AND an array of **impact dimensions** (`impacts`). The application lifecycle describes the *intended engineering boundary*, not automatically a working runtime control.

| Color / boundary | Meaning | Native v0.4 bridge support |
|---|---|---|
| Mint / `live` | Could be changed without restarting, given a safe control channel and valid component contract | None of these are exposed as a native hot-update today |
| Lime / `prime` | Receiver must be re-primed; output interruption possible | Not implemented in bridge |
| Amber / `stream` | Reopen AAudio/WASAPI stream or device route | Not implemented in bridge |
| Purple / `session` | Stop/start authenticated physical session or renegotiate parameters | Supported only for the four current physical setting families |
| Coral / `build` | Source-level fixed setting or protocol change; rebuild matched binaries | Not implemented in bridge |
| Slate / `future` | Research, new backend, device support or protocol capability | Not implemented |

**Impact dimensions**: latency, continuity, fidelity, bandwidth, power, CPU/heat, synchronization, privacy, security, and data cost. These tags indicate a *potentially influenced metric*, **not** an automatically predicted increase or decrease. One option can help latency and harm continuity; outcomes require representative tests.

A third independent attribute, `status`, distinguishes:

- `physical`: physical runner supports the selectable parameter at **test/session start**, subject to one-factor policy.
- `fixed`: production/prototype code currently contains the feature, but this atlas does **not** control it in native execution.
- `observed`: hardware/readout/telemetry from the physical code or preserved listening test; it is not a knob.
- `proposed`: a forward-looking experiment, not a working native control.

The four currently supported physical parameter families are:

1. Android AAudio buffer request: **platform default 0** (reference), **2 bursts**, **3 bursts**.
2. 128-tap streaming sinc: **on** (reference) or identity bypass.
3. Anti-click fades: **on** (reference) or off.
4. Short-gap concealment: **on** (reference) or off.

Each is applied by **a new verified physical test** using the existing local runner, never silently. Only one factor may differ from the listening-test reference in an isolated comparison. All remaining catalog values are saved as a **local planning draft**, not shipped to the receiver.

## Scenarios and systems

Scenario lenses: local Wi-Fi, cellular/WAN, video + A/V, full duplex, reverse link, and device-to-device/peer-to-peer. Systems cover Windows capture, IPC/scheduling, QUIC transport, Android output, native DSP and recovery, cellular/WAN, video, A/V synchronization, reverse/duplex, P2P/multi-receiver, adaptive profiles, evidence/privacy. Scenarios are *filters*; each option can belong to several.

## Extending the atlas

`atlas-controls.json` is the **editable, canonical registry**. Every row contains:

- `id`: unique stable machine-readable identifier.
- `title`, `group`, `detail`: user-visible identity, subsystem, and cautious technical explanation.
- `kind`: `select`, `range`, or `readout`.
- `options`: choice strings for select; `[minimum, maximum, step, unit]` for range; `[]` for readout.
- `baseline`: either the tested/current value, or a **planning seed** when status is proposed. Seeds are not treated as deployed defaults.
- `apply`: lifecycle boundary, selected from the table above.
- `status`: actual readiness level, selected from the table above.
- `impacts`: non-exclusive dimensions; do not assert direction without measurement.
- `scopes`: applicable scenarios.

After editing, run `py -3 sync_atlas.py` in this standalone folder. It validates schema and writes `atlas-data.js` for loading in browsers even with `file://` preview. It **does not** write to the main repository. New catalog options appear automatically; there is no hard-coded control-count ceiling in the UI. If introducing a genuinely new impact, lifecycle, status or scenario keyword, its label and appearance must also be added to `atlas.js` / `atlas.css`.

## Draft configuration workflow

Use scenario and impact filters; open a subsystem; adjust selected values. The planner counts deviations, identifies the **highest required change boundary**, and stores only the draft in browser storage. Export/import a portable `.json` plan. None of these actions modifies binaries, code, Windows routing, AAudio or Xiaomi.

To hear a currently supported setting, change *only one* supported physical control, choose **SET UP LISTENING TRIAL**, and separately start a physical test in the bridge panel after its Release-artifact, credential and branch preflight. A proposed option never receives a real playback button.

## Findings that must constrain automatic policies

- Candidate standard: MM-1, stereo 48 kHz PCM16, 5 ms packet, shared low-latency **requested**, actual device output may negotiate otherwise; post-callback reserve 1,440 frames, sinc128, ±400 ppm / 100 ppm/s slew, fades 96 frames, concealment up to 480 frames.
- One 30-second default AAUDIO buffer-0 run was uninterrupted, with zero active reprimes/reanchors. That does **not** qualify longer sessions; a subsequent 120-second control failed early.
- Three-burst mode lowered one nominal AAudio pending estimate but produced **more interruptions** and 12 reprimes. It is not a recommended lower-latency default.
- Reported 4,104 buffer frames are a hardware buffer size/capacity observation, **not** measured acoustic or end-to-end latency.
- AAudio performance was requested LOW_LATENCY but observed NONE/SHARED on the Xiaomi route. Don't assume EXCLUSIVE/LOW_LATENCY is supported without actual negotiation tests.
- Future cellular/mobile video, bidirectional and device-to-device capabilities require authenticated protocols, ownership/clock rules and independent validation, not simply extra user-facing sliders.

## Safety boundary

The local bridge remains bound to loopback, has an explicit static asset allow-list, requires same-origin and a run token for starting physical playback, and verifies matching Release build hashes. The atlas never invokes `/api/start`, never disables media authentication and never accesses audio keys. Draft export has a separate schema from physical runner settings to prevent accidental application.

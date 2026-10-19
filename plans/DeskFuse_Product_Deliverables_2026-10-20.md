# DeskFuse product deliverables — device-first implementation backlog

Status: delivery checklist, 20 October 2026. Distinguish implemented building blocks from a tested product.

## Product invariants

1. The phone always presents **one focused Windows activity full-screen**; switching activities must feel phone-native. The Windows desktop is an optional fallback, not the workspace model.
2. A spatial topic/grid overview is **optional and not yet settled**. It must be a phone-side organization of independent copies/sources. Never move, resize, minimize, foreground or reorganize the Windows user's physical desktop to maintain the grid. Only explicitly requested real close/launch actions may change app lifetime, with appropriate confirmation.
3. The GPU/virtual-monitor rendering and DPI POC is being worked on independently. Do not modify its branch, installed display driver, test machine configuration, or findings. Integrate its proven interface and measurements when delivered.
4. Each device feature needs explicit phone permission, trusted-peer authorization, a visible on/off state, teardown on disconnect, and device-specific capability/fallback reporting. Never pretend a stream is a Windows device until an actual OS-facing endpoint is available.
5. Preserve the existing secure LAN channel; never forward video/audio/sensor data to cloud endpoints by default.

## Deliverables (delivery order, not test results)

| ID | Priority | Deliverable | Existing / remaining |
| --- | --- | --- | --- |
| DF-01 | P0 (POC-owned) | Native-resolution, DPI-verified virtual-monitor/window rendering and GPU capture | Delegated GPU POC. Exact 1440×3120 @ genuine Windows 336 DPI remains unproven |
| DF-02 | P0 (main app) | Isolated per-window presentation at phone dimensions, focus routing and native Android-style activity switching | Existing desktop stream and native window listing; safe separate window copies/full-screen focus still missing |
| DF-03 | P0 | Physical Windows desktop preservation | Existing topology guard; host window activation from old picker and draft grid is **not isolated** and must not be merged as default window-first behavior |
| DF-04 | P1 | Windows playback audio -> Android speakers/headphones | Implement authenticated, opt-in WASAPI loopback -> bounded audio packets -> Android AudioTrack, with audio focus, mute, reset and stop |
| DF-05 | P1 | Android microphone -> Windows application-selectable microphone | Permissioned AudioRecord and streaming, plus genuine Windows capture-device integration; OS endpoint is still missing |
| DF-06 | P1 | Android camera(s) -> Windows application-selectable webcam | Permissioned CameraX/Camera2 with format/frame controls, plus Windows Media Foundation virtual-camera source. Windows 11 version/build support varies |
| DF-07 | P1 | Device Bridge media controls | Per-peer capability advertisement, stream leases, privacy/revocation controls, hotplug, errors and independent streaming lifetime |
| DF-08 | P2 | Advanced Windows interaction from phone | True mapped touch/pen, IME/text, keyboard/mouse modes, safe input ownership; no global mouse capture for workspace |
| DF-09 | P2 | Android phone sensors in Windows | Permissioned per-sensor stream (orientation/motion, optional location), explicit Windows consumers/adapters; avoid inventing generic Windows HID mapping |
| DF-10 | P2 | Productivity | Clipboard image/MIME, file manager mutations, OS-native open-with actions, Explorer extension, phone Sharesheet and safe write-back |
| DF-11 | P2 | Activity UX and Quick View | Recents/switcher, source thumbnails, fast focused handoff, back navigation, per-app state and disconnected UI |
| DF-12 | P3 | Workspace organization | OPTIONAL topic cards/grid/2.5D, grouped **copies**, no physical desktop layout effects; revisit after DF-02 |
| DF-13 | P3 | Managed Chromium | Phone-specific CSS viewport/DPR, session/profile controls, offscreen Chromium performance evaluation; ordinary browser HWND fallback |
| DF-14 | P3 | Smart adaptive streaming | Single focused high-quality pipeline; cached/low-fps unfocused sources, latency budgets, GPU/VRAM limits and graceful downgrades |
| DF-15 | P3 | Device profiles and resumption | Stable enrolled peer identities, per-device virtual display preferences, tablet/foldable refresh, reconnect with current scaling verification |
| DF-16 | P4 | Gaming, conferencing and media quality | Optional supported gamepad, stereo/mono/voice routing, audio/video sync, camera+microphone concurrency, encoding efficiency |
| DF-17 | P4 | Security and release | Threat model, consent UI, narrowly scoped privileged broker only if needed, signed installers, undo/recovery, Windows/Android distribution |

## Near-term implementation sequence

**A.** Deliver PC -> phone audio on a separate branch (independent of virtual-monitor POC). Use system-output loopback explicitly, not an unverified per-app feed. Default OFF.

**B.** Implement phone mic capture + network path after establishing actual Windows virtual capture endpoint strategy. The network path alone is not a functional virtual microphone.

**C.** Integrate Windows user-visible camera using supported Media Foundation APIs plus an Android camera producer, not a fake UVC claim. Prefer current-user scope, permission-bound lifetime, safe fallback on older Windows.

**D.** Build the isolated focus/window-source interface once POC measured evidence is returned. Defer physical-window focus/grid behavior; do not let layout mutate the physical desktop.

## Scope boundaries

The earlier draft PR #4 is **not a suitable default UX until revised**: its host focus operation calls SetForegroundWindow against ordinary Windows windows. Keep it draft/unmerged and design against DF-02/03 instead.

Acceptance and hardware/device testing may be scheduled later per owner direction. Code committed or interface added does not imply delivered hardware support.

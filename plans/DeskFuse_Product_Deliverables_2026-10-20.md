# DeskFuse product deliverables — device-first implementation backlog

Status: delivery checklist, 20 October 2026. Distinguish implemented building blocks from a tested product.

## Product invariants

1. The phone always presents **one focused Windows activity full-screen**; switching activities must feel phone-native. The Windows desktop is an optional fallback, not the workspace model.
2. A spatial topic/grid overview is **optional and not yet settled**. It must be a phone-side organization of independent copies/sources. Never move, resize, minimize, foreground or reorganize the Windows user's physical desktop to maintain the grid. Only explicitly requested real close/launch actions may change app lifetime, with appropriate confirmation.
3. The GPU/virtual-monitor rendering and DPI POC is being worked on independently. Do not modify its branch, installed display driver, test machine configuration, or findings. Integrate its proven interface and measurements when delivered.
4. Each device feature needs explicit phone permission, trusted-peer authorization, a visible on/off state, teardown on disconnect, and device-specific capability/fallback reporting. Never pretend a stream is a Windows device until an actual OS-facing endpoint is available.
5. Preserve the existing secure LAN channel; never forward video/audio/sensor data to cloud endpoints by default.

## Binding media identity: Option A — per-device Windows endpoints (decided 20 October 2026)

Each **enrolled Android device** has its own long-lived, Windows-visible audio/video device identities, scoped to the Windows host's signed-in user and the authenticated Android enrollment. **Do not use one shared DeskFuse microphone, camera or speaker**. Physical disconnection must produce genuine Windows device-removal/unavailability notifications; reconnection brings back the same recognizable instance IDs and user-selected preferences. Do not mask a disconnected phone by keeping its mic ACTIVE and feeding silence. Do not impersonate USB transport: use supported Windows software-device/virtual-camera mechanisms that give the native Plug-and-Play experience. Applications and Windows choose their own fallback when a preferred endpoint becomes unavailable.

| Capability | Phone A | Tablet B | Connection ownership |
| --- | --- | --- | --- |
| Camera | DeskFuse · Phone A Camera | DeskFuse · Tablet B Camera | Independent per-device camera source; front/back lens choice remains within that device |
| Microphone | DeskFuse · Phone A Microphone | DeskFuse · Tablet B Microphone | Independent per-device Windows capture endpoint |
| Speaker/output | DeskFuse · Phone A Speakers | DeskFuse · Tablet B Speakers | Independent per-device Windows render endpoint; sound delivered to the selected phone |
| Remote visual workspace | Optional | Full-screen focused workspace | **One reserved virtual-display owner initially**, separate from any camera/mic/audio device owner |

This means a meeting running on Windows may display on **Tablet B**, take high-quality **Phone A** camera/microphone, and route audio to **Tablet B** speakers, at the same time. Source selection is **per endpoint**, not a single global 'active phone' toggle. A peer can serve media without owning a virtual monitor. All media tracks have independent connect/disconnect and explicit authorization.

**Key host prerequisite:** `SessionHost.RunSessionsAsync` currently awaits one `PhoneSession` before it accepts another. Introduce concurrently authenticated peers with a dedicated **media-only session role** that never starts or removes FocusIdd monitors, changes DPI, focuses original physical windows, or commandeers mouse/input. At most one display owner in the initial version; multiple independent camera, microphone and render-audio peers. Protect against two connections claiming the same device identity and against a stale disconnect invalidating a newer authenticated connection.

**Windows device availability:** A disconnected endpoint must be removed/marked not present by its real underlying provider, and re-added with a stable instance ID when the authorized phone returns. For audio, preserve KS interface reference strings and pin IDs; avoid repeated uninstallation/reinstallation that destroys Windows user preferences. Windows may choose a fallback device; never silently enforce a default by writing undocumented policy keys. App behavior on hotplug varies and must be treated honestly.

**Camera:** Use Windows 11 Media Foundation virtual-camera support with per-device stable instance parameters; Windows 10 needs a separate compatibility path if camera functionality is required there. No pretend webcam from a network stream alone. One device-specific camera endpoint may allow switching its front/rear optics without changing the Windows endpoint ID.

**Mobile input:** Physical USB/Bluetooth keyboards, mice, touchpads and gamepads paired with the Android phone/tablet are handled through the phone's input stack and DeskFuse's authenticated remote-input modes (roughly matching Desklink), not automatically re-enumerated as separate Windows PnP hardware. That means phone camera+mic can complement tablet display without surrendering either device's own input ownership.

**Remembered preferences:** Persist device routing intentions per authenticated enrollment, not per IP address. Reconnecting may restore routes after verifying trust, OS/phone permissions and user preferences. Let Windows and applications own default-device and per-app selection. Camera/mic/speaker streams are distinct; remember each choice independently. Revoking/unpairing one device invalidates its capabilities and removes its dedicated endpoints, without affecting other devices.

**Speaker distinction:** The existing optional `PC sound -> Android` implementation mirrors the PC's default output via WASAPI. It **does not yet** create genuine selectable per-device Windows speakers. Deliver a Windows audio render endpoint to satisfy USB-like speaker semantics.

**Privacy:** Device identity is durable, but capture consent is revocable. A disappeared phone is not an active device, and no stream may continue from a disconnected/revoked phone. Reconnection only resumes user-authorized media when Android permissions and platform background-capture policies allow it.

## Deliverables (delivery order, not test results)

| ID | Priority | Deliverable | Existing / remaining |
| --- | --- | --- | --- |
| DF-01 | P0 (POC-owned) | Native-resolution, DPI-verified virtual-monitor/window rendering and GPU capture | Delegated GPU POC. Exact 1440×3120 @ genuine Windows 336 DPI remains unproven |
| DF-02 | P0 (main app) | Isolated per-window presentation at phone dimensions, focus routing and native Android-style activity switching | Existing desktop stream and native window listing; safe separate window copies/full-screen focus still missing |
| DF-03 | P0 | Physical Windows desktop preservation | Existing topology guard; host window activation from old picker and draft grid is **not isolated** and must not be merged as default window-first behavior |
| DF-04 | P1 | Per-device Windows speakers + PC -> Android audio | Initial WASAPI mixed-output mirroring is distinct; deliver separate stable, hotpluggable Windows render endpoint **per enrolled phone/tablet**, with remembered user preference |
| DF-05 | P1 | Per-device Windows microphone | Permissioned Android AudioRecord + encrypted PCM + real stable hotpluggable Windows microphone endpoint **for each enrolled device**; normal device arrival/removal |
| DF-06 | P1 | Per-device Windows camera | Permissioned Android camera feed + per-enrollment stable Windows webcam (MF virtual camera for Windows 11; compatibility path for Windows 10); no display-session dependency |
| DF-07 | P0/P1 | Concurrent Device Bridge peers and endpoint identities | Multiple authenticated phones/tablets at once; media-only role independent of display ownership; stable separate camera/mic/speaker identities, hotplug, revocation, isolated routing |
| DF-08 | P2 | Advanced Windows interaction from phone | True mapped touch/pen, IME/text, keyboard/mouse modes, safe input ownership; no global mouse capture for workspace |
| DF-09 | P2 | Android phone sensors in Windows | Permissioned per-sensor stream (orientation/motion, optional location), explicit Windows consumers/adapters; avoid inventing generic Windows HID mapping |
| DF-10 | P2 | Productivity | Clipboard image/MIME, file manager mutations, OS-native open-with actions, Explorer extension, phone Sharesheet and safe write-back |
| DF-11 | P2 | Activity UX and Quick View | Recents/switcher, source thumbnails, fast focused handoff, back navigation, per-app state and disconnected UI |
| DF-12 | P3 | Workspace organization | OPTIONAL topic cards/grid/2.5D, grouped **copies**, no physical desktop layout effects; revisit after DF-02 |
| DF-13 | P3 | Managed Chromium | Phone-specific CSS viewport/DPR, session/profile controls, offscreen Chromium performance evaluation; ordinary browser HWND fallback |
| DF-14 | P3 | Smart adaptive streaming | Single focused high-quality pipeline; cached/low-fps unfocused sources, latency budgets, GPU/VRAM limits and graceful downgrades |
| DF-15 | P3 | Device profiles and resumption | Stable enrolled peer identities, routing and media preferences per device, display preferences, tablet/foldable refresh and reconnection verification |
| DF-16 | P4 | Gaming, conferencing and media quality | Optional supported gamepad, stereo/mono/voice routing, audio/video sync, camera+microphone concurrency, encoding efficiency |
| DF-17 | P4 | Security and release | Threat model, consent UI, narrowly scoped privileged broker only if needed, signed installers, undo/recovery, Windows/Android distribution |

## Near-term implementation sequence

**A.** Deliver PC -> phone audio on a separate branch (independent of virtual-monitor POC). Use system-output loopback explicitly, not an unverified per-app feed. Default OFF on the phone AND host. The host operator must opt in with `--enable-pc-audio`; the phone must request playback. PCM16 stereo, 48kHz LAN transport is an initial implementation, not a measured A/V sync or performance claim. Older hosts advertise no audio capability.

**B.** Build concurrent **media-only authenticated sessions** and source IDs per enrolled device. Phone mic capture and endpoint implementation must connect the authenticated phone to its own Windows microphone without taking over the tablet's display. Never present a silent disconnected endpoint as active.

**C.** Build per-device Windows webcams plus per-device Windows speaker render endpoints. Keep enrolled identity stable through hotplug and app restart. The virtual camera must use an OS-recognized source; never advertise capability before working hardware/software endpoint exists.

**D.** Build the isolated focus/window-source interface once POC measured evidence is returned. Defer physical-window focus/grid behavior; do not let layout mutate the physical desktop.

## Scope boundaries

The earlier draft PR #4 is **not a suitable default UX until revised**: its host focus operation calls SetForegroundWindow against ordinary Windows windows. It was closed unmerged and design against DF-02/03 instead.

Acceptance and hardware/device testing may be scheduled later per owner direction. Code committed or interface added does not imply delivered hardware support.

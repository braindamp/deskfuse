# DeskFuse product deliverables — device-first implementation backlog

Status: delivery checklist, 20 October 2026. Distinguish implemented building blocks from a tested product.

## Product invariants

1. The phone always presents **one focused Windows activity full-screen**; switching activities must feel phone-native. The Windows desktop is an optional fallback, not the workspace model.
2. A spatial topic/grid overview is **optional and not yet settled**. It must be a phone-side organization of independent copies/sources. Never move, resize, minimize, foreground or reorganize the Windows user's physical desktop to maintain the grid. Only explicitly requested real close/launch actions may change app lifetime, with appropriate confirmation.
3. The GPU/virtual-monitor rendering and DPI POC is being worked on independently. Do not modify its branch, installed display driver, test machine configuration, or findings. Integrate its proven interface and measurements when delivered.
4. Each device feature needs explicit phone permission, trusted-peer authorization, a visible on/off state, teardown on disconnect, and device-specific capability/fallback reporting. Never pretend a stream is a Windows device until an actual OS-facing endpoint is available.
5. Preserve the existing secure LAN channel; never forward video/audio/sensor data to cloud endpoints by default.

## Binding product experience: seamless remembered device routing

DeskFuse should disappear from the user's workflow after an **explicit initial choice**. A Windows AI agent or meeting application must be able to keep the same named **DeskFuse Phone Microphone** as its microphone, even if the phone temporarily disconnects. On reconnect the phone should feed that existing selected endpoint again without forcing a microphone picker in the Windows app. Similarly, when the user has chosen PC sound on the phone, reconnect should resume PC -> phone speakers by remembered preference, not demand another sound-toggle tap. Remember preferences **per enrolled host/phone**, not as one global setting across devices.

- **Endpoint identity vs active phone:** The Windows capture endpoint must remain OS-visible and stable when phone data is absent. Disconnection delivers **silence**, never an automatic fallback to physical PC mic or takeover of Windows global defaults. Do not tear down/recreate the endpoint just because the phone is absent.
- **Route binding:** Store the stable Windows audio endpoint ID separately from an ephemeral stream ID/session. Windows apps may remember the user-selected input in their own preferences. DeskFuse does **not** silently rewrite global/per-app default audio devices; these are user/OS selections. A bundled stable DeskFuse virtual endpoint is the desired product; until a signed device endpoint exists, an expressly configured **already-installed virtual audio cable** can bridge mic PCM into Windows. This temporary dependency is not the final integrated endpoint.
- **Output preference:** The Android app remembers whether the last approved host session had PC playback to the phone enabled. On reconnect it resumes when the host advertises permission and Android can play audio. It **does not** change the physical PC's speaker output, interrupt its audio, or claim that the desktop app is the audio default.
- **Privacy:** Mic capture only follows a user opt-in and Android RECORD_AUDIO permission, remains clearly visible and stoppable, obeys microphone foreground-service policy if it continues in the background, stops on explicit off/disconnect or host denial, and resumes on reconnect **only when permission/policy still allow**. A revoked permission or absent endpoint fails closed and retains the user's preference without leaking samples.
- **First-use UX:** Ask once for a chosen function and route, then remember it. Subsequent disconnect/reconnect, phone sleep/rotate and host restart should not require reselecting. Show unobtrusive status with one-tap override instead of recurring dialogs. Per-device preferences are revocable when unpairing.
- **Priority:** Dictation into Windows AI agents and voice-controlled applications is a first-class workflow, not ancillary AV-call functionality. Phone camera as Windows webcam and other permitted sensors follow the same device bridge/selection model.

## Deliverables (delivery order, not test results)

| ID | Priority | Deliverable | Existing / remaining |
| --- | --- | --- | --- |
| DF-01 | P0 (POC-owned) | Native-resolution, DPI-verified virtual-monitor/window rendering and GPU capture | Delegated GPU POC. Exact 1440×3120 @ genuine Windows 336 DPI remains unproven |
| DF-02 | P0 (main app) | Isolated per-window presentation at phone dimensions, focus routing and native Android-style activity switching | Existing desktop stream and native window listing; safe separate window copies/full-screen focus still missing |
| DF-03 | P0 | Physical Windows desktop preservation | Existing topology guard; host window activation from old picker and draft grid is **not isolated** and must not be merged as default window-first behavior |
| DF-04 | P1 | Windows playback audio -> Android speakers/headphones | Initial opt-in WASAPI loopback + AudioTrack code is on audio branch; remember user's per-host choice and automatically resume on reconnect when permitted; no Windows global default output changes |
| DF-05 | P1 | Android microphone -> **stable Windows application-selectable microphone** | Consent-based capture + stream + Windows virtual endpoint; application choice persists when phone disappears; reconnect resumes into same endpoint; temporary external virtual cable is optional bridge, bundled endpoint still missing |
| DF-06 | P1 | Android camera(s) -> Windows application-selectable webcam | Permissioned CameraX/Camera2 with format/frame controls, plus Windows Media Foundation virtual-camera source. Windows 11 version/build support varies |
| DF-07 | P1 | Device Bridge media controls and preferences | Per-peer capability advertisement, consent, durable selected audio endpoint and output preference, stream leases, reconnect rebind, privacy/revocation, hotplug and independent stream lifetime |
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

**A.** Deliver PC -> phone audio on a separate branch (independent of virtual-monitor POC). Use system-output loopback explicitly, not an unverified per-app feed. Default OFF on the phone AND host. The host operator must opt in with `--enable-pc-audio`; the phone must request playback. PCM16 stereo, 48kHz LAN transport is an initial implementation, not a measured A/V sync or performance claim. Older hosts advertise no audio capability.

**B.** Implement permissioned phone mic capture + network route into an explicitly configured existing virtual cable playback endpoint so Windows can choose that cable's capture side. The stable OS endpoint (cable's recording side) is selected once in Windows. **Never** claim this is the final bundled DeskFuse virtual microphone: a maintained, signed, first-party Windows capture endpoint remains to be delivered. On disconnect feed silence and keep the endpoint installed.

**C.** Integrate Windows user-visible camera using supported Media Foundation APIs plus an Android camera producer, not a fake UVC claim. Prefer current-user scope, permission-bound lifetime, safe fallback on older Windows.

**D.** Build the isolated focus/window-source interface once POC measured evidence is returned. Defer physical-window focus/grid behavior; do not let layout mutate the physical desktop.

## Scope boundaries

The earlier draft PR #4 is **not a suitable default UX until revised**: its host focus operation calls SetForegroundWindow against ordinary Windows windows. It was closed unmerged and design against DF-02/03 instead.

Acceptance and hardware/device testing may be scheduled later per owner direction. Code committed or interface added does not imply delivered hardware support.

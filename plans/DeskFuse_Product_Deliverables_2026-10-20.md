# DeskFuse product deliverables — device-first implementation backlog

Status: delivery checklist, 20 October 2026. Distinguish implemented building blocks from a tested product.

## Product invariants

1. The phone always presents **one focused Windows activity full-screen**; switching activities must feel phone-native. The Windows desktop is an optional fallback, not the workspace model.
2. A spatial topic/grid overview is **optional and not yet settled**. It must be a phone-side organization of independent copies/sources. Never move, resize, minimize, foreground or reorganize the Windows user's physical desktop to maintain the grid. Only explicitly requested real close/launch actions may change app lifetime, with appropriate confirmation.
3. The GPU/virtual-monitor rendering and DPI POC is being worked on independently. Do not modify its branch, installed display driver, test machine configuration, or findings. Integrate its proven interface and measurements when delivered.
4. Each device feature needs explicit phone permission, trusted-peer authorization, a visible on/off state, teardown on disconnect, and device-specific capability/fallback reporting. Never pretend a stream is a Windows device until an actual OS-facing endpoint is available.
5. Preserve the existing secure LAN channel; never forward video/audio/sensor data to cloud endpoints by default.

## Audio device behavior: USB-like arrival/removal (binding goal; topology undecided)

**Owner correction, 20 October 2026:** A disconnected phone must **not** remain an active Windows microphone/speaker producing artificial silence. The desired experience is an ordinary **disconnected audio device**, with Windows and apps receiving native device arrival/removal events when the phone drops or returns, retaining the same recognizable endpoint identity on reconnect. The host must not replace the user's physical mic/speakers by an arbitrary fallback policy. Windows itself may choose a different active fallback while the device is away; when it returns the OS applies its remembered user and per-app preferences (actual third-party app recovery varies). The goal is native USB-like semantics, **not USB protocol emulation**.

The audio topology is **NOT DECIDED**; do not implement an endpoint lifecycle or hardcode silence-on-disconnect until it is chosen:

- **Option A — Per-phone endpoint pair.** Each trusted phone has its own stable Windows microphone + speaker identities and separate PnP presence, enabling simultaneous phones and per-application routing. Requires identity/provisioning and device-limit policy.
- **Option B — One shared DeskFuse endpoint pair.** One Windows microphone + speaker; one designated phone owns it at a time. The endpoint appears when the designated phone connects and disappears when it disconnects. Simple picker, but phone handover/ownership and source priority are required.
- **Option C — Shared default plus optional per-phone endpoints.** Extends B with advanced dedicated devices, at increased install/configuration complexity.

The OS-facing endpoint must be a genuine Windows audio device (e.g. a signed virtual audio driver using supported Windows audio interfaces), with consistent device instance identity and appropriate device state notifications, not merely incoming network samples. `DEVICE_STATE_UNPLUGGED` specifically requires jack-presence detection, whereas a dynamically removed software audio device would typically be `NOTPRESENT` / reappear. Exact lifetime semantics are an engineering design item. The install/runtime privilege boundary must be established separately. Do not attempt to spoof arbitrary USB device classes.

**Independent choice:** whether DeskFuse only honors Windows/app audio selection versus offering an **explicitly opted-in** 'restore my last Windows preferred default on reappearance' feature. Do not silently force or overwrite user/system/app defaults. Windows may remember preferences for previously chosen devices, but not all applications automatically reopen an invalidated audio client.

**Speaker meaning:** `PC sound -> phone` currently implemented on the audio branch is *loopback mirroring*, not a selectable Windows output device. The desired Windows-render endpoint would route sound to the phone as a genuine speaker destination, without necessarily playing it from the physical PC output. These are separate capabilities.

**Mic meaning:** Phone microphone PCM transport/Android permission is not equivalent to an app-selectable Windows microphone; both sender and real capture endpoint are required.

**Privacy/reconnect invariant:** Restore a remembered **routing preference**, subject to the selected phone being paired, authorized, physically connected and Android microphone permissions. Losing the connection closes the active mic capture; returning may automatically resume only while the user-granted policy and phone permissions remain valid. Persistent endpoint identity does not mean always-on mic access.

**Decision needed before implementing the Windows device:** A, B or C, and whether OS preference handling alone is sufficient or a user-opted explicit re-selection feature is needed. Virtual-display/rendering/grid POC remains untouched.

## Deliverables (delivery order, not test results)

| ID | Priority | Deliverable | Existing / remaining |
| --- | --- | --- | --- |
| DF-01 | P0 (POC-owned) | Native-resolution, DPI-verified virtual-monitor/window rendering and GPU capture | Delegated GPU POC. Exact 1440×3120 @ genuine Windows 336 DPI remains unproven |
| DF-02 | P0 (main app) | Isolated per-window presentation at phone dimensions, focus routing and native Android-style activity switching | Existing desktop stream and native window listing; safe separate window copies/full-screen focus still missing |
| DF-03 | P0 | Physical Windows desktop preservation | Existing topology guard; host window activation from old picker and draft grid is **not isolated** and must not be merged as default window-first behavior |
| DF-04 | P1 | Windows sound -> Android audio, plus eventual selectable Windows speakers | Existing branch implements optional *mixed-PC-output mirroring*, not native Windows render endpoint; separate PnP audio destination and remembered choice still needed |
| DF-05 | P1 | Phone mic -> real Windows microphone with USB-like presence semantics | Permissioned phone AudioRecord + encrypted PCM path + true Windows capture endpoint; stable ID across reappearance; no silent active endpoint; topology A/B/C pending decision |
| DF-06 | P1 | Android camera(s) -> Windows application-selectable webcam | Permissioned CameraX/Camera2 with format/frame controls, plus Windows Media Foundation virtual-camera source. Windows 11 version/build support varies |
| DF-07 | P1 | Device Bridge control, endpoint identity and consent | Negotiated per-peer media capabilities, selected-phone ownership, user-granted routing preference, real PnP endpoint availability, hotplug notification, reconnect, revocation |
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

**B.** Prepare permissioned Android mic capture and encrypted packets independently of the OS endpoint. Do **not** choose device disappearance/persistent-silence behavior or assert Windows selectable-mic functionality until the owner chooses A/B/C topology. Evaluate signed Windows virtual audio device implementation and user/OS selection semantics. An existing third-party virtual cable is only a temporary development adapter, not native USB-like behavior.

**C.** Integrate Windows user-visible camera using supported Media Foundation APIs plus an Android camera producer, not a fake UVC claim. Prefer current-user scope, permission-bound lifetime, safe fallback on older Windows.

**D.** Build the isolated focus/window-source interface once POC measured evidence is returned. Defer physical-window focus/grid behavior; do not let layout mutate the physical desktop.

## Scope boundaries

The earlier draft PR #4 is **not a suitable default UX until revised**: its host focus operation calls SetForegroundWindow against ordinary Windows windows. It was closed unmerged and design against DF-02/03 instead.

Acceptance and hardware/device testing may be scheduled later per owner direction. Code committed or interface added does not imply delivered hardware support.

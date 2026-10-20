# DeskFuse product deliverables — device-first implementation backlog

Status: delivery checklist, 20 October 2026. Distinguish implemented building blocks from a tested product.

## Product invariants

1. **Default mode:** one focused Windows app fills the phone, with Android-style switching and no physical-desktop rearrangement. **Separate extended-display mode:** a phone/tablet can instead behave as an additional ordinary Windows monitor. These modes must not be conflated.
2. A spatial topic/grid overview is **optional and not yet settled**. It must be a phone-side organization of independent copies/sources. Never move, resize, minimize, foreground or reorganize the Windows user's physical desktop to maintain the grid. Only explicitly requested real close/launch actions may change app lifetime, with appropriate confirmation.
3. The GPU/virtual-monitor rendering and DPI POC is being worked on independently. Do not modify its branch, installed display driver, test machine configuration, or findings. Integrate its proven interface and measurements when delivered.
4. Each device feature needs explicit phone permission, trusted-peer authorization, a visible on/off state, teardown on disconnect, and device-specific capability/fallback reporting. Never pretend a stream is a Windows device until an actual OS-facing endpoint is available.
5. Preserve the existing secure LAN channel; never forward video/audio/sensor data to cloud endpoints by default.

## Binding product vision: FUSE, not merely remote control (confirmed 20 October 2026)

DeskFuse **fuses a trusted Android phone/tablet and a Windows PC into one high-performance computing experience**. Once an enrolled device is approved, the product should discover and make **all hardware/software capabilities that it can actually support** available in both directions: microphones, cameras, speaker/output routes (including surround where supported), keyboard/mouse/touchpad, sensor streams, displays, clipboard, files and future capabilities. Enrollment establishes **trust and capability discovery**, not an Android runtime permission bypass. The OS still owns restricted permissions and their indicators; every real capture/playback route must be identifiable, revocable, and scoped to that enrolled device. Avoid repeatedly bothering the user after initial consent.

**Foreground MVP:** Real-time media and sensor bridging is enabled during an active foreground DeskFuse experience. Windows-facing device presence mirrors the actual availability of its phone. Android mic/camera capture cannot silently continue under unrelated background conditions. User-approved routing choices are remembered per enrollment; reconnection restores them when supported and permitted. The word FUSE includes being useful without the original Windows stream visible, e.g. phone microphone dictating into a Windows AI agent or an independent camera feeding a PC conference.

**Future product/pricing candidate — NOT an implemented limit or finalized paywall:** Consider a **Pro background-device mode** that keeps authorized streaming/services running with the phone screen off or DeskFuse UI closed: microphone to Windows agent, camera where platform permits, PC audio -> phone headphones, etc. Background mode must explicitly respect Android's microphone/camera foreground-service types, notifications, while-in-use permission rules, system microphone/camera privacy indicators, power/thermal behavior, and platform policy. Pricing/entitlements are a later product decision, not a reason to hardwire the transport around foreground-only access. Avoid confusing a user-visible Android microphone foreground service with unrestricted silent background capture.

**Audio fidelity:** Preserve Windows and Android native channel layouts, sample-rate metadata, and hardware output capability. If an output device truly supports multi-channel 5.1 or 7.1, deliver spatial channels in the original speaker order rather than always flattening to stereo. Unsupported playback routes must negotiate an explicit stereo fallback with a real speaker-aware downmix; never simply truncate/discard rear/center channels or make up surround. An attached USB/HDMI headset/DAC may support more than the phone's built-in speakers. Current PC output mirroring is distinct from an eventual per-phone **Windows selectable speaker** endpoint.

**Performance is architectural, not a last-minute optimization:** Prefer direct native capture APIs, small preallocated audio blocks, accurate source timestamps and channel layouts, nonblocking callbacks, bounded low-latency queues, priority-aware transport, independent control/media lifetimes and a future separate latency-sensitive data lane so large video/file packets cannot indefinitely head-of-line-block microphone/audio. No forced PCM->stereo conversion, repeated full-frame copies, arbitrarily large buffers or unmeasured "zero-copy" claims. Audio/video synchronization and low jitter must be measured later, but designs must carry timestamps and enable pacing/clock correction. Avoid imposing hardcoded 48kHz/2-channel limits at transport/product boundaries. When using compatibility paths, label them rather than letting accumulated shortcuts become architectural limits.

## Binding media identity: Option A — per-device Windows endpoints (decided 20 October 2026)

Each **enrolled Android device** has its own long-lived, Windows-visible audio/video device identities, scoped to the Windows host's signed-in user and the authenticated Android enrollment. **Do not use one shared DeskFuse microphone, camera or speaker**. Physical disconnection must produce genuine Windows device-removal/unavailability notifications; reconnection brings back the same recognizable instance IDs and user-selected preferences. Do not mask a disconnected phone by keeping its mic ACTIVE and feeding silence. Do not impersonate USB transport: use supported Windows software-device/virtual-camera mechanisms that give the native Plug-and-Play experience. Applications and Windows choose their own fallback when a preferred endpoint becomes unavailable.

| Capability | Phone A | Tablet B | Connection ownership |
| --- | --- | --- | --- |
| Camera | DeskFuse · Phone A Camera | DeskFuse · Tablet B Camera | Independent per-device camera source; front/back lens choice remains within that device |
| Microphone | DeskFuse · Phone A Microphone | DeskFuse · Tablet B Microphone | Independent per-device Windows capture endpoint |
| Speaker/output | DeskFuse · Phone A Speakers | DeskFuse · Tablet B Speakers | Independent per-device Windows render endpoint; sound delivered to the selected phone |
| Remote visual workspace | Focus or extended-display mode | Focus or extended-display mode | Independent per-device resources when supported; current host-global FocusIdd backend has one exclusive safe reservation |

This means a meeting running on Windows may display on **Tablet B**, take high-quality **Phone A** camera/microphone, and route audio to **Tablet B** speakers, at the same time. Source selection is **per endpoint**, not a single global 'active phone' toggle. A peer can serve media without owning a virtual monitor. All media tracks have independent connect/disconnect and explicit authorization.

**Key host prerequisite:** The host supports concurrent authenticated peers and a display-admission boundary. Current FocusIdd creation/removal is host-global, so its backend admission remains exclusive until safely refactored into per-display resources. This is a **temporary runtime safety limit, not a one-display product architecture**. A peer may provide several functions simultaneously. Accessory connections never create a monitor, and stale disconnects cannot evict newer authorized peer leases.

**Windows device availability:** A disconnected endpoint must be removed/marked not present by its real underlying provider, and re-added with a stable instance ID when the authorized phone returns. For audio, preserve KS interface reference strings and pin IDs; avoid repeated uninstallation/reinstallation that destroys Windows user preferences. Windows may choose a fallback device; never silently enforce a default by writing undocumented policy keys. App behavior on hotplug varies and must be treated honestly.

**Camera:** Use Windows 11 Media Foundation virtual-camera support with per-device stable instance parameters; Windows 10 needs a separate compatibility path if camera functionality is required there. No pretend webcam from a network stream alone. One device-specific camera endpoint may allow switching its front/rear optics without changing the Windows endpoint ID.

**Mobile input:** USB/Bluetooth keyboards, mice and trackpads paired with Android are handled by Android and forwarded as input modes to the selected PC target, not automatically re-enumerated as Windows USB devices. The phone itself can become a **standalone touchpad** with left/right/middle buttons, scroll and gestures, with no screen streaming requirement. Another phone can independently provide camera/microphone. Input authorization and collision/arbitration between peers must be explicit.

**Remembered preferences:** Persist device routing intentions per authenticated enrollment, not per IP address. Reconnecting may restore routes after verifying trust, OS/phone permissions and user preferences. Let Windows and applications own default-device and per-app selection. Camera/mic/speaker streams are distinct; remember each choice independently. Revoking/unpairing one device invalidates its capabilities and removes its dedicated endpoints, without affecting other devices.

**Speaker distinction:** The existing optional `PC sound -> Android` implementation mirrors the PC's default output via WASAPI. It **does not yet** create genuine selectable per-device Windows speakers. Deliver a Windows audio render endpoint to satisfy USB-like speaker semantics.

**Privacy:** Device identity is durable, but capture consent is revocable. A disappeared phone is not an active device, and no stream may continue from a disconnected/revoked phone. Reconnection only resumes user-authorized media when Android permissions and platform background-capture policies allow it.

## Composable per-device modes (decided 20 October 2026)

**One-device MVP, multi-device architecture from the beginning.** Enrollment, connection, permission, capability and Windows endpoint identity are separate concepts. One Android device may combine any supported functions, and one Windows workflow may use multiple devices simultaneously. No singleton active device or media owner is permitted in the product architecture.

| Contribution/mode | Experience | Host-side meaning |
| --- | --- | --- |
| Focused phone-native workspace (default) | Selected Windows activity fills the phone; Android-style app switching | Isolated per-app virtual presentation without rearranging physical windows |
| Extended Windows display (separate mode) | Phone/tablet is an additional Windows monitor, not a copy or grid | Independent native Windows monitor with per-device resolution/DPI/refresh/rotation; POC-dependent |
| Standalone touchpad + mouse buttons | Phone acts as a trackpad with buttons, scrolling and gestures | Relative pointer/button/scroll routing to explicitly selected Windows target; not a new Windows mouse |
| Keyboard/IME | Physical keyboard paired to Android or on-screen input controls PC | Android input forwarding, text composition and shortcuts; target-scoped |
| Per-device camera | Phone camera selectable in Windows conferencing | Actual Windows camera endpoint; front/back camera routing within same device |
| Per-device microphone | Phone mic selectable for dictation/agents | Actual Windows capture endpoint with USB-like presence |
| Per-device speakers | Windows app outputs through chosen phone/tablet | Actual Windows audio render endpoint with USB-like presence; NOT merely mixed-PC loopback |
| Sensors | Per-device optional motion/orientation data | Permissioned sensor adapter; no fictitious generic Windows device |

Example: Tablet B supplies the extended monitor and speakers, Phone A supplies a high-quality camera/microphone, Phone C supplies a touchpad; a Bluetooth keyboard paired to Tablet B can still control a selected PC target. This is a **future composition example**, not something already functional.

**Capabilities:** display, camera, microphone, speakers, touchpad, keyboard, sensors. A peer may request any subset, but *requested* is not the same as *authorized* or *active*. A media/accessory connection cannot take display ownership; a display session must use the display backend's admission and resource lifecycle. At present there are logical endpoint keys and capability status only, **no actual Windows virtual mic/camera/speaker providers**. One device's display may also need accessory functions, carried on a separate authenticated channel initially.

**Input arbitration:** Relative pointer/button/keyboard input is directed to an explicitly authorized target, never implicitly to the physical PC user's work. If multiple devices want control of one target, access must be coordinated. Existing Desklink-like Android input modes remain the interaction model; touchpad/keyboard do not need independent Windows PnP peripherals.

**Display scaling dependency:** Keep the separate GPU/virtual-monitor POC isolated. Until it proves independent per-display ownership, retain the existing exclusive runtime gate for FocusIdd, but do not encode its limit into client roles, paired-device count or product UX architecture.

## Deliverables (delivery order, not test results)

| ID | Priority | Deliverable | Existing / remaining |
| --- | --- | --- | --- |
| DF-01 | P0 (POC-owned) | Native-resolution, DPI-verified virtual-monitor/window rendering and GPU capture | Delegated GPU POC. Exact 1440×3120 @ genuine Windows 336 DPI remains unproven |
| DF-02 | P0 (main app) | Isolated per-window presentation at phone dimensions, focus routing and native Android-style activity switching | Existing desktop stream and native window listing; safe separate window copies/full-screen focus still missing |
| DF-03 | P0 | Physical Windows desktop preservation | Existing topology guard; host window activation from old picker and draft grid is **not isolated** and must not be merged as default window-first behavior |
| DF-04 | P1 | Per-device Windows speakers + PC -> Android audio | Initial WASAPI mixed-output mirroring is distinct; deliver separate stable, hotpluggable Windows render endpoint **per enrolled phone/tablet**, with remembered user preference |
| DF-05 | P1 | Per-device Windows microphone | Permissioned Android AudioRecord + encrypted PCM + real stable hotpluggable Windows microphone endpoint **for each enrolled device**; normal device arrival/removal |
| DF-06 | P1 | Per-device Windows camera | Permissioned Android camera feed + per-enrollment stable Windows webcam (MF virtual camera for Windows 11; compatibility path for Windows 10); no display-session dependency |
| DF-07 | P0/P1 | Concurrent composable Device Bridge capabilities | Multiple authenticated peers; per-enrollment camera/mic/speakers, touchpad/keyboard intentions, independent grants/status, reconnect/hotplug, input arbitration |
| DF-08 | P2 | Remote input modes including standalone phone touchpad | Existing Android inputs plus relative touchpad/buttons/scroll, keyboard/IME, explicit target selection and multi-device collision handling; no Windows USB mouse required |
| DF-09 | P2 | Android phone sensors in Windows | Permissioned per-sensor stream (orientation/motion, optional location), explicit Windows consumers/adapters; avoid inventing generic Windows HID mapping |
| DF-10 | P2 | Productivity | Clipboard image/MIME, file manager mutations, OS-native open-with actions, Explorer extension, phone Sharesheet and safe write-back |
| DF-11 | P2 | Activity UX and Quick View | Recents/switcher, source thumbnails, fast focused handoff, back navigation, per-app state and disconnected UI |
| DF-12 | P3 | Workspace organization | OPTIONAL topic cards/grid/2.5D, grouped **copies**, no physical desktop layout effects; revisit after DF-02 |
| DF-13 | P3 | Managed Chromium | Phone-specific CSS viewport/DPR, session/profile controls, offscreen Chromium performance evaluation; ordinary browser HWND fallback |
| DF-14 | P3 | Smart adaptive streaming | Single focused high-quality pipeline; cached/low-fps unfocused sources, latency budgets, GPU/VRAM limits and graceful downgrades |
| DF-15 | P3 | Device profiles and resumption | Stable enrolled peer identities, independent role settings/routes, per-device extended-display profiles, tablet/foldable modes and reconnect verification |
| DF-16 | P1/P3 | Surround/multichannel audio, gaming and conferencing fidelity | Native 2/6/8-channel layouts where actually supported, loss-aware stereo downmix, clocked A/V, low-jitter routing, camera+mic concurrency, optional gamepad, efficient codec/transport |
| DF-17 | P4 | Security and release | Threat model, consent UI, narrowly scoped privileged broker only if needed, signed installers, undo/recovery, Windows/Android distribution |
| DF-18 | P2 (POC-dependent) | Tablet/phone as native extended Windows display | Separate mode from focused app and grid; independent virtual monitor life cycles, profiles and safe multi-display topology |
| DF-19 | Future product decision | Pro background device mode | Optional paid always-available bridge with screen off / UI closed, per-OS-consent foreground service/notifications for capture, intelligent power/network policy; **not committed pricing or shipped capability** |
| DF-20 | P1 cross-cutting | End-to-end low-latency performance architecture | Media/control priority, timestamps, bounded queues, A/V synchronization, channel layouts, nonblocking capture callbacks and hardware-native paths; benchmark later, do not paint product into a high-latency corner |

## Near-term implementation sequence

**A.** Deliver PC -> phone audio on a separate branch (independent of virtual-monitor POC). Use system-output loopback explicitly, not an unverified per-app feed. Default OFF on the phone AND host. The host operator must opt in with `--enable-pc-audio`; the phone must request playback. PCM16 stereo, 48kHz LAN transport is an initial implementation, not a measured A/V sync or performance claim. Older hosts advertise no audio capability.

**B.** Build concurrent **media-only authenticated sessions** and source IDs per enrolled device. Phone mic capture and endpoint implementation must connect the authenticated phone to its own Windows microphone without taking over the tablet's display. Never present a silent disconnected endpoint as active.

**C.** Build per-device Windows webcams plus per-device Windows speaker render endpoints. Keep enrolled identity stable through hotplug and app restart. The virtual camera must use an OS-recognized source; never advertise capability before working hardware/software endpoint exists.

**D.** After POC evidence, implement isolated focused-app presentation and extended Windows display as distinct modes. Only permit simultaneous virtual monitors after resources and teardown are truly per-display. Defer the grid.

## Scope boundaries

Draft grid PR #4 was closed unmerged: physical HWND foregrounding and layout effects are not the correct window-first architecture.

Acceptance and hardware/device testing may be scheduled later per owner direction. Code committed or interface added does not imply delivered hardware support.

# DESKFUSE — NEXT-SESSION ENGINEERING HANDOVER
**Date:** 2026-10-22 (Europe/Berlin)
**Repo:** https://github.com/braindamp/deskfuse
**Working branch:** `main` — **no experimental checkout**.
**Integration:** PR #26 merged; integration commit `56377b092c37bd856b5a7091201f21082c1a6231`. This handover is a later docs-only commit.
**Last passing source/CI:** `91262cfe004d5c48deaaf35c155a89cc6c7c6fde` — https://github.com/braindamp/deskfuse/actions/runs/39701085238. Windows and Android succeeded. Artifacts `deskfuse-windows-x64`, `deskfuse-audio-broker-x64`, `deskfuse-android-debug`.
**Frozen audio recovery branch:** `reference/audio-event-android-20261021`, SHA `7e765cedf499c3fabe70af1996a3b8cfdc3adff7`. NEVER edit this baseline.
**Historical experimental/draft stacked PRs:** #17/#18/#20–#25 included by #26 and closed as superseded. Separate, untested silence-edge PR #19 is NOT included.

## Product rule: DESKFUSE GETS OUT OF THE WAY
- One initial, low-profile **Windows-side** approval per phone; the phone is cryptographically trusted indefinitely until **explicitly revoked by the Windows owner**. No repeated pairing/acceptance codes during ordinary reconnects. Later settings may offer stricter trust policy; do not build settings now. Identity = verified per-device credential, NOT device name, IP address or network. Preserve secure credential storage (Windows DPAPI; Android encrypted SharedPreferences), real auth checks, and a reliable revoke mechanism.
- Once a phone is connected, its supported capabilities appear automatically: in particular a real Windows-selectable DeskFuse speaker endpoint. Windows decides whether to route sound to it. No separate user-facing speaker On/Off; an advanced developer setting could disable speakers for debugging later. Speaker must stay selectable while connected and silent, independently of any virtual display. Support speaker-only with no video/Notepad/GPU.
- Android knows when it has no default network (e.g. airplane mode). There should be **NO DeskFuse alert, failure page, "reconnecting" notification or repeated attempts** in that state; just hold the last decoded screen frame and quietly suspend. On default-network return automatically reauthenticate and resume; no approval. When a usable route exists but host is unreachable, keep retrying under the covers, with bounded backoff to avoid battery drain. Do NOT falsely claim continuous audio or trust authentication/protocol errors.
- True roaming Wi-Fi→4G/5G while listening to home PC is a major next goal. DeskFuse must supply its OWN rendezvous/signaling + direct/NAT traversal + fallback relay; no dependency on Tailscale. Old private LAN IP cannot connect over arbitrary cellular Internet.

## Sources: ORIGINAL DESKLINK CODE.zip (user attachment)
- This is the user's real DeskLink predecessor, NOT public `zerlinpi/DeskLink` (unrelated; never cite/use as predecessor).
- Source components examined: `host.py`, `devices.py`, `viewer/viewer.js`, `SPEC-internet-access-and-security.md`. Has UDP LAN discovery, WebSocket screen transport, low-profile owner approval, remembered phones, revoke/disconnect, quiet socket replacement/recovery, retry 1/2/4/8/15s, and a nicer Windows tray concept. **Internet access/NAT/relay is explicitly a PLAN, NOT IMPLEMENTED** in attached code. Borrow product/UX and resilience fragments; retain DeskFuse's stronger existing crypto/enrollment, host, and audio.
- Desired Windows tray, smooth approval/revocation UX later; technically superior reliable transport/audio first.

## What is actually merged into main (PR #26)
- **Good audio:** real signed DeskFuse WaveRT/SysVad Windows speaker endpoint with genuine Windows notification sound, Windows event-driven WASAPI loopback capture (enable with `DESKFUSE_WASAPI_EVENT_SYNC=1`), 10ms/48kHz frames, stable ~200 capture callbacks/2s. Android PCM seam-guard baseline (NOT additional silence-edge experiment). User reports audio very good, latest seek crackle **NOT heard**; marginal crackle sometimes on Windows volume-flyout slider remains low-priority polish.
- **Fixed session teardown:** Android `SpeakerService` & `SpeakerController` immediate `LanTransport.disconnectNow()`, cancel reconnect/socket loops; reduces stale TCP sockets. Last good excerpt ~200 callbacks/2s, txEvict=0, txWriteMaxMs=0 (vs bad episode txEvict=1233 and socket write up to 6405ms); don't overclaim causality.
- **Broker PnP endpoint lifecycle:** releases per-phone instance on disconnect. Old removal timed out 20s while device remained in taskbar; newer broker uses `SetupDiCallClassInstaller(DIF_REMOVE)`, verifies live PnP departure via Config Manager, and warns separately if CoreAudio refresh lags. Built but real-device removal *still needs validation*. Current broker still follows physical TCP/pipe leases, not a unified logical-phone lifetime.
- **Android reconnect UX:** neutral network errors (no raw "Connection refused" to the user); `DefaultNetworkHandoff` listens for Android default network lost/available (no polling); `LanTransport` uses socket generations so stale reader cannot destroy a replacement; speaker-only foreground service and display Activity now suppress errors and suspend indefinitely with no default network; hold previous rendered frame; auto retry on restored network. Normally 4-second quiet UI grace when a network exists; this is UX-only, not lossless stream migration. Both Windows and Android CI passed at source SHA above.
- This remains **unverified on the physical Xiaomi**. Main is forward development, not public-stable release.

## Important known gaps (next tasks)
1. **Test latest main on hardware first**: Xiaomi airplane mode 2min -> no DeskFuse warnings/retry attempts, frozen last screen; switch network back -> auto connects with same prior approval. Speaker-only and monitor modes separately. Watch duplicate PnP endpoints and failure of taskbar speaker removal/reappearance. Test audio quality, real Windows ding and moving volume slider. This flow is NOT yet a true seamless cellular-home handoff.
2. **Broker speaker presence grace / FUSE presence**: briefly losing authenticated TCP must NOT remove/recreate Windows endpoint or change selected Windows audio device, especially if roaming. Unified logical phone presence, temporary grace (network is changing), eventual cleanup on real departure and immediate cleanup upon revocation. Existing separate monitor and speaker socket flows must become one logical phone presence; ordinary Android app should no longer require speaker toggle. Keep authenticated identities, do not leak PnP nodes.
3. **DeskFuse-owned WAN connectivity**: signaling/rendezvous authenticated by durable device keys; direct-first UDP/ICE/hole-punch when feasible; our own TURN/relay fallback with ephemeral scoped credentials, preferably independent low-latency audio channel rather than head-of-line-blocking TCP. Measure latency/jitter/A/V sync over 4G/5G. No router port-forwarding requirement. Deploy/operate actual service; predecessor code has NO WAN implementation to copy.
4. **Trust revocation must kick ACTIVE sessions**, not just prevent future credential use; tray UI and settings for more restrictive trust levels can follow. Do not introduce recurrent approval codes.
5. Audio polish only if reproduced (volume flyout slider minor pop); independent silence-edge experiment (#19) untested, not in main. Preserve best audio regression baseline.
6. `SessionController` network watcher currently belongs to Android `MainActivity`; for truly persistent/headless display presence, move lifetime to a long-lived connection service. Speaker already has a mediaPlayback foreground service. Need code/device tests for rapid handovers/reconnect loops, stale state, network loss while initial pairing, and network availability not proving PC reachability.

## Real PC environment / commands (CMD, not PowerShell shell)
Windows 10 19045; installed signed audio driver v16.1.56.615 `oem128.inf` (DO NOT uninstall/reinstall). PC code directory `D:\Coding\DeskFuse\v0.3`. The prior working Windows agent lives in `out\audio-event-retest` and was 153,088 bytes; an earlier downloaded `audio-event-test` EXE became 0 bytes, so use fresh CI artifact directories. Do not run the old "phone monitor" alongside the agent: both bind TCP 47810 and cause SocketException 10048. Only ONE unelevated agent and ONE elevated `DeskFuse.AudioBroker.exe` at a time.

Source checkout:
```cmd
cd /d "D:\Coding\DeskFuse\v0.3"
git fetch origin
git switch main
git pull --ff-only origin main
git log -1 --oneline
```
No checkout required if only testing CI binaries. Fresh CI bundles:
```cmd
gh run download 39701085238 -R braindamp/deskfuse -n deskfuse-windows-x64 -D "D:\Coding\DeskFuse\v0.3\out\main-20261022-agent"
gh run download 39701085238 -R braindamp/deskfuse -n deskfuse-audio-broker-x64 -D "D:\Coding\DeskFuse\v0.3\out\main-20261022-broker"
gh run download 39701085238 -R braindamp/deskfuse -n deskfuse-android-debug -D "%TEMP%\deskfuse-main-20261022-android"
adb install -r "%TEMP%\deskfuse-main-20261022-android\app-debug.apk"
```
If the Android debug APK is signed by an incompatible CI debug keystore, `adb install -r` fails: then `adb uninstall com.focuswindow.client.debug` and `adb install ...` but this WILL clear secure pairing and require one fresh Windows approval. Do not mistake debug-build re-pairing for a production trust-policy flaw.

Stop old broker and agent cleanly before launching new binaries. Start broker **Administrator CMD**:
```cmd
cd /d "D:\Coding\DeskFuse\v0.3\out\main-20261022-broker"
DeskFuse.AudioBroker.exe
```
Start agent **normal CMD**:
```cmd
cd /d "D:\Coding\DeskFuse\v0.3\out\main-20261022-agent"
set "DESKFUSE_WASAPI_EVENT_SYNC=1"
FocusWindow.Agent.exe --enable-pc-audio --workspace-app "%WINDIR%\System32\notepad.exe"
```
`Media measured: ~32 video fps` is expected from present combined host even if mainly testing audio; speaker-only no-GPU architecture is a future optimization.

For current speaker PnP node count:
```cmd
powershell -NoProfile -Command "Get-PnpDevice -PresentOnly -Class Media | Where-Object { $_.InstanceId -like 'ROOT\DESKFUSEAUDIO\DESKFUSE-SPEAKERS-*' } | Format-Table Status,FriendlyName,InstanceId"
```
Android logs: `adb logcat -v time -s DeskFuseSpeaker:I DeskFuseSession:I DeskFuseTransport:I`.
Check terminal/tray output, no extra endpoints, and no new authentication prompts on normal same-install reconnect.

## Communication and cautions
- User prefers focused engineering, CMD commands, exact artifacts, not large docs each turn. Does NOT want separate experimental branches anymore: build forward on `main`; keep the frozen audio reference branch as a fallback.
- **Do not assert "cellular streaming works"** until own real WAN connectivity is built and tested. **Do not assert "speaker is fused with monitor"** yet; currently separate services/sessions. Report actual field results vs CI honestly.
- No Tailscale as product dependency. No separate Android speaker switch in eventual regular UX; keep it only as optional developer control. No repeated approval codes after first verified Windows enrollment.

# Per-phone Windows speaker lifetime

**Invariant:** an enrolled but disconnected phone is not an available Windows
render output. Enrollment identity survives, the per-phone PnP device does not.

**Related architecture:** [Speakers-only connection](speakers-only-architecture.md)
requires an independent authenticated audio/accessory role, with no
\`--workspace-app\`, virtual monitor, Windows Graphics Capture or NVENC
requirement. This document covers PnP endpoint lifetime, not the complete
Android-to-accessory session UX.

The old elevated audio broker intentionally kept the root-enumerated device
after a phone session ended. This left a clickable Windows speaker that could
not deliver audio. This behavior is independent of the Windows audio
processing-mode issue (DeskFuse category test 2/7, physical MM-1 7/7).

## Broker fix in this branch

- On an authenticated broker `release` request, remove only that phone's
  enrollment-derived PnP instance, and wait until Windows no longer
  enumerates its audio endpoint.
- On a broken or closed broker pipe, remove every device still owned by
  that connection. A generation/lease check prevents a recently
  reconnected phone's newer session from being removed by the old one.
- On broker startup, find leftover DeskFuse per-phone nodes from an
  unexpected shutdown or previous broker versions, and remove them
  **before** accepting new host connections. Only the audio-class
  `ROOT\\DESKFUSEAUDIO\\DESKFUSE-SPEAKERS-<32-hex-peer-id>` devices
  whose hardware ID is `Root\\DeskFuseAudio` qualify. Never touch
  physical speakers, GPU/display drivers or the installed audio package.
- Allow only one instance of the updated broker per interactive session,
  avoiding simultaneous startup cleanup from two updated broker processes.

The device lifetime follows the authenticated connection with the
*speakers capability*. A user pressing Stop Speakers may stop PCM
capture while their connection remains; that does not mean the entire
phone is offline.

## Existing stale device: one-time manual recovery

When the phone is genuinely offline, stop the old host/broker first.
In **administrator** PowerShell, inspect the exact instance path, then
remove **only the stale device** (not the signed driver package):

```powershell
$pnp = "$env:WINDIR\System32\pnputil.exe"
if (!(Test-Path $pnp)) { $pnp = "$env:WINDIR\Sysnative\pnputil.exe" }
$device = 'ROOT\DESKFUSEAUDIO\DESKFUSE-SPEAKERS-F8D6479478A5FDE3C725C797508F38E9'
& $pnp /enum-devices /instanceid $device
# With the phone disconnected and the instance verified:
& $pnp /remove-device $device
```

This is PnP-device removal only: **do not** use
`pnputil /delete-driver`; the candidate's signed driver remains in the
Windows driver store. Reconnecting the authorized phone and
re-provisioning speakers recreates the correct per-phone instance.

## Test acceptance

1. Broker launched while phone offline: DeskFuse phone speaker absent in
   Windows output selector (including across a Windows reboot).
2. Authorized phone connects and requests speakers: exactly one DeskFuse
   render endpoint exists for that phone.
3. Windows category test on phone: verify driver mode fixes separately.
4. Phone disconnects without a clean audio stop (close app, drop LAN): the
   corresponding endpoint disappears after host detects departure.
5. Phone reconnects immediately after drop: stale release cannot remove
   new device; audio endpoint ID is re-resolved.
6. Phone disconnects during Windows media playback: Windows falls back
   to another available output or shows none, rather than silently
   continuing to render into an offline phone.
7. Broker process terminated unexpectedly and restarted with no phone
   connected: startup reconciliation removes any leftover DeskFuse
   speaker. Physical devices and installed driver package stay intact.
8. Two enrolled phones: disconnecting one removes only its device.

## Build / deployment

The Windows CI job compiles the elevated broker, but CI does not
create/remove real devices. A local interactive PnP acceptance test is
still mandatory. To build the broker, use:

```powershell
dotnet publish windows/audio/DeskFuse.AudioBroker/DeskFuse.AudioBroker.csproj -c Release -r win-x64 --self-contained true -o out/broker-lifecycle-test
```

The broker must run elevated; the existing Windows host remains
unelevated. Stop old broker instances before starting the new binary.
The kernel audio driver, phone app and streaming protocol do not need
to be rebuilt for this lifecycle fix.

# DeskFuse Audio — the phone's Windows playback endpoint

One installed driver package. One render endpoint per enrolled phone.

The Windows sound list must show the phone as a real playback device, so the
PC's audio can be sent to it. Windows only enumerates playback endpoints that
come from an audio driver: there is no user-mode way to register a render
device. This package is that driver.

## What it is

A KMDF WaveRT miniport, derived from the Microsoft **SysVad** sample
(`audio/sysvad`, upstream revision `4d4618400525f9b2ce66fa2e267f58a55043be40`,
Microsoft Public License — see `LICENSE.upstream`).

The upstream sample is a *virtual* audio device: its "hardware" is a small
register block, not a bus. The render path is pure software — PortCls
allocates the buffer, the Windows audio engine writes the application's real
mixed samples into it, and the stream tracks position against a clock. That is
exactly what a phone endpoint needs, and it is why no sound card is required.

## What is different from upstream

| Area | Upstream | DeskFuse Audio |
| --- | --- | --- |
| Endpoints | speaker, headphone, HDMI, S/PDIF, 4 microphone arrays, Bluetooth and USB sideband | one speaker render endpoint |
| Capture | 4 microphone endpoints | none |
| Audio modules | the sample's APO demonstration effect | none |
| Sample data files | `savedata` support | compiled in, writing disabled |
| Hardware ids | `Root\sysvad_ComponentizedAudioSample` | `Root\DeskFuseAudio` and `DeskFuse\AudioEndpoint` |
| Service | `sysvad_componentizedaudiosample` | `DeskFuseAudio` |
| Product id | SysVad's | DeskFuse's own GUID |
| INF | componentized `.inx`, Windows 11 21H2 (22621) and later | classic KMDF INF, Windows 10 19045 and later |

The endpoint keeps the upstream **speaker topology**: a wave filter with a
software audio engine node and a software loopback pin, bridged to a two pin
topology filter. The samples that reach the bridge are the engine's real
output; the driver never synthesises audio. The tone generator in the
upstream sample is only reachable from the capture path, which this driver
does not have.

The INF deliberately sets **no** `FriendlyName` on the filter interfaces: the
name Windows shows in brackets comes from the device instance, so that each
phone's endpoint can carry its own name.

## Build

Requirements: Visual Studio 2022 with the Windows Driver Kit
(`10.0.26100.0`), KMDF 1.27 (present on the supported Windows 10 19045
baseline, whose runtime is 1.29).

```powershell
msbuild DeskFuseAudio.vcxproj /p:Configuration=Release /p:Platform=x64
```

Output: `x64\Release\DeskFuseAudio\DeskFuseAudio.sys`, `.inf` and `.cat`.

## Install (development machine)

The driver is signed with a WDK test certificate, so that certificate has to
be trusted by the machine and the package installed:

```powershell
certutil -addstore Root x64\Release\DeskFuseAudio.cer
pnputil /add-driver x64\Release\DeskFuseAudio\DeskFuseAudio.inf /install
```

Production distribution replaces the test certificate with a WHQL/attestation
signature through the Windows Hardware Dev Center; that is a process and cost
decision, not a code one.

## Per-phone device instances

The package is installed **once**. The endpoint of a phone is a *device
instance* of it, created by `DeskFuse.AudioBroker` when the phone asks for the
speakers capability, and removed when that phone's connection ends.

* the instance id is derived from the authenticated enrollment
  (`DevicePeerRegistry.Identities(deviceId).Speakers`), never from an address
  or a random value;
* the instance is a real PnP device node, so Windows gives it its own
  driver load, its own PortCls adapter and its own endpoint;
* removing the node removes the endpoint: a phone that disconnects takes its
  playback device with it.

## The audio path

1. The user selects the phone's endpoint as the Windows output.
2. Windows renders the application into it.
3. The host samples **that endpoint** (WASAPI loopback, explicit endpoint id)
   and sends the samples to the phone over the existing encrypted lane.
4. The phone plays them on its own speaker, and mirrors the Windows volume
   control.

The PC's default output is never captured on this route.

## Known gaps

* The endpoint name in the Windows sound list is currently the device
  description ("DeskFuse Phone Speakers"). Making it the phone's own name
  requires the name to be written to the endpoint's interface key
  (`DeviceClasses\...\FriendlyName`) once the device has started.
* The broker's device-instance creation is implemented against
  `SetupDiCreateDeviceInfo`; the registration step still needs the exact
  class-installer contract on this Windows build.

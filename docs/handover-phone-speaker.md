# Handover: DeskFuse phone speaker route

State at the end of this session, what is proven, what is not, and what
must not be repeated. Written for whoever picks this up next.

## The goal

The phone appears in the Windows sound list as its own playback device, the
user selects it, and the PC's sound follows to the phone. The Windows volume
control is the phone's volume control.

## What is proven, on this machine

| Thing | Evidence |
| --- | --- |
| The driver builds, signs, catalogues, installs and loads | `oem127.inf`; `pnputil /enum-devices /class MEDIA` shows the DeskFuse device `Status: Started` |
| The endpoint is a **real render endpoint** in the Windows sound list | `FocusWindow.Agent.exe speakers devices` lists `Speakers (DeskFuse Xiaomi 24117RN76E)` |
| It **accepts real application audio** | the capture returns the application's samples (the earlier "3 kHz tone" was SysVad's hardware loopback, now removed) |
| The phone's **speaker session reaches the host** as an authenticated accessory peer | host log `Authenticated accessory peer joined: a9e4c4f7-…` |
| The host creates **one device instance per phone**, with a stable path | `ROOT\DESKFUSEAUDIO\DESKFUSE-SPEAKERS-F8D6479478A5FDE3C725C797508F38E9` (the enrollment hash) |
| The phone **starts its audio output** when the PC says the capability is live | phone log `bridge state: requested=[speakers] active=[speakers]`, then `starting the phone audio output` |
| The Windows volume **moves the phone's own media slider** | the phone's `STREAM_MUSIC` index followed the PC's level (8 → 10) |
| The transport carries the audio with **nothing dropped** | host log `phone audio: 1014 packets sent, 0 dropped` |

## What is not proven yet

* **Sound actually leaving the phone.** The protocol, the endpoint, the
  capture and the transport are all in place and the phone starts its
  output, but the last live run ended before the sound was confirmed on the
  phone. The next step is exactly one action: turn the phone speaker on with
  something playing on the PC, and listen.
* **No pops.** The host now releases the captured audio evenly, so the
  phone's buffer cannot empty between bursts. The owner heard this as
  "much better"; it has not been listened to since.
* **Audio/video alignment.** Designed (`docs/av-sync-design.md`), not built.
  The measurement scaffolding is in place: a shared host clock
  (`FocusWindow.Core/Media/HostClock.cs`), an `audio_clock` control message,
  and `media/SyncMeasurement.kt` on the phone.
* **Android → PC over Bluetooth.** Still blocked on this PC having no
  Bluetooth radio. The receiver code exists
  (`windows/audio/DeskFuse.Audio/SpeakerCommands.cs`).

## The two mistakes that cost the most, and their guards

Both are in `ERRORS.md`; they are the first two things to read.

1. **The SetupAPI argument orders and constants.** `DIF_REGISTERDEVICE` is
   `0x19` (25), not 24. `SetupDiCallClassInstaller` takes the install
   function **first**; `SetupDiSetDeviceRegistryProperty` takes the
   `DeviceInfoData` **second**; `SP_DEVINFO_DATA` is 32 bytes with the class
   GUID stored by value; `out` zeroes `cbSize`. The device-info set must be
   **class-less** for a caller-chosen instance id, and the device name must
   be the **full instance path**. Every one of these produced a plausible
   but wrong error code, and the whole evening went into reading the SDK
   headers instead of trusting them.

2. **Destroying the device Windows is playing through.** Every speaker
   session created a device node and destroyed it on disconnect, so the
   endpoint flickered into the sound list and vanished, and the shell's sound
   flyout could be left without a current device. The endpoint now belongs to
   the **enrollment**: created once, reused on reconnect, removed when the
   phone is unenrolled. The host says plainly when the endpoint goes away.

## The state of the machine

* Host: `out\acceptance\speakers\windows-final\FocusWindow.Agent.exe`, run as
  the **interactive user** (never elevated: the host refuses to run
  elevated, by design), launched through the scheduled task
  `DeskFuseFinalHost`. It must be restarted as that user, not as an
  administrator.
* Broker: `out\acceptance\speakers\broker-real\DeskFuse.AudioBroker.exe`,
  the only elevated process. It creates the phone's device instance and
  names it.
* Driver: installed as `oem127.inf`; the package is built under
  `windows\driver\DeskFuseAudio` and the built package is under
  `out\acceptance\speakers\driver-real\`.
* Phone app: installed from `out\acceptance\speakers\app-audio-volume.apk`,
  signed with codexlab's debug key so the enrollment survives. The phone
  stores the enrollment; do not uninstall the app.
* The USB DAC "MM-1" shows as `Disconnected` (physically unplugged). It is
  not broken.

## Rules for the next person

1. **One UAC prompt per change, and never during the owner's work.** Every
   host restart interrupts their session; the workspace app is not closed
   cleanly, so each restart leaves another Notepad behind.
2. **Do not change the Windows audio setup for the user.** The product asks
   them to select the phone's endpoint; it does not switch the default
   output itself.
3. **The user's words win over our jargon.** "Preparing the display" is not
   something a person can act on. `TODO-user-language.md` lists every string
   that leaks implementation detail, and the host now sends a plain
   sentence plus a separate technical detail.
4. **Measure, do not assume.** The "real audio" proof was wrong once
   already: SysVad's hardware loopback generates a tone, and a diagnostic
   that only counted bytes called it the application's sound.
5. **Record every error in `ERRORS.md` with its guard.** That file is the
   only reason this session produced more than a pile of fixes.

## If you want the sound working today

1. The phone speaker service must be running (the in-session toggle in the
   DeskFuse app starts it).
2. The PC's Windows output must be `Speakers (DeskFuse Xiaomi 24117RN76E)`.
3. Play something on the PC and listen on the phone.
4. If there is no sound, read the host log for `phone audio: N packets sent`
   (the transport), the phone's log for `audio path: packets=… partial=…`
   (the write path), and the broker's log for the endpoint's life.

## TODO (not now, but must not be lost)

* **A regression test for the control-message contract.** A control message
  added on the PC must not be able to end the phone's session: the phone has
  to skip what it does not understand (it does now), and there should be a
  test that sends an unknown message and asserts the session survives, plus
  one per message type. This cost an hour of a broken session today.
* **A test for the sound itself**: a signal of a known frequency played
  into the phone's endpoint must come back on the phone with that
  frequency, and be silent before and after. Never accept "there are bytes".
* **The audio/video alignment**, per `docs/av-sync-design.md`.
* **The Windows volume slider should give the Windows ding** on the phone.
  The PC already plays it through the endpoint, so this is a matter of the
  phone not fighting it with its own tone (the phone-side tone was removed).
* **The workspace app outlives the host.** Every host restart leaves
  another Notepad behind, because the host never kills apps with unsaved
  documents. The next host should adopt the instance it launched before
  rather than starting a new one.

# Experimental single-mode WaveRT renderer (not installed)

Status: **source-only candidate**. This branch does not modify a running
Windows machine, sign or install the driver, change Android, or emit any sound.
It deliberately remains separate from the read-only diagnostic PR #12.

## Reproducible failure and physical endpoint control

DeskFuse phone endpoint, same `IAudioClient2` category probe:

| Category | DeskFuse audible / loopback | MM-1 audible / loopback |
| --- | --- | --- |
| Other | yes, 13/13 signal/nonzero packets | yes, 13/13 |
| SoundEffects | no, 0/0 (peak 0) | yes, 13/13 |
| Alerts | no, 0/0 (peak 0) | yes, 13/13 |
| Media | no, 0/0 (peak 0) | yes, 12/12 |
| Movie | no, 0/0 (peak 0) | yes, 12/12 |
| GameEffects | yes, 13/13 | yes, 13/13 |
| RawOther | no, 0/0 (peak 0) | yes, 13/13 |

All seven `IAudioClient2` calls returned success on **both** endpoints.
The user heard exactly two tones on DeskFuse and seven tones on MM-1.
The phone succeeded at max peak ~0.0377075 (MM-1 at 0.08), consistent
with nonzero Windows volume attenuation. The all-zero failed categories
cannot be explained by the 80/32768 signal threshold. The small taskbar
flyout still produced no ding even with other video audio actively
streaming through DeskFuse.

This establishes a **category-dependent endpoint defect**. It does not
by itself prove that audio is reaching an uncaptured driver pin, nor
that the Windows taskbar uses a particular processing mode.

Microsoft source documentation:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-signal-processing-modes
- https://learn.microsoft.com/en-us/previous-versions/windows/hardware/cert-program/deviceaudio-requirements
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-audiosignalprocessing-modes

## Hypothesis under test

Original SysVad-derived render filter advertises six modes
(RAW, DEFAULT, MEDIA, MOVIE, COMMUNICATIONS, NOTIFICATION)
and supports six concurrent system-pin instances, but exposes
neither a kernel SUM mixing node nor a functional hardware loopback.
DeskFuse forwards only the Windows audio-engine software loopback,
which may not include separately routed non-DEFAULT streams.

Microsoft's Device.Audio mixing requirements describe a
`KSNODETYPE_SUM` node and a functional loopback pin for a driver that
supports concurrent mode mixing. Microsoft's mode documentation
specifies a DEFAULT/RAW minimum with optional additional modes, and
fallback when a mode isn't available.

## Exactly what this experiment changes

1. `speakerwavtable.h`: advertise only DEFAULT at 48 kHz on the
   WaveRT system render pin. The wave formats, pin topology, and
   **six allowed input-pin instances remain unchanged** deliberately;
   this isolates the supported processing-mode list.
2. `minipairs.h`: remove the now-obsolete MOVIE packet constraint
   and update the constraint count from two to one. Preserve the
   existing DEFAULT minimum interval and frame size.
3. `DeskFuseAudio.inf`: bump `DriverVer` to 2026-10-21,1.0.0.1
   to distinguish the candidate from the currently installed package.

Everything else, including wave buffer code, host loopback, encrypted
transport, Android playback, Windows/phone volume mirroring and
the driver's no-synthetic-tone policy, is unchanged.

**It has not been built with the WDK, signed, installed or empirically
validated yet. Do not treat it as a fix until those steps and the tests
below succeed.** The normal GitHub .NET checks do not compile this kernel
driver, so their success cannot validate its correctness.

## Next validation gates, in order

1. **Source review / WDK build without installation.** From the
   experimental branch using a Visual Studio Developer PowerShell
   with WDK 10.0.26100.0 and the appropriate Visual C++ toolset:

   ```powershell
   msbuild windows/driver/DeskFuseAudio/DeskFuseAudio.vcxproj /p:Configuration=Release /p:Platform=x64
   ```

   Capture compiler warnings/errors and ensure the INF/catalog are
   generated correctly. *Building is not the same as installing.*
2. **Versioned test signing and rollback preparation.** Retain the
   original driver package and signing materials; inspect
   `pnputil /enum-drivers` and the currently selected endpoint IDs.
   Use an isolated Windows test machine/VM first if possible. Prepare
   a tested restoration procedure. No install should occur without
   explicit user approval.
3. **Controlled real endpoint validation.** On the candidate test
   endpoint, run the exact same seven-category diagnostic. All seven
   must be audible **and** produce nonzero loopback if this candidate is
   to be accepted; especially verify explicit RAW behavior because the
   driver no longer advertises RAW. A RAW failure could be a meaningful
   compatibility regression even though DEFAULT meets the documented
   minimum mode contract.
4. **Specific flyout acceptance.** With phone as default eConsole and
   eMultimedia, click/release the small taskbar flyout at multiple
   comfortable volumes. Confirm the **real Windows-generated** ding is
   audible on the phone and detectable on loopback. Also verify the
   Volume Mixer, explicit System Sounds, playing video, and multiple
   simultaneous application streams.
5. **Product regressions.** Check bidirectional Windows/phone volume,
   encrypted streaming and reconnect, registered PnP instance and
   friendly name persistence, and no extra synthetic sound or video
   sync changes. Test re-enumeration/rollback if the endpoint ID changes.

If category filtering remains broken, revert the candidate and
instrument `CMiniportWaveRT::NewStream` and
`CMiniportWaveRTStream::SetState` (mode GUID, pin number, stream state,
per-stream PCM peak and NTSTATUS) rather than guessing about INF
event-driven, APO or jack properties.

### Safety

No `pnputil` install/remove command is automated in this branch.
No Windows audio services, sound schemes or global registry values are
modified. The active DeskFuse session and phone remain untouched.

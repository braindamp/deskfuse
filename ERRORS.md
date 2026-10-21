# Error log

Every error that cost time on the way to "the phone is a real Windows
playback device", what actually caused it, and what now stands in the way
of it happening again.

The point is not the history. It is that each entry has a **guard**: a
check, an assertion, a test or a rule that turns the mistake into something
that is caught immediately rather than diagnosed over hours.

Status: `guard` = in place, `wanted` = the check does not exist yet.

---

## Driver and device installation

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 1 | The driver never loaded: Code 39, `0xC000003A` | The INF's `ServiceBinary` was `%12%\System32\drivers\DeskFuseAudio.sys`. `%12%` **is** the driver directory, so the path doubled and the file was not found. | guard: the service's `ImagePath` is checked after install (the path must be exactly `%SystemRoot%\System32\drivers\<file>`). The INF now says `%12%\DeskFuseAudio.sys`. |
| 2 | The second phone device instance failed with Code 10 | The vendored SysVad `NewAdapterCommon` allowed **one adapter only**, so a second instance of the same driver could not start. | guard: the singleton is gone; each device instance owns its adapter. Rule: never reintroduce a process-global or driver-global endpoint owner. |
| 3 | The endpoint "played" a steady 3 kHz tone regardless of the application | SysVad's speaker endpoint advertises a **hardware loopback pin** whose implementation *generates* a sine. Our diagnostic read non-zero bytes and called it the application's audio. | guard: `LoopbackSignalProbe` requires the captured signal's frequency content to match the supplied signal, and requires silence before and after. The endpoint now exposes an ordinary two-pin shared-mode render filter, so the synthetic path is unreachable. **Never accept "there are bytes" as evidence of audio.** |
| 4 | The endpoint was registered but the driver produced a silent endpoint | An endpoint with a hardware audio-engine node and an offload pin needs a matching topology; a half-configured pair registers and then does nothing. | guard: the render filter is now the plain two-pin form (system sink -> bridge), with the shared mixer and the genuine WASAPI software loopback. |

## SetupAPI: creating the phone's device instance

These five cost the most time, because every one of them looked like a
"not found" rather than a mistake.

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 5 | `SetupDiCallClassInstaller` failed with `ERROR_INVALID_HANDLE` (6) | The install function is the **first** argument, the device info set the second. They were swapped. | guard: the declarations are quoted from the SDK in `challenge.md`; the call site passes `(DifRegisterDevice, infoSet, ref info)`. |
| 6 | The same call failed with `0xE0000201` | `SetupDiSetDeviceRegistryProperty` takes the `DeviceInfoData` **second**, not last. The property was written to the wrong object and silently lost. | guard: five-argument form, with the SDK declaration in `challenge.md`. |
| 7 | Registration failed with `0xE000020E` (`ERROR_DI_DO_DEFAULT`) | `DIF_REGISTERDEVICE` is **0x19** (25). The constant was 24 (`0x18`), so the class installer was asked to do an action it did not know, and fell back to the default. | guard: the constant is named `DifRegisterDevice = 0x19` and the SDK value is quoted in `challenge.md`. **Any numeric constant taken from memory is a defect until it is checked against the header.** |
| 8 | `SetupDiCreateDeviceInfo` failed with `ERROR_INVALID_PARAMETER` (1004) | Two mistakes at once: the `DWORD CreationFlags` argument was missing, so every later argument was shifted, and the output struct was declared `out`, which zeroes `cbSize` before the call. | guard: `SP_DEVINFO_DATA` is passed by `ref` with `cbSize` set by hand; the SDK declaration is in `challenge.md`. |
| 9 | The device was created but never appeared in the device tree | The device info set must be **class-less** (`SetupDiCreateDeviceInfoList(NULL, NULL)`) for a caller-chosen instance id, and the device name must be the **full instance path** with `DICD_GENERATE_ID` omitted. | guard: the broker's `Matrix()` command prints one line per creation form; the working form is the one in `DeviceInstance.Create`. |

## Driver store and signing

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 10 | The catalog failed with a future-date error | The catalog's timestamp is compared with the local time, and a midnight local/UTC difference makes the catalog look signed in the future. | guard: the build uses `/p:Inf2CatUseLocalTime=true`; the existing Arne signing certificate is reused instead of creating a new one. |
| 11 | A driver would not install | The WDK test certificate has to be trusted by the machine, and the *same* certificate has to sign the package that is installed. | guard: the certificate thumbprint is recorded in the handover; `certutil -addstore Root` is a deliberate, announced step. Never change the machine's trust or Secure Boot state without saying so. |

## Endpoints, leases and lifetimes

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 12 | "The phone speaker endpoint appeared but Windows would not open it" | The broker had cached the endpoint's **GUID** from an earlier provisioning. Windows gives an endpoint a new GUID when the device is re-enumerated (a driver reinstall does that), so the cached id pointed at nothing. | guard: the broker re-resolves the endpoint every time, and matches on the **device instance path** (the endpoint's property store carries it), never on a cached GUID. Rule: the stable identity is the PnP path, not the endpoint GUID. |
| 13 | The endpoint was captured, then the capture died with the device | The removal path returned before Windows had actually removed the endpoint, so a replacement matched the dying one. | guard: `RemoveAsync` holds the broker's device gate until the old endpoint is really gone (bounded at 20 s). |
| 14 | The Windows volume slider stopped having any effect | Disposing the `MMDevice` invalidated the `AudioEndpointVolume` wrapper that was cached beside it. | guard: the device is retained for the endpoint's lifetime; the volume wrapper is unsubscribed and the device disposed exactly once. |
| 15 | The first volume update was lost | The volume arrives before the `AudioTrack` exists, and it was applied to nothing. | guard: the phone caches the requested level and applies it before playback starts; the cache survives a stop and restart. |

## Audio quality

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 16 | The phone's sound popped | A **non-blocking** `AudioTrack.write` can be partial, and the remainder of the packet was thrown away. One click per occurrence. | guard: the remainder is carried into the next packet; only genuinely stale audio is dropped. The phone reports `packets / partial / carry` so the path can be seen. |
| 17 | The phone's sound was quiet and the volume slider did nothing | The level was applied to the `AudioTrack`'s gain, not to the phone's media stream, and the phone's own slider stayed where it was. | guard: the level is applied to the media stream, and the phone reports its own slider back to the PC, so the two are one control. |
| 18 | A few pops remained with nothing dropped | A **hole in the captured stream** is not a dropped packet: the engine renders in bursts, and a gap in the arrival is a dropout. | guard: the capture now reports its packet count and its longest hole, so a dropout can be told apart from a dropped packet. wanted: a paced stream or a jitter buffer, so a hole in the capture cannot reach the phone. |

## Process and session hygiene

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 19 | The host refused to start | By design: the LAN host must not run elevated. | guard: the broker is the only elevated process; the host is launched as the interactive user. |
| 20 | "FocusIdd already has a display that this host does not own" | The previous host was killed while it owned the virtual display, so the driver still had it. | wanted: the host releases its display on shutdown, and the driver reclaims a display whose owner is gone. **The message is also wrong** and belongs to the user-language work. |
| 21 | Removing the stale display device took the virtual display *driver* with it | `ROOT\DISPLAY\0000` was the driver's own device, not a leftover monitor. | guard: the driver device is restored with `devcon install FocusIdd.inf Root\FocusIdd`. Rule: never remove a display *device* to clear a stale *monitor*. |
| 22 | The phone sat on "Preparing the display" after the host had already given up | The phone showed a stage it was waiting for, and nothing reconciled it with the host's state. | guard: the phone's preparing state now has a timeout and a plain message, and the stage code is never shown as a label. |

| 24 | The Kotlin compiler rejected a plain `for (var i = 0; i < n; i++)` with "Method 'iterator()' is ambiguous" | Kotlin 2.0.21's K2 parser mis-parses the C-style `for` loop in this project. | guard: use `repeat(n) { i -> ... }` in the Android sources. |
| 25 | A text replacement that "should" have matched silently did nothing | The Android sources are CRLF and the edit patterns were written with `
`, so the replacement never matched and the old code stayed. | guard: edits to the Android sources are written with `
` (or read/write with `newline=''` and patterns that match). Always confirm the file changed. |

## User-facing language

| # | Symptom | Root cause | Guard |
| --- | --- | --- | --- |
| 23 | The phone showed "FocusIdd already has a display that this host does not own", "Creating the phone-shaped monitor", driver and Win32 text | The host sent one string that was both a log line and a user-facing sentence. | guard: the stage now carries a plain sentence **and** a separate technical detail; the phone shows the sentence, the diagnostics show the detail, the log keeps both. See `TODO-user-language.md`. |

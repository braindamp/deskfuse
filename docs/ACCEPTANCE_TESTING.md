# DeskFuse acceptance: get the first real session working

Start here as the PC/phone owner. Use the unified app, not the old POC.
Allow roughly 15–20 minutes for the first session and cleanup; audio and stress
tests come afterwards. If a step fails, keep its error and fix that step first.

## 1. Prepare the PC and phone

- Windows with the existing compatible FocusIdd driver and a supported NVIDIA
  NVENC GPU. This guide does not install/update drivers or change their ACLs.
- An Android 11/API 30 or newer phone on the same trusted LAN. Keep it unlocked and DeskFuse in
  the foreground for the first test. Avoid guest-network/client isolation.
- Choose a real Windows app that supports a **new independent process/window**,
  PMv2 DPI, and resizing to the phone's exact pixels. Use a disposable document.
  Know its absolute `.exe` path and any app-specific new-instance arguments.
  Modern Notepad/browser launchers may hand off to another process and be refused;
  that is a source-compatibility result, not proof that the decoder failed.
- For the first session, prefer an app needing **no launch arguments**. The
  current CLI parser rejects values starting with `--`; a source requiring
  `--workspace-args "--new-window"` needs that parser defect fixed first. Tell the
  coding model rather than repeatedly retrying the launch. Other argument forms
  are also subject to the app's own new-instance support.
- Note the phone model/Android version, PC Windows version, GPU model, app/version,
  phone display scaling setting, refresh setting and network type.

Open a **normal, non-administrator PowerShell**. Copy this setup block:

```powershell
Set-Location -LiteralPath 'D:\Coding\DeskFuse\v0.3'
$agent = 'D:\Coding\DeskFuse\v0.3\out\unified-windows\FocusWindow.Agent.exe'
$apk = 'D:\Coding\DeskFuse\v0.3\android\app\build\outputs\apk\debug\app-debug.apk'
$evidence = Join-Path (Get-Location) ('out\acceptance\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
git rev-parse HEAD | Set-Content (Join-Path $evidence 'commit.txt')
Get-FileHash $agent, (Join-Path (Split-Path $agent) 'FocusWindow.Gpu.dll'), $apk |
    Format-List | Out-File (Join-Path $evidence 'build-hashes.txt')
& $agent --version
& $agent display-paths 2>&1 | Tee-Object (Join-Path $evidence 'display-paths-before.txt')
```

If an artifact is missing, rebuild using `UNIFIED_RUNTIME.md`, or download the
Windows and Android artifacts from the [successful main build](https://github.com/braindamp/deskfuse/actions/runs/39369997315).
Keep all Windows publish files together. Do not copy just the EXE.

Before testing, note/screenshot Windows Display Settings: physical monitor
arrangement, resolution, scale and primary display. `display-paths` records
identities and sizes; it does **not** independently record every position/DPI.

Close other DeskFuse/POC sessions normally. With no other display owner active,
run **one** status check:

```powershell
& $agent driver-status 2>&1 | Tee-Object (Join-Path $evidence 'driver-before.txt')
```

Proceed only if the protocol matches, `monitor arrived` is `0`, and there is no
unresolved driver error. Status IOCTLs can refresh the driver's watchdog: do not
poll status continuously, particularly during a crash-cleanup test.

| Result | Next action |
| --- | --- |
| Access denied / missing driver / protocol mismatch | Save the error. Give it to the coding model before live streaming. Normal-user driver access is an unfinished integration boundary on installations that deny it. |
| `monitor arrived 1` before your test | Find/close its owning session, or let its watchdog expire. Do not remove an unknown monitor. |
| Physical arrangement already unexpected | Resolve the baseline before starting a test. |

Do not work around a failure by running the network host as administrator,
weakening driver permissions, reinstalling the driver, or changing global DPI.

## 2. Install the Android build

Copy `app-debug.apk` to the phone using your normal file transfer method. Open
it in Android's file manager and approve installation for that source if asked.
The debug package is `com.focuswindow.client.debug`. Open it after installation.
ADB is not required. If Android reports an incompatible signature/update, record
that first; do not silently uninstall a previous app and erase its pairing data.

## 3. Launch the real app session

In the same normal PowerShell, enter your selected source:

```powershell
$workspaceApp = Read-Host 'Absolute path of the independent PMv2 app EXE'
$workspaceArgs = Read-Host 'New-instance arguments, or Enter for none'
if (-not [IO.Path]::IsPathRooted($workspaceApp) -or
    -not (Test-Path -LiteralPath $workspaceApp -PathType Leaf) -or
    [IO.Path]::GetExtension($workspaceApp) -ine '.exe') {
    throw 'Choose an existing absolute .exe path.'
}
$hostOptions = @('--workspace-app', $workspaceApp, '--fps', '60',
    '--bitrate', '20000', '--log', (Join-Path $evidence 'host.log'))
if ($workspaceArgs) { $hostOptions += @('--workspace-args', $workspaceArgs) }
& $agent @hostOptions
```

This starts the LAN host. The phone session will create its virtual display and
launch/place the new app window. Leave this console open. For these operations,
run the block only when ready for the temporary virtual display to be attached.

On Android, open PC pairing/discovery and select your PC from **Your Windows PCs**.
If discovery fails, use **PC IP address or hostname** in that dialog. The normal
host TCP port is 47810. Check LAN reachability and Windows firewall prompts for
the trusted private network; do not disable the firewall globally.

On first enrollment, compare the displayed code on both devices, then click
**Allow this phone** in the Windows approval dialog within 60 seconds. This
approval includes desktop control and Windows-file access. Only approve your
own matching phone. `--auto-approve` is not needed and does not bypass enrollment.

**First checkpoint:** actual content from the selected app appears on the phone.
A blank test form, desktop mirror, connection spinner or target metadata alone
does not pass. If it fails, capture the phone error and the host log from the
first failed stage. Avoid repeated reconnect attempts before recording it.

## 4. Check picture, input and idle behavior

Use **STATS** on the phone. Save a screenshot and write down the numbers.

| Test | Pass / evidence to collect |
| --- | --- |
| Pixels and DPI | `target` equals `panel` in the negotiated orientation; presentation `scale` is 1.00×; reported density matches expected density. No clipped/blurred forced fit being described as native. Retain the host density evidence too. |
| Real image | Small text, thin lines and normal app content render clearly, with no persistent black screen, corruption or stale image. Codec should identify H.264/YUV420; record decoder name and hw/sw status. |
| Color smoke check | Inspect dark/light gray steps and familiar colors in the app. Record washed-out/crushed/clipped colors. BT.709 full-range metadata alone is not color acceptance; visual inspection is only a smoke check, not a calibrated fidelity measurement. |
| Touch | Tap several controls near each corner and the center, scroll, and type disposable text. Actions must hit the visible target. Keyboard input requires the source to have foreground focus. |
| Focus safety | Switch focus to a different existing PC app. Phone typing must not type into that unrelated app. Returning to the source should restore intended input. Do not move existing physical windows for this test. |
| Final repaint | Scroll/type, stop abruptly, and compare the last visible state. The last update must arrive even when no further repaint occurs. |
| Idle | Leave the app genuinely static for 60 seconds, then interact again. Connection must stay alive and resume promptly; no periodic reconnect or large recovery burst loop. Record the actual idle bandwidth/fps. |
| Physical PC | Original monitor arrangement, DPI/scale, primary display and existing app windows stay unchanged. The newly launched DeskFuse-owned app is allowed to occupy its virtual display. |

The host prints `Media measured:` every five seconds: delivered video fps,
**total encrypted outbound kbit/s** and queued/in-flight video records. This is
not video-only bitrate. With audio/files off, record at least three intervals
while scrolling and three while idle. Expect load/bandwidth to fall substantially
on idle; report the numbers rather than inventing a universal Mbps threshold.
The `cadence cap` is not measured fps. Phone decode/glass latency fields are
diagnostic estimates; use an external timed/high-speed recording for a genuine
end-to-end latency claim.

## 5. Disconnect, verify cleanup, then reconnect

1. Use **End** on the phone. Let the host finish session cleanup.
2. Check that the virtual monitor departs and physical Display Settings match
   your baseline. The new app/document may reappear on the PC; disconnect must
   not kill it or discard unsaved work.
3. Reconnect to the same still-running host. The same owned app/document should
   be reused and its current picture should decode promptly.
4. Disconnect again, then press **Ctrl+C** in the host console and wait for exit.
5. In that console, collect the final state:

```powershell
& $agent driver-status 2>&1 | Tee-Object (Join-Path $evidence 'driver-after.txt')
& $agent display-paths 2>&1 | Tee-Object (Join-Path $evidence 'display-paths-after.txt')
$density = Join-Path $env:LOCALAPPDATA 'FocusWindow\last-session-evidence.md'
if (Test-Path -LiteralPath $density) {
    Copy-Item -LiteralPath $density -Destination (Join-Path $evidence 'last-session-evidence.md')
}
```

Pass: `monitor arrived 0`, physical state unchanged, no leftover streaming host,
and document intact. Compare physical settings as well as the text snapshots.
A host restart can launch another app; unknown existing windows are not adopted.
Do not close the owned app/document while testing reconnect reuse.

## 6. Expand only after the first session passes

| Scenario | How to test / expected result |
| --- | --- |
| PC sound | Restart the host with `--enable-pc-audio` added to `$hostOptions`. Play disposable PC audio, toggle **PC sound** on Android, check sound and off/on behavior. It can include other PC apps' audio. Reconnect and check the saved preference. |
| Refresh/rotation | Change the phone/session refresh setting if exposed; rotate it. Record whether geometry invalidation/reconnect works and the new target matches the panel. Unsupported modes must fail honestly, never use stale touch coordinates or claim fake native pixels. |
| File/clipboard regression | Use a disposable file and text. Transfer/paste while interacting with the source. Check input/audio responsiveness and that denied permissions stay denied. Record stalls and corruption. |
| Congestion recovery | Introduce ordinary competing LAN traffic, then stop it. Check image recovery without permanent corruption, repeated IDR/reconnect loops, or stale input. Record fps/kbit/s and symptoms. |
| Mic, optional | First use `audio-devices` and `trusted-phones` to identify the explicit cable render endpoint and enrolled device. Use the three documented mic flags in `UNIFIED_RUNTIME.md`/`--help`, then Android's **Phone mic** toggle and OS permission. Listen/record through the cable's matching recording endpoint in another app. Never choose physical speakers as the mic sink. Confirm capture stops when Android leaves foreground. No existing virtual cable means this test is NOT RUN, not PASS. |
| Real surround, optional | Use an actual compatible multichannel Android output and channel-identification content. Stereo phone speakers cannot prove 5.1/7.1. |
| Crash cleanup, last/optional | After clean cleanup has passed, use a disposable session and manually end only `FocusWindow.Agent` in Task Manager. Leave driver polling off for at least 15 seconds, then check status once. Expect the owned virtual monitor to be gone and physical state unchanged. Record any failure; do not install/uninstall drivers to hide it. |

If physical display state changes unexpectedly, stop the test and preserve
evidence before reconnecting. Do not have the model run speculative repair,
registry, global scale or unowned-monitor-removal commands.

## 7. Report back with this small result

Save `result.md` in your `$evidence` folder. Use PASS / FAIL / NOT RUN explicitly.
Keep raw logs and screenshots local: they may contain private labels, addresses,
paths or document content. Do not share pairing secrets or DPAPI/approval files.

```text
Tested commit / build hashes:
Windows / GPU / driver protocol:
Phone model / Android / display scaling / network:
Source app version / EXE / arguments:
Enrollment and real picture: PASS / FAIL / NOT RUN
Target / panel / scale / DPI / decoder:
Touch and focus isolation:
Final repaint / 60-second idle / reconnect:
Scrolling: video fps __ ; total outbound kbit/s __ ; backlog __
Idle:      video fps __ ; total outbound kbit/s __ ; backlog __
Clean disconnect: monitor arrived __ ; physical state unchanged? __
Audio / mic / rotation / congestion / crash: PASS / FAIL / NOT RUN
First failure: exact action, expected result, actual result, error text
Evidence folder and relevant host.log excerpt:
```

For the next coding chat, paste:

> Read `D:\Coding\DeskFuse\v0.3\HANDOVER_ACCEPTANCE_DESKFUSE.md` and
> `docs/ACCEPTANCE_TESTING.md`. Continue on current main. Help me execute the
> first unpassed acceptance checkpoint and fix the first failing component.
> Preserve existing features and optimize runtime load, especially bandwidth.
> Here is my result / first error: …

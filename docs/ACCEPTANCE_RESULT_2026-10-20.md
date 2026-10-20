# First unified application hardware result — 2026-10-20

Source: mainline `1778285e7b96a3f6eb44c5354d5c65775459d352`. Local artifacts were
built from its source changes, then exercised on the actual PC and phone. Raw logs,
screenshots and test reports remain in ignored `out/acceptance/continuation/`.

## Scenario and results

Windows 10 build 19045, supported NVIDIA NVENC, existing FocusIdd protocol 2;
host runs unelevated as the interactive desktop user. Xiaomi 24117RN76E,
Android 16, 1080x2400 panel, Android DPR 2.8125. USB developer connection is
used for diagnostics/install; encrypted session traffic travels over the LAN.
Source: an independent `C:\Windows\System32\notepad.exe` instance.

| Check | Result and evidence |
| --- | --- |
| Existing enrollment | PASS: reconnect authenticates using existing app data |
| Display calibration | PASS: 1800x2400 backing at 120 Hz, 300% / 288 DPI, native PMv2 probe and 300x300 px ruler |
| Actual video | PASS: WGC → GPU ROI → NVENC → AES-GCM → phone hardware MediaCodec; visible Notepad at 1080x2400, presentation scale 1.0 |
| Exact phone density | NOT MET: requested 270 DPI, rendered 288 DPI; layout 6.7% larger; truthfully reported as quantized 1:1 |
| Phone typing | PASS: owner confirmed typing in the live document; direct IME input also observed |
| Normal phone UI | PASS: technical cards moved to STATS; diagnostics and HIDE exercised on the installed update |
| Clean End | PASS: host receives goodbye, restores virtual scale, reports physical displays unchanged; one subsequent driver check reports monitor arrived 0 and error 0 |
| Reconnect reuse | PASS: End, reopen Android, reconnect to the same running host; original owned Notepad process remains and video resumes without another Notepad process |
| Unsaved document retention | PASS: earlier unsaved test document remains open after its session/host ended |
| Android system-bar overlap | OPEN: title is covered. Owner chose future immersive fullscreen with swipe-down system bars; layout work deferred until reviewing old Desklink code |
| Minimize stability / explicit retry | PASS for containment: one stable screen, no reconnect loop, clean monitor departure; Try again restored the same process and video. Normal minimize UX remains unfinished; owner requested no further minimize work now |
| Audio, files, clipboard, rotation, congestion, crash cleanup | NOT RUN in this continuation |

The 6.7% is a layout-size difference. Windows rasterizes at 288 DPI; captured
pixels are presented 1:1, without resampling to imitate 270 DPI. H.264 4:2:0
compression is still lossy; 1:1 geometry is not a claim of lossless video.

## Failures fixed

- A missing host listener explained the initial connect failure after changing
  Windows accounts. No enrollment migration or driver installation was needed.
- WinForms' probe could remain at 96 DPI. A native PMv2 popup created directly
  on the virtual monitor measures actual window DPI and the captured ruler.
- Windows cached modes per EDID serial. Adaptive backing modes now use distinct,
  stable phone + mode + refresh cache keys.
- WGC reported 1114x2417 while DWM's visible frame was 1080x2400. The extra
  17 px left/right/bottom borders are excluded by a measured GPU subresource
  copy. Arbitrary size changes still fail; pixels are never resized to fit.
- Driver removal acknowledgement could precede Windows desktop detachment.
  Cleanup now waits for both before checking the physical display snapshot.
- Minimizing Notepad invalidated capture geometry. Android previously replaced
  the fatal host error with automatic socket-loss retries, repeatedly allocating
  and removing the display. Fatal stages now stop on a named error with manual
  Try again. Explicit retry restores the same owned minimized HWND without
  activating or adopting unrelated windows. The live retest passed, with no
  automatic reconnect between the error and explicit retry.

## Performance observations and remaining work

Five-second host samples during Notepad testing showed roughly 32–45 delivered
video fps, 110–305 kbit/s **total encrypted outbound**, and zero video records
queued/in flight. The configured cap was 60 fps and bitrate budget 20000 kbit/s.
Do not confuse the budget with measured traffic or the configured cap with
delivered fps. A controlled scrolling benchmark and end-to-end latency were
not measured. A mostly static document continued producing frames and around
120–150 kbit/s; eliminating unnecessary work while retaining final repaints
is still a performance task, not a completed idle optimization.

The owner subsequently chose full immersive fullscreen, with Android system
bars hidden and revealed by a downward swipe. The alternative smaller safe-area
workspace is not the current direction. Layout changes are deferred until
reviewing the old Desklink code. Minimize should eventually remove that window
from the Android workspace as a normal action, without an error. The current
stop/manual-retry behavior only contains the previous flicker loop; do not
expand it into a new minimize framework now.

## Software validation and tested artifacts

- Windows publish: Release, win-x64, self-contained, warnings as errors.
- .NET: 184 passed, 10 existing optional/historical skips, zero failures,
  including actual C#/Kotlin encrypted TCP interoperability.
- Android: 21 unit tests passed, including two fatal/control dispatch regression
  tests; debug assembly succeeded.
- Kotlin protocol checks: crypto vectors and 513 authenticated ordered records
  from eight parallel senders passed.
- Native ABI, capture-region and latest-frame pacing contract executable passed.
- Both unified CI jobs passed for the tested source:
  [run 39467372648](https://github.com/braindamp/deskfuse/actions/runs/39467372648).

SHA-256 of the tested files:

```text
out/unified-windows/FocusWindow.Agent.dll
E75E4F957AFE765168CFC94599E0D386454A2D772704DD22D9292CB38AED2A98
out/unified-windows/FocusWindow.Gpu.dll
1D20B0CFFA557B78B82A79D242FF99615F8F39A53EE845DC5DA5B57D0D97FB5F
out/acceptance/continuation/app-debug-signed.apk
90AFF1E7C4176C088469CB1C358380F2A4B559FA5BDCC612F14B5F5BBF147543
```

The installed debug APK was re-signed with the original default debug key,
explicitly authorized by the owner, so updating across Windows accounts kept
the phone's enrollment/data. The ordinary Gradle output uses the current
account's debug key and is not interchangeable for update installation.

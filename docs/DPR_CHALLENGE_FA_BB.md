# DeskFuse 350% DPI hardware challenge — FA + BB

**Status:** source and driver-free tests, not a successful hardware result. This does **not** change the shipped driver, streaming protocol, DPI policy or FocusIdd known-good install.

This lab answers three distinct questions:

1. **FA:** Does the reported `-3` DPI maximum grow with *virtual source width* (a proposed 600/640-DIP minimum), independently of EDID physical size? The predicted columns are hypotheses, not operating-system facts.
2. **FA:** If Windows reports 225% maximum at 1440×3120, does an explicitly opted-in undocumented `DisplayConfigSetDeviceInfo(-4)` request for **350%** return an error, silently clamp, or genuinely apply 336 DPI? The relative parameter is a **scale-table step offset from the recommended value**, never a percentage difference.
3. **BB:** On a larger virtual desktop (2240×3120 or 2240×3360), will Windows actually give a PMv2 window **336 DPI**, an actual **350×350 px** reference square, and a **1440×3120 crop** with exactly one source pixel per output pixel?

All three questions are independent of which AI suggested them. Until measured on a real Windows 10 22H2 console, they are **unproven**.

## Safety contract — read before running

- Use the **already installed, known-good** FocusIdd on a Windows test PC. **No driver install, remove, reinstall, INF/ABI changes, registry writes, or system-wide DPI settings** are part of this experiment.
- No existing virtual display/session may be active: the lab **refuses** to run if FocusIdd already reports an arrived monitor.
- The experiment controls only the **new virtual display identified by the existing topology-delta guard**; never select an output by resolution alone.
- Each temporary mode is removed before the next. Before/after **physical display rectangles, DPI and primary flags** are checked on every step; abort on drift.
- The real driver has a **10-second no-heartbeat monitor teardown**. The runner also kills a hung agent at its timeout. These are not guarantees against a severe Windows graphics failure. This lab is **not** a replacement for the independently guarded driver-install procedure.
- A 350% SET is **not attempted by default**. The extra beyond-reported-max attempt requires *two distinct opt-ins*, including a typed interactive confirmation in the PowerShell runner.
- After **any** attempted `-4` SET, the original virtual-monitor scale is set back and **read back** before the monitor is removed. Restore failures are reported as failures, never hidden.
- A **physical display drift**, failed teardown or virtual DPI restore stops the sweep immediately. Do not continue testing until the machine is verified.
- The lab creates a temporary borderless PMv2 test window on the virtual monitor; it does **not** inject input, stream to a phone, or change existing physical windows.

## Build on your Windows PC (Visual Studio/.NET 8)

From `D:\Coding\DeskFuse\v0.3` (after pulling the updated GitHub `main`):

```powershell
dotnet build .\windows\agent\FocusWindow.Agent\FocusWindow.Agent.csproj -c Release -p:Platform=x64
dotnet test .\windows\agent\FocusWindow.Tests\FocusWindow.Tests.csproj -c Release
```

Choose the resulting **FocusWindow.Agent.exe** under `windows\agent\FocusWindow.Agent\bin\x64\Release\net8.0-windows\` (verify its actual location after the build).

## Plan-only (does not touch the driver or Windows display state)

```powershell
& "<path-to-FocusWindow.Agent.exe>" dpi-challenge
```

Default source modes: **1440×3120, 1600×3120, 1920×3120, 2100×3120, 2240×3120, 2240×3360**, all at 60 Hz, with 170 mm diagonal. The final 2240×3360 mode remains within the driver maximum of 8,294,400 pixels. The **2100**-wide case is useful because the 600-DIP and 640-DIP hypotheses predict different maximums.

## Run 1: observe the caps and raster ruler, without DPI writes

Use an **interactive elevated PowerShell window**, with Windows visible so you can see unexpected display changes:

```powershell
powershell.exe -NoProfile -STA -File .\tools\run-dpr-challenge.ps1 `
  -AgentExe "<path-to-FocusWindow.Agent.exe>" -Run `
  -OutDirectory "D:\Coding\DeskFuse\dpr-challenge-evidence"
```

The command asks you to type `RUN` before it creates anything. This first run observes the display limits, current PMv2 DPI and physical ruler at each width; it **will not try to force 350%**.

Inspect `DPR_CHALLENGE_*.csv` and `DPR_CHALLENGE_*.json`. If maximum remains 225% across widths, the BB width-floor hypothesis is falsified on this machine. If it rises at 2100/2240, examine the prediction columns rather than simply assuming a 640-DIP rule.

## Run 2: attempt 350% within Windows' *reported* range

```powershell
powershell.exe -NoProfile -STA -File .\tools\run-dpr-challenge.ps1 `
  -AgentExe "<path-to-FocusWindow.Agent.exe>" -Run -Try350 `
  -Modes "2240x3120,2240x3360"
```

A `-4` write only happens **if `-3` reports that 350% is permitted**. The test records the raw return code, monitor DPI, PMv2 window DPI, 100-DIP ruler and 1440×3120 crop. An accepted API return **alone never counts as success**.

A **passing BB prototype** needs all of:

- `GetDpiForWindow` of a PMv2 window ≥336 DPI (and monitor effective DPI ≥336).
- Actual 100-DIP green square approximately 350×350 pixels in a **real driver frame**.
- Exactly 1440×3120 physical pixels in the sampled crop, **with no resampling**.
- Physical-monitor before/after snapshot identical and temporary virtual monitor removed.
- Virtual-monitor scale returned to its original value, confirmed by `-3` readback.

The current crop is an **exact CPU memory copy solely for the experiment**, not a production zero-copy D3D11/NVENC crop. Window/popup/work-area handling, phone input and application compatibility need separate engineering tests.

## Run 3: FA's *unsupported* beyond-max write (only if justified)

**Not a default test.** Run separately after a clean read-only sweep:

```powershell
powershell.exe -NoProfile -STA -File .\tools\run-dpr-challenge.ps1 `
  -AgentExe "<path-to-FocusWindow.Agent.exe>" -Run -Try350 -TryAboveCap `
  -Modes "1440x3120"
```

The wrapper asks you to type `RUN` **and** `UNSUPPORTED`. The command sends `-4` even when `-3` reports 225% maximum. It classifies results:

- `REJECTED`: Windows returns a nonzero error.
- `ACCEPTED_BUT_CLAMPED_OR_IGNORED`: API returns success but measured DPI/ruler do not reach 336/350 px.
- `APPLIED_AND_PIXEL_VERIFIED`: PMv2 and actual captured pixels agree at the target.
- `RESTORE_FAILED_OR_UNVERIFIED`: treat as **hardware safety failure**, even if the earlier readback was encouraging.

The experimental API is undocumented. Even an unexpectedly successful SET would require repeatability and reboot/mode-change testing before considering it supportable.

## Independent DPI witness (optional cross-check)

The existing source `tools/df-density-readonly.cpp` can be built with the VS2022 x64 developer compiler:

```bat
cl /nologo /W4 /EHsc /std:c++17 /DUNICODE /D_UNICODE tools\df-density-readonly.cpp user32.lib shcore.lib /Fe:df-density-readonly.exe
```

It reports real PMv2 `GetDpiForWindow`, secondary `GetDpiForMonitor`, decoded relative `-3` offsets and two alternative width-floor predictions. Run it **while** the temporary virtual monitor exists if you need a second-process witness. It performs **no** display writes. The challenge runner's main DPI/ruler observations are made by its own PMv2 probe and real driver frame.

## What the experiment cannot prove

A green CI run only means the code compiles and math/crop unit tests pass. It does **not** prove hardware 350%, a successful native GPU crop, arbitrary third-party app behavior or a shipping solution. A per-monitor virtual-display result is also **not equivalent** to Windows global custom scaling, or to RDP desktopScaleFactor in another session. Do not install or change an RDP host for this experiment.

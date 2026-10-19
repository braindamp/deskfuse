# FocusIdd — the virtual display driver

A session-scoped indirect display driver (IddCx / UMDF 2). It exists for exactly one
reason: **DPI in Windows is a property of the display target, not of a window.** There
is no supported API to render one existing window at a phone's density. So the driver
creates a monitor whose EDID describes the phone, and Windows selects its own DPI settings; read-back is mandatory.

## What it does

1. At load, the adapter exists but **no monitor is reported**. Your desktop is
   untouched while Focus Window is idle.
2. The host agent sends `IOCTL_FOCUSIDD_SET_TARGET` (1440×3120 @ 120 Hz, 70×152 mm,
   scale hint 350, serial).
3. The driver synthesizes an EDID for the requested panel. Windows MAY select a suitable
   recommended scale; this driver does NOT apply a scale. `density-probe` independently
   measures Windows' actual choice, including a captured 100-DIP ruler.
4. Finished frames are GPU-copied into a staging texture, mapped, and published into
   `Global\FocusWindowSurface.v2` (3 slots, seqlock, BGRA).
5. `IOCTL_FOCUSIDD_REMOVE_MONITOR` departs the monitor. The guard compares physical displays before/after; topology restoration is NOT guaranteed.

Structure follows Microsoft's **IddSampleDriver** wherever the OS contract is involved;
the deltas are the runtime EDID, the on-demand monitor lifecycle, the IOCTL control
channel, and the shared-surface writer.

## Build

Requirements: Visual Studio 2022 with the **Windows Driver Kit** (IddCx 1.4+ headers).
The driver cannot be built with the plain Windows SDK, and cannot be built in a Linux
sandbox — `iddcx.h`, the UMDF toolset and `stampinf` are WDK-only.

```powershell
msbuild FocusIdd.vcxproj /p:Configuration=Release /p:Platform=x64
```

Output: `FocusIdd.dll` + `FocusIdd.inf` + test-signed catalog.

## Install (development machine)

Test signing must be on for a self-signed catalog:

```powershell
bcdedit /set testsigning on    # reboot
pnputil /add-driver FocusIdd.inf /install
devcon install FocusIdd.inf Root\FocusIdd   # or: pnputil + Device Manager
```

Verify:

```powershell
devcon status Root\FocusIdd
```

Uninstall:

```powershell
devcon remove Root\FocusIdd
pnputil /delete-driver FocusIdd.inf
bcdedit /set testsigning off   # reboot
```

## Shipping

Production distribution requires WHQL/attestation signing through the Windows Hardware
Dev Center. That is a process and cost decision, not a code one, and it is the long
pole in the schedule (plan P5). Until then, `SignMode=TestSign` in the project file.

## Contract with the agent

Everything the agent needs is in `../shared/FocusShared.h` — mirrored by hand in
`../agent/FocusWindow.Core/Display/DriverContract.cs`. Change one, change the other, bump
`FOCUSIDD_PROTOCOL_VERSION`.

| Channel | Direction | Purpose |
|---|---|---|
| `IOCTL_FOCUSIDD_SET_TARGET` (0x800) | agent → driver | (re)create the monitor for a phone profile |
| `IOCTL_FOCUSIDD_GET_STATUS` (0x801) | agent ← driver | arrived? dimensions? frames published? last error |
| `IOCTL_FOCUSIDD_REMOVE_MONITOR` (0x802) | agent → driver | teardown |
| `IOCTL_FOCUSIDD_PING` (0x803) | both | protocol version |
| `Global\FocusWindowSurface.v2` | driver → agent | published BGRA frames (3 slots, seqlock) |
| `Global\FocusWindowFrameReady.v2` | driver → agent | auto-reset event, one per published frame |

## Deliberate non-features

- **One monitor.** `MaxMonitorsSupported = 1`. More would let Windows remember
  topologies we cannot honor on teardown.
- **No resolution changes on a live monitor.** Changing an IddCx monitor's mode set is
  departure + arrival with a fresh descriptor. That is what we do; the stable EDID
  serial keeps Windows treating it as the same physical panel.
- **No legacy display APIs in the target path.** Nothing here calls
  `ChangeDisplaySettingsEx` or `SetDisplayConfig`. Those are agent-side tools, used
  only for the scale-factor ladder, and both are known to break for new IDD modes.

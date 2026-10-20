# Display status and density UX

Density negotiation is an internal host quality gate, not a phone workflow step.
The phone shows one stable status while the host prepares the workspace:

```
Preparing your display
Setting up the phone-sized workspace.
```

Windows scale caps, DPI probes, rulers and backing-display searches stay in
`host.log` and density evidence.

## Accepted display modes

- **Exact**: the phone DPR is a Windows scale step. The GPU workspace may call
  this native after a measured PMv2/window/ruler proof.
- **Quantized 1:1**: the phone DPR is not a Windows step. The host uses the
  nearest step and an exact phone-pixel viewport from a larger backing display.
  Pixels are sharp and 1:1, but the app layout is slightly quantized. It is never
  labelled native, and the host logs the measured difference.
- **Capped or unverified**: no streaming. The host fails honestly instead of
  rescaling and claiming native pixels.

## Constraints

- The session identity is the enrolled phone (`phone.DeviceId`). The FocusIdd
  EDID serial is a display cache key scoped to phone + backing size + refresh:
  stable for the same mode, distinct across modes. Windows 10 restores the mode
  it remembers for a serial; reusing one phone serial across a backing change was
  measured to force `1080x2400` or `1920x1080` instead of the requested timing.
  This is not a second session identity.
- Use per-monitor scale through the existing FocusIdd/DisplayConfig path only.
  Windows global custom scaling, DPI registry edits, RDP scale changes and
  physical-monitor changes are non-goals.
- Exact phone pixels are mandatory. If the phone DPR is not a Windows step, use
  the nearest step and capture the phone-sized window 1:1; never resample to
  imitate exact density. For example, Android 2.8125x (281.25%) is not a Windows
  scale step; 300% is the nearest supported step.
- Calibration asks for the final backing size on the first monitor creation.
  This avoids unnecessary per-mode serial entries, but per-mode serials remain
  mandatory whenever calibration must change backing modes.

A future optional quality indicator may expose "exact" versus "scaled app
layout" in settings. It must not turn calibration internals into a blocking
technical screen.

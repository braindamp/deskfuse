using System.ComponentModel;
using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Agent.Display;
internal sealed record PhoneTarget(
    Win32Layout.Luid AdapterId,
    uint SourceId,
    uint TargetId,
    bool Active,
    string FriendlyName,
    string GdiName,
    string DevicePath);
/// <summary>What was attached BEFORE we asked the driver to create its new monitor.</summary>
internal sealed record DisplayTopologyBefore(
    HashSet<string> ActiveTargetKeys,
    HashSet<string> ActiveGdiNames);
/// <summary>
/// Everything that asks Windows (not the driver) about the phone monitor: where it is, whether it is
/// active, what DPI it is really at, and the per-monitor scale limits.
/// </summary>
internal static unsafe class DisplayConfigApi
{
    /// <summary>The EDID driver reports vendor FOC, product 0x0046: Windows PnP id FOC0046 (FocusShared.h).</summary>
    private const string PnpId = "FOC0046";
    public static List<Win32Layout.PathInfo> QueryPaths(bool activeOnly)
    {
        var flags = activeOnly ? Native.QdcOnlyActivePaths : Native.QdcAllPaths;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rc = Native.GetDisplayConfigBufferSizes(flags, out var np, out var nm);
            if (rc != 0) throw new Win32Exception(rc, "GetDisplayConfigBufferSizes failed");
            var paths = new Win32Layout.PathInfo[np];
            var modes = new Win32Layout.ModeInfo[nm];
            rc = Native.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == Native.ErrorInsufficientBuffer) continue; // topology changed between the two calls
            if (rc != 0) throw new Win32Exception(rc, "QueryDisplayConfig failed");
            return paths.Take((int)np).ToList();
        }
        throw new DisplayException("display", "Windows' display topology kept changing while it was being read.");
    }
    private static string TargetName(Win32Layout.PathInfo p, out string devicePath, out ushort manufacturer, out ushort product)
    {
        var packet = new Win32Layout.TargetDeviceName();
        packet.Header.Type = Win32Layout.DeviceInfoGetTargetName;
        packet.Header.Size = (uint)sizeof(Win32Layout.TargetDeviceName);
        packet.Header.AdapterId = p.TargetInfo.AdapterId;
        packet.Header.Id = p.TargetInfo.Id;
        devicePath = "";
        manufacturer = 0;
        product = 0;
        if (Native.GetTargetName(ref packet) != 0) return "";
        manufacturer = packet.EdidManufactureId;
        product = packet.EdidProductCodeId;
        devicePath = new string(packet.MonitorDevicePath);
        return new string(packet.MonitorFriendlyDeviceName);
    }
    private static string SourceGdiName(Win32Layout.PathInfo p)
    {
        var packet = new Win32Layout.SourceDeviceName();
        packet.Header.Type = Win32Layout.DeviceInfoGetSourceName;
        packet.Header.Size = (uint)sizeof(Win32Layout.SourceDeviceName);
        packet.Header.AdapterId = p.SourceInfo.AdapterId;
        packet.Header.Id = p.SourceInfo.Id;
        return Native.GetSourceName(ref packet) != 0 ? "" : new string(packet.ViewGdiDeviceName);
    }
    private static string TargetKey(Win32Layout.PathInfo path) =>
        $"{path.TargetInfo.AdapterId.HighPart:X8}:{path.TargetInfo.AdapterId.LowPart:X8}:{path.TargetInfo.Id}";

    /// <summary>
    /// The driver's EDID identity is not guaranteed to be exposed through
    /// DISPLAYCONFIG_TARGET_DEVICE_NAME on Windows 10 indirect displays.
    /// Capture ACTIVE targets and desktop GDI monitors before monitor arrival,
    /// so the new target can be found without relying on its friendly name.
    /// </summary>
    public static DisplayTopologyBefore CaptureTopology() => new(
        QueryPaths(activeOnly: true).Where(p => p.IsActive)
            .Select(TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
        EnumerateGdiMonitors().Select(m => m.GdiName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private sealed record GdiMonitor(string GdiName, RectI Bounds, IntPtr Handle);

    private static List<GdiMonitor> EnumerateGdiMonitors()
    {
        var monitors = new List<GdiMonitor>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var info = new Native.MonitorInfoEx { CbSize = Marshal.SizeOf<Native.MonitorInfoEx>() };
            if (Native.GetMonitorInfo(hmon, ref info) && !string.IsNullOrWhiteSpace(info.Device))
                monitors.Add(new GdiMonitor(info.Device,
                    new RectI(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height), hmon));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static bool IsFocusEdid(string name, string devicePath, ushort manufacturer, ushort product)
    {
        // FOC0046 is the driver's EDID manufacturer/product pair.
        // The friendly name and device path are often blank for a newly arrived IddCx target.
        // EDID stores FOC as bytes 0x19,0xE3 (big-endian); the Windows
        // DISPLAYCONFIG_TARGET_DEVICE_NAME uint16 reads those bytes as 0xE319.
        // This was observed on Windows 10 as EDID=E319:0046.
        const ushort focManufacturerWindows = 0xE319;
        return (manufacturer == focManufacturerWindows && product == 0x0046) ||
               devicePath.Contains(PnpId, StringComparison.OrdinalIgnoreCase) ||
               name.Equals("FocusPhone", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Identify ONLY an unambiguous new active desktop display. First use the
    /// FocusPhone EDID if exposed. If Windows 10 omits it, correlate newly
    /// active CCD targets with newly visible GDI monitor names (and requested
    /// dimensions). Never select a pre-existing physical monitor by resolution
    /// alone, and never activate a path or change the user's display layout.
    /// </summary>
    public static PhoneTarget? FindCreatedPhoneMonitor(
        DisplayTopologyBefore before, int requestedWidth, int requestedHeight,
        out string diagnostic)
    {
        var paths = QueryPaths(activeOnly: false);
        var monitors = EnumerateGdiMonitors();
        var known = new List<PhoneTarget>();
        var appeared = new List<PhoneTarget>();
        PhoneTarget? inactive = null;
        foreach (var path in paths)
        {
            var name = TargetName(path, out var devicePath, out var manufacturer, out var product);
            var focus = IsFocusEdid(name, devicePath, manufacturer, product);
            var gdiName = path.IsActive ? SourceGdiName(path) : "";
            var target = new PhoneTarget(path.SourceInfo.AdapterId, path.SourceInfo.Id,
                path.TargetInfo.Id, path.IsActive, name, gdiName, devicePath);
            if (!path.IsActive)
            {
                if (focus) inactive ??= target;
                continue;
            }
            if (string.IsNullOrEmpty(gdiName) ||
                !monitors.Any(m => string.Equals(m.GdiName, gdiName, StringComparison.OrdinalIgnoreCase)))
                continue; // Windows has no active HMONITOR for this path yet.
            var isNewTarget = !before.ActiveTargetKeys.Contains(TargetKey(path));
            var isNewDesktopMonitor = !before.ActiveGdiNames.Contains(gdiName);
            if (focus && (isNewTarget || isNewDesktopMonitor)) known.Add(target);
            if (isNewTarget && isNewDesktopMonitor) appeared.Add(target);
        }
        // EDID is strongest evidence, but only when it refers to a newly attached display.
        // In the fallback, insist that both CCD and GDI agree that a NEW display appeared.
        var candidates = known.Count > 0 ? known : appeared;
        candidates = candidates.GroupBy(c =>
            $"{c.AdapterId.HighPart:X8}:{c.AdapterId.LowPart:X8}:{c.TargetId}")
            .Select(g => g.First()).ToList();
        if (candidates.Count > 1)
        {
            // Prefer the mode that the driver just requested, but do not silently pick
            // a monitor if multiple candidates still match.
            var exact = candidates.Where(c => monitors.Any(m =>
                string.Equals(m.GdiName, c.GdiName, StringComparison.OrdinalIgnoreCase) &&
                m.Bounds.Width == requestedWidth && m.Bounds.Height == requestedHeight)).ToList();
            if (exact.Count == 1) candidates = exact;
        }
        var newlyVisible = monitors.Where(m => !before.ActiveGdiNames.Contains(m.GdiName))
            .Select(m => $"{m.GdiName}={m.Bounds.Width}x{m.Bounds.Height}").ToArray();
        diagnostic = $"new desktop monitors: [{string.Join(", ", newlyVisible)}]; " +
                     $"new CCD target candidates: {appeared.Count}, EDID candidates: {known.Count}, " +
                     $"selected candidates: {candidates.Count}";
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1) return null; // Ambiguous. NEVER guess a physical monitor.
        return inactive; // May have arrived in PnP but is not connected to desktop.
    }

    /// <summary>Read-only CCD and GDI diagnostics; never changes Windows topology.</summary>
    public static string DescribePaths()
    {
        var rows = new List<string>();
        foreach (var path in QueryPaths(activeOnly: false))
        {
            var name = TargetName(path, out var devicePath, out var manufacturer, out var product);
            if (!path.IsActive && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(devicePath))
                continue;
            rows.Add($"target={TargetKey(path)} source={path.SourceInfo.Id} " +
                     $"active={path.IsActive} available={path.TargetInfo.TargetAvailable != 0} " +
                     $"EDID={manufacturer:X4}:{product:X4} GDI='{(path.IsActive ? SourceGdiName(path) : "")}' " +
                     $"name='{name}' path='{devicePath}'");
            if (rows.Count >= 30) break;
        }
        var gdi = EnumerateGdiMonitors().Select(m =>
            $"{m.GdiName} {m.Bounds.Width}x{m.Bounds.Height}");
        return $"CCD: {(rows.Count == 0 ? "none" : string.Join(" | ", rows))}; GDI: {string.Join(" | ", gdi)}";
    }

    public static (RectI Bounds, IntPtr Handle)? FindMonitor(string gdiName)
    {
        (RectI, IntPtr)? found = null;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var info = new Native.MonitorInfoEx { CbSize = Marshal.SizeOf<Native.MonitorInfoEx>() };
            if (Native.GetMonitorInfo(hmon, ref info) &&
                string.Equals(info.Device, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                found = (new RectI(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height), hmon);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static (int X, int Y)? MonitorDpi(IntPtr hmon)
    {
        if (Native.GetDpiForMonitor(hmon, 0, out var x, out var y) != 0) return null;
        return ((int)x, (int)y);
    }
    /// <summary>
    /// Reads the recommended / current / maximum scale via the UNDOCUMENTED type -3 packet. Returns null if
    /// the call fails or the indices fall outside the known step table (which means Windows changed the
    /// packet's meaning and nothing here may be trusted).
    /// </summary>
    public static ScaleCaps? ReadScaleCaps(PhoneTarget t)
    {
        var packet = new Win32Layout.DpiScaleGet();
        packet.Header.Type = Win32Layout.DeviceInfoGetDpiScale;
        packet.Header.Size = (uint)sizeof(Win32Layout.DpiScaleGet);
        packet.Header.AdapterId = t.AdapterId;
        packet.Header.Id = t.SourceId;
        if (Native.GetDpiScale(ref packet) != 0) return null;
        return ScaleCaps.FromRelative(packet.MinScaleRel, packet.CurScaleRel, packet.MaxScaleRel);
    }
    /// <summary>Sets the per-monitor scale via the UNDOCUMENTED type -4 packet. The caller must read the result back.</summary>
    public static ScaleSetResult SetScale(PhoneTarget t, int percent)
    {
        var caps = ReadScaleCaps(t);
        if (caps is null)
            return new ScaleSetResult(false, "scale limits unreadable; refusing to send an index that cannot be validated");
        if (!caps.Allows(percent))
            return new ScaleSetResult(false, $"{percent}% is outside what Windows allows for this monitor (max {caps.MaxPercent}%)");
        var packet = new Win32Layout.DpiScaleSet { ScaleRel = caps.RelativeFor(percent) };
        packet.Header.Type = Win32Layout.DeviceInfoSetDpiScale;
        packet.Header.Size = (uint)sizeof(Win32Layout.DpiScaleSet);
        packet.Header.AdapterId = t.AdapterId;
        packet.Header.Id = t.SourceId;
        var rc = Native.SetDpiScale(ref packet);
        return rc == 0
            ? new ScaleSetResult(true, $"DisplayConfigSetDeviceInfo(-4) accepted relative index {packet.ScaleRel} for {percent}%")
            : new ScaleSetResult(false, $"DisplayConfigSetDeviceInfo(-4) returned {rc} ({new Win32Exception(rc).Message})");
    }
    /// <summary>
    /// FA challenge ONLY: issue the undocumented -4 SET even if Windows reports
    /// the requested step above its maximum. Production SetScale remains guarded.
    /// The caller MUST hold an explicitly identified new virtual display, capture
    /// the original scale, read back, and restore it in a finally block.
    /// </summary>
    internal static ScaleSetResult TrySetScaleForChallenge(PhoneTarget t, int percent,
        bool explicitlyAllowBeyondMaximum)
    {
        var caps = ReadScaleCaps(t);
        if (caps is null)
            return new ScaleSetResult(false, "REJECTED_PRECHECK: cannot decode virtual monitor DPI range");
        if (WindowsScale.IndexOf(percent) < 0)
            return new ScaleSetResult(false, $"REJECTED_PRECHECK: {percent}% is not a supported Windows step");
        if (percent > caps.MaxPercent && !explicitlyAllowBeyondMaximum)
            return new ScaleSetResult(false,
                $"REJECTED_PRECHECK: {percent}% exceeds reported max {caps.MaxPercent}%; " +
                "requires explicit challenge-only beyond-cap permission");
        var relative = DprChallengeTheory.UnrestrictedRelativeStep(caps.RecommendedPercent, percent);
        var packet = new Win32Layout.DpiScaleSet { ScaleRel = relative };
        packet.Header.Type = Win32Layout.DeviceInfoSetDpiScale;
        packet.Header.Size = (uint)sizeof(Win32Layout.DpiScaleSet);
        packet.Header.AdapterId = t.AdapterId;
        packet.Header.Id = t.SourceId;
        var rc = Native.SetDpiScale(ref packet);
        return new ScaleSetResult(rc == 0,
            $"DisplayConfigSetDeviceInfo(-4) requested={percent}% recommended={caps.RecommendedPercent}% " +
            $"reported-max={caps.MaxPercent}% relative-step={relative} win32-return={rc}" +
            (rc == 0 ? " (API accepted, actual DPI still UNVERIFIED)" :
             $" ({new Win32Exception(rc).Message})"));
    }
    public static string DescribeAwareness(IntPtr hwnd)
    {
        var ctx = Native.GetWindowDpiAwarenessContext(hwnd);
        if (Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxPerMonitorV2)) return "PerMonitorV2";
        if (Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxPerMonitor)) return "PerMonitor (v1)";
        if (Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxSystemAware)) return "System-aware (bitmap-scaled on a monitor with another DPI)";
        if (Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxUnaware) ||
            Native.AreDpiAwarenessContextsEqual(ctx, Native.CtxUnawareGdiScaled)) return "Unaware (DPI-virtualised)";
        return "unknown awareness";
    }
}

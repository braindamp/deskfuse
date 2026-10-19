using FocusWindow.Core.Protocol;
namespace FocusWindow.Core.Display;
public sealed record MonitorRequest(
    int Width,
    int Height,
    int RefreshHz,
    int PhysWidthMm,
    int PhysHeightMm,
    uint Serial,
    int ScaleHintPercent);
public sealed record MonitorInfo(RectI Bounds, string GdiName, string Description);
/// <summary>Reported monitor DPI; process awareness can affect the Windows query.
/// Native-density certification also requires the PMv2 GetDpiForWindow+ruler measurement.</summary>
public sealed record DensityReadout(int MonitorDpiX, int MonitorDpiY, RectI Bounds);
public sealed record ScaleSetResult(bool Ok, string Detail);
/// <summary>
/// What a real window created on the phone monitor reports and renders. The ruler fields are
/// the pixel-level proof: a 100 DIP square drawn by the probe and measured in the captured frame.
/// </summary>
public sealed record WindowDensityReport(
    int WindowDpi,
    string Awareness,
    RectI Window,
    RectI Client,
    int RulerDips,
    int? MeasuredRulerWidthPx,
    int? MeasuredRulerHeightPx,
    bool? RulerOk,
    string? Note);
public sealed record DriverHealth(
    bool Reachable,
    int ProtocolVersion,
    bool MonitorArrived,
    int Width,
    int Height,
    int RefreshHz,
    ulong FramesPublished,
    ulong FramesDropped,
    uint LastError,
    uint SurfaceWin32Error);
public interface IFrameSource : IDisposable
{
    /// <summary>Waits up to <paramref name="timeoutMs"/> for a frame newer than the last one returned.</summary>
    bool TryRead(int timeoutMs, ref byte[] buffer, out FrameInfo info);
}
/// <summary>
/// Everything the session needs from the display side. The Windows implementation talks to
/// the IddCx driver and the display-config APIs; tests substitute a fake so the negotiation
/// and session logic run on any OS.
/// </summary>
public interface IDisplayBackend : IAsyncDisposable
{
    /// <summary>(Re)creates the phone monitor and returns only once Windows reports it ACTIVE.</summary>
    Task<MonitorInfo> CreateMonitorAsync(MonitorRequest request, CancellationToken ct);
    DensityReadout ReadDensity(MonitorInfo monitor);
    /// <summary>Recommended/current/max scale, or null when the (undocumented) query is unavailable.</summary>
    ScaleCaps? ReadScaleCaps(MonitorInfo monitor);
    ScaleSetResult TrySetScale(MonitorInfo monitor, int percent);
    Task<WindowDensityReport?> ProbeWindowAsync(MonitorInfo monitor, bool drawRuler, CancellationToken ct);
    Task RemoveMonitorAsync(CancellationToken ct);
    DriverHealth ReadDriverHealth();
    /// <summary>Tells the driver's watchdog the agent is alive.</summary>
    void Heartbeat();
    IFrameSource OpenFrameSource();
}
/// <summary>A failure with a stage and a remediation, so the phone never shows a bare "error".</summary>
public sealed class DisplayException : Exception
{
    public string Stage { get; }
    public string? Remediation { get; }
    public DisplayException(string stage, string message, string? remediation = null, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        Remediation = remediation;
    }
}
public static class PhysicalSize
{
    /// <summary>
    /// Splits the phone's diagonal into width/height in millimetres using the pixel aspect
    /// ratio. This is the EDID physical size handed to Windows, the one density input a
    /// console-session driver controls.
    /// </summary>
    public static (int WidthMm, int HeightMm) FromDiagonal(int diagonalMm, int widthPx, int heightPx)
    {
        var diagPx = Math.Sqrt((double)widthPx * widthPx + (double)heightPx * heightPx);
        var w = (int)Math.Round(diagonalMm * widthPx / diagPx);
        var h = (int)Math.Round(diagonalMm * heightPx / diagPx);
        return (Math.Max(1, w), Math.Max(1, h));
    }
    /// <summary>Stable 32-bit serial from the device id (FNV-1a) so Windows keeps per-monitor memory per phone.</summary>
    public static uint StableSerial(string deviceId)
    {
        var hash = 2166136261u;
        foreach (var c in deviceId)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash == 0 ? 1u : hash;
    }
}
/// <summary>Input injection seam. The Windows implementation uses SendInput.</summary>
public interface IInputSink
{
    void Handle(InputEventDto e, InputGeometry geometry);
    /// <summary>Releases any key or button this sink is holding down (called at session end).</summary>
    void ReleaseAll();
}
/// <summary>Maps stream pixels to desktop pixels. In the native path both are the same size.</summary>
public sealed record InputGeometry(RectI MonitorBounds, int StreamWidth, int StreamHeight)
{
    public (int X, int Y) ToDesktop(int x, int y)
    {
        var cx = Math.Clamp(x, 0, StreamWidth - 1);
        var cy = Math.Clamp(y, 0, StreamHeight - 1);
        var dx = MonitorBounds.X + (int)Math.Round(cx * (MonitorBounds.Width / (double)StreamWidth));
        var dy = MonitorBounds.Y + (int)Math.Round(cy * (MonitorBounds.Height / (double)StreamHeight));
        return (dx, dy);
    }
}
public sealed record PhysicalMonitorInfo(string DeviceName, RectI Bounds, int Dpi, bool Primary);
public sealed record DisplaySnapshot(IReadOnlyList<PhysicalMonitorInfo> Monitors);
public sealed record GuardReport(bool Clean, IReadOnlyList<string> Drift);
public interface IPhysicalDisplayGuard
{
    DisplaySnapshot Snapshot();
    GuardReport Verify(DisplaySnapshot before);
}
public static class DisplayGuardLogic
{
    /// <summary>
    /// FR-DPI-10: every physical monitor must keep its rectangle, DPI and primary flag. Pure
    /// so it is unit-tested. Monitors present only in one snapshot are drift too — except the
    /// phone monitor, which the caller excludes before snapshotting.
    /// </summary>
    public static GuardReport Compare(DisplaySnapshot before, DisplaySnapshot after)
    {
        var drift = new List<string>();
        var afterByName = after.Monitors.ToDictionary(m => m.DeviceName);
        foreach (var b in before.Monitors)
        {
            if (!afterByName.TryGetValue(b.DeviceName, out var a))
            {
                drift.Add($"{b.DeviceName}: disappeared");
                continue;
            }
            if (a.Bounds != b.Bounds) drift.Add($"{b.DeviceName}: rectangle {b.Bounds} -> {a.Bounds}");
            if (a.Dpi != b.Dpi) drift.Add($"{b.DeviceName}: DPI {b.Dpi} -> {a.Dpi}");
            if (a.Primary != b.Primary) drift.Add($"{b.DeviceName}: primary flag {b.Primary} -> {a.Primary}");
        }
        var beforeNames = before.Monitors.Select(m => m.DeviceName).ToHashSet();
        foreach (var a in after.Monitors.Where(m => !beforeNames.Contains(m.DeviceName)))
            drift.Add($"{a.DeviceName}: appeared");
        return new GuardReport(drift.Count == 0, drift);
    }
}

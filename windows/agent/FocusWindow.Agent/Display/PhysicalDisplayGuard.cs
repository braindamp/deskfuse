using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Agent.Display;
/// <summary>
/// FR-DPI-10 — the user's REAL monitors must come out of a session exactly as they went in: same
/// rectangle, same DPI, same primary flag. This is read-only: it detects drift and reports it, it never
/// tries to "restore" a topology (rewriting a user's display config from a stale snapshot is riskier than
/// the drift it would fix). The phone monitor itself is excluded.
/// </summary>
internal sealed class PhysicalDisplayGuard : IPhysicalDisplayGuard
{
    private readonly Func<string?> _phoneGdiName;
    public PhysicalDisplayGuard(Func<string?> phoneGdiName) => _phoneGdiName = phoneGdiName;
    public DisplaySnapshot Snapshot()
    {
        var phone = _phoneGdiName();
        var monitors = new List<PhysicalMonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var info = new Native.MonitorInfoEx { CbSize = Marshal.SizeOf<Native.MonitorInfoEx>() };
            if (!Native.GetMonitorInfo(hmon, ref info)) return true;
            if (phone is not null && string.Equals(info.Device, phone, StringComparison.OrdinalIgnoreCase)) return true;
            var dpi = DisplayConfigApi.MonitorDpi(hmon)?.X ?? 0;
            monitors.Add(new PhysicalMonitorInfo(
                info.Device,
                new RectI(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height),
                dpi,
                (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return new DisplaySnapshot(monitors);
    }
    public GuardReport Verify(DisplaySnapshot before) => DisplayGuardLogic.Compare(before, Snapshot());
}

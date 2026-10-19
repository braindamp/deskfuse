using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FocusWindow.Core.Session;
namespace FocusWindow.Agent;

/// <summary>
/// Integrates Desklink C's app/window picker into the canonical user-session host.
/// Explicitly limited to top-level, named windows of THIS logon session. No window
/// movement or resizing here: focusing changes foreground input and is user-initiated.
/// </summary>
internal sealed class WindowsWindowWorkflow : IWindowWorkflow
{
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback cb, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    private const uint GwOwner = 4;
    private static readonly uint CurrentSession = (uint)Process.GetCurrentProcess().SessionId;

    public IReadOnlyList<WindowItem> ListVisible()
    {
        var result = new List<WindowItem>();
        EnumWindows((handle, _) =>
        {
            var item = Describe(handle);
            if (item is not null && result.Count < 100) result.Add(item);
            return result.Count < 100;
        }, nint.Zero);
        return result;
    }

    public bool Activate(string id)
    {
        if (!ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
        var handle = new nint(unchecked((long)parsed));
        // Never trust a stale phone-provided window ID. Re-enumerate the allowlist
        // and require a current, visible top-level window in this logon session.
        if (!ListVisible().Any(item => item.Id == id)) return false;
        return SetForegroundWindow(handle);
    }

    private static WindowItem? Describe(nint handle)
    {
        if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != nint.Zero) return null;
        var length = GetWindowTextLengthW(handle);
        if (length <= 0 || length > 1024) return null;
        var threadId = GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0 || !ProcessIdToSessionId(pid, out var session) || session != CurrentSession) return null;
        var title = new StringBuilder(length + 1);
        if (GetWindowTextW(handle, title, title.Capacity) <= 0) return null;
        string app;
        // Process creation timestamp distinguishes a restarted app when the kernel
        // reuses a PID. Thread ID and HWND distinguish windows in the same process.
        // This remains a live-session identity, not a persistent app identity.
        long processBornUtcTicks = 0;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            app = process.ProcessName;
            try { processBornUtcTicks = process.StartTime.ToUniversalTime().Ticks; }
            catch { /* Some system processes deny start-time readback. */ }
        }
        catch { app = "Application"; }
        var hwnd = unchecked((ulong)handle.ToInt64()).ToString("X16");
        var instanceKey = $"{hwnd}:{pid:X8}:{threadId:X8}:{processBornUtcTicks:X16}";
        return new WindowItem(hwnd, title.ToString(), app, instanceKey);
    }
}

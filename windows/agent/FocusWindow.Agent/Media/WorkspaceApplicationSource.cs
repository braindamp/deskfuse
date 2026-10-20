using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Agent.Media;

/// <summary>
/// A locally selected NEW application instance. Only a newly created top-level
/// HWND belonging to the launched process may be placed on the owned monitor.
/// Single-instance handoff to existing physical windows is deliberately refused.
/// </summary>
internal sealed class WorkspaceApplicationSource(string executable, string? arguments) : IAsyncDisposable
{
    private delegate bool EnumCallback(nint hwnd, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(
        nint monitor, ref MonitorInfoEx info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
        nint hwnd, uint attribute, out NativeRect rect, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePoint(int x, int y)
    { public readonly int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private Process? _process;
    private VideoPipelineSettings? _settings;
    private int _attached;
    public nint Handle { get; private set; }
    public RectI Bounds { get; private set; }
    public int Dpi { get; private set; }
    public string SourceId { get; } = Guid.NewGuid().ToString("N");
    public string AppLabel => Path.GetFileNameWithoutExtension(executable);

    public void Attach()
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0)
            throw new DisplayException("source-owned", "This application source already has an active capture owner.");
    }
    public void Detach()
    {
        _settings = null;
        Volatile.Write(ref _attached, 0);
    }

    public async Task StartAsync(VideoPipelineSettings settings, CancellationToken ct)
    {
        if (Volatile.Read(ref _attached) != 1) throw new InvalidOperationException("Acquire the application source first");
        var monitor = settings.BackingMonitorBounds;
        if (monitor.Width < settings.Width || monitor.Height < settings.Height ||
            string.IsNullOrWhiteSpace(settings.MonitorGdiName))
            throw new DisplayException("source", "An identified virtual display and exact viewport are required.");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new DisplayException("source", "No supported application source was selected.",
                "Start the host with --workspace-app and an absolute .exe path. The phone cannot choose arbitrary executables.");

        ct.ThrowIfCancellationRequested();
        var existing = new HashSet<nint>();
        if (_process?.HasExited == true)
        {
            _process.Dispose(); _process = null; Handle = nint.Zero;
        }
        if (_process is null)
        {
            EnumWindows((hwnd, _) => { existing.Add(hwnd); return true; }, nint.Zero);
            _process = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = arguments ?? "", UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            }) ?? throw new DisplayException("source", "The selected application could not be started.");
        }
        GetWindowThreadProcessId(Handle, out var currentPid);
        if (currentPid != _process.Id) Handle = nint.Zero;
        var deadline = Stopwatch.StartNew();
        while (Handle == nint.Zero && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new DisplayException("source", "The application exited or handed off to an existing instance.",
                    "Choose an application that supports a new independent process/window; existing windows are never moved.");
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == _process.Id && !existing.Contains(hwnd) && IsWindowVisible(hwnd) &&
                    GetAncestor(hwnd, 3 /* GA_ROOTOWNER */) == hwnd)
                { Handle = hwnd; return false; }
                return true;
            }, nint.Zero);
            if (Handle == nint.Zero) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (Handle == nint.Zero)
            throw new DisplayException("source", "The new application did not create a capturable window.");

        _settings = settings;
        var desired = new RectI(monitor.X + (monitor.Width - settings.Width) / 2,
            monitor.Y, settings.Width, settings.Height);
        // WGC captures DWM's extended frame, not GetWindowRect or just the
        // client area. Account for invisible resize borders without scaling.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            RequireOwnedWindow();
            // A taskbar minimize survives monitor removal. On explicit retry,
            // restore only this catalog-owned window before measuring its frame.
            // Never activate it or adopt another Notepad window.
            if (IsIconic(Handle))
            {
                ShowWindowAsync(Handle, 4 /* SW_SHOWNOACTIVATE */);
                await Task.Delay(150, ct).ConfigureAwait(false);
                if (IsIconic(Handle)) continue;
            }
            var frame = ReadFrameBounds();
            if (!GetWindowRect(Handle, out var outer)) throw new DisplayException("source", "Window bounds unavailable.");
            if (!SetWindowPos(Handle, nint.Zero,
                desired.X - (frame.X - outer.Left), desired.Y - (frame.Y - outer.Top),
                desired.Width + (outer.Right - outer.Left - frame.Width),
                desired.Height + (outer.Bottom - outer.Top - frame.Height),
                0x0014 /* SWP_NOZORDER | SWP_NOACTIVATE */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(150, ct).ConfigureAwait(false);
            Bounds = ReadFrameBounds();
            Dpi = checked((int)GetDpiForWindow(Handle));
            if (Bounds == desired && Dpi == settings.RequiredDpi) break;
        }
        if (Bounds != desired || !IsCurrent())
            throw new DisplayException("source-geometry", "The selected app cannot provide the exact phone window size/DPI.",
                "Choose a PerMonitorV2 application that accepts this size. DeskFuse never rescales to claim native pixels.");
    }

    private void RequireOwnedWindow()
    {
        GetWindowThreadProcessId(Handle, out var pid);
        if (_process is null || _process.HasExited || pid != _process.Id)
            throw new DisplayException("source-closed", "The selected application window closed.",
                "Reconnect to start a new application source.");
    }

    private RectI ReadFrameBounds()
    {
        if (DwmGetWindowAttribute(Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */,
                out var rect, Marshal.SizeOf<NativeRect>()) != 0)
            throw new DisplayException("source-geometry", "DWM capture bounds are unavailable.");
        return new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public bool IsCurrent()
    {
        RequireOwnedWindow();
        if (_settings is null || IsIconic(Handle) || !IsWindowVisible(Handle) || ReadFrameBounds() != Bounds ||
            GetDpiForWindow(Handle) != Dpi ||
            !AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(Handle), new nint(-4))) return false;
        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
        return GetMonitorInfoW(MonitorFromWindow(Handle, 0), ref info) &&
            string.Equals(info.Device, _settings.MonitorGdiName, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryPrepareInput(InputEventDto input)
    {
        if (!IsCurrent()) return false;
        if (input.Kind is "key" or "text") return GetForegroundWindow() == Handle;
        // Relative motion is clamped by SendInputSink, but must not operate on
        // a physical window if the PC user has taken the foreground back.
        if (input.Kind == "relative") return GetForegroundWindow() == Handle;
        if (input.Kind is not ("pointer" or "scroll")) return false;
        var x = Bounds.X + Math.Clamp(input.X, 0, Bounds.Width - 1);
        var y = Bounds.Y + Math.Clamp(input.Y, 0, Bounds.Height - 1);
        return GetAncestor(WindowFromPoint(new NativePoint(x, y)), 2 /* GA_ROOT */) == Handle;
    }

    public ValueTask DisposeAsync()
    {
        // Preserve documents on disconnect. Never kill the app or dismiss save
        // dialogs; Windows rehomes this NEW window when its display departs.
        Handle = nint.Zero;
        Detach();
        _process?.Dispose();
        _process = null;
        return ValueTask.CompletedTask;
    }
}

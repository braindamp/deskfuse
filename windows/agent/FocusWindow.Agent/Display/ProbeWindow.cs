using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FocusWindow.Agent.Interop;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Agent.Display;

/// <summary>
/// A real, PerMonitorV2 window created on the phone monitor. It exists to answer two questions with
/// evidence instead of assumption:
///   1. What does Windows say a window on that monitor is at? (GetDpiForWindow + its awareness context)
///   2. Is it actually RENDERED at that density? It paints a 100 DIP square (pure green on magenta),
///      and the rendering is measured in a frame captured back through the driver. If the DPI Windows
///      reported and the pixels disagree, or the capture path resampled, the ruler fails.
///
/// This is deliberately a native WS_POPUP window, created directly at the target monitor
/// coordinates. The POC proved that this receives the monitor's per-monitor DPI. A WinForms Form
/// created through the same path can remain at 96 DPI even when it is PMv2 and sits on a 336-DPI
/// monitor; it is not a valid measurement window.
/// </summary>
internal static class ProbeWindow
{
    private const string ClassName = "DeskFuse.ProbeWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint PmRemove = 0x0001;
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmDpiChanged = 0x02E0;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly object ClassLock = new();
    private static readonly WndProcDelegate WndProc = WindowProc;
    private static readonly ConcurrentDictionary<IntPtr, ProbeState> States = new();
    private static bool _classRegistered;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public bool Erase;
        public Native.Rect Paint;
        public bool Restore;
        public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    private sealed class ProbeState
    {
        public required RectI Target;
        public bool Ruler;
        public IntPtr Hwnd;
        public IntPtr Magenta;
        public IntPtr Green;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Native.Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr hwnd,
        uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    public static Task<WindowDensityReport> RunAsync(
        RectI bounds, bool drawRuler, Func<(byte[] Bgra, int Width, int Height)?> captureNewest)
    {
        var tcs = new TaskCompletionSource<WindowDensityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(RunOnStaThread(bounds, drawRuler, captureNewest));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "focus-probe-window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            var instance = GetModuleHandle(null);
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Style = 0x0001 | 0x0002, // CS_VREDRAW | CS_HREDRAW
                WindowProc = WndProc,
                Instance = instance,
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
                    throw new DisplayException("probe-window", $"RegisterClassExW failed ({error}).");
            }
            _classRegistered = true;
        }
    }

    private static void Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            Thread.Sleep(10);
        }
    }

    private static WindowDensityReport RunOnStaThread(
        RectI bounds, bool drawRuler, Func<(byte[] Bgra, int Width, int Height)?> captureNewest)
    {
        // WinForms can create an independent STA using an inherited awareness
        // context. Require PMv2 on THAT thread, otherwise 96 DPI means the
        // window was DPI-unaware, not that Windows clamped the monitor.
        var previousContext = Native.SetThreadDpiAwarenessContext(Native.CtxPerMonitorV2);
        if (previousContext == IntPtr.Zero)
            throw new DisplayException("probe-dpi-context",
                "Could not establish PerMonitorV2 DPI awareness on the probe STA.");
        try
        {
            EnsureClass();
            var state = new ProbeState
            {
                Target = bounds,
                Ruler = drawRuler,
                Magenta = CreateSolidBrush(0x00FF00FF),
                Green = CreateSolidBrush(0x0000FF00),
            };
            if (state.Magenta == IntPtr.Zero || state.Green == IntPtr.Zero)
                throw new DisplayException("probe-window", "Could not create the probe brushes.");
            var hwnd = CreateWindowExW(
                WsExTopmost | WsExToolWindow | WsExNoActivate,
                ClassName, "DeskFuse density probe", WsPopup,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new DisplayException("probe-window",
                    $"CreateWindowExW failed ({Marshal.GetLastWin32Error()}).");
            state.Hwnd = hwnd;
            States[hwnd] = state;
            try
            {
                ShowWindow(hwnd, SwShowNoActivate);
                Native.SetWindowPos(hwnd, HwndTopmost, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height,
                    Native.SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                Pump(400);
                Native.SetWindowPos(hwnd, HwndTopmost, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height,
                    Native.SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                Pump(300);
                var dpi = (int)Native.GetDpiForWindow(hwnd);
                var awareness = DisplayConfigApi.DescribeAwareness(hwnd);
                var hmon = Native.MonitorFromWindow(hwnd, Native.MonitorDefaultToNearest);
                var monitorInfo = new Native.MonitorInfoEx
                {
                    CbSize = Marshal.SizeOf<Native.MonitorInfoEx>(),
                    Device = "",
                };
                Native.GetMonitorInfo(hmon, ref monitorInfo);
                var monitorBounds = new RectI(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
                    monitorInfo.Monitor.Width, monitorInfo.Monitor.Height);
                var monitorDpi = 0;
                if (Native.GetDpiForMonitor(hmon, 0, out var monitorDpiX, out _) == 0)
                    monitorDpi = (int)monitorDpiX;
                Native.GetWindowRect(hwnd, out var wr);
                Native.GetClientRect(hwnd, out var cr);
                var window = new RectI(wr.Left, wr.Top, wr.Width, wr.Height);
                var client = new RectI(0, 0, cr.Width, cr.Height);
                int? mw = null, mh = null;
                bool? rulerOk = null;
                string? note = drawRuler ? null : "ruler not requested";
                if (drawRuler)
                {
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                    Pump(500);
                    RectI box = default;
                    var found = false;
                    for (var attempt = 0; attempt < 6 && !found; attempt++)
                    {
                        if (captureNewest() is { } frame &&
                            frame.Width == bounds.Width && frame.Height == bounds.Height &&
                            DensityMeasure.TryFindColorBox(frame.Bgra, frame.Width, frame.Height, 0, 255, 0, 40, out box))
                        {
                            found = true;
                            break;
                        }
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                        Pump(250);
                    }
                    if (!found)
                    {
                        rulerOk = false;
                        note = "the green ruler square never appeared in a captured frame (nothing rendered to the phone monitor, or an unexpected frame size)";
                    }
                    else
                    {
                        var result = DensityMeasure.EvaluateRuler(box, DensityNegotiator.RulerDips, dpi);
                        mw = result.MeasuredWidthPx;
                        mh = result.MeasuredHeightPx;
                        rulerOk = result.Ok;
                        note = result.Detail;
                    }
                }
                return new WindowDensityReport(dpi, awareness, window, client, DensityNegotiator.RulerDips,
                    mw, mh, rulerOk, note, monitorBounds, monitorDpi, monitorInfo.Device);
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        }
        finally { Native.SetThreadDpiAwarenessContext(previousContext); }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!States.TryGetValue(hwnd, out var state))
            return DefWindowProc(hwnd, message, wParam, lParam);
        switch (message)
        {
            case WmPaint:
            {
                var paint = new PaintStruct { Reserved = new byte[32] };
                var hdc = BeginPaint(hwnd, ref paint);
                try
                {
                    if (Native.GetClientRect(hwnd, out var client))
                        FillRect(hdc, ref client, state.Magenta);
                    if (state.Ruler)
                    {
                        var dpi = (int)Native.GetDpiForWindow(hwnd);
                        var pixels = (int)Math.Round(DensityNegotiator.RulerDips * dpi / 96.0);
                        var offset = (int)Math.Round(50.0 * dpi / 96.0);
                        var box = new Native.Rect
                        {
                            Left = offset,
                            Top = offset,
                            Right = offset + pixels,
                            Bottom = offset + pixels,
                        };
                        FillRect(hdc, ref box, state.Green);
                    }
                }
                finally { EndPaint(hwnd, ref paint); }
                return IntPtr.Zero;
            }
            case WmEraseBkgnd:
                return new IntPtr(1);
            case WmDpiChanged:
            {
                var target = state.Target;
                Native.SetWindowPos(hwnd, HwndTopmost, target.X, target.Y,
                    target.Width, target.Height,
                    Native.SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }
            case WmDestroy:
            {
                States.TryRemove(hwnd, out _);
                if (state.Magenta != IntPtr.Zero) DeleteObject(state.Magenta);
                if (state.Green != IntPtr.Zero) DeleteObject(state.Green);
                return IntPtr.Zero;
            }
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}

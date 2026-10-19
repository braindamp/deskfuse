using System.Drawing;
using System.Windows.Forms;
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
/// The window is no-activate / tool-window so it never steals focus from what the user is doing.
/// </summary>
internal sealed class ProbeForm : Form
{
    private readonly Rectangle _target;
    private readonly bool _ruler;
    public ProbeForm(Rectangle target, bool ruler)
    {
        _target = target;
        _ruler = ruler;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(255, 0, 255);
        DoubleBuffered = true;
        Bounds = target;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }
    /// <summary>Windows may resize a window when it lands on a monitor with a different DPI; put it back.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Bounds = _target;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_ruler) return;
        // DeviceDpi is this window's own DPI. A PerMonitorV2 window is NOT scaled by Windows, so a
        // 100 DIP square must be drawn by us at 100 * dpi / 96 physical pixels.
        var px = (int)Math.Round(DensityNegotiatorConstants.RulerDips * DeviceDpi / 96.0);
        var offset = (int)Math.Round(50.0 * DeviceDpi / 96.0);
        using var brush = new SolidBrush(Color.FromArgb(0, 255, 0));
        e.Graphics.FillRectangle(brush, offset, offset, px, px);
    }
}
internal static class DensityNegotiatorConstants
{
    public const int RulerDips = DensityNegotiator.RulerDips;
}
internal static class ProbeWindow
{
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
    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
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
        var target = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using var form = new ProbeForm(target, drawRuler);
        form.Show();
        Pump(400);
        form.Bounds = target; // after the first DPI change has settled
        Pump(300);
        var hwnd = form.Handle;
        var dpi = (int)Native.GetDpiForWindow(hwnd);
        var awareness = DisplayConfigApi.DescribeAwareness(hwnd);
        Native.GetWindowRect(hwnd, out var wr);
        Native.GetClientRect(hwnd, out var cr);
        var window = new RectI(wr.Left, wr.Top, wr.Width, wr.Height);
        var client = new RectI(0, 0, cr.Width, cr.Height);
        int? mw = null, mh = null;
        bool? rulerOk = null;
        string? note = drawRuler ? null : "ruler not requested";
        if (drawRuler)
        {
            form.Invalidate();
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
                form.Invalidate();
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
        form.Close();
        return new WindowDensityReport(dpi, awareness, window, client, DensityNegotiator.RulerDips, mw, mh, rulerOk, note);
        }
        finally { Native.SetThreadDpiAwarenessContext(previousContext); }
    }
}

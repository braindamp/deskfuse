using System.Globalization;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Core.Display;
public sealed class NegotiatorOptions
{
    public int SettleTimeoutMs { get; init; } = 6000;
    public bool DrawRuler { get; init; }
    /// <summary>
    /// Diagnostic proof mode: keep the initially created 1:1 phone display even when
    /// Windows reports a lower scaling limit. Never destroy/recreate it at reduced
    /// pixels while attempting to measure the real DPI.
    /// </summary>
    public bool PreserveRequestedResolution { get; init; } = false;
    /// <summary>
    /// UNDOCUMENTED Windows DisplayConfig -4 operation. Never enabled by default.
    /// This test flag must not be enabled in unattended or production sessions.
    /// </summary>
    public bool AllowExperimentalScaleWrite { get; init; } = false;
    public int MaxRefreshHz { get; init; } = 120;
}
public sealed record NegotiationOutcome(
    MonitorInfo Monitor,
    DensityPlan Plan,
    DensityReadout Readout,
    ScaleCaps? Caps,
    WindowDensityReport? Probe,
    int EffectiveDpi,
    bool Native,
    DensityMethod Method,
    string? DegradedReason,
    EvidenceLog Evidence,
    int? RestoreScalePercent = null);
/// <summary>
/// The density ladder (FR-DPI-02..04). Every rung is followed by a READ-BACK; nothing is
/// assumed to have worked.
///
///   rung 0  IddCx MonitorScaleFactor     NOT AVAILABLE. It is only reachable through
///                                        IddCxAdapterDisplayConfigUpdate(2), which Microsoft
///                                        documents for REMOTE-session drivers. This driver is
///                                        console-session, so the hint cannot be sent. Recorded
///                                        as evidence, never attempted.
///   rung 1  EDID physical size           Windows derives its RECOMMENDED scale from it. We set
///                                        it from the phone's real diagonal, then read back what
///                                        Windows chose.
///   rung 2  per-monitor scale override   Undocumented DisplayConfigSetDeviceInfo (-4).
///                                        DISABLED unless explicitly opted in for a test PC;
///                                        requires full read-back of the outcome.
///   rung 3  honest degradation           If Windows will not reach the plan (cap, quantisation,
///                                        refusal), the session is labelled not-native with the
///                                        measured numbers. It is NEVER silently rescaled.
/// </summary>
public sealed class DensityNegotiator
{
    public const int RulerDips = 100;
    private readonly IDisplayBackend _backend;
    private readonly NegotiatorOptions _options;
    private readonly ILog _log;
    public DensityNegotiator(IDisplayBackend backend, NegotiatorOptions options, ILog log)
    {
        _backend = backend;
        _options = options;
        _log = log;
    }
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    public async Task<NegotiationOutcome> RunAsync(
        PhoneProfile p,
        Action<string, string>? progress,
        CancellationToken ct)
    {
        var ev = new EvidenceLog();
        ev.Info("iddcx-scale-api",
            "IddCx monitor-scale request (MonitorScaleFactor)",
            "not available to this driver",
            "The field exists but is only reachable via IddCxAdapterDisplayConfigUpdate(2), documented for remote-session drivers; " +
            "this is a console-session driver. Density is applied by the agent and verified below.");
        var problem = p.Validate();
        if (problem.Length > 0)
            throw new DisplayException("profile", "The phone's display profile is not usable: " + problem,
                "Send a diagnostics report from the phone.");
        if (p.PhysicalW % 2 != 0 || p.PhysicalH % 2 != 0)
            throw new DisplayException("profile",
                $"{p.PhysicalW}x{p.PhysicalH}: H.264 4:2:0 needs even pixel dimensions.",
                "This phone reports an odd resolution; it cannot be streamed 1:1.");
        var (mmW, mmH) = PhysicalSize.FromDiagonal(p.DiagonalMm, p.PhysicalW, p.PhysicalH);
        var refresh = Math.Min(p.MaxRefreshHz, _options.MaxRefreshHz);
        var serial = PhysicalSize.StableSerial(p.DeviceId);
        MonitorRequest Request(int w, int h) => new(w, h, refresh, mmW, mmH, serial, p.RequestedScalePercent);
        // ---- rung 1: create the monitor with the phone's real physical size -----------------
        progress?.Invoke("Creating the phone display",
            $"{p.PhysicalW}x{p.PhysicalH} px, {mmW}x{mmH} mm, {refresh} Hz");
        var monitor = await _backend.CreateMonitorAsync(Request(p.PhysicalW, p.PhysicalH), ct).ConfigureAwait(false);
        ev.Pass("monitor-active", "Phone monitor is active in Windows", null, $"{monitor.GdiName} {monitor.Bounds}");
        var caps = _backend.ReadScaleCaps(monitor);
        if (caps is null)
        {
            ev.Warn("windows-scale-caps", "Windows scale limits for this monitor", null, "unavailable",
                "The per-monitor scale query (undocumented DisplayConfigGetDeviceInfo type -3) failed or returned values outside the known step table. " +
                "The agent cannot set a scale and will report whatever Windows chose.");
        }
        else
        {
            ev.Info("windows-scale-caps", "Windows scale limits for this monitor",
                $"recommended {caps.RecommendedPercent}% (from the EDID physical size), current {caps.CurrentPercent}%, max {caps.MaxPercent}%");
        }
        var plan = DensityPolicy.Plan(p, caps);
        // ---- Windows capped the scale below what the phone needs ------------------------------
        if (plan.Kind == PlanKind.CappedReducedPixels && caps is not null)
        {
            if (_options.PreserveRequestedResolution)
            {
                // The original 1440x3120 display is already ACTIVE and verified.
                // Recreating it at 926x2006 to work around a scaling cap can select
                // an unintended 1920x1080 fallback mode on Windows 10. For proof,
                // leave the working monitor alone and measure Windows' real DPI.
                var cap = caps.MaxPercent;
                var keepReason = $"Windows reports a {cap}% scaling maximum for " +
                    $"{p.PhysicalW}x{p.PhysicalH} (phone requests {p.RequestedScalePercent}%). " +
                    "The DPI proof kept the original 1:1 monitor; no resolution fallback " +
                    "was attempted. Native density is not established.";
                plan = DensityPolicy.FallbackWiderLayout(p, caps) with { Reason = keepReason };
                ev.Warn("cap-vs-pixels", "Preserve requested pixels during DPI proof",
                    $"{p.PhysicalW}x{p.PhysicalH}",
                    $"{monitor.Bounds.Width}x{monitor.Bounds.Height}", keepReason);
                progress?.Invoke("DPI proof - preserving the working monitor",
                    $"{p.PhysicalW}x{p.PhysicalH} kept; Windows reports max {cap}%, requested {p.RequestedScalePercent}%. No second monitor creation.");
            }
            else
            {
                progress?.Invoke("Windows limits this display's scale",
                    $"Trying {plan.PixelW}x{plan.PixelH} px so the {F(plan.CanvasDpW)}x{F(plan.CanvasDpH)} dp layout still fits");
                monitor = await _backend.CreateMonitorAsync(Request(plan.PixelW, plan.PixelH), ct).ConfigureAwait(false);
                var reducedCaps = _backend.ReadScaleCaps(monitor);
                if (reducedCaps is not null && reducedCaps.MaxPercent >= plan.ScalePercent)
                {
                    caps = reducedCaps;
                    ev.Info("cap-vs-pixels", "Scale cap at reduced pixel count",
                        $"cap {reducedCaps.MaxPercent}% at {plan.PixelW}x{plan.PixelH} (needed {plan.ScalePercent}%)",
                        "Shrinking the pixel count kept the phone-sized layout reachable.");
                }
                else
                {
                    ev.Warn("cap-vs-pixels", "Scale cap at reduced pixel count",
                        $"cap >= {plan.ScalePercent}%", reducedCaps is null ? "unreadable" : $"cap {reducedCaps.MaxPercent}%",
                        "The cap fell with the pixel count, so it is a floor on the LOGICAL desktop size, not a pixel limit: no pixel count makes a phone-sized layout reachable.");
                    monitor = await _backend.CreateMonitorAsync(Request(p.PhysicalW, p.PhysicalH), ct).ConfigureAwait(false);
                    caps = _backend.ReadScaleCaps(monitor) ?? caps;
                    plan = DensityPolicy.FallbackWiderLayout(p, caps);
                }
            }
        }
        ev.Info("plan", "Density plan",
            $"{plan.Kind}: {plan.PixelW}x{plan.PixelH} px at {plan.ScalePercent}% = {plan.PlannedDpi} DPI " +
            $"(phone wants {p.ExpectedDpi} DPI); layout {F(plan.CanvasDpW)}x{F(plan.CanvasDpH)} dp; 14 dp reference token = {F(plan.ApparentTokenDp)} dp",
            plan.Reason);
        // ---- rung 2: apply, only if Windows' own choice differs ------------------------------
        DensityMethod method;
        if (caps is null)
        {
            method = DensityMethod.AlreadyCorrect; // corrected below if the read-back disagrees
            ev.Warn("scale-applied", "Per-monitor scale applied", $"{plan.ScalePercent}%", "not attempted",
                "No scale limits could be read, so no override was sent.");
        }
        else if (caps.CurrentPercent == plan.ScalePercent)
        {
            method = DensityMethod.AlreadyCorrect;
            ev.Pass("scale-applied", "Per-monitor scale applied", $"{plan.ScalePercent}%", $"{caps.CurrentPercent}% (already in force)",
                "Windows' own recommendation from the EDID physical size already matched the plan.");
        }
        else if (!_options.AllowExperimentalScaleWrite)
        {
            method = DensityMethod.FallbackScaled;
            ev.Warn("scale-applied", "Per-monitor scale applied", $"{plan.ScalePercent}%",
                $"{caps.CurrentPercent}% (unchanged)",
                "Experimental DisplayConfig scale writes are OFF. Change scale manually in Windows Settings " +
                "for this phone monitor, or rerun a disposable test with --experimental-scale-write.");
        }
        else
        {
            ev.Warn("experimental-scale-write", "Undocumented Windows API explicitly enabled", null,
                "DisplayConfigSetDeviceInfo type -4 (experimental)",
                "This may alter Windows' remembered scale settings for the virtual monitor.");
            progress?.Invoke("Setting the display scale", $"{plan.ScalePercent}% ({plan.PlannedDpi} DPI)");
            var set = _backend.TrySetScale(monitor, plan.ScalePercent);
            method = set.Ok ? DensityMethod.PerMonitorOverride : DensityMethod.FallbackScaled;
            if (set.Ok)
                ev.Pass("scale-applied", "Per-monitor scale applied", $"{plan.ScalePercent}%", "accepted", set.Detail);
            else
                ev.Fail("scale-applied", "Per-monitor scale applied", $"{plan.ScalePercent}%", "refused", set.Detail);
        }
        // ---- read back what Windows is ACTUALLY using ----------------------------------------
        progress?.Invoke("Verifying density", $"Waiting for Windows to apply {plan.PlannedDpi} DPI");
        var readout = await SettleAsync(monitor, plan.PlannedDpi, ct).ConfigureAwait(false);
        var dpiMatch = Math.Abs(readout.MonitorDpiX - plan.PlannedDpi) <= 1 &&
                       Math.Abs(readout.MonitorDpiY - plan.PlannedDpi) <= 1;
        if (dpiMatch)
            ev.Pass("monitor-dpi", "Monitor effective DPI (GetDpiForMonitor)", $"{plan.PlannedDpi}", $"{readout.MonitorDpiX}");
        else
            ev.Fail("monitor-dpi", "Monitor effective DPI (GetDpiForMonitor)", $"{plan.PlannedDpi}", $"{readout.MonitorDpiX}x{readout.MonitorDpiY}",
                "Windows is not rendering this monitor at the planned density. This is reported, not rescaled.");
        if (!dpiMatch) method = DensityMethod.FallbackScaled;
        // ---- a real window on that monitor ----------------------------------------------------
        progress?.Invoke("Verifying density", "Opening a test window on the phone display");
        var probe = await _backend.ProbeWindowAsync(monitor, _options.DrawRuler, ct).ConfigureAwait(false);
        // No probe is not proof. A failed or missing 100-DIP ruler must never be called native.
        var windowMatch = false;
        // Native density requires a measured physical ruler, not just an API DPI
        // value. A skipped ruler is unverified, never a successful density proof.
        var rulerOk = false;
        if (probe is null)
        {
            ev.Fail("window-dpi", "Window DPI (GetDpiForWindow)",
                "a real PerMonitorV2 probe window", "not available",
                "No verified probe window. The session cannot claim native-density rendering.");
        }
        else
        {
            // GetDpiForWindow on our PMv2 probe is the authoritative application-DPI
            // assertion. GetDpiForMonitor is a secondary observation (not recommended
            // as a PMv2-thread API); neither an EDID nor a scale hint is proof.
            windowMatch = Math.Abs(probe.WindowDpi - plan.PlannedDpi) <= 1 &&
                          probe.Awareness == "PerMonitorV2";
            if (windowMatch)
                ev.Pass("window-dpi", "PMv2 probe DPI (GetDpiForWindow)", $"{plan.PlannedDpi}", $"{probe.WindowDpi}", probe.Awareness);
            else
                ev.Fail("window-dpi", "PMv2 probe DPI (GetDpiForWindow)", $"{plan.PlannedDpi}", $"{probe.WindowDpi}",
                    $"{probe.Awareness}: requested app DPI not demonstrated (legacy apps may be bitmap scaled).");
            var fullClient = probe.Client.Width == monitor.Bounds.Width &&
                             probe.Client.Height == monitor.Bounds.Height;
            if (fullClient)
                ev.Pass("probe-client-size", "Borderless window client pixels", $"{monitor.Bounds.Width}x{monitor.Bounds.Height}",
                    $"{probe.Client.Width}x{probe.Client.Height}");
            else
            {
                windowMatch = false;
                ev.Fail("probe-client-size", "Borderless window client pixels", $"{monitor.Bounds.Width}x{monitor.Bounds.Height}",
                    $"{probe.Client.Width}x{probe.Client.Height}",
                    "Client does not fill phone target; no resizing is allowed to hide this.");
            }
            if (probe.RulerOk is { } ok)
            {
                rulerOk = ok;
                var expectedPx = (int)Math.Round(probe.RulerDips * probe.WindowDpi / 96.0);
                var measured = $"{probe.MeasuredRulerWidthPx}x{probe.MeasuredRulerHeightPx}";
                if (ok)
                    ev.Pass("ruler", $"Rendered pixels: a {probe.RulerDips} DIP square, measured in the captured frame", $"{expectedPx} px", measured, probe.Note);
                else
                    ev.Fail("ruler", $"Rendered pixels: a {probe.RulerDips} DIP square, measured in the captured frame", $"{expectedPx} px", measured, probe.Note);
            }
            else if (_options.DrawRuler)
            {
                rulerOk = false;
                ev.Fail("ruler", "Rendered pixels (ruler)", "measured 100-DIP square",
                    "not measured", probe.Note ?? "frame unavailable");
            }
            else
            {
                ev.Skip("ruler", "Rendered pixels (ruler)", probe.Note ?? "ruler probe not requested");
            }
        }
        var canvasW = plan.PixelW * 96.0 / readout.MonitorDpiX;
        var expectedCanvasW = p.PhysicalW / (double)p.Dpr;
        if (Math.Abs(canvasW - expectedCanvasW) <= expectedCanvasW * 0.015)
            ev.Pass("canvas-dp", "Logical canvas width (dp)", F(expectedCanvasW), F(canvasW));
        else
            ev.Warn("canvas-dp", "Logical canvas width (dp)", F(expectedCanvasW), F(canvasW),
                "Apps will lay out in a canvas that differs from the phone's own dp width.");
        if (plan.PixelW == p.PhysicalW && plan.PixelH == p.PhysicalH)
            ev.Pass("pixels-1to1", "Stream pixels equal the phone's panel", $"{p.PhysicalW}x{p.PhysicalH}", $"{plan.PixelW}x{plan.PixelH}");
        else
            ev.Warn("pixels-1to1", "Stream pixels equal the phone's panel", $"{p.PhysicalW}x{p.PhysicalH}", $"{plan.PixelW}x{plan.PixelH}",
                "The phone will upscale the stream, so it is not 1:1.");
        var native = plan.WouldBeNative && dpiMatch && windowMatch && rulerOk;
        var reasons = new List<string>();
        if (plan.Reason is { Length: > 0 }) reasons.Add(plan.Reason);
        if (!dpiMatch) reasons.Add($"Windows applied {readout.MonitorDpiX} DPI, not the planned {plan.PlannedDpi} DPI.");
        if (!windowMatch && probe is not null)
            reasons.Add($"PMv2 probe reported {probe.WindowDpi} DPI (requested {plan.PlannedDpi}); " +
                $"awareness: {probe.Awareness}; client " +
                $"{probe.Client.Width}x{probe.Client.Height} (target {monitor.Bounds.Width}x{monitor.Bounds.Height}).");
        if (!rulerOk)
            reasons.Add(probe?.Note is { Length: > 0 } note
                ? note : "100-DIP pixel-ruler readback was not verified.");
        var reason = reasons.Count == 0 ? null : string.Join(" ", reasons);
        _log.Info(native
            ? $"density verified: {readout.MonitorDpiX} DPI, {plan.PixelW}x{plan.PixelH} 1:1"
            : $"density NOT native: {reason}");
        return new NegotiationOutcome(
            monitor, plan, readout, caps, probe, readout.MonitorDpiX, native, method, reason, ev);
    }
    private async Task<DensityReadout> SettleAsync(MonitorInfo monitor, int targetDpi, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + _options.SettleTimeoutMs;
        DensityReadout last;
        while (true)
        {
            last = _backend.ReadDensity(monitor);
            if (Math.Abs(last.MonitorDpiX - targetDpi) <= 1) return last;
            if (Environment.TickCount64 >= deadline) return last;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}

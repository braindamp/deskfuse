using System.Collections.Concurrent;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Display;

/// <summary>
/// Calibrates a backing IddCx desktop for the actual phone DPR. A DPI range
/// (-3) is used only to SEARCH; an applied PMv2 DPI plus a 100-DIP ruler in a
/// real, unscaled phone-sized captured crop is mandatory to CERTIFY a mode.
///
/// Never assumes the proposed 600-DIP Windows threshold is a fact. The exact
/// even-pixel cutoff is discovered from the real Windows scale-cap query.
/// </summary>
public sealed class AdaptiveViewportCalibration
{
    // Matches FocusShared.h (driver protocol unchanged).
    public const int MaxPixels = 8_294_400;
    public const int MaxDimension = 4095;
    private static readonly ConcurrentDictionary<string, int> VerifiedWidths = new();
    /// <summary>
    /// Reuse the last proven width on later reconnects within the host process.
    /// Cached widths are NEVER trusted without a fresh DPI + pixel ruler proof.
    /// </summary>
    public bool RememberVerifiedWidth { get; init; } = false;
    private readonly IDisplayBackend _backend;
    private readonly IPhysicalDisplayGuard _guard;
    private readonly DisplaySnapshot _before;
    private readonly ILog _log;
    /// <summary>Production settle delays; driver-free unit tests use zero.</summary>
    public int InitialSettlingMs { get; init; } = 750;
    public int RetrySettlingMs { get; init; } = 1100;

    public AdaptiveViewportCalibration(IDisplayBackend backend, IPhysicalDisplayGuard guard,
        DisplaySnapshot before, ILog log)
    {
        _backend = backend;
        _guard = guard;
        _before = before;
        _log = log;
    }

    public static int? ExactWindowsStep(PhoneProfile phone)
    {
        // Android reports density every time the profile is sent; NEVER invent it.
        // Nonstandard densities (e.g. 2.625) aren't equal to Windows' scale steps.
        if (!float.IsFinite(phone.Dpr) || phone.Dpr is < 1f or > 5f) return null;
        var wanted = 100.0 * phone.Dpr;
        var rounded = (int)Math.Round(wanted);
        return WindowsScale.IndexOf(rounded) >= 0 && Math.Abs(wanted - rounded) < 0.05
            ? rounded : null;
    }

    public static int MaxEligibleWidth(int height)
    {
        if (height is <= 0 or > MaxDimension) return 0;
        return Math.Min(MaxDimension, MaxPixels / height) & ~1;
    }

    public static bool CanUseBacking(int width, int height, int phoneW, int phoneH) =>
        width >= phoneW && height >= phoneH && width > 0 && height > 0 &&
        width <= MaxDimension && height <= MaxDimension &&
        ((width | height) & 1) == 0 && (long)width * height <= MaxPixels;

    /// <summary>
    /// Returns a pixel-certified phone-sized viewport, or null for the old honest
    /// density path. OS/driver/display-safety failures are thrown, never disguised
    /// as an ordinary candidate rejection.
    /// </summary>
    public async Task<NegotiationOutcome?> TryRunAsync(PhoneProfile phone,
        Action<string, string>? progress, CancellationToken ct)
    {
        var errors = phone.Validate();
        if (errors.Length != 0)
            throw new DisplayException("profile", "Invalid phone density profile: " + errors);
        var percent = ExactWindowsStep(phone);
        if (percent is null || (phone.PhysicalW & 1) != 0 || (phone.PhysicalH & 1) != 0)
            return null;

        var requestedDpi = WindowsScale.DpiFor(percent.Value);
        if (Math.Abs(requestedDpi - phone.ExpectedDpi) > 1) return null;
        // A portrait phone usually needs extra width; after rotation the
        // restrictive axis may be height. 600/480 DIPs are search hints only.
        var expandWidth = phone.Dpr * 600.0 - phone.PhysicalW >=
                          phone.Dpr * 480.0 - phone.PhysicalH;
        var phoneExtent = expandWidth ? phone.PhysicalW : phone.PhysicalH;
        var fixedExtent = expandWidth ? phone.PhysicalH : phone.PhysicalW;
        var extentMax = MaxEligibleWidth(fixedExtent);
        if (extentMax < phoneExtent ||
            !CanUseBacking(phone.PhysicalW, phone.PhysicalH, phone.PhysicalW, phone.PhysicalH))
            return null;

        var refresh = Math.Min(phone.MaxRefreshHz, 120);
        var serial = PhysicalSize.StableSerial(phone.DeviceId);
        MonitorInfo? lastMonitor = null;
        int? lastExtent = null;
        ScaleCaps? lastCaps = null;

        (int Width, int Height) Backing(int extent) =>
            expandWidth ? (extent, phone.PhysicalH) : (phone.PhysicalW, extent);

        async Task<ScaleCaps?> AtExtent(int extent)
        {
            ct.ThrowIfCancellationRequested();
            var (width, height) = Backing(extent);
            if (!CanUseBacking(width, height, phone.PhysicalW, phone.PhysicalH))
                return null;
            if (lastExtent == extent && lastCaps is not null) return lastCaps;
            progress?.Invoke("Calibrating display density",
                $"Testing {width}x{height} px at {percent}%");
            var (mmW, mmH) = PhysicalSize.FromDiagonal(phone.DiagonalMm,
                width, height);
            lastMonitor = await _backend.CreateMonitorAsync(
                new MonitorRequest(width, height, refresh, mmW, mmH,
                    serial, percent.Value), ct).ConfigureAwait(false);
            lastExtent = extent;
            lastCaps = _backend.ReadScaleCaps(lastMonitor);
            var safety = _guard.Verify(_before);
            if (!safety.Clean)
                throw new DisplayException("physical-display-guard",
                    "Physical monitors changed during DPI calibration: " +
                    string.Join("; ", safety.Drift));
            return lastCaps;
        }

        var cacheKey = $"{Environment.OSVersion.Version}:{phone.DeviceId}:" +
            $"{phone.PhysicalW}x{phone.PhysicalH}:{percent}:{phone.DiagonalMm}";
        var candidate = 0;
        if (RememberVerifiedWidth &&
            VerifiedWidths.TryGetValue(cacheKey, out var remembered) &&
            CanUseBacking(Backing(remembered).Width, Backing(remembered).Height,
                phone.PhysicalW, phone.PhysicalH))
        {
            // One monitor instead of ~10 during reconnect. Still reverify
            // DPI, scale cap, ruler and physical displays before streaming.
            candidate = remembered;
            _log.Info($"adaptive DPI reusing verified {candidate} px " +
                (expandWidth ? "width" : "height"));
        }
        if (candidate == 0)
        {
            // Windows' reported cap is a cheap eligibility test only.
            // Never infer rendered DPI from the cap (see 2240 observation).
            var initial = await AtExtent(phoneExtent).ConfigureAwait(false);
            if (initial is null) return null;
            if (initial.MaxPercent >= percent.Value)
            {
                candidate = phoneExtent;
            }
            else
            {
                // 600-DIP is a SEARCH SEED, not a Windows rule.
                var hintDips = expandWidth ? 600.0 : 480.0;
                var estimated = Math.Max(phoneExtent + 2,
                    (int)Math.Ceiling(hintDips * percent.Value / 100.0 / 2.0) * 2);
                var upper = Math.Min(extentMax, estimated);
                var upperCaps = await AtExtent(upper).ConfigureAwait(false);
                if (upperCaps is null || upperCaps.MaxPercent < percent.Value)
                {
                    upper = extentMax;
                    upperCaps = await AtExtent(upper).ConfigureAwait(false);
                }
                if (upperCaps is null || upperCaps.MaxPercent < percent.Value)
                    return null;

                var lower = phoneExtent;
                while (upper - lower > 2)
                {
                    var mid = ((lower + upper) / 4) * 2;
                    if (mid <= lower) mid = lower + 2;
                    var caps = await AtExtent(mid).ConfigureAwait(false);
                    if (caps is { MaxPercent: var max } && max >= percent.Value)
                        upper = mid;
                    else
                        lower = mid;
                }
                candidate = upper;
            }
        }

        // Some Windows 10 transitions return 336 monitor DPI with a 96-DPI
        // window briefly (observed at 2240). Recreate the PMv2 window for
        // several bounded attempts rather than accepting either reading alone.
        // The exact cap boundary can be eligible but transient; at most 3
        // subsequent widths are tested before falling back honestly.
        for (var offset = 0; offset <= 6; offset += 2)
        {
            var extent = candidate + offset;
            if (extent > extentMax) break;
            var caps = await AtExtent(extent).ConfigureAwait(false);
            var (width, height) = Backing(extent);
            if (caps is null || caps.MaxPercent < percent.Value) continue;
            var monitor = lastMonitor!;
            var beforeScale = caps.CurrentPercent;
            var attemptedScale = false;
            var succeeded = false;
            try
            {
                if (beforeScale != percent.Value)
                {
                    attemptedScale = true;
                    var set = _backend.TrySetScale(monitor, percent.Value);
                    _log.Info($"adaptive DPI {width}x{height} -4: {set.Detail}");
                    if (!set.Ok) continue;
                }

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(attempt == 1 ? InitialSettlingMs : RetrySettlingMs, ct).ConfigureAwait(false);
                    var actualCaps = _backend.ReadScaleCaps(monitor);
                    var readback = _backend.ReadDensity(monitor);
                    // Independent crop verifies the exact 1440x3120 phone view.
                    // For native-size modes this is the full display.
                    var viewport = new RectI(monitor.Bounds.X, monitor.Bounds.Y,
                        phone.PhysicalW, phone.PhysicalH);
                    var probe = await _backend.ProbeViewportAsync(monitor,
                        viewport, drawRuler: true, ct: ct).ConfigureAwait(false);
                    var expectedRulerPx = (int)Math.Round(100.0 * requestedDpi / 96);
                    var ruler = probe is { RulerOk: true } &&
                        Math.Abs((probe.MeasuredRulerWidthPx ?? 0) - expectedRulerPx) <= 3 &&
                        Math.Abs((probe.MeasuredRulerHeightPx ?? 0) - expectedRulerPx) <= 3;
                    var verified = actualCaps?.CurrentPercent == percent.Value &&
                        Math.Abs(readback.MonitorDpiX - requestedDpi) <= 1 &&
                        Math.Abs(readback.MonitorDpiY - requestedDpi) <= 1 &&
                        probe is { Awareness: "PerMonitorV2" } &&
                        Math.Abs(probe.WindowDpi - requestedDpi) <= 1 &&
                        probe.Client.Width == phone.PhysicalW &&
                        probe.Client.Height == phone.PhysicalH &&
                        ruler;
                    _log.Info($"adaptive DPI {width}x{height} attempt {attempt}: " +
                        $"caps current={actualCaps?.CurrentPercent}, monitor={readback.MonitorDpiX}, " +
                        $"PMv2={probe?.WindowDpi} [{probe?.Awareness}], " +
                        $"ruler={probe?.MeasuredRulerWidthPx}x{probe?.MeasuredRulerHeightPx}, " +
                        $"verified={verified}");
                    if (!verified) continue;
                    var physical = _guard.Verify(_before);
                    if (!physical.Clean)
                        throw new DisplayException("physical-display-guard",
                            "Physical monitors changed during DPI proof: " +
                            string.Join("; ", physical.Drift));
                    succeeded = true;
                    if (RememberVerifiedWidth) VerifiedWidths[cacheKey] = extent;

                    var ev = new EvidenceLog();
                    ev.Pass("adaptive-cutoff", "Minimum eligible backing axis extent",
                        $"{extent} px {(expandWidth ? "width" : "height")} candidate at {percent}%",
                        $"{extent} px", 
                        "Measured -3 cap boundary; each session rechecks rendered DPI and pixels.");
                    ev.Pass("adaptive-dpi", "True phone density in captured 1:1 viewport",
                        $"{requestedDpi} DPI; {expectedRulerPx}px ruler",
                        $"{probe!.WindowDpi} DPI; {probe.MeasuredRulerWidthPx}x{probe.MeasuredRulerHeightPx}px");
                    ev.Pass("adaptive-viewport", "Native phone frame without resampling",
                        $"{phone.PhysicalW}x{phone.PhysicalH} px",
                        $"{viewport.Width}x{viewport.Height} px at backing {width}x{height}");
                    var crop = width > phone.PhysicalW || height > phone.PhysicalH;
                    var plan = new DensityPlan(crop ? PlanKind.VerifiedViewport : PlanKind.Exact,
                        phone.PhysicalW, phone.PhysicalH, percent.Value, percent.Value,
                        true, phone.PhysicalW * 100.0 / percent.Value,
                        phone.PhysicalH * 100.0 / percent.Value,
                        DensityPolicy.TokenDp(percent.Value, phone.Dpr),
                        crop ? $"Verified high-density {width}x{height} backing desktop " +
                               $"with an exact {phone.PhysicalW}x{phone.PhysicalH} phone viewport." : null);
                    progress?.Invoke("Display density verified",
                        $"{requestedDpi} DPI, {width}x{height} backing, " +
                        $"{phone.PhysicalW}x{phone.PhysicalH} visible");
                    return new NegotiationOutcome(monitor, plan, readback, actualCaps,
                        probe, readback.MonitorDpiX, true,
                        beforeScale == percent.Value ? DensityMethod.AlreadyCorrect :
                        DensityMethod.PerMonitorOverride, null, ev,
                        RestoreScalePercent: attemptedScale ? beforeScale : null);
                }
                _log.Warn($"adaptive DPI {width}x{height}: available according to Windows, " +
                    "but real PMv2/ruler test failed; no native claim");
            }
            finally
            {
                if (!succeeded && attemptedScale)
                {
                    var restore = _backend.TrySetScale(monitor, beforeScale);
                    var after = _backend.ReadScaleCaps(monitor);
                    if (!restore.Ok || after?.CurrentPercent != beforeScale)
                        throw new DisplayException("scale-restore",
                            $"Could not verify original virtual-display scale {beforeScale}% " +
                            $"after failed calibration ({restore.Detail}).");
                }
            }
        }
        if (RememberVerifiedWidth) VerifiedWidths.TryRemove(cacheKey, out _);
        return null;
    }
}

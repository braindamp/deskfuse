using System.Globalization;
using System.Text;
using System.Text.Json;
using FocusWindow.Agent.Display;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Agent;

/// <summary>
/// FA/BB hardware experiment. No PnP installation, driver source modification,
/// registry editing, physical display scale SET, or production-session changes.
/// Only the FocusIdd monitor that this process creates is ever a scale-SET target.
/// Running this on the user's PC requires explicit opt-in; normal invocation only
/// prints the proposed modes and safety constraints.
/// </summary>
internal static class DprChallengeLab
{
    private const int TargetScale = 350;
    private const int PhoneWidth = 1440;
    private const int PhoneHeight = 3120;
    private const int PixelBudget = 8_294_400; // FocusShared.h FW_MAX_PIXELS, unchanged
    private const string DefaultModes = "1440x3120,1600x3120,1920x3120,2100x3120,2240x3120,2240x3360";

    private sealed record Mode(int Width, int Height)
    {
        public override string ToString() => $"{Width}x{Height}";
    }

    private sealed record Observation
    {
        public string Mode { get; init; } = "";
        public int RefreshHz { get; init; }
        public string SerialStrategy { get; init; } = "";
        public int EdidDiagonalMm { get; init; }
        public int Predicted640Max { get; init; }
        public int Predicted600Max { get; init; }
        public int? RecommendedPercent { get; set; }
        public int? InitialPercent { get; set; }
        public int? ReportedMaximumPercent { get; set; }
        public int? MonitorDpiX { get; set; }
        public int? WindowDpi { get; set; }
        public string? WindowAwareness { get; set; }
        public int? RulerWidthPx { get; set; }
        public int? RulerHeightPx { get; set; }
        public List<string> DpiProbeAttempts { get; init; } = new();
        public bool? RulerMatchesReadback { get; set; }
        public string WriteAttempt { get; set; } = "NOT_REQUESTED";
        public string WriteApiResult { get; set; } = "NOT_REQUESTED";
        public string WriteVerdict { get; set; } = "NOT_ATTEMPTED";
        public string RestoreResult { get; set; } = "NOT_NEEDED";
        public string CropVerdict { get; set; } = "NOT_TESTED";
        public int? CropWindowDpi { get; set; }
        public int? CropRulerWidthPx { get; set; }
        public int? CropRulerHeightPx { get; set; }
        public bool PhysicalDisplaysUnchanged { get; set; }
        public bool DriverMonitorRemoved { get; set; }
        public string? Error { get; set; }
    }

    private static string? Option(string[] args, string name) => CliArguments.Value(args, name);

    private static List<Mode> ParseModes(string? text)
    {
        var requested = (text ?? DefaultModes).Split(',', StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length is < 1 or > 10)
            throw new ArgumentException("Specify 1–10 comma-separated modes.");
        var modes = new List<Mode>();
        foreach (var item in requested)
        {
            var parts = item.Split('x', 'X');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
                width < 320 || height < 320 || width > 4095 || height > 4095 ||
                (long)width * height > PixelBudget ||
                (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException($"Mode '{item}' exceeds the unchanged FocusIdd pixel/dimension budget or is invalid.");
            var candidate = new Mode(width, height);
            if (!modes.Contains(candidate)) modes.Add(candidate);
        }
        return modes;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            return await ExecuteAsync(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
                                  or DisplayException or OperationCanceledException)
        {
            Console.Error.WriteLine("DPR challenge refused/aborted: " + ex.Message);
            return 2;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var modes = ParseModes(Option(args, "--modes"));
        var diag = int.TryParse(Option(args, "--diag-mm"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : 170;
        if (diag is < 60 or > 500)
            throw new ArgumentException("--diag-mm must be 60-500.");
        var refresh = int.TryParse(Option(args, "--refresh"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsedRefresh) ? parsedRefresh : 60;
        if (refresh is < 30 or > 240)
            throw new ArgumentException("--refresh must be 30-240 Hz.");
        var sameSerial = args.Contains("--same-serial");
        var serialOverride = uint.TryParse(Option(args, "--serial"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsedSerial) ? parsedSerial : (uint?)null;
        if (serialOverride == 0)
            throw new ArgumentException("--serial must be a nonzero 32-bit EDID serial.");
        var settleMs = int.TryParse(Option(args, "--settle-ms"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsedSettle) ? parsedSettle : 900;
        if (settleMs is < 0 or > 10000)
            throw new ArgumentException("--settle-ms must be 0-10000.");
        var run = args.Contains("--run");
        var try350 = args.Contains("--try-350");
        var beyondCap = args.Contains("--try-above-cap");
        if (beyondCap && (!try350 || !args.Contains("--i-accept-unsupported-scale-risk")))
            throw new ArgumentException(
                "--try-above-cap requires --try-350 and --i-accept-unsupported-scale-risk.");
        if (run && !args.Contains("--i-accept-virtual-display-test"))
            throw new ArgumentException("--run requires --i-accept-virtual-display-test.");
        if (run && !Environment.UserInteractive)
            throw new InvalidOperationException("Hardware lab requires an interactive Windows logon.");

        Console.WriteLine("DeskFuse DPR challenge — FA diagnostic + BB oversized viewport");
        Console.WriteLine("This is a hardware EXPERIMENT, not an assertion of the Windows 640-DIP rule.");
        Console.WriteLine("Driver install/uninstall, PnP, registry, physical display SET: NEVER performed.");
        foreach (var m in modes)
            Console.WriteLine($"  {m}: 640-DIP hypothesis max " +
                $"{DprChallengeTheory.PredictedMaximum(m.Width, m.Height, 640)}%, " +
                $"600-DIP hypothesis max {DprChallengeTheory.PredictedMaximum(m.Width, m.Height, 600)}%");
        Console.WriteLine($"  Mode timing: {refresh} Hz; serial strategy: " +
            (serialOverride is { } fixedSerial
                ? $"FIXED serial {fixedSerial} (host-identity regression)"
                : sameSerial
                    ? "SAME serial across modes (mode-switch regression)"
                    : "DISTINCT serial per mode (new monitor each time)") +
            $"; inter-mode settle: {settleMs} ms");
        Console.WriteLine("  Requested target: genuine 336 DPI; 350 physical pixels for a 100-DIP square.");
        Console.WriteLine("  Independent PMv2 witness: tools/df-density-readonly.cpp, run while a mode is attached.");
        Console.WriteLine("  A full 1440x3120 GPU/NVENC crop is NOT implemented by this lab.");
        if (!run)
        {
            Console.WriteLine("PLAN ONLY. To change only the virtual monitor, add " +
                "--run --i-accept-virtual-display-test.");
            Console.WriteLine("For legal -4 writes add --try-350; for FA's out-of-cap experiment " +
                "also add --try-above-cap --i-accept-unsupported-scale-risk.");
            return 0;
        }

        var outDir = Option(args, "--out") ??
            Path.Combine(Environment.CurrentDirectory, "dpr-challenge-evidence");
        Directory.CreateDirectory(outDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonFile = Path.Combine(outDir, $"DPR_CHALLENGE_{stamp}.json");
        var csvFile = Path.Combine(outDir, $"DPR_CHALLENGE_{stamp}.csv");
        var observations = new List<Observation>();
        string? failure = null;

        using var log = new ConsoleLog();
        await using var backend = new WindowsDisplayBackend(log);
        // Read first, and REFUSE an existing virtual monitor. CreateMonitorAsync
        // would otherwise remove it, which is unsafe if a production session owns it.
        var initialHealth = backend.ReadDriverHealth();
        if (!initialHealth.Reachable || initialHealth.ProtocolVersion != DriverContract.ProtocolVersion)
            throw new DisplayException("preflight", "Installed FocusIdd is absent or protocol does not match.");
        if (initialHealth.MonitorArrived)
            throw new DisplayException("preflight",
                "FocusIdd is already displaying a monitor; close its existing session first.");

        var guard = new PhysicalDisplayGuard(() => backend.PhoneGdiName);
        var before = guard.Snapshot();
        if (before.Monitors.Count == 0 || before.Monitors.Any(m => m.Dpi <= 0))
            throw new DisplayException("preflight",
                "Cannot independently verify DPI for every physical monitor; refusing display changes.");

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatStop.IsCancellationRequested)
                {
                    var health = backend.ReadDriverHealth(); // IOCTL renews driver's 10-s timeout
                    if (!health.Reachable) throw new IOException("FocusIdd heartbeat lost");
                    await Task.Delay(1000, heartbeatStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Driver keepalive failure: " + ex.Message);
                heartbeatStop.Cancel();
            }
        }, CancellationToken.None);

        try
        {
            foreach (var mode in modes)
            {
                heartbeatStop.Token.ThrowIfCancellationRequested();
                var row = new Observation
                {
                    Mode = mode.ToString(),
                    RefreshHz = refresh,
                    SerialStrategy = serialOverride is { } rowSerial
                        ? $"FIXED:{rowSerial}"
                        : sameSerial ? "SAME" : "DISTINCT_PER_MODE",
                    EdidDiagonalMm = diag,
                    Predicted640Max = DprChallengeTheory.PredictedMaximum(mode.Width, mode.Height, 640),
                    Predicted600Max = DprChallengeTheory.PredictedMaximum(mode.Width, mode.Height, 600)
                };
                observations.Add(row);
                MonitorInfo? monitor = null;
                ScaleCaps? originalCaps = null;
                var wroteScale = false;
                Console.WriteLine($"\n=== Measuring FocusIdd {mode} ===");
                try
                {
                    var (mmW, mmH) = PhysicalSize.FromDiagonal(diag, mode.Width, mode.Height);
                    var serial = serialOverride ??
                        (sameSerial
                            ? PhysicalSize.StableSerial("DeskFuse.DprChallenge.SameSerial")
                            : PhysicalSize.StableSerial("DeskFuse.DprChallenge." + mode));
                    var request = new MonitorRequest(mode.Width, mode.Height, refresh, mmW, mmH,
                        serial, TargetScale);
                    monitor = await backend.CreateMonitorAsync(request, heartbeatStop.Token)
                        .ConfigureAwait(false);
                    CheckPhysical(guard, before, row, "arrival");
                    originalCaps = backend.ReadScaleCaps(monitor);
                    if (originalCaps is null)
                        throw new IOException("Undocumented -3 DPI range was unreadable; no SET will be attempted.");
                    row.RecommendedPercent = originalCaps.RecommendedPercent;
                    row.InitialPercent = originalCaps.CurrentPercent;
                    row.ReportedMaximumPercent = originalCaps.MaxPercent;

                    var wantSet = try350 && (originalCaps.Allows(TargetScale) ||
                        (beyondCap && mode.Width == PhoneWidth && mode.Height == PhoneHeight));
                    if (wantSet && originalCaps.CurrentPercent != TargetScale)
                    {
                        row.WriteAttempt = originalCaps.Allows(TargetScale)
                            ? "WITHIN_REPORTED_CAP" : "ABOVE_REPORTED_CAP_UNDOCUMENTED";
                        // Mark intent BEFORE the call; an exception must still trigger restoration.
                        wroteScale = true;
                        var result = backend.TrySetChallengeScale(monitor, TargetScale,
                            allowBeyondMaximum: row.WriteAttempt == "ABOVE_REPORTED_CAP_UNDOCUMENTED");
                        row.WriteApiResult = result.Detail;
                        await Task.Delay(1500, heartbeatStop.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        row.WriteAttempt = wantSet ? "ALREADY_350" :
                            (try350 ? "NOT_ALLOWED_BY_CAP" : "READ_ONLY");
                    }

                    WindowDensityReport? window = null;
                    for (var witness = 1; witness <= 3; witness++)
                    {
                        var currentDensity = backend.ReadDensity(monitor);
                        row.MonitorDpiX = currentDensity.MonitorDpiX;
                        window = await backend.ProbeWindowAsync(monitor, true,
                            heartbeatStop.Token).ConfigureAwait(false);
                        if (window is null) throw new IOException("PMv2 ruler window unavailable");
                        var note = $"witness {witness}: monitor={currentDensity.MonitorDpiX} DPI, " +
                            $"window={window.WindowDpi} DPI, awareness={window.Awareness}, " +
                            $"bounds={window.Window}, client={window.Client}, " +
                            $"ruler={window.MeasuredRulerWidthPx}x{window.MeasuredRulerHeightPx}, " +
                            $"ruler-ok={window.RulerOk}";
                        row.DpiProbeAttempts.Add(note);
                        Console.WriteLine($"  {note}");
                        if (window.Awareness == "PerMonitorV2" &&
                            currentDensity.MonitorDpiX == window.WindowDpi &&
                            window.RulerOk == true)
                            break;
                        if (witness < 3) await Task.Delay(900, heartbeatStop.Token);
                    }
                    row.WindowDpi = window!.WindowDpi;
                    row.WindowAwareness = window.Awareness;
                    row.RulerWidthPx = window.MeasuredRulerWidthPx;
                    row.RulerHeightPx = window.MeasuredRulerHeightPx;
                    row.RulerMatchesReadback = window.RulerOk;

                    var ruler350 = window.RulerOk == true &&
                        window.MeasuredRulerWidthPx is >= 345 and <= 355 &&
                        window.MeasuredRulerHeightPx is >= 345 and <= 355;
                    var observed350 = window.Awareness == "PerMonitorV2" &&
                        window.WindowDpi >= 336 && ruler350 &&
                        row.MonitorDpiX >= 336;
                    row.WriteVerdict = wroteScale
                        ? !row.WriteApiResult.Contains("win32-return=0", StringComparison.Ordinal)
                            ? "REJECTED"
                            : observed350 ? "APPLIED_AND_PIXEL_VERIFIED" : "ACCEPTED_BUT_WINDOW_RENDERING_UNVERIFIED"
                        : observed350 ? "ALREADY_AT_350_VERIFIED" : "NOT_APPLIED";

                    // BB's actual viewport proof: an independent 1440x3120 borderless
                    // window on the larger monitor. Verify its green ruler in a
                    // 1:1 CPU-copied crop of a real driver frame.
                    if (mode.Width >= 2240 && mode.Height >= PhoneHeight)
                    {
                        var crop = await backend.ProbeCropViewportAsync(monitor, PhoneWidth,
                            PhoneHeight, heartbeatStop.Token).ConfigureAwait(false);
                        row.CropWindowDpi = crop?.WindowDpi;
                        row.CropRulerWidthPx = crop?.MeasuredRulerWidthPx;
                        row.CropRulerHeightPx = crop?.MeasuredRulerHeightPx;
                        row.CropVerdict = crop is { Awareness: "PerMonitorV2", RulerOk: true,
                            WindowDpi: >= 336, MeasuredRulerWidthPx: >= 345 and <= 355,
                            MeasuredRulerHeightPx: >= 345 and <= 355 }
                            ? "336_DPI_IN_1440x3120_1TO1_CPU_CROP"
                            : "NOT_VERIFIED_OR_NOT_350";
                    }
                    CheckPhysical(guard, before, row, "measurement");
                    Console.WriteLine($"  -3 recommended/current/max: " +
                        $"{row.RecommendedPercent}/{originalCaps.CurrentPercent}/{row.ReportedMaximumPercent}%");
                    Console.WriteLine($"  independent observations: monitor={row.MonitorDpiX} DPI; " +
                        $"PMv2={row.WindowDpi} DPI; ruler={row.RulerWidthPx}x{row.RulerHeightPx}px");
                    Console.WriteLine($"  -4: {row.WriteApiResult}; verdict={row.WriteVerdict}");
                    Console.WriteLine($"  BB viewport: {row.CropVerdict}");
                }
                catch (Exception ex)
                {
                    row.Error = ex.ToString();
                    failure = $"{mode}: {ex.Message}";
                    Console.Error.WriteLine("ABORTING sweep: " + failure);
                }
                finally
                {
                    try
                    {
                        if (wroteScale && monitor is not null && originalCaps is not null)
                        {
                            // Try even after a nonzero -4 return: don't assume Windows
                            // left its per-monitor configuration untouched.
                            var restored = backend.TrySetChallengeScale(monitor,
                                originalCaps.CurrentPercent, allowBeyondMaximum: false);
                            await Task.Delay(900, CancellationToken.None).ConfigureAwait(false);
                            var check = backend.ReadScaleCaps(monitor);
                            row.RestoreResult = restored.Ok &&
                                check?.CurrentPercent == originalCaps.CurrentPercent
                                ? "RESTORED_AND_READ_BACK"
                                : "RESTORE_FAILED_OR_UNVERIFIED: " + restored.Detail +
                                  $"; readback={(check is null ? "missing" : check.CurrentPercent.ToString(CultureInfo.InvariantCulture))}";
                            if (row.RestoreResult != "RESTORED_AND_READ_BACK")
                                failure ??= $"{mode}: original virtual monitor scale not restored";
                        }
                    }
                    catch (Exception ex)
                    {
                        row.RestoreResult = "RESTORE_EXCEPTION: " + ex.Message;
                        failure ??= $"{mode}: restore exception";
                    }
                    try
                    {
                        await backend.RemoveMonitorAsync(CancellationToken.None).ConfigureAwait(false);
                        if (settleMs > 0)
                            await Task.Delay(settleMs, CancellationToken.None).ConfigureAwait(false);
                        row.DriverMonitorRemoved = !backend.ReadDriverHealth().MonitorArrived;
                        if (!row.DriverMonitorRemoved)
                            failure ??= $"{mode}: virtual monitor did not depart";
                        var physical = guard.Verify(before);
                        row.PhysicalDisplaysUnchanged = physical.Clean;
                        if (!physical.Clean)
                            failure ??= $"{mode}: physical monitor drift: {string.Join("; ", physical.Drift)}";
                    }
                    catch (Exception ex)
                    {
                        row.DriverMonitorRemoved = false;
                        row.PhysicalDisplaysUnchanged = false;
                        failure ??= $"{mode}: monitor teardown failed: {ex.Message}";
                    }
                }
                if (failure is not null) break;
            }
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* teardown still runs in backend.DisposeAsync */ }
            // The records are written even when the run was aborted early.
            var output = new
            {
                StartedFrom = "DeskFuse v0.3 FA+BB test, not a shipping DPI implementation",
                TimestampUtc = DateTimeOffset.UtcNow,
                WindowsBuild = Environment.OSVersion.Version.ToString(),
                InitialPhysicalMonitors = before.Monitors,
                RefreshHz = refresh,
                SerialStrategy = serialOverride is { } outputSerial
                    ? $"FIXED:{outputSerial}"
                    : sameSerial ? "SAME" : "DISTINCT_PER_MODE",
                OptInWrite = try350,
                OptInAboveCap = beyondCap,
                Failure = failure,
                Observations = observations
            };
            await File.WriteAllTextAsync(jsonFile, JsonSerializer.Serialize(output,
                new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None)
                .ConfigureAwait(false);
            var csv = new StringBuilder();
            csv.AppendLine("mode,refresh_hz,serial_strategy,diag_mm,predicted_640_max,predicted_600_max,recommended,initial,max,monitor_dpi,window_dpi,ruler_w,ruler_h,write_verdict,crop_verdict,restored,physical_unchanged,monitor_removed,error");
            foreach (var o in observations)
            {
                string Escape(object? v) => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"";
                csv.AppendLine(string.Join(",", new object?[] { o.Mode, o.RefreshHz,
                    o.SerialStrategy, o.EdidDiagonalMm,
                    o.Predicted640Max, o.Predicted600Max, o.RecommendedPercent, o.InitialPercent,
                    o.ReportedMaximumPercent, o.MonitorDpiX, o.WindowDpi, o.RulerWidthPx,
                    o.RulerHeightPx, o.WriteVerdict, o.CropVerdict, o.RestoreResult,
                    o.PhysicalDisplaysUnchanged, o.DriverMonitorRemoved, o.Error }.Select(Escape)));
            }
            await File.WriteAllTextAsync(csvFile, csv.ToString(), CancellationToken.None)
                .ConfigureAwait(false);
            Console.WriteLine($"\nDPR evidence: {jsonFile}\n              {csvFile}");
            Console.WriteLine(failure is null ? "SWEEP COMPLETED — inspect measured rows; no theory marked proven automatically."
                                              : "SWEEP INCOMPLETE/UNSAFE — " + failure);
        }
        return failure is null ? 0 : 2;
    }

    private static void CheckPhysical(PhysicalDisplayGuard guard, DisplaySnapshot original,
        Observation row, string stage)
    {
        var comparison = guard.Verify(original);
        if (comparison.Clean) return;
        row.PhysicalDisplaysUnchanged = false;
        throw new IOException($"{stage}: physical monitor changed: {string.Join("; ", comparison.Drift)}");
    }
}

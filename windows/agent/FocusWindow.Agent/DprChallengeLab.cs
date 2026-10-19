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
        public int EdidDiagonalMm { get; init; }
        public int Predicted640Max { get; init; }
        public int Predicted600Max { get; init; }
        public int? RecommendedPercent { get; set; }
        public int? InitialPercent { get; set; }
        public int? PostWritePercent { get; set; }
        public string? NativeWitnessOutput { get; set; }
        public string NativeRulerVerdict { get; set; } = "NOT_RUN";
        public int? NativeRulerProcessDpi { get; set; }
        public int? NativeRulerWidthPx { get; set; }
        public int? NativeRulerHeightPx { get; set; }
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

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1] : null;
    }

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
            throw new ArgumentException("--diag-mm must be 60–500.");
        var run = args.Contains("--run");
        var try350 = args.Contains("--try-350");
        var beyondCap = args.Contains("--try-above-cap");
        var nativeWitnessExe = Option(args, "--native-witness");
        if (nativeWitnessExe is not null && !File.Exists(nativeWitnessExe))
            throw new ArgumentException("--native-witness must identify an existing executable.");
        var settleMs = int.TryParse(Option(args, "--settle-ms"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var requestedSettle) ? requestedSettle : 1500;
        if (settleMs < 500 || settleMs > 30000)
            throw new ArgumentException("--settle-ms must be 500–30000.");
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
        Console.WriteLine("  Requested target: genuine 336 DPI; 350 physical pixels for a 100-DIP square.");
        Console.WriteLine($"  Post-write wait: {settleMs} ms (change with --settle-ms 10000).");
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
                    var request = new MonitorRequest(mode.Width, mode.Height, 60, mmW, mmH,
                        PhysicalSize.StableSerial("DeskFuse.DprChallenge." + mode), TargetScale);
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
                        await Task.Delay(settleMs, heartbeatStop.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        row.WriteAttempt = wantSet ? "ALREADY_350" :
                            (try350 ? "NOT_ALLOWED_BY_CAP" : "READ_ONLY");
                    }

                    var postSetCaps = backend.ReadScaleCaps(monitor);
                    row.PostWritePercent = postSetCaps?.CurrentPercent;
                    Console.WriteLine($"  -3 readback after settle: " +
                        (postSetCaps is null ? "UNAVAILABLE" :
                            $"recommended={postSetCaps.RecommendedPercent}% current={postSetCaps.CurrentPercent}% max={postSetCaps.MaxPercent}%"));
                    if (nativeWitnessExe is not null)
                    {
                        row.NativeWitnessOutput = await RunNativeWitnessAsync(nativeWitnessExe,
                            heartbeatStop.Token).ConfigureAwait(false);
                        Console.WriteLine("  === independent native Win32 process witness ===");
                        Console.WriteLine(row.NativeWitnessOutput);
                        Console.WriteLine("  === end independent witness ===");
                        var nativeRuler = await RunNativeRulerWitnessAsync(nativeWitnessExe,
                            backend, monitor, heartbeatStop.Token).ConfigureAwait(false);
                        row.NativeRulerProcessDpi = nativeRuler.Dpi;
                        row.NativeRulerWidthPx = nativeRuler.Width;
                        row.NativeRulerHeightPx = nativeRuler.Height;
                        var nativeRulerMatchesDpi = nativeRuler.Dpi is > 0 &&
                            nativeRuler.Width is not null && nativeRuler.Height is not null &&
                            Math.Abs(nativeRuler.Width.Value -
                                Math.Round(100.0 * nativeRuler.Dpi.Value / 96.0)) <= 2 &&
                            Math.Abs(nativeRuler.Height.Value -
                                Math.Round(100.0 * nativeRuler.Dpi.Value / 96.0)) <= 2;
                        row.NativeRulerVerdict = nativeRulerMatchesDpi
                            ? nativeRuler.Dpi == 336 ? "336_DPI_NATIVE_PIXEL_VERIFIED"
                                : $"NATIVE_PIXEL_VERIFIED_BUT_{nativeRuler.Dpi}_DPI"
                            : "NATIVE_RULER_NOT_VERIFIED";
                        Console.WriteLine("  native captured ruler: " + nativeRuler.Detail);
                        Console.WriteLine("  native ruler verdict: " + row.NativeRulerVerdict);
                    }
                    WindowDensityReport? window = null;
                    for (var witness = 1; witness <= 3; witness++)
                    {
                        var currentDensity = backend.ReadDensity(monitor);
                        var witnessCaps = backend.ReadScaleCaps(monitor);
                        row.MonitorDpiX = currentDensity.MonitorDpiX;
                        window = await backend.ProbeWindowAsync(monitor, true,
                            heartbeatStop.Token).ConfigureAwait(false);
                        if (window is null) throw new IOException("PMv2 ruler window unavailable");
                        var note = $"witness {witness}: -3-current={witnessCaps?.CurrentPercent.ToString() ?? "UNAVAILABLE"}%, " +
                            $"monitor={currentDensity.MonitorDpiX} DPI, " +
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
                    var observed350 = (window.Awareness == "PerMonitorV2" &&
                        window.WindowDpi >= 336 && ruler350 &&
                        row.MonitorDpiX >= 336) ||
                        row.NativeRulerVerdict == "336_DPI_NATIVE_PIXEL_VERIFIED";
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
                    Console.WriteLine($"  -3 initial recommended/current/max: " +
                        $"{row.RecommendedPercent}/{originalCaps.CurrentPercent}/{row.ReportedMaximumPercent}%; " +
                        $"post-set current={row.PostWritePercent?.ToString() ?? "UNAVAILABLE"}%");
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
                        await Task.Delay(900, CancellationToken.None).ConfigureAwait(false);
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
                OptInWrite = try350,
                OptInAboveCap = beyondCap,
                Failure = failure,
                Observations = observations
            };
            await File.WriteAllTextAsync(jsonFile, JsonSerializer.Serialize(output,
                new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None)
                .ConfigureAwait(false);
            var csv = new StringBuilder();
            csv.AppendLine("mode,diag_mm,predicted_640_max,predicted_600_max,recommended,initial,post_set,max,monitor_dpi,window_dpi,ruler_w,ruler_h,native_process_dpi,native_ruler_w,native_ruler_h,native_ruler_verdict,write_verdict,crop_verdict,restored,physical_unchanged,monitor_removed,error");
            foreach (var o in observations)
            {
                string Escape(object? v) => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"";
                csv.AppendLine(string.Join(",", new object?[] { o.Mode, o.EdidDiagonalMm,
                    o.Predicted640Max, o.Predicted600Max, o.RecommendedPercent, o.InitialPercent,
                    o.PostWritePercent, o.ReportedMaximumPercent, o.MonitorDpiX, o.WindowDpi, o.RulerWidthPx,
                    o.RulerHeightPx, o.NativeRulerProcessDpi, o.NativeRulerWidthPx,
                    o.NativeRulerHeightPx, o.NativeRulerVerdict, o.WriteVerdict,
                    o.CropVerdict, o.RestoreResult,
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

    private static async Task<(string Detail, int? Dpi, int? Width, int? Height)>
        RunNativeRulerWitnessAsync(string exe, WindowsDisplayBackend backend, MonitorInfo monitor,
            CancellationToken ct)
    {
        using var child = new System.Diagnostics.Process();
        child.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.GetFullPath(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        child.StartInfo.ArgumentList.Add("--ruler-window");
        child.StartInfo.ArgumentList.Add(monitor.GdiName);
        child.StartInfo.ArgumentList.Add("--hold-ms");
        child.StartInfo.ArgumentList.Add("6000");
        try
        {
            if (!child.Start()) return ("native ruler process could not start", null, null, null);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await Task.Delay(500, ct).ConfigureAwait(false);
            var box = await backend.MeasureExternalNativeRulerAsync(monitor, ct)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                return ("native ruler process timed out or was cancelled", null, null, null);
            }
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var parsed = System.Text.RegularExpressions.Regex.Match(output, @"\bdpi=(\d+)\b");
            int? dpi = parsed.Success &&
                       int.TryParse(parsed.Groups[1].Value, CultureInfo.InvariantCulture, out var v)
                ? v : null;
            if (child.ExitCode != 0) return ($"native exit={child.ExitCode}: {error}", null, null, null);
            return ($"{output.Trim()} captured=" +
                (box is null ? "NO_GREEN_RULER_IN_DRIVER_FRAME" :
                    $"{box.Value.Width}x{box.Value.Height}px in FocusIdd raw BGRA"),
                dpi, box?.Width, box?.Height);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return ("native ruler witness failed: " + ex.Message, null, null, null);
        }
        finally
        {
            if (child.StartInfo is not null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> RunNativeWitnessAsync(string exe, CancellationToken ct)
    {
        using var child = new System.Diagnostics.Process();
        child.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.GetFullPath(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            if (!child.Start()) return "NATIVE_WITNESS_START_FAILED";
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limited.CancelAfter(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(limited.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                return "NATIVE_WITNESS_TIMEOUT_OR_CANCELLED";
            }
            return $"exit={child.ExitCode}\n{await stdout.ConfigureAwait(false)}\n" +
                   $"stderr: {await stderr.ConfigureAwait(false)}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return "NATIVE_WITNESS_FAILED: " + ex.Message;
        }
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

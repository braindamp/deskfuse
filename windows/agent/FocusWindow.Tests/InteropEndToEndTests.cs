using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using Xunit;
using Xunit.Abstractions;
namespace FocusWindow.Tests;
[CollectionDefinition("cpu", DisableParallelization = true)]
public class CpuCollection { }
public sealed class CapturingApproval : IApprovalPolicy
{
    public bool Allow = true;
    public string? Code;
    public string? DeviceId;
    public string? Label;
    public Task<bool> ApproveAsync(string deviceId, string deviceLabel, string verificationCode, CancellationToken ct)
    {
        DeviceId = deviceId; Label = deviceLabel; Code = verificationCode;
        return Task.FromResult(Allow);
    }
}
/// <summary>Paints a known picture: flat background, a white 100x100 square, and a moving blue band.</summary>
public sealed class FrameArtist
{
    public const int Band = 24;
    private readonly int _w, _h;
    private readonly byte[] _base, _work;
    private int _prevY = -1;
    public FrameArtist(int w, int h)
    {
        _w = w; _h = h;
        _base = TestFrames.Make(w, h, 20, 120, 220, squareX: 50, squareY: 50, square: 100);
        _work = (byte[])_base.Clone();
    }
    public static int BandY(int index, int h) => (index * 37) % (h - Band);
    public byte[] Render(int index)
    {
        if (_prevY >= 0) Buffer.BlockCopy(_base, _prevY * _w * 4, _work, _prevY * _w * 4, Band * _w * 4);
        var y0 = BandY(index, _h);
        for (var y = y0; y < y0 + Band; y++)
            for (var x = 300; x < _w; x++)
            {
                var o = (y * _w + x) * 4;
                _work[o] = 255; _work[o + 1] = 0; _work[o + 2] = 0;
            }
        _prevY = y0;
        return _work;
    }
}
/// <summary>
/// The whole pipe, with nothing mocked that could hide a protocol bug:
///   synthetic "driver" writes frames into the seqlock surface (same layout as FocusShared.h)
///   -> production SurfaceReader/SurfaceFrameSource
///   -> production SessionHost/PhoneSession (negotiation, density evidence, pacing, resync)
///   -> real ffmpeg x264 encode at 1440x3120
///   -> production crypto + framing over a real TCP socket
///   -> an INDEPENDENT phone client (Node, written from the Kotlin sources, with its own crypto)
///   -> the received H.264 is decoded by ffmpeg and measured pixel by pixel.
/// What is NOT in the loop: Windows itself (the display backend is a scripted fake), the IddCx driver,
/// and a real Android decoder. Those are listed as unverified in windows/EVIDENCE.md.
/// </summary>
[Collection("cpu")]
public class InteropEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public InteropEndToEndTests(ITestOutputHelper o) => _out = o;
    private sealed record Rig(
        FakeDisplayBackend Backend, RecordingInputSink Input, StaticGuard Guard,
        CapturingApproval Approval, ListLog Log, SessionHost Host, PairingOffer Offer, Task Sessions,
        CancellationTokenSource Cts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Sessions.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            Host.Dispose();
            Cts.Dispose();
        }
    }
    private async Task<Rig> StartRig(Action<FakeDisplayBackend>? configure = null, bool approve = true, bool ruler = true, bool singleSession = true, TimeSpan? rigLifetime = null)
    {
        var backend = new FakeDisplayBackend(maxW: 3200, maxH: 3200);
        configure?.Invoke(backend);
        var input = new RecordingInputSink();
        var guard = new StaticGuard();
        var approval = new CapturingApproval { Allow = approve };
        var log = new ListLog();
        var options = new HostOptions
        {
            Port = 0, Bind = "127.0.0.1", Fps = 30, BitrateKbps = 15000, MaxRefreshHz = 120,
            FfmpegPath = ToolLocator.Ffmpeg!, HostName = "TestPC", SingleSession = singleSession, DrawRulerProbe = ruler,
        };
        var services = new HostServices(() => backend, () => input, () => guard, approval, new MemoryPairingStore());
        var host = new SessionHost(options, services, log);
        var cts = new CancellationTokenSource(rigLifetime ?? TimeSpan.FromMinutes(3));
        var offer = await host.StartAsync(cts.Token);
        var sessions = Task.Run(() => host.RunSessionsAsync(cts.Token));
        return new Rig(backend, input, guard, approval, log, host, offer, sessions, cts);
    }
    /// <summary>Publishes moving frames at the size of whatever monitor the host last created, then one FINAL frame, then goes idle.</summary>
    private static Task<(int W, int H, int Index, int Count, DateTimeOffset FinalAt)> Publish(FakeDisplayBackend be, TimeSpan runFor, int intervalMs, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var artists = new Dictionary<(int, int), FrameArtist>();
            var end = DateTime.UtcNow + runFor;
            var i = 0;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                var req = be.LastRequest;
                if (req is null) { await Task.Delay(20, ct); continue; }
                if (!artists.TryGetValue((req.Width, req.Height), out var artist))
                    artists[(req.Width, req.Height)] = artist = new FrameArtist(req.Width, req.Height);
                be.Surface.Publish(artist.Render(i), req.Width, req.Height);
                i++;
                await Task.Delay(intervalMs, ct);
            }
            // The FINAL frame: after this the desktop is idle (no more frames are published).
            // On cancellation (a failed soak) skip it: the surface may be about to be freed.
            var finalReq = be.LastRequest!;
            var finalIndex = i + 1000;
            if (!ct.IsCancellationRequested)
            {
                if (!artists.TryGetValue((finalReq.Width, finalReq.Height), out var finalArtist))
                    artists[(finalReq.Width, finalReq.Height)] = finalArtist = new FrameArtist(finalReq.Width, finalReq.Height);
                be.Surface.Publish(finalArtist.Render(finalIndex), finalReq.Width, finalReq.Height);
            }
            return (finalReq.Width, finalReq.Height, finalIndex, i + 1, DateTimeOffset.UtcNow);
        }, ct);
    private async Task<(JsonObject Report, string OutDir, string ConsoleOut)> RunPhone(Rig rig, string extraArgs, string? outName = null, TimeSpan? timeout = null)
    {
        var node = ToolLocator.Node!;
        var script = Path.Combine(AppContext.BaseDirectory, "interop", "phone-client.mjs");
        var outDir = Path.Combine(Path.GetTempPath(), "fw-e2e-" + (outName ?? Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(outDir);
        var (code, output) = await ToolLocator.RunAsync(node,
            $"\"{script}\" --host 127.0.0.1 --port {rig.Host.Port} --out \"{outDir}\" {extraArgs}",
            timeout ?? TimeSpan.FromMinutes(2),
            new Dictionary<string, string> { ["FOCUS_URI"] = rig.Offer.Uri });
        _out.WriteLine("node: " + output.Trim());
        Assert.True(code == 0, $"phone client exited {code}: {output}");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "report.json")))!.AsObject();
        return (report, outDir, output);
    }
    private static int Int(JsonNode? n) => n!.GetValue<int>();
    [HistoricalCpuSession]
    public async Task Full_pipeline_at_1440x3120_from_driver_surface_to_an_independent_phone_client()
    {
        await using var rig = await StartRig();
        using var publishCts = new CancellationTokenSource();
        var publisher = Publish(rig.Backend, TimeSpan.FromSeconds(9), 66, publishCts.Token);
        var (r, dir, _) = await RunPhone(rig,
            // The idle window must outlast the publisher's final frame. A shorter
            // window made the client leave just before the final picture arrived,
            // so the pixel comparison below was racing the test's own publisher.
            "--frames 24 --keyframe-at 8 --rotate-at 14 --inputs --until-idle-ms 4000");
        var (finalW, finalH, finalIndex, _, _) = await publisher;
        _out.WriteLine($"publisher final frame index {finalIndex}");
        _out.WriteLine("host log:\n" + string.Join("\n", rig.Log.Lines));
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        // ---- the protocol was spoken correctly, as an independent implementation sees it ----
        _out.WriteLine("violations: " + r["violations"]!.ToJsonString());
        Assert.Empty(r["violations"]!.AsArray());
        Assert.True((bool)r["handshake"]!["hostKeyMatchesQr"]!);
        Assert.Equal(rig.Approval.Code, (string)r["handshake"]!["code"]!);   // both sides show the SAME 6 digits
        Assert.Equal(3, Int(r["hello"]!["protocolVersion"]));
        Assert.Equal("TestPC", (string)r["hello"]!["hostName"]!);
        Assert.Contains(r["stages"]!.AsArray(), s => ((string)s!["stage"]!).StartsWith("Waiting for approval"));
        Assert.Equal("node-test-device", rig.Approval.DeviceId);
        // ---- the Target the phone was told about: the density contract, on the wire ----
        var t1 = r["targets"]![0]!;
        Assert.Equal((1, 1440, 3120, 336), (Int(t1["version"]), Int(t1["widthPx"]), Int(t1["heightPx"]), Int(t1["dpi"])));
        Assert.Equal((411, 891), (Int(t1["canvasDpW"]), Int(t1["canvasDpH"])));
        Assert.True((bool)t1["native"]!);
        Assert.Equal("already_correct", (string)t1["densityMethod"]!);
        Assert.Equal(("h264", "YUV420", "canvas"), ((string)t1["codec"]!, (string)t1["pixelFormat"]!, (string)t1["occupant"]!));
        Assert.Equal("shared_view", (string)t1["stateModel"]!);
        // ---- frames: keyframe-first, one stream per target version, resync on request ----
        var v1 = r["frames"]!["perVersion"]!["1"]!;
        Assert.True((bool)v1["firstIsKeyframe"]!);
        Assert.True(Int(v1["frames"]) >= 8);
        Assert.True((bool)r["keyframeRequest"]!["satisfied"]!, "the host must answer a keyframe request with a new keyframe");
        Assert.True(Int(v1["keyframes"]) >= 2, "the restart after the request must have produced a second keyframe");
        // ---- rotation re-targets the monitor and opens version 2 with a keyframe ----
        var t2 = r["rotation"]!["target"]!;
        Assert.Equal((2, 3120, 1440), (Int(t2["version"]), Int(t2["widthPx"]), Int(t2["heightPx"])));
        Assert.Equal(336, Int(t2["dpi"]));
        var v2 = r["frames"]!["perVersion"]!["2"]!;
        Assert.True((bool)v2["firstIsKeyframe"]!);
        Assert.True(rig.Backend.Created.Count >= 2);
        Assert.Equal((3120, 1440), (rig.Backend.Created[^1].Width, rig.Backend.Created[^1].Height));
        // ---- a quiet desktop must still be pinged (the client times out after 20 s of silence) ----
        Assert.True(Int(r["pings"]) >= 1, "no idle ping reached the phone");
        Assert.True(rig.Backend.Heartbeats >= 3, "the driver watchdog heartbeat was not sent");
        // ---- input arrived with the right geometry and the stylus data intact ----
        Assert.True(rig.Input.Events.Count >= 8, $"only {rig.Input.Events.Count} input events reached the sink");
        var down = rig.Input.Events.First(e => e.Event.Kind == "pointer" && e.Event.Action == "down");
        Assert.Equal((100, 200, "left"), (down.Event.X, down.Event.Y, down.Event.Button));
        Assert.Equal((1440, 3120), (down.Geometry.StreamWidth, down.Geometry.StreamHeight));
        var pen = rig.Input.Events.First(e => e.Event.IsStylus);
        Assert.Equal(0.6f, pen.Event.Pressure, 3);
        Assert.Equal(12.5f, pen.Event.TiltX, 3);
        Assert.Equal(-3.0f, pen.Event.TiltY, 3);
        Assert.Contains(rig.Input.Events, e => e.Event.Kind == "key" && e.Event.AndroidKeyCode == 29 && e.Event.Modifiers == Modifiers.Ctrl);
        Assert.Contains(rig.Input.Events, e => e.Event.Kind == "text" && e.Event.Text == "héllo");
        // ---- teardown restored everything ----
        Assert.True(rig.Backend.RemoveCalls >= 1, "the phone monitor was never removed");
        Assert.True(rig.Input.Released >= 1, "held keys/buttons were not released");
        Assert.Contains(rig.Log.Lines, l => l.Contains("restore check: physical displays unchanged"));
        // ---- decode what the PHONE received and measure PIXELS ----
        var v1Count = Int(v1["frames"]);
        var (n1, first1, _) = await FfmpegEncoderTests.DecodeAsync(Path.Combine(dir, "stream-v1.h264"), 1440, 3120);
        _out.WriteLine($"v1: phone received {v1Count} frames, decoded {n1} at 1440x3120");
        Assert.Equal(v1Count, n1);
        Assert.True(DensityMeasure.TryFindColorBox(first1, 1440, 3120, 255, 255, 255, 40, out var sq));
        _out.WriteLine($"100x100 px square arrived as {sq}");
        Assert.InRange(sq.Width, 98, 102);
        Assert.InRange(sq.X, 49, 51);
        var v2Count = Int(v2["frames"]);
        var (n2, _, last2) = await FfmpegEncoderTests.DecodeAsync(Path.Combine(dir, "stream-v2.h264"), 3120, 1440);
        _out.WriteLine($"v2: phone received {v2Count} frames, decoded {n2} at 3120x1440");
        Assert.Equal(v2Count, n2);
        Assert.Equal((3120, 1440), (finalW, finalH));
        // IDLE FLUSH: the last frame the phone shows must be the FINAL frame the PC drew, even though
        // nothing was published afterwards to push it through the encoder pipe.
        Assert.True(DensityMeasure.TryFindColorBox(last2, 3120, 1440, 255, 0, 0, 60, out var band));
        var expectedY = FrameArtist.BandY(finalIndex, 1440);
        _out.WriteLine($"final blue band decoded at y={band.Y} (expected {expectedY}), height {band.Height}");
        Assert.InRange(band.Y, expectedY - 2, expectedY + 2);
        Assert.InRange(band.Height, FrameArtist.Band - 3, FrameArtist.Band + 3);
    }
    [HistoricalCpuSession]
    public async Task A_peer_without_the_pairing_secret_is_rejected_and_the_real_phone_can_still_connect_afterwards()
    {
        await using var rig = await StartRig();
        using var pub = new CancellationTokenSource();
        var (attacker, _, _) = await RunPhone(rig, "--wrong-secret");
        Assert.True((bool)attacker["authRejected"]!);
        Assert.Null(attacker["hello"]?.GetValue<object>());
        Assert.Empty(rig.Backend.Created);                      // no monitor was created for a stranger
        Assert.Null(rig.Approval.Code);                         // and nobody was asked to approve it
        var publisher = Publish(rig.Backend, TimeSpan.FromSeconds(3), 100, pub.Token);
        var (legit, _, _) = await RunPhone(rig, "--frames 3 --until-idle-ms 1500");
        await publisher;
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(legit["violations"]!.AsArray());
        Assert.True(Int(legit["frames"]!["accepted"]) >= 3);
        Assert.Contains(rig.Log.Lines, l => l.Contains("rejected"));
    }
    [FactIfNode]
    public async Task A_declined_approval_gives_the_phone_a_named_fatal_stage_and_creates_no_monitor()
    {
        await using var rig = await StartRig(approve: false);
        var (r, _, _) = await RunPhone(rig, "--frames 1");
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("approval", (string)r["fatalStage"]!["stage"]!);
        Assert.NotNull(r["fatalStage"]!["remediation"]);
        Assert.Empty(rig.Backend.Created);
    }
    [FactIfNode]
    public async Task A_protocol_mismatch_is_a_named_fatal_stage_not_a_silent_hang()
    {
        await using var rig = await StartRig();
        var (r, _, _) = await RunPhone(rig, "--protocol 2 --frames 1");
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("version", (string)r["fatalStage"]!["stage"]!);
        Assert.Equal(UserFacing.For("version"), (string)r["fatalStage"]!["detail"]!);
        Assert.Contains("protocol 2", (string)r["fatalStage"]!["technical"]!);
        Assert.Empty(rig.Backend.Created);
    }
    [HistoricalCpuSession]
    public async Task A_dpi_virtualised_window_reaches_the_phone_as_not_native_with_the_reason()
    {
        await using var rig = await StartRig(b => { b.ProbeWindowDpi = 96; b.ProbeAwareness = "Unaware (DPI-virtualised)"; });
        using var pub = new CancellationTokenSource();
        var publisher = Publish(rig.Backend, TimeSpan.FromSeconds(3), 100, pub.Token);
        var (r, _, _) = await RunPhone(rig, "--frames 3 --until-idle-ms 1500");
        await publisher;
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        var t = r["targets"]![0]!;
        Assert.False((bool)t["native"]!);
        Assert.Equal(336, Int(t["dpi"]));                       // the MONITOR density is still reported truthfully
        Assert.Contains("Unaware", (string)t["degradedReason"]!);
        Assert.Empty(r["violations"]!.AsArray());               // and it is still valid wire protocol
    }
    [HistoricalCpuSession]
    public async Task A_windows_scale_cap_reaches_the_phone_as_a_wider_layout_with_the_real_numbers()
    {
        await using var rig = await StartRig(b =>
        {
            b.RecommendedPercent = 250;
            b.MaxPercentFor = (w, _) => (int)(w / 5.76);        // a floor on the logical desktop size
        });
        using var pub = new CancellationTokenSource();
        var publisher = Publish(rig.Backend, TimeSpan.FromSeconds(3), 100, pub.Token);
        var (r, _, _) = await RunPhone(rig, "--frames 3 --until-idle-ms 1500");
        await publisher;
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        var t = r["targets"]![0]!;
        _out.WriteLine(t.ToJsonString());
        Assert.False((bool)t["native"]!);
        Assert.Equal((1440, 3120), (Int(t["widthPx"]), Int(t["heightPx"])));
        Assert.Equal(240, Int(t["dpi"]));                       // 250%: the highest Windows would give
        Assert.Equal((576, 1248), (Int(t["canvasDpW"]), Int(t["canvasDpH"]))); // 1440 px * 96 / 240 DPI = 576 dp, not the phone's 411
        // PreserveRequestedResolution deliberately keeps the 1:1 monitor and reports
        // the measured cap instead of silently recreating it at a smaller pixel count.
        Assert.Contains("250% scaling maximum", (string)t["degradedReason"]!);
        Assert.Contains("no resolution fallback", (string)t["degradedReason"]!);
        Assert.Empty(r["violations"]!.AsArray());
    }
    /// <summary>
    /// The SESSION_METRICS soak: the production pipeline (real session code, real ffmpeg,
    /// real TCP, independent Node client) run for FOCUS_SOAK_MINUTES and measured, not
    /// estimated. Excluded from the normal suite: without FOCUS_SOAK_MINUTES this test
    /// reports SKIPPED, never PASS. Software-path evidence only (synthetic driver surface,
    /// same machine, Node client, no Android MediaCodec) - it does not satisfy the E3
    /// 10-minute hardware gate, which needs the installed FocusIdd driver.
    /// </summary>
    [HistoricalCpuSession]
    public async Task Soak_measures_native_pixels_fps_bytes_and_publish_to_receive_latency()
    {
        var minutes = int.Parse(Environment.GetEnvironmentVariable("FOCUS_SOAK_MINUTES")!, CultureInfo.InvariantCulture);
        await using var rig = await StartRig(rigLifetime: TimeSpan.FromMinutes(minutes + 5));
        using var publishCts = new CancellationTokenSource();
        var publisher = Publish(rig.Backend, TimeSpan.FromMinutes(minutes), 33, publishCts.Token);
        JsonObject r;
        try
        {
            (r, _, _) = await RunPhone(rig,
                $"--frames 30 --keyframe-at 50 --inputs --until-idle-ms 5000 --timeout-ms {minutes * 60_000 + 120_000}",
                "soak",
                TimeSpan.FromMinutes(minutes + 3));
        }
        catch
        {
            // If the phone client fails, the publisher is still writing into the native
            // surface. Stop it and wait for it to leave BEFORE the rig frees the memory:
            // a live publisher over freed memory is an AccessViolation, not a test failure.
            publishCts.Cancel();
            try { await publisher; } catch (OperationCanceledException) { }
            throw;
        }
        var (w, h, _, publishedCount, finalAt) = await publisher;
        await rig.Sessions.WaitAsync(TimeSpan.FromSeconds(20));
        var durationMs = Int(r["durationMs"]!);
        var accepted = Int(r["frames"]!["accepted"]!);
        var total = Int(r["frames"]!["total"]!);
        var keyframes = Int(r["frames"]!["keyframes"]!);
        var bytes = (long)r["frames"]!["bytes"]!;
        var pings = Int(r["pings"]!);
        // Same-machine clocks: the Node report carries its own epoch start, so the final
        // publish->receive figure needs no spawn-skew correction. It measures encode +
        // authenticated transport + client receive; it is NOT a MediaCodec glass-to-glass
        // latency and NOT an E3 hardware measurement.
        var nodeStart = DateTimeOffset.FromUnixTimeMilliseconds((long)r["startedAtEpochMs"]!);
        var lastFrameArrival = nodeStart.AddMilliseconds(Int(r["lastFrameMs"]!));
        var latencyMs = (long)(lastFrameArrival - finalAt).TotalMilliseconds;
        var acceptedFps = accepted * 1000.0 / Math.Max(1, durationMs);
        _out.WriteLine($"soak: {minutes} min, target {w}x{h} native pixels, encoder {rig.Log.Lines.FirstOrDefault(l => l.Contains("video encoder"))}");
        _out.WriteLine($"soak: published {publishedCount}, client accepted {accepted} of {total}, keyframes {keyframes}, bytes {bytes}, pings {pings}");
        _out.WriteLine($"soak: accepted fps {acceptedFps:0.0}, node-measured duration {durationMs} ms, publish->receive (final frame) {latencyMs} ms");
        var csv = Environment.GetEnvironmentVariable("FOCUS_SOAK_CSV");
        if (!string.IsNullOrWhiteSpace(csv))
        {
            var invariant = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "metric,value,method,environment",
                $"run_minutes,{minutes},FOCUS_SOAK_MINUTES,synthetic-software-path",
                $"target_pixels,{w}x{h},negotiated Target widthPx x heightPx,synthetic-software-path",
                $"published_frames,{publishedCount},publisher frame count,synthetic-software-path",
                $"client_accepted_frames,{accepted},frame headers accepted by the independent Node client,synthetic-software-path",
                $"client_total_frames,{total},all frame headers received,synthetic-software-path",
                $"keyframes,{keyframes},flags bit 1 on accepted frames,synthetic-software-path",
                $"media_bytes,{bytes},H.264 payload bytes accepted,synthetic-software-path",
                $"idle_pings,{pings},host ping packets,synthetic-software-path",
                $"accepted_fps,{acceptedFps.ToString("0.0", invariant)},accepted frames / node-measured duration,synthetic-software-path",
                $"publish_to_receive_ms_final,{latencyMs},final publish wallclock vs node last-frame arrival (same machine),synthetic-software-path",
                $"encoder,{rig.Log.Lines.FirstOrDefault(l => l.Contains("video encoder")) ?? "unknown"},host log,synthetic-software-path",
                $"session_epochs,1,one authenticated session in this run,synthetic-software-path",
                "hardware_10min_session,NOT_AVAILABLE,FocusIdd driver not installed; see UNRESOLVED_BLOCKERS.md,hardware-focusidd",
            };
            await File.AppendAllLinesAsync(csv, lines);
            _out.WriteLine("soak CSV appended: " + csv);
        }
        // Truthful invariants a broken run must fail: frames flowed, the transport stayed
        // silent-protocol clean, and no protocol violation was observed.
        Assert.Empty(r["violations"]!.AsArray());
        Assert.True(accepted > minutes * 100, $"only {accepted} frames were accepted over {minutes} minutes");
        Assert.True(latencyMs >= 0 && latencyMs < 5000, $"final publish->receive latency was {latencyMs} ms");
    }
}

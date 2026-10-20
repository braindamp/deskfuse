using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
using Xunit;
namespace FocusWindow.Tests;
/// <summary>Historical CPU session fixture, retained for provenance, never GPU acceptance.</summary>
public sealed class HistoricalCpuSessionAttribute : FactAttribute
{
    public HistoricalCpuSessionAttribute() => Skip =
        "Historical CPU/FFmpeg PhoneSession fixture; superseded by mandatory UnifiedSessionTests at the encoded GPU seam";
}
/// <summary>Tests that need a real ffmpeg are skipped (not faked) when FOCUS_FFMPEG is unset.</summary>
public sealed class FactIfFfmpegAttribute : FactAttribute
{
    public FactIfFfmpegAttribute()
    {
        if (ToolLocator.Ffmpeg is null) Skip = "FOCUS_FFMPEG is not set; skipping the real-encoder test";
    }
}
public sealed class FactIfFfmpegAndNodeAttribute : FactAttribute
{
    public FactIfFfmpegAndNodeAttribute()
    {
        if (ToolLocator.Ffmpeg is null) Skip = "FOCUS_FFMPEG is not set";
        else if (ToolLocator.Node is null) Skip = "node is not on PATH";
    }
}
public sealed class FactIfNodeAttribute : FactAttribute
{
    public FactIfNodeAttribute()
    {
        if (ToolLocator.Node is null) Skip = "node is not on PATH";
    }
}
/// <summary>
/// The 10-minute soak is deliberately excluded from the normal suite and the
/// acceptance matrix; it runs only when an operator asks for it. Skips (never
/// silently passes) when the tools or FOCUS_SOAK_MINUTES are absent.
/// </summary>
public sealed class FactIfSoakAttribute : FactAttribute
{
    public FactIfSoakAttribute()
    {
        if (ToolLocator.Ffmpeg is null) Skip = "FOCUS_FFMPEG is not set";
        else if (ToolLocator.Node is null) Skip = "node is not on PATH";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FOCUS_SOAK_MINUTES")))
            Skip = "FOCUS_SOAK_MINUTES is not set; the soak is excluded from the normal suite";
    }
}
public static class ToolLocator
{
    public static string? Ffmpeg => Existing(Environment.GetEnvironmentVariable("FOCUS_FFMPEG"));
    public static string? Ffprobe => Existing(Environment.GetEnvironmentVariable("FOCUS_FFPROBE"));
    public static string? Node
    {
        get
        {
            var explicitPath = Existing(Environment.GetEnvironmentVariable("FOCUS_NODE"));
            if (explicitPath is not null) return explicitPath;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
    private static string? Existing(string? p) => !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? p : null;
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string exe, string args, TimeSpan timeout, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException($"{exe} {args} timed out"); }
        return (p.ExitCode, await stdout + await stderr);
    }
}
/// <summary>
/// Test-side port of the DRIVER's SharedSurfaceWriter::Publish (Driver.cpp), including the
/// slot invalidation that makes the seqlock sound. It writes the exact layout in
/// FocusShared.h into native memory, so the production SurfaceReader is tested against the
/// same bytes the C++ writer produces.
/// </summary>
public sealed unsafe class TestSurfaceWriter : IDisposable
{
    private readonly byte* _base;
    private readonly long _length;
    private readonly uint _slotStride;
    private ulong _seq;
    private uint _nextSlot;
    public readonly AutoResetEvent FrameReady = new(false);
    public TestSurfaceWriter(int maxWidth, int maxHeight, uint slotCount = DriverContract.SlotCount)
    {
        var pixelBytes = (uint)(maxWidth * maxHeight * 4);
        _slotStride = DriverContract.SlotHeaderBytes + pixelBytes;
        _length = DriverContract.HeaderBytes + (long)slotCount * _slotStride;
        _base = (byte*)NativeMemory.AllocZeroed((nuint)_length);
        W32(DriverContract.HdrVersion, DriverContract.SurfaceVersion);
        W32(DriverContract.HdrSlotCount, slotCount);
        W32(DriverContract.HdrSlotStrideBytes, _slotStride);
        W32(DriverContract.HdrMaxPixels, (uint)(maxWidth * maxHeight));
        Thread.MemoryBarrier();
        W32(DriverContract.HdrMagic, DriverContract.SurfaceMagic); // published last, like the driver
    }
    public byte* Base => _base;
    public long Length => _length;
    public SurfaceReader CreateReader() => new(_base, _length);
    private void W32(int off, uint v) => Volatile.Write(ref *(uint*)(_base + off), v);
    public void SetMonitorArrived(bool arrived) => W32(DriverContract.HdrMonitorArrived, arrived ? 1u : 0u);
    /// <param name="invalidateFirst">
    /// true = the fixed protocol (Seq=0 before pixels). false reproduces the v1 driver bug and
    /// exists ONLY as a negative control proving the stress test can actually see torn frames.
    /// </param>
    public bool Publish(ReadOnlySpan<byte> bgra, int width, int height, bool invalidateFirst = true)
    {
        var bytes = (long)width * height * 4;
        if (bytes > _slotStride - DriverContract.SlotHeaderBytes || bgra.Length < bytes) return false;
        var slotCount = *(uint*)(_base + DriverContract.HdrSlotCount);
        var index = _nextSlot;
        _nextSlot = (_nextSlot + 1) % slotCount;
        var slot = _base + DriverContract.HeaderBytes + (long)index * _slotStride;
        if (invalidateFirst)
        {
            Volatile.Write(ref *(ulong*)(slot + DriverContract.SlotSeq), 0UL); // INVALIDATE before touching pixels
            Thread.MemoryBarrier();
        }
        fixed (byte* src = bgra)
            Buffer.MemoryCopy(src, slot + DriverContract.SlotHeaderBytes, bytes, bytes);
        Volatile.Write(ref *(uint*)(slot + DriverContract.SlotWidth), (uint)width);
        Volatile.Write(ref *(uint*)(slot + DriverContract.SlotHeight), (uint)height);
        Thread.MemoryBarrier();
        var seq = ++_seq;
        Volatile.Write(ref *(ulong*)(slot + DriverContract.SlotSeq), seq);
        Thread.MemoryBarrier();
        W32(DriverContract.HdrWidth, (uint)width);
        W32(DriverContract.HdrHeight, (uint)height);
        W32(DriverContract.HdrStride, (uint)(width * 4));
        W32(DriverContract.HdrLatestSlot, index);
        Thread.MemoryBarrier();
        Volatile.Write(ref *(ulong*)(_base + DriverContract.HdrPublishedSeq), seq);
        FrameReady.Set();
        return true;
    }
    public void Dispose()
    {
        NativeMemory.Free(_base);
        FrameReady.Dispose();
    }
}
/// <summary>A frame with a known, checkable layout: flat background + a 100x100 square.</summary>
public static class TestFrames
{
    public static byte[] Make(int w, int h, byte b, byte g, byte r, int squareX = 50, int squareY = 50, int square = 100)
    {
        var f = new byte[w * h * 4];
        for (var i = 0; i < f.Length; i += 4)
        {
            f[i] = b; f[i + 1] = g; f[i + 2] = r; f[i + 3] = 255;
        }
        for (var y = squareY; y < Math.Min(h, squareY + square); y++)
            for (var x = squareX; x < Math.Min(w, squareX + square); x++)
            {
                var o = (y * w + x) * 4;
                f[o] = 255; f[o + 1] = 255; f[o + 2] = 255; f[o + 3] = 255;
            }
        return f;
    }
}
/// <summary>
/// Scriptable stand-in for the Windows display stack. It does not pretend Windows behaves
/// well: tests configure the recommended scale, the cap, whether SET is accepted, what DPI
/// Windows actually ends up using, and what a probe window reports — so every branch of the
/// negotiation ladder is exercised, including the failures.
/// </summary>
public sealed class FakeDisplayBackend : IDisplayBackend
{
    public int RecommendedPercent = 350;
    public int CurrentPercent = 350;
    public Func<int, int, int> MaxPercentFor = (_, _) => 500; // (pixelW, pixelH) -> cap
    public bool CapsAvailable = true;
    public bool AcceptSet = true;
    public Func<int, int> DpiAppliedForSetPercent = p => WindowsScale.DpiFor(p); // what Windows ends up using
    public int? ProbeWindowDpi;                // null => follow the monitor
    public string ProbeAwareness = "PerMonitorV2";
    public bool? ProbeRulerOk = true;
    public bool ProbeAvailable = true;
    public bool ProbeClientFullSize = true;
    public int? ProbeViewportWindowDpi;
    public bool ProbeViewportAvailable = true;
    public int ViewportProbeCalls;
    public bool FailOnFrameSource;

    public readonly List<MonitorRequest> Created = new();
    private MonitorRequest? _lastRequest;
    /// <summary>Thread-safe view of the newest monitor request (the test publisher reads it while the session runs).</summary>
    public MonitorRequest? LastRequest => Volatile.Read(ref _lastRequest);
    public readonly List<int> SetCalls = new();
    public int RemoveCalls;
    public int Heartbeats;
    public TestSurfaceWriter Surface { get; }
    private int _dpi;
    private RectI _bounds;
    public FakeDisplayBackend(int maxW = 2048, int maxH = 3200)
    {
        Surface = new TestSurfaceWriter(maxW, maxH);
        _dpi = WindowsScale.DpiFor(CurrentPercent);
    }
    public Task<MonitorInfo> CreateMonitorAsync(MonitorRequest request, CancellationToken ct)
    {
        lock (Created) Created.Add(request);
        Volatile.Write(ref _lastRequest, request);
        _bounds = new RectI(2560, 0, request.Width, request.Height);
        // A fresh monitor starts at Windows' own recommendation, as a real arrival does.
        CurrentPercent = RecommendedPercent;
        _dpi = WindowsScale.DpiFor(CurrentPercent);
        Surface.SetMonitorArrived(true);
        return Task.FromResult(new MonitorInfo(_bounds, @"\\.\DISPLAY9", "FocusPhone"));
    }
    public DensityReadout ReadDensity(MonitorInfo monitor) => new(_dpi, _dpi, _bounds);
    public ScaleCaps? ReadScaleCaps(MonitorInfo monitor) =>
        !CapsAvailable ? null : new ScaleCaps(RecommendedPercent, CurrentPercent, MaxPercentFor(_bounds.Width, _bounds.Height));
    public ScaleSetResult TrySetScale(MonitorInfo monitor, int percent)
    {
        SetCalls.Add(percent);
        if (!AcceptSet) return new ScaleSetResult(false, "DisplayConfigSetDeviceInfo returned ERROR_INVALID_PARAMETER");
        CurrentPercent = percent;
        _dpi = DpiAppliedForSetPercent(percent);
        return new ScaleSetResult(true, $"relative index applied for {percent}%");
    }
    public Task<WindowDensityReport?> ProbeWindowAsync(MonitorInfo monitor, bool drawRuler, CancellationToken ct)
    {
        if (!ProbeAvailable) return Task.FromResult<WindowDensityReport?>(null);
        var dpi = ProbeWindowDpi ?? _dpi;
        var expected = (int)Math.Round(100 * dpi / 96.0);
        bool? rulerOk = drawRuler ? ProbeRulerOk : null;
        return Task.FromResult<WindowDensityReport?>(new WindowDensityReport(
            dpi, ProbeAwareness, _bounds,
            ProbeClientFullSize ? _bounds : new RectI(0, 0, _bounds.Width, _bounds.Height - 38), 100,
            drawRuler ? expected : null, drawRuler ? expected : null, rulerOk,
            rulerOk == false ? "ruler did not match" : drawRuler ? "ruler matched" : "ruler not requested"));
    }
    public Task<WindowDensityReport?> ProbeViewportAsync(
        MonitorInfo monitor, RectI viewport, bool drawRuler, CancellationToken ct)
    {
        ViewportProbeCalls++;
        if (!ProbeViewportAvailable) return Task.FromResult<WindowDensityReport?>(null);
        var actualDpi = ProbeViewportWindowDpi ?? _dpi;
        var expected = (int)Math.Round(100 * actualDpi / 96.0);
        return Task.FromResult<WindowDensityReport?>(new WindowDensityReport(
            actualDpi, ProbeAwareness, viewport,
            ProbeClientFullSize ? new RectI(0, 0, viewport.Width, viewport.Height)
                : new RectI(0, 0, viewport.Width, viewport.Height - 1),
            100, drawRuler ? expected : null, drawRuler ? expected : null,
            drawRuler ? ProbeRulerOk : null, "fake pixel-exact cropped viewport"));
    }
    public Task RemoveMonitorAsync(CancellationToken ct)
    {
        RemoveCalls++;
        Surface.SetMonitorArrived(false);
        return Task.CompletedTask;
    }
    public DriverHealth ReadDriverHealth() => new(true, DriverContract.ProtocolVersion, true, _bounds.Width, _bounds.Height, 60, 0, 0, 0, 0);
    public void Heartbeat() => Heartbeats++;
    public IFrameSource OpenFrameSource() =>
        FailOnFrameSource ? throw new InvalidOperationException("Production video must use the encoded GPU seam") :
        new SurfaceFrameSource(Surface.CreateReader(), ms => Surface.FrameReady.WaitOne(ms));
    public ValueTask DisposeAsync()
    {
        Surface.Dispose();
        return ValueTask.CompletedTask;
    }
}
public sealed class RecordingInputSink : IInputSink
{
    public readonly List<(InputEventDto Event, InputGeometry Geometry)> Events = new();
    public int Released;
    public void Handle(InputEventDto e, InputGeometry geometry)
    {
        lock (Events) Events.Add((e, geometry));
    }
    public void ReleaseAll() => Released++;
}
public sealed class StaticGuard : IPhysicalDisplayGuard
{
    public bool Drift;
    public DisplaySnapshot Snapshot() => new(new[] { new PhysicalMonitorInfo(@"\\.\DISPLAY1", new RectI(0, 0, 2560, 1440), 96, true) });
    public GuardReport Verify(DisplaySnapshot before) =>
        Drift ? new GuardReport(false, new[] { "synthetic drift" }) : new GuardReport(true, Array.Empty<string>());
}
public sealed class ListLog : ILog
{
    public readonly List<string> Lines = new();
    public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }
    public void Warn(string message) { lock (Lines) Lines.Add("WARN " + message); }
    public void Error(string message) { lock (Lines) Lines.Add("ERR  " + message); }
}
public static class TestProfiles
{
    public static PhoneProfile Flagship(float dpr = 3.5f, int w = 1440, int h = 3120, int diagMm = 170) => new(
        1, "test-device-flagship", "Test Flagship", w, h, dpr, 0, 0, 0, 0, 1f, 120, diagMm, "PORTRAIT", "PHONE");
}

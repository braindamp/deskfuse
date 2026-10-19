using System.Runtime.Versioning;
using FocusWindow.Core;
using FocusWindow.Core.Display;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Agent.Display;
/// <summary>
/// The real IDisplayBackend: creates the phone monitor through the FocusIdd driver, waits until WINDOWS
/// (not just the driver) reports it active, and answers every density question by asking Windows.
///
/// Nothing in here is exercised by the Linux test suite; it is compile-checked there and must be run on
/// Windows with the driver installed (windows/BUILD.md, `density-probe`).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayBackend : IDisplayBackend
{
    private readonly ILog _log;
    private DriverDevice? _driver;
    private WindowsSurface? _surface;
    private PhoneTarget? _target;
    public WindowsDisplayBackend(ILog log) => _log = log;
    public string? PhoneGdiName => _target?.GdiName;
    private DriverDevice Driver()
    {
        if (_driver is not null) return _driver;
        var d = DriverDevice.Open();
        var version = d.Ping();
        if (version != DriverContract.ProtocolVersion)
        {
            d.Dispose();
            throw new DisplayException("driver",
                $"The installed display driver speaks protocol {version}; this agent needs {DriverContract.ProtocolVersion}.",
                "Install the driver build that ships with this agent (windows/driver).");
        }
        _driver = d;
        _log.Info($"display driver found: {d.DevicePath} (protocol {version})");
        return d;
    }
    public async Task<MonitorInfo> CreateMonitorAsync(MonitorRequest request, CancellationToken ct)
    {
        var driver = Driver();
        _surface ??= WindowsSurface.Open();
        // A deterministic start: if a monitor is already up (from a previous target), remove it and
        // wait for the departure, so "MonitorArrived == 1" below can only mean the NEW monitor.
        if (driver.GetStatus().MonitorArrived == 1)
        {
            driver.RemoveMonitor();
            await WaitAsync(() => driver.GetStatus().MonitorArrived == 0, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        }
        // Snapshot the ACTUAL active desktop topology BEFORE asking IddCx to attach.
        // Windows 10 may omit the EDID name/path even for a visible virtual display.
        var topologyBefore = DisplayConfigApi.CaptureTopology();
        _log.Info("DeskFuse topology-delta detection v0.2.20 enabled");
        driver.SetTarget(new DriverContract.SetTarget
        {
            WidthPx = (uint)request.Width,
            HeightPx = (uint)request.Height,
            RefreshHz = (uint)request.RefreshHz,
            PhysWidthMm = (uint)request.PhysWidthMm,
            PhysHeightMm = (uint)request.PhysHeightMm,
            ScaleHintPercentInfoOnly = (uint)request.ScaleHintPercent, // recorded by the driver, never applied
            Serial = request.Serial,
        });
        // ---- 1. the driver says the monitor arrived -----------------------------------------------
        DriverContract.Status status = default;
        var arrived = await WaitAsync(() =>
        {
            status = driver.GetStatus();
            return status.MonitorArrived == 1 || status.LastError != 0;
        }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!arrived || status.LastError != 0 || status.MonitorArrived != 1)
        {
            throw new DisplayException("display",
                $"The display driver did not bring the phone monitor up (status 0x{status.LastError:X8}, surface error {status.SurfaceWin32Error}).",
                status.SurfaceWin32Error != 0
                    ? "The driver could not create its shared frame surface; check that the UMDF host may create Global\\ objects."
                    : "Open Device Manager and look for a problem code on 'Focus Window Virtual Display'.");
        }
        _log.Info($"driver: monitor up at {status.WidthPx}x{status.HeightPx}@{status.RefreshHz} Hz");
        // ---- 2. WINDOWS says it is an active display ----------------------------------------------
        PhoneTarget? found = null;
        (RectI Bounds, IntPtr Handle)? monitor = null;
        var identification = "no topology change yet";
        var ready = await WaitAsync(() =>
        {
            found = DisplayConfigApi.FindCreatedPhoneMonitor(
                topologyBefore, request.Width, request.Height, out identification);
            if (found is not { Active: true }) return false;
            monitor = DisplayConfigApi.FindMonitor(found.GdiName);
            return monitor is not null;
        }, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        if (!ready)
        {
            _log.Warn("Monitor identification: " + identification);
            try { _log.Warn("Windows CCD paths: " + DisplayConfigApi.DescribePaths()); }
            catch (Exception ex) { _log.Warn("Unable to enumerate CCD paths: " + ex.Message); }
            if (found is { Active: false })
                throw new DisplayException("display",
                    "Windows detected the phone monitor but left it INACTIVE.",
                    "The projection mode is probably 'PC screen only' (Win+P). Choose 'Extend', then reconnect. " +
                    "The agent does not change your projection mode for you.");
            throw new DisplayException("display",
                "The driver reported monitor arrival, but no unambiguous NEW active Windows display path was identified: " + identification,
                "Check the CCD/GDI diagnostics. A monitor shown in Settings may still be inactive; DeskFuse will not select a physical display by guesswork.");
        }
        _target = found!;
        var bounds = monitor!.Value.Bounds;
        if (bounds.Width != request.Width || bounds.Height != request.Height)
        {
            throw new DisplayException("mode",
                $"Windows set the phone monitor to {bounds.Width}x{bounds.Height}, not the requested {request.Width}x{request.Height}.",
                "Check Display settings for the phone monitor's resolution; the agent does not rescale to hide this.");
        }
        _log.Info($"phone monitor identified by topology delta: {_target.GdiName} {bounds}; {identification}");
        return new MonitorInfo(bounds, _target.GdiName, _target.FriendlyName);
    }
    public DensityReadout ReadDensity(MonitorInfo monitor)
    {
        var m = DisplayConfigApi.FindMonitor(monitor.GdiName)
                ?? throw new DisplayException("display", "The phone monitor disappeared while its density was being read.");
        var dpi = DisplayConfigApi.MonitorDpi(m.Handle)
                  ?? throw new DisplayException("display", "GetDpiForMonitor failed for the phone monitor.");
        return new DensityReadout(dpi.X, dpi.Y, m.Bounds);
    }
    public ScaleCaps? ReadScaleCaps(MonitorInfo monitor) =>
        _target is null ? null : DisplayConfigApi.ReadScaleCaps(_target);
    public ScaleSetResult TrySetScale(MonitorInfo monitor, int percent) =>
        _target is null
            ? new ScaleSetResult(false, "no phone monitor")
            : DisplayConfigApi.SetScale(_target, percent);
    public async Task<WindowDensityReport?> ProbeWindowAsync(MonitorInfo monitor, bool drawRuler, CancellationToken ct)
    {
        if (_surface is null) return null;
        // The capture runs on the probe's STA thread; it only reads, through its own frame source.
        using var frames = _surface.CreateFrameSource();
        var buffer = Array.Empty<byte>();
        (byte[] Bgra, int Width, int Height)? Capture()
        {
            if (!frames.TryRead(400, ref buffer, out var info)) return null;
            return (buffer, info.Width, info.Height);
        }
        return await ProbeWindow.RunAsync(monitor.Bounds, drawRuler, Capture).ConfigureAwait(false);
    }
    public async Task RemoveMonitorAsync(CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return;
        try
        {
            driver.RemoveMonitor();
            await WaitAsync(() => driver.GetStatus().MonitorArrived == 0, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        }
        catch (DisplayException ex)
        {
            _log.Warn("removing the phone monitor: " + ex.Message);
        }
        _target = null;
    }
    public DriverHealth ReadDriverHealth()
    {
        try
        {
            var s = Driver().GetStatus();
            return new DriverHealth(true, (int)s.ProtocolVersion, s.MonitorArrived == 1, (int)s.WidthPx, (int)s.HeightPx,
                (int)s.RefreshHz, s.FramesPublished, s.FramesDropped, s.LastError, s.SurfaceWin32Error);
        }
        catch (DisplayException)
        {
            return new DriverHealth(false, 0, false, 0, 0, 0, 0, 0, 0, 0);
        }
    }
    public void Heartbeat()
    {
        try { _driver?.Ping(); }
        catch (DisplayException ex) { _log.Warn("driver heartbeat failed: " + ex.Message); }
    }
    public IFrameSource OpenFrameSource() =>
        (_surface ?? throw new DisplayException("driver", "The frame surface is not open yet.")).CreateFrameSource();
    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await RemoveMonitorAsync(CancellationToken.None).ConfigureAwait(false);
        _surface?.Dispose();
        _driver?.Dispose();
        _surface = null;
        _driver = null;
    }
}

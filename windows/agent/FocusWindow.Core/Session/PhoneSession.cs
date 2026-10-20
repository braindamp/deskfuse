using System.Diagnostics;
using System.Security.Cryptography;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
/// <summary>
/// One phone, from authentication to teardown (FR-OPS-03: every state is named, every
/// failure is a Stage message with a remediation, never a dead connection).
///
///   hello -> approval -> negotiate density -> start encoder -> Target -> frames ... -> restore
///
/// What this session does NOT do, and says so to the phone where it matters: it serves the
/// DESKTOP CANVAS tier only. The clone tier (a second view of the user's document) is not
/// implemented, so a request for it is answered with a named, non-fatal Stage message.
/// </summary>
internal sealed class PhoneSession
{
    private readonly HostOptions _o;
    private readonly HostServices _s;
    private readonly ILog _log;
    private PhoneConnection _conn = null!;
    private IDisplayBackend _backend = null!;
    private IInputSink _input = null!;
    private IPhysicalDisplayGuard _guard = null!;
    private DisplaySnapshot _before = null!;
    private DensityNegotiator _negotiator = null!;
    private NegotiationOutcome _outcome = null!;
    private PhoneProfile _profile = null!;
    private InputGeometry _geometry = null!;
    private IVideoPipeline? _video;
    private FileUploadReceiver? _uploads;
    private CancellationTokenSource? _downloadStop;
    private Task? _downloadTask;
     private long _version;
    private int _fps;
    private volatile bool _resync;
    private long _framesSeen;
    private readonly long _ptsOrigin = Stopwatch.GetTimestamp();
    private long _lastPtsUs;
    public PhoneSession(HostOptions o, HostServices s, string encoderName, ILog log)
    {
        _o = o;
        _s = s;
        _log = log;
        _fps = Math.Clamp(o.Fps, 1, 240);
    }
    public async Task RunAsync(PhoneConnection conn, ClientHello hello, CancellationToken ct)
    {
        _conn = conn;
        _profile = hello.Profile;
        await using var _ = conn;
        conn.StartPumps();
        if (hello.ProtocolVersion != Wire.ProtocolVersion)
        {
            Fatal("version", $"The phone speaks protocol {hello.ProtocolVersion}; this host speaks {Wire.ProtocolVersion}.",
                "Update the Android app or the host agent so both are the same release.");
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        conn.SendControl(ServerMessages.Hello(_o.HostVersion, _o.HostName, _o.Channel));
        conn.SendControl(ServerMessages.Stage("Waiting for approval", $"Confirm code {conn.VerificationCode} on the PC."));
        var approved = await _s.Approval
            .ApproveAsync(_profile.DeviceId, _profile.DeviceLabel, conn.VerificationCode, ct).ConfigureAwait(false);
        if (!approved)
        {
            Fatal("approval", "The owner of this PC declined the connection.", "Ask them to approve it, then connect again.");
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        _guard = _s.CreateGuard();
        _before = _guard.Snapshot();
        _backend = _s.CreateBackend();
        _input = _s.CreateInput();
        _uploads = new FileUploadReceiver(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusWindow", "Incoming"), _o.AllowFullUserFiles);
        _negotiator = new DensityNegotiator(
            _backend,
            new NegotiatorOptions { DrawRuler = true, MaxRefreshHz = _o.MaxRefreshHz,
                PreserveRequestedResolution = true },
            _log);
        try
        {
            await StreamAsync(ct).ConfigureAwait(false);
        }
        catch (DisplayException ex)
        {
            _log.Error($"[{ex.Stage}] {ex.Message}");
            Fatal(ex.Stage, ex.Message, ex.Remediation);
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or IOException or InvalidOperationException)
        {
            _log.Error("native GPU pipeline: " + ex.Message);
            Fatal("gpu", ex.Message, "Install the matching DeskFuse native GPU library and verify WGC/NVENC support.");
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // host shutting down
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
    }
    // ------------------------------------------------------------------------------------
    private async Task StreamAsync(CancellationToken ct)
    {
        _conn.SendControl(ServerMessages.Stage("Preparing the display", "Creating the phone-shaped monitor."));
        _outcome = await NegotiatePhoneDisplayAsync(_profile, ct).ConfigureAwait(false);
        _s.OnNegotiated?.Invoke(_outcome);
        // Structural integration checkpoint: GPU/WGC is the authoritative
        // video path. We do NOT instantiate the driver CPU surface or FFmpeg.
        // Real focused app source selection and ScaleEnforcer are subsequent
        // integration tasks. Unsupported native DPI fails explicitly.
        if (!_outcome.Native)
            throw new DisplayException("density", "Verified native DPI is required for the GPU workspace.",
                "This display profile is not yet supported by the native-density host.");
        // POC-proven per-client scale enforcement: Windows may reset the
        // virtual monitor to 100% on reattach. No capture before readback.
        var beforeScale = _backend.ReadScaleCaps(_outcome.Monitor);
        var enforced = await ScaleEnforcer.RequireAsync(
            _backend, _outcome.Monitor, _outcome.Plan.ScalePercent, ct).ConfigureAwait(false);
        if (enforced.Changed && _outcome.RestoreScalePercent is null)
            _outcome = _outcome with { RestoreScalePercent = beforeScale?.CurrentPercent };
        _version = 1;
        _video = _s.CreateVideoPipeline?.Invoke() ??
            throw new DisplayException("gpu", "No native GPU pipeline is configured.");
        await _video.StartAsync(new VideoPipelineSettings(
            _outcome.Monitor.Bounds, _outcome.Plan.PixelW, _outcome.Plan.PixelH,
            _fps, _o.BitrateKbps, _outcome.EffectiveDpi), ct).ConfigureAwait(false);
        _geometry = new InputGeometry(_video.Info.SourceBounds,
            _video.Info.Width, _video.Info.Height);
        _resync = true;
        SendTarget();
        await SessionLifetime.RunUntilFirstStopAsync(
            control: ControlLoopAsync,
            video: PumpEncodedGpuVideoAsync,
            housekeeping: HousekeepingAsync,
            transportCompletion: _conn.Completion,
            shutdown: ct).ConfigureAwait(false);
    }
    private async Task<NegotiationOutcome> NegotiatePhoneDisplayAsync(PhoneProfile phone, CancellationToken ct)
    {
        void Progress(string stage, string detail)
        {
            // Repeated resolution-search updates are diagnostics, not useful phone UI.
            if (stage == "Calibrating display density") _log.Info(detail);
            else _conn.SendControl(ServerMessages.Stage(stage, detail));
        }

        // Initialize the driver interface on this thread BEFORE starting
        // the heartbeat task, so two threads never race to open the handle.
        var initialDriver = _backend.ReadDriverHealth();
        if (!initialDriver.Reachable)
            throw new DisplayException("driver",
                "FocusIdd did not respond before adaptive density calibration.");
        // During calibration there is no HousekeepingAsync yet. Keep the
        // existing FocusIdd ten-second dead-agent removal timer fed while
        // Windows creates/replaces candidate virtual displays.
        using var pulseStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pulse = Task.Run(async () =>
        {
            try
            {
                while (!pulseStop.IsCancellationRequested)
                {
                    _backend.Heartbeat();
                    await Task.Delay(1000, pulseStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pulseStop.IsCancellationRequested) { }
        }, CancellationToken.None);
        try
        {
            if (_o.AdaptiveDpiViewport)
            {
                var calibrated = await new AdaptiveViewportCalibration(
                    _backend, _guard, _before, _log) { RememberVerifiedWidth = true }
                    .TryRunAsync(phone, Progress, ct).ConfigureAwait(false);
                if (calibrated is not null) return calibrated;
                _log.Warn("Exact-DPR adaptive viewport not verified; reverting to honest normal density probe.");
            }
            return await _negotiator.RunAsync(phone, Progress, ct).ConfigureAwait(false);
        }
        finally
        {
            pulseStop.Cancel();
            await pulse.ConfigureAwait(false);
        }
    }

    private void RestoreAdaptiveScale()
    {
        var outcome = _outcome;
        var original = outcome?.RestoreScalePercent;
        if (outcome is null || original is null || _backend is null) return;
        var monitor = outcome.Monitor;
        var result = _backend.TrySetScale(monitor, original.Value);
        ScaleCaps? readback = null;
        if (result.Ok)
        {
            // -4 can return before the Windows DPI transition settles.
            for (var retry = 0; retry < 12; retry++)
            {
                readback = _backend.ReadScaleCaps(monitor);
                if (readback?.CurrentPercent == original.Value) break;
                Thread.Sleep(150);
            }
        }
        if (!result.Ok || readback?.CurrentPercent != original.Value)
            throw new DisplayException("scale-restore",
                $"Original virtual-display scale {original}% not restored: {result.Detail}; " +
                $"readback={readback?.CurrentPercent.ToString() ?? "unavailable"}");
        _log.Info($"Adaptive virtual-display scale restored to {original}%");
    }

    private async Task PumpEncodedGpuVideoAsync(CancellationToken ct)
    {
        var video = _video ?? throw new InvalidOperationException("Video pipeline not started");
        await foreach (var packet in video.ReadAccessUnitsAsync(ct).ConfigureAwait(false))
        {
            if (_resync && !packet.Keyframe) { video.RequestKeyframe(); continue; }
            if (packet.Keyframe) _resync = false;
            if (!_conn.TrySendFrame(packet.AnnexB, packet.Keyframe, NextPtsUs(),
                    Interlocked.Read(ref _version)))
            {
                // Never transmit undecodable P/B frames after packet loss.
                _resync = true;
                video.RequestKeyframe();
                continue;
            }
            Interlocked.Increment(ref _framesSeen);
        }
    }

    private long NextPtsUs()
    {
        var now = (long)(Stopwatch.GetElapsedTime(_ptsOrigin).TotalMilliseconds * 1000.0);
        var next = Math.Max(now, _lastPtsUs + 1); // strictly increasing: the client keys latency by pts
        _lastPtsUs = next;
        return next;
    }
    private void SendTarget()
    {
        var plan = _outcome.Plan;
        var video = _video?.Info ?? throw new InvalidOperationException("GPU video not initialized");
        var record = new TargetRecord(
            Version: Interlocked.Read(ref _version),
            Occupant: OccupantKind.Window,
            WidthPx: plan.PixelW,
            HeightPx: plan.PixelH,
            Dpi: _outcome.EffectiveDpi,
            CanvasDpW: (int)Math.Round(plan.PixelW * 96.0 / _outcome.EffectiveDpi),
            CanvasDpH: (int)Math.Round(plan.PixelH * 96.0 / _outcome.EffectiveDpi),
            Native: _outcome.Native,
            Method: _outcome.Method,
            Codec: video.Codec,
            PixelFormat: video.PixelFormat,
            RefreshHz: Volatile.Read(ref _fps),
            DegradedReason: _outcome.DegradedReason,
            State: StateModel.SharedView,
            AppLabel: "DeskFuse GPU workspace (managed source)",
            DocumentLabel: null);
        _conn.SendControl(ServerMessages.Target(record));
    }
    // ------------------------------------------------------------------------------------
    // ------------------------------------------------------------------------------------
    private async Task ControlLoopAsync(CancellationToken ct)
    {
        await foreach (var message in _conn.Messages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case ClientProfile p when GeometryChanged(p.Profile):
                    _conn.SendControl(ServerMessages.Stage("geometry",
                        "Phone geometry changed. Reconnect to provision a new native GPU window."));
                    return;
                case ClientRefresh r:
                    // The native encoder must renegotiate cadence with the
                    // source; changing only an integer would misreport fps.
                    _conn.SendControl(ServerMessages.Stage("refresh",
                        "Refresh change will take effect on the next GPU session."));
                    break;
                case ClientKeyframeRequest:
                    _video?.RequestKeyframe();
                    break;
                case ClientInput i:
                    try { _input.Handle(i.Event, _geometry); }
                    catch (Exception ex) { _log.Warn("input injection failed: " + ex.Message); }
                    break;
                case ClientOccupant o:
                    _log.Info($"phone asked for the '{o.Want}' tier; only the desktop canvas is implemented");
                    _conn.SendControl(ServerMessages.Stage(
                        "Tier unavailable",
                        $"This host build only serves the desktop-canvas tier; '{o.Want}' (a second view of one app) is not implemented.",
                        "Open the app on the phone display instead."));
                    break;
                case ClientDensity d:
                    _log.Info($"density preset '{d.Preset}' noted; this host always uses the phone's exact density");
                    break;
                case ClientClipboard clip:
                    await HandleClipboardAsync(clip, ct).ConfigureAwait(false);
                    break;
                case ClientWindows windows:
                    HandleWindows(windows);
                    break;
                case ClientFiles files:
                    HandleFiles(files, ct);
                    break;
                case ClientFile file:
                    HandleFile(file);
                    break;
                case ClientBye b:
                    _log.Info("phone said goodbye: " + b.Reason);
                    return;
            }
        }
    }
    private void HandleFiles(ClientFiles request, CancellationToken sessionCt)
    {
        // Pairing alone is insufficient when the console approval was bypassed.
        if (!_o.AllowFullUserFiles)
        {
            const string denied = "Windows user approval is required for full file access. Restart without --auto-approve.";
            if (request.Action == "get" && request.Id is not null)
                _conn.SendControl(ServerMessages.FileTransfer("error", request.Id, error: denied));
            else
                _conn.SendControl(ServerMessages.Stage("files", denied));
            return;
        }
        try
        {
            switch (request.Action)
            {
                case "list":
                    _conn.SendControl(ServerMessages.FileList(UserFileBrowser.List(request.Path, request.Offset)));
                    break;
                case "get" when request.Path is not null && request.Id is not null:
                    if (!Guid.TryParseExact(request.Id, "N", out _))
                        throw new InvalidDataException("Invalid download ID");
                    if (_downloadTask is { IsCompleted: false })
                    {
                        _conn.SendControl(ServerMessages.Stage("files", "Another download is active."));
                        break;
                    }
                    _downloadStop?.Dispose();
                    _downloadStop = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
                    _downloadTask = StreamDownloadAsync(request.Path, request.Id, _downloadStop.Token);
                    break;
                case "cancel" when request.Id is not null:
                    if (request.Id == _currentDownloadId) _downloadStop?.Cancel();
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("filesystem command rejected: " + ex.GetType().Name);
            const string denied = "Folder or file unavailable to the signed-in Windows user.";
            if (request.Action == "get" && request.Id is not null)
                _conn.SendControl(ServerMessages.FileTransfer("error", request.Id, error: denied));
            else
                _conn.SendControl(ServerMessages.Stage("files", denied));
        }
    }

    private string? _currentDownloadId;

    private async Task StreamDownloadAsync(string requestedPath, string id, CancellationToken ct)
    {
        _currentDownloadId = id;
        try
        {
            var path = UserFileBrowser.FullPath(requestedPath);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length < 0) throw new IOException("Unknown file length");
            await _conn.SendFileControlAsync(ServerMessages.FileTransfer("begin", id,
                Path.GetFileName(path), file.Length), ct).ConfigureAwait(false);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileUploadReceiver.ChunkBytes];
            long sent = 0;
            while (true)
            {
                int read = await file.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                sent += read;
                sha.AppendData(buffer, 0, read);
                await _conn.SendFileControlAsync(ServerMessages.FileTransfer("chunk", id,
                    base64: Convert.ToBase64String(buffer, 0, read)), ct).ConfigureAwait(false);
            }
            if (sent != file.Length) throw new IOException("File changed during download");
            await _conn.SendFileControlAsync(ServerMessages.FileTransfer("end", id,
                sha256: Convert.ToHexString(sha.GetHashAndReset())), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("file download interrupted: " + ex.GetType().Name);
            // Never disclose raw local paths in unauthenticated diagnostics.
            try { await _conn.SendFileControlAsync(ServerMessages.FileTransfer("error", id,
                error: "File is unavailable or changed during transfer."), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
        }
        finally { _currentDownloadId = null; }
    }

    private void HandleFile(ClientFile request)
    {
        if (_uploads is null) return;
        try
        {
            var status = _uploads.Handle(request);
            if (status is not null) _conn.SendControl(ServerMessages.Stage("file", status));
            var openPath = _uploads.TakeCompletedOpenPath();
            if (openPath is not null) {
                // Triggered only by an approved, authenticated user's explicit
                // Android "Open on PC" action, after size + SHA-256 verification.
                // Shell uses the Windows user's existing default file association.
                try {
                    Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true, Verb = "open" });
                    _conn.SendControl(ServerMessages.Stage("file", "Opened with the PC's default application."));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                    _log.Warn("remote document open failed: " + e.GetType().Name);
                    _conn.SendControl(ServerMessages.Stage("file", "Upload complete, but Windows could not open the document."));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Never log file contents, payload, hash, or device-supplied path.
            _log.Warn("file upload rejected: " + ex.GetType().Name);
            _conn.SendControl(ServerMessages.Stage("file", "Upload failed; incomplete file removed."));
        }
    }

    private void HandleWindows(ClientWindows request)
    {
        if (_s.Windows is null) return;
        try
        {
            switch (request.Action)
            {
                case "list":
                    _conn.SendControl(ServerMessages.WindowList(_s.Windows.ListVisible()));
                    break;
                case "focus" when request.Id is not null:
                    if (!_s.Windows.Activate(request.Id))
                        _conn.SendControl(ServerMessages.Stage("window", "Windows did not allow activation of that window."));
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("window operation rejected: " + ex.GetType().Name);
            _conn.SendControl(ServerMessages.Stage("window", "The chosen application window is no longer available."));
        }
    }

    // Clipboard transfers are strictly user-triggered. A null text requests PC -> phone;
    // a non-null text writes phone -> PC. Never poll, broadcast, or retain clipboard data.
    private async Task HandleClipboardAsync(ClientClipboard request, CancellationToken ct)
    {
        if (request.Mime != "text/plain" || _s.Clipboard is null)
        {
            _conn.SendControl(ServerMessages.Stage("clipboard", "Only explicit text/plain clipboard exchange is available."));
            return;
        }
        const int maxUtf8Bytes = 64 * 1024;
        try
        {
            if (request.Text is not null)
            {
                if (System.Text.Encoding.UTF8.GetByteCount(request.Text) > maxUtf8Bytes)
                {
                    _conn.SendControl(ServerMessages.Stage("clipboard", "Phone clipboard text exceeds 64 KiB."));
                    return;
                }
                await _s.Clipboard.WriteTextAsync(request.Text, ct).ConfigureAwait(false);
            }
            else
            {
                var text = await _s.Clipboard.ReadTextAsync(ct).ConfigureAwait(false);
                if (text is null) return;
                if (System.Text.Encoding.UTF8.GetByteCount(text) > maxUtf8Bytes)
                {
                    _conn.SendControl(ServerMessages.Stage("clipboard", "PC clipboard text exceeds 64 KiB."));
                    return;
                }
                _conn.SendControl(ServerMessages.ClipboardText(text));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No clipboard contents in diagnostics; failures are not fatal to video.
            _log.Warn("clipboard operation failed: " + ex.GetType().Name);
            _conn.SendControl(ServerMessages.Stage("clipboard", "Windows clipboard is currently unavailable; retry the action."));
        }
    }
    private bool GeometryChanged(PhoneProfile next) =>
        !float.IsFinite(next.Dpr) ||
        next.PhysicalW != _profile.PhysicalW ||
        next.PhysicalH != _profile.PhysicalH ||
        Math.Abs(next.Dpr - _profile.Dpr) > 0.001f ||
        next.DiagonalMm != _profile.DiagonalMm;
    private async Task HousekeepingAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            tick++;
            _backend.Heartbeat(); // feeds the driver watchdog
            // Mandatory: the client's socket times out after 20 s of silence and a static
            // desktop sends no frames at all.
            if (tick % 2 == 0) _conn.SendPing();
            if (tick % 5 == 0)
            {
                var report = _guard.Verify(_before);
                _conn.SendControl(ServerMessages.Invariant(report.Clean, report.Drift));
                if (!report.Clean) _log.Warn("physical display drift: " + string.Join("; ", report.Drift));
            }
            if (Interlocked.Read(ref _framesSeen) == 0)
            {
                if (tick == 4)
                    _conn.SendControl(ServerMessages.Stage("Waiting for the first frame", "The phone display is up but Windows has not drawn to it yet."));
                if (tick >= 15)
                    throw new DisplayException("capture", "The native WGC/NVENC pipeline has not delivered a frame.",
                        "Verify the source HWND is visible, at the target DPI, and NVENC is available.");
            }
        }
    }
    private string DescribeNoFrames()
    {
        var h = _backend.ReadDriverHealth();
        if (!h.Reachable) return "The display driver is not responding. Reinstall it (see the host README).";
        if (h.SurfaceWin32Error != 0) return $"The driver could not create the shared frame surface (Win32 error {h.SurfaceWin32Error}).";
        if (h.LastError == DriverContract.ErrUnsupportedFormat)
            return "Windows is sending HDR / advanced-colour frames the driver cannot carry. Turn HDR off for the phone display.";
        if (h.LastError == DriverContract.ErrFrameTooLarge) return "The monitor mode is larger than the driver's frame limit.";
        return $"Driver status: dropped {h.FramesDropped}, published {h.FramesPublished}, last error 0x{h.LastError:X8}.";
    }
    private void Fatal(string stage, string detail, string? remediation) =>
        _conn.SendControl(ServerMessages.Stage(stage, detail, remediation, fatal: true));
    private async Task TeardownAsync()
    {
        _downloadStop?.Cancel();
        if (_downloadTask is not null)
        {
            try { await _downloadTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* tearing down authenticated session */ }
        }
        _downloadStop?.Dispose();
        // A phone Bye ends the control worker but not necessarily the socket reader.
        // Close authenticated transport BEFORE tearing down the virtual monitor so
        // no late input or file request can race with driver departure. The outer
        // await-using disposal is idempotent.
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("closing phone transport: " + ex.Message); }
        _uploads?.Dispose(); // removes incomplete encrypted-upload parts on disconnect
        try { _input?.ReleaseAll(); } catch (Exception ex) { _log.Warn("releasing input: " + ex.Message); }
        var video = _video;
        _video = null;
        if (video is not null)
        {
            try { await video.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("GPU pipeline cleanup: " + ex.Message); }
        }
        if (_backend is not null)
        {
            try { RestoreAdaptiveScale(); }
            catch (Exception ex) { _log.Warn("restoring the virtual monitor's original scale: " + ex.Message); }
            try { await _backend.RemoveMonitorAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("removing the phone monitor: " + ex.Message); }
            try { await _backend.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
        if (_guard is not null && _before is not null)
        {
            var report = _guard.Verify(_before);
            _log.Info(report.Clean
                ? "restore check: physical displays unchanged"
                : "restore check FAILED: " + string.Join("; ", report.Drift));
        }
    }
}

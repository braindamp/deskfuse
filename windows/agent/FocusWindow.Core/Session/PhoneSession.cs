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
    private string _encoderName;
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
    private IVideoEncoder? _encoder;
    private IHostAudioCapture? _pcAudio;
    private int _pcAudioStreamId;
    private FileUploadReceiver? _uploads;
    private CancellationTokenSource? _downloadStop;
    private Task? _downloadTask;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _version;
    private int _fps;
    private volatile bool _resync;
    private volatile bool _keyframeRequested;
    private long _lastRestartTick;
    private int _restartCount;
    private long _framesSeen;
    private readonly long _ptsOrigin = Stopwatch.GetTimestamp();
    private long _lastPtsUs;
    private byte[] _frameBuf = Array.Empty<byte>();
    // A wider backing monitor is NEVER encoded at backing resolution:
    // the phone gets only its exact native-pixel top-left viewport.
    private byte[] _visibleFrameBuf = Array.Empty<byte>();
    private int _frameBytes;
    public PhoneSession(HostOptions o, HostServices s, string encoderName, ILog log)
    {
        _o = o;
        _s = s;
        _encoderName = encoderName;
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
        catch (EncoderUnavailableException ex)
        {
            _log.Error("encoder: " + ex.Message);
            Fatal("encoder", ex.Message, "Make sure ffmpeg.exe sits next to the agent, or pass --ffmpeg <path>.");
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
        using var frames = _backend.OpenFrameSource();
        _geometry = ViewportInputGeometry(_outcome);
        _version = 1;
        await SelectNativeEncoderAsync(ct).ConfigureAwait(false);
        _encoder = StartEncoder(_version);
        SendTarget();
        await SessionLifetime.RunUntilFirstStopAsync(
            control: ControlLoopAsync,
            video: token => FrameLoopAsync(frames, token),
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

    private static InputGeometry ViewportInputGeometry(NegotiationOutcome outcome)
    {
        var bounds = outcome.Monitor.Bounds;
        var width = outcome.Plan.PixelW;
        var height = outcome.Plan.PixelH;
        // Input coordinates and the streamed pixels share the SAME origin
        // and 1:1 size. Never stretch touch across the hidden backing region.
        var viewport = outcome.Plan.Kind == PlanKind.VerifiedViewport
            ? new RectI(bounds.X, bounds.Y, width, height)
            : bounds;
        return new InputGeometry(viewport, width, height);
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

    private async Task SelectNativeEncoderAsync(CancellationToken ct)
    {
        var plan = _outcome.Plan;
        _conn.SendControl(ServerMessages.Stage("Preparing GPU video", $"Qualifying H.264 encoder at {plan.PixelW}×{plan.PixelH} native pixels."));
        var target = new EncoderSettings(plan.PixelW, plan.PixelH, _fps, _o.BitrateKbps, _o.FfmpegPath, "libx264");
        _encoderName = await EncoderPicker.PickForTargetAsync(target, _log, ct).ConfigureAwait(false);
        _conn.SendControl(ServerMessages.Stage("Video encoder ready", $"{_encoderName} can produce native {plan.PixelW}×{plan.PixelH} H.264 frames."));
    }
    private IVideoEncoder StartEncoder(long version)
    {
        var settings = new EncoderSettings(
            _outcome.Plan.PixelW, _outcome.Plan.PixelH, _fps, _o.BitrateKbps, _o.FfmpegPath, _encoderName);
        return FfmpegEncoder.Start(settings, au => OnAccessUnit(au, version), _log);
    }
    private ValueTask OnAccessUnit(EncodedAccessUnit au, long version)
    {
        if (version != Interlocked.Read(ref _version)) return ValueTask.CompletedTask; // output of a retired encoder
        // After a drop, inter frames are undecodable. Skip them until a keyframe starts the
        // stream again; the frame loop is already restarting the encoder to produce one.
        if (_resync)
        {
            if (!au.Keyframe) return ValueTask.CompletedTask;
            _resync = false;
        }
        if (!_conn.TrySendFrame(au.Data, au.Keyframe, NextPtsUs(), version))
        {
            _resync = true; // the phone cannot keep up: restart from a keyframe
        }
        return ValueTask.CompletedTask;
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
        var encoder = _encoder!;
        var record = new TargetRecord(
            Version: Interlocked.Read(ref _version),
            Occupant: OccupantKind.Canvas,
            WidthPx: plan.PixelW,
            HeightPx: plan.PixelH,
            Dpi: _outcome.EffectiveDpi,
            CanvasDpW: (int)Math.Round(plan.PixelW * 96.0 / _outcome.EffectiveDpi),
            CanvasDpH: (int)Math.Round(plan.PixelH * 96.0 / _outcome.EffectiveDpi),
            Native: _outcome.Native,
            Method: _outcome.Method,
            Codec: encoder.CodecName,
            PixelFormat: encoder.PixelFormat,
            RefreshHz: Volatile.Read(ref _fps),
            DegradedReason: _outcome.DegradedReason,
            State: StateModel.SharedView,
            AppLabel: "Phone display (desktop canvas)",
            DocumentLabel: null);
        _conn.SendControl(ServerMessages.Target(record));
    }
    // ------------------------------------------------------------------------------------
    private async Task FrameLoopAsync(IFrameSource frames, CancellationToken ct)
    {
        var dirty = false;
        long nextDueMs = 0;
        var mismatches = 0;
        while (!ct.IsCancellationRequested)
        {
            var nowMs = Environment.TickCount64;
            var wait = dirty ? (int)Math.Clamp(nextDueMs - nowMs, 1, 100) : 100;
            if (frames.TryRead(wait, ref _frameBuf, out var info))
            {
                var plan = _outcome.Plan;
                var backing = _outcome.Monitor.Bounds;
                if (info.Width != backing.Width || info.Height != backing.Height)
                {
                    // Right after a mode change a stale frame of the old size can still arrive.
                    // A persistent mismatch is real: the driver is not delivering the target.
                    if (++mismatches > 60)
                        throw new DisplayException("mode",
                            $"The display driver delivers {info.Width}x{info.Height} but the backing monitor is {backing.Width}x{backing.Height}.",
                            "Windows picked a different mode for the phone monitor. Check Display settings, then reconnect.");
                    continue;
                }
                mismatches = 0;
                _frameBytes = info.Width * info.Height * 4;
                if (plan.Kind == PlanKind.VerifiedViewport)
                {
                    var visibleBytes = checked(plan.PixelW * plan.PixelH * 4);
                    if (_visibleFrameBuf.Length != visibleBytes)
                        _visibleFrameBuf = new byte[visibleBytes];
                    DprChallengeTheory.CopyViewportBgra(
                        _frameBuf.AsSpan(0, _frameBytes), info.Width, info.Height,
                        new RectI(0, 0, plan.PixelW, plan.PixelH),
                        _visibleFrameBuf);
                }
                Interlocked.Increment(ref _framesSeen);
                dirty = true;
            }
            nowMs = Environment.TickCount64;
            // The fps cap must never swallow the LAST frame of a burst: a frame skipped
            // because it arrived early stays "dirty" and is submitted at the next interval,
            // so the picture on the phone always converges to the PC's.
            if (dirty && nowMs >= nextDueMs)
            {
                await SubmitFrameAsync(ct).ConfigureAwait(false);
                dirty = false;
                nextDueMs = nowMs + 1000 / Math.Max(1, Volatile.Read(ref _fps));
            }
            if (_keyframeRequested || _resync || (_encoder?.HasExited ?? false))
            {
                _keyframeRequested = false;
                await RestartEncoderAsync(ct).ConfigureAwait(false);
            }
        }
    }
    private async Task SubmitFrameAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_encoder is null || _frameBytes == 0) return;
            using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
            write.CancelAfter(TimeSpan.FromSeconds(5)); // a wedged ffmpeg must not wedge the session
            try
            {
                await _encoder.WriteFrameAsync(
                    _outcome.Plan.Kind == PlanKind.VerifiedViewport
                        ? _visibleFrameBuf.AsMemory(0, checked(_outcome.Plan.PixelW *
                            _outcome.Plan.PixelH * 4))
                        : _frameBuf.AsMemory(0, _frameBytes),
                    write.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EncoderUnavailableException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _log.Warn("encoder stalled or died; restarting: " + ex.Message);
                _resync = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    /// <summary>
    /// ffmpeg cannot be told to emit an IDR on demand, so a keyframe request (or a resync after a
    /// slow phone) restarts the process and immediately re-feeds the newest frame. Rate-limited
    /// so a misbehaving client cannot make the host thrash.
    /// </summary>
    private async Task RestartEncoderAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastRestartTick < 1500) return;
            if (++_restartCount > 12)
                throw new EncoderUnavailableException(
                    "The video encoder keeps failing. " + ((_encoder as FfmpegEncoder)?.StderrTail ?? ""));
            _lastRestartTick = now;
            var old = _encoder;
            _encoder = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            _resync = true; // output of the new process is accepted from its first keyframe on
            _encoder = StartEncoder(Interlocked.Read(ref _version));
            if (_frameBytes > 0)
            {
                var view = _outcome.Plan.Kind == PlanKind.VerifiedViewport
                    ? _visibleFrameBuf.AsMemory(0, checked(_outcome.Plan.PixelW *
                        _outcome.Plan.PixelH * 4))
                    : _frameBuf.AsMemory(0, _frameBytes);
                await _encoder.WriteFrameAsync(view, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task RenegotiateAsync(PhoneProfile next, CancellationToken ct)
    {
        var invalidProfile = next.Validate();
        if (invalidProfile.Length != 0)
            throw new DisplayException("profile",
                "Phone sent an invalid updated display profile: " + invalidProfile);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _log.Info($"profile changed: {_profile.PhysicalW}x{_profile.PhysicalH}@{_profile.Dpr} -> {next.PhysicalW}x{next.PhysicalH}@{next.Dpr}");
            _profile = next;
            var old = _encoder;
            _encoder = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            RestoreAdaptiveScale();
            _outcome = await NegotiatePhoneDisplayAsync(next, ct).ConfigureAwait(false);
            _s.OnNegotiated?.Invoke(_outcome);
            _geometry = ViewportInputGeometry(_outcome);
            _frameBytes = 0; // the old frame is the wrong size now
            var version = Interlocked.Increment(ref _version);
            _resync = true;
            await SelectNativeEncoderAsync(ct).ConfigureAwait(false);
            _encoder = StartEncoder(version);
            SendTarget();
        }
        finally
        {
            _gate.Release();
        }
    }
    // ------------------------------------------------------------------------------------
    private async Task ControlLoopAsync(CancellationToken ct)
    {
        await foreach (var message in _conn.Messages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case ClientProfile p when GeometryChanged(p.Profile):
                    await RenegotiateAsync(p.Profile, ct).ConfigureAwait(false);
                    break;
                case ClientRefresh r:
                    Volatile.Write(ref _fps, Math.Clamp(Math.Min(r.Hz, _o.Fps), 1, 240));
                    break;
                case ClientKeyframeRequest:
                    _keyframeRequested = true;
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
                case ClientAudio audio:
                    HandlePcAudio(audio.Action);
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

    private void HandlePcAudio(string action)
    {
        if (action == "stop_pc")
        {
            StopPcAudio();
            _conn.SendControl(ServerMessages.AudioState("stopped", _pcAudioStreamId));
            return;
        }
        if (action != "start_pc" || _s.CreateAudioCapture is null)
        {
            _conn.SendControl(ServerMessages.AudioState("error", _pcAudioStreamId,
                detail: "Windows playback capture unavailable."));
            return;
        }
        StopPcAudio();
        try
        {
            var capture = _s.CreateAudioCapture();
            _pcAudio = capture;
            var streamId = ++_pcAudioStreamId;
            _conn.SendControl(ServerMessages.AudioState("started", streamId,
                capture.SampleRate, capture.Channels));
            capture.Start(samples => _conn.TrySendAudio(samples.Span, streamId));
        }
        catch (Exception ex)
        {
            _log.Warn("PC audio capture unavailable: " + ex.GetType().Name);
            StopPcAudio();
            _conn.SendControl(ServerMessages.AudioState("error", _pcAudioStreamId,
                detail: "Could not capture the selected Windows playback endpoint."));
        }
    }

    private void StopPcAudio()
    {
        var capture = _pcAudio;
        _pcAudio = null;
        if (capture is null) return;
        try { capture.Dispose(); }
        catch (Exception ex) { _log.Warn("stopping PC audio: " + ex.GetType().Name); }
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
                    throw new DisplayException("capture", "The display driver never delivered a frame.", DescribeNoFrames());
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
        StopPcAudio();
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
        var encoder = _encoder;
        _encoder = null;
        if (encoder is not null)
        {
            try { await encoder.DisposeAsync().ConfigureAwait(false); } catch { /* process already gone */ }
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

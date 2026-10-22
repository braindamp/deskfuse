using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

/// <summary>
/// The phone speaker route: one enrolled phone's own Windows playback
/// endpoint, the audio Windows renders into it, and the volume control.
///
/// The endpoint is a real render device. The user selects it like any other
/// Windows output; whatever the PC plays into it is sampled from that
/// endpoint alone and streamed to the phone over the encrypted transport.
/// The PC's default output is never captured on this route.
/// </summary>
internal sealed class PhoneSpeakerRoute(
    PhoneConnection conn,
    HostServices services,
    string peerId,
    string label,
    ILog log) : IAsyncDisposable
{
    private IPhoneSpeakerEndpoint? _endpoint;
    private IHostAudioCapture? _capture;
    // Timestamp is attached at the existing enqueue point; the buffer is
    // still owned by the queue and sent unchanged. The struct adds no heap
    // allocation and does not change pacing or packet size.
    private readonly record struct TimedPcm(byte[] Data, long EnqueuedQpc);
    private System.Threading.Channels.Channel<TimedPcm>? _paced;
    private CancellationTokenSource? _paceCancellation;
    private Task? _paceTask;
    private long _sentPackets;
    private long _droppedPackets;
    private long _routeEvicted;
    private long _routeRejected;
    private long _routeDequeued;
    private long _paceResets;
    private long _paceDelayCalls;
    private long _paceOversleep5;
    private long _paceOversleep10;
    private long _paceOversleepMaxMs;
    private long _routeAgeMaxMs;
    private long _routeAgeOver50;
    private long _routeAgeOver100;
    private long _lastReport;
    // Windows may produce dozens of CoreAudio notifications while a tray
    // volume thumb is dragged. Do not forward each event to Android's
    // discrete system media-volume API: it triggers audible gain steps.
    // Only the newest level is sent, at most once per short interval.
    private int _pendingVolume = -1;
    private int _lastForwardedVolume = -1;
    private CancellationTokenSource? _volumeStop;
    private Task? _volumePump;
    private int _streamId;
    private int _disposed;
    private IReadOnlyList<string> _requested = Array.Empty<string>();
    private CancellationToken _ct = CancellationToken.None;

    /// <summary>
    /// Provisions the phone's endpoint when it asked for the speakers
    /// capability, and returns the capabilities that are really active.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProvisionAsync(
        IReadOnlyList<string> requested, CancellationToken ct)
    {
        _requested = requested;
        _ct = ct;
        if (!requested.Contains(DeviceCapabilities.Speakers)) return Array.Empty<string>();
        if (services.PhoneSpeakers is null)
        {
            log.Warn("The phone asked for speakers but this host has no phone speaker provider.");
            return Array.Empty<string>();
        }
        var (endpoint, error) = await services.PhoneSpeakers
            .ProvisionAsync(peerId, label, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            log.Warn("Phone speaker endpoint unavailable: " + (error ?? "unknown reason"));
            conn.SendControl(ServerMessages.Stage("speakers", UserFacing.For("speakers"),
                "Try again in a moment.", technical: error));
            return Array.Empty<string>();
        }
        _endpoint = endpoint;
        endpoint.VolumeChanged += OnVolumeChanged;
        log.Info($"Phone speaker endpoint ready: {endpoint.FriendlyName}");
        var initialVolume = VolumePercent(endpoint.Volume);
        Volatile.Write(ref _lastForwardedVolume, initialVolume);
        conn.SendControl(ServerMessages.AudioVolume(initialVolume));
        _volumeStop = new CancellationTokenSource();
        _volumePump = ForwardVolumeChangesAsync(_volumeStop.Token);
        return new[] { DeviceCapabilities.Speakers };
    }

    /// <summary>
    /// The phone's own volume slider moved: the endpoint follows it, so the
    /// two controls are one control.
    /// </summary>
    public void HandleVolume(int level)
    {
        if (_endpoint is null) return;
        // Prevent echo of the Android slider back to the same phone.
        Interlocked.Exchange(ref _pendingVolume, -1);
        Volatile.Write(ref _lastForwardedVolume, level);
        try { _endpoint.SetVolume(level / 100f); }
        catch (Exception ex) { log.Warn("applying the phone volume: " + ex.Message); }
    }

    /// <summary>Starts or stops the audio stream for this phone's endpoint.</summary>
    public async Task HandleAsync(ClientAudio request)
    {
        if (request.Action == "stop_phone_speakers")
        {
            await StopCaptureAsync().ConfigureAwait(false);
            conn.SendControl(ServerMessages.AudioState("stopped", _streamId));
            return;
        }
        if (_endpoint is null)
        {
            // The phone may ask again after the provider became available.
            var active = await ProvisionAsync(_requested, _ct).ConfigureAwait(false);
            if (active.Contains(DeviceCapabilities.Speakers) && _endpoint is not null)
            {
                conn.SendControl(ServerMessages.BridgeState(
                    _requested, DeviceCapabilities.ActiveForAccessory(active)));
            }
            else
            {
                conn.SendControl(ServerMessages.AudioState("error", _streamId,
                    detail: "This phone has no Windows playback endpoint."));
                return;
            }
        }
        if (services.CreateEndpointAudioCapture is null)
        {
            conn.SendControl(ServerMessages.AudioState("error", _streamId,
                detail: UserFacing.For("speakers")));
            return;
        }
        await StopCaptureAsync().ConfigureAwait(false);
        try
        {
            // Only this phone's own endpoint is captured. A missing endpoint
            // fails closed: the PC's default output is never substituted.
            var capture = services.CreateEndpointAudioCapture(
                _endpoint.EndpointId, request.Layouts ?? Array.Empty<string>(), request.MaxPcmBytes);
            if (capture is null)
                throw new InvalidOperationException("The phone speaker endpoint is not available.");
            _capture = capture;
            var streamId = ++_streamId;
            var queue = System.Threading.Channels.Channel.CreateBounded<TimedPcm>(
                new System.Threading.Channels.BoundedChannelOptions(16)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                },
                // DropOldest's TryWrite returns true EVEN WHEN old PCM was lost.
                // The .NET 8 callback reports actual evictions without a new queue policy.
                itemDropped: _ => Interlocked.Increment(ref _routeEvicted));
            _paced = queue;
            _paceCancellation = new CancellationTokenSource();
            _paceTask = PaceAsync(queue.Reader, streamId, capture.SampleRate,
                capture.Channels, _paceCancellation.Token);
            conn.SendControl(ServerMessages.AudioState("started", streamId,
                capture.SampleRate, capture.Channels, layout: capture.Layout));
            capture.Start(samples =>
            {
                // The capture delivers in bursts; the phone plays steadily.
                // Releasing the packets evenly keeps the phone's output
                // buffer from emptying between bursts, which is an audible
                // pop. The bytes are the same either way.
                var packet = new TimedPcm(samples.ToArray(),
                    System.Diagnostics.Stopwatch.GetTimestamp());
                if (!queue.Writer.TryWrite(packet))
                    Interlocked.Increment(ref _routeRejected);
            });
            // Tell the phone where this stream sits on the host's clock, so it
            // can measure the sound's journey against the picture's.
            conn.SendControl(ServerMessages.AudioClock(streamId,
                FocusWindow.Core.Media.HostClock.NowMicroseconds(), capture.SampleRate));
        }
        catch (Exception ex)
        {
            log.Warn("phone speaker capture unavailable: " + ex.Message);
            await StopCaptureAsync().ConfigureAwait(false);
            conn.SendControl(ServerMessages.AudioState("error", _streamId,
                detail: UserFacing.For("speakers"), technical: ex.Message));
        }
    }

    /// <summary>
    /// Sends the captured audio to the phone at the rate it was captured,
    /// so the phone's output buffer stays at a steady level.
    /// </summary>
    private async Task PaceAsync(System.Threading.Channels.ChannelReader<TimedPcm> reader,
        int streamId, int sampleRate, int channels, CancellationToken ct)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        var bytesPerSecond = checked(sampleRate * channels * 2);
        var nextPacket = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - nextPacket > frequency / 10)
                {
                    Interlocked.Increment(ref _paceResets);
                    nextPacket = now;
                }
                while (reader.TryRead(out var packet))
                {
                    Interlocked.Increment(ref _routeDequeued);
                    var remaining = nextPacket - System.Diagnostics.Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        var beforeDelay = System.Diagnostics.Stopwatch.GetTimestamp();
                        await Task.Delay(TimeSpan.FromSeconds(remaining / (double)frequency), ct).ConfigureAwait(false);
                        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - beforeDelay;
                        var overshootTicks = Math.Max(0L, elapsed - remaining);
                        var overshootMs = (long)(overshootTicks * 1000.0 / frequency);
                        Interlocked.Increment(ref _paceDelayCalls);
                        if (overshootTicks > frequency / 200) Interlocked.Increment(ref _paceOversleep5);
                        if (overshootTicks > frequency / 100) Interlocked.Increment(ref _paceOversleep10);
                        if (overshootMs > _paceOversleepMaxMs) _paceOversleepMaxMs = overshootMs;
                    }
                    ct.ThrowIfCancellationRequested();
                    // Time spent behind both the route channel and existing pacer.
                    // This is not end-to-end latency or capture-to-phone time.
                    var ageMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() -
                        packet.EnqueuedQpc) * 1000.0 / frequency);
                    if (ageMs > _routeAgeMaxMs) _routeAgeMaxMs = ageMs;
                    if (ageMs > 50) Interlocked.Increment(ref _routeAgeOver50);
                    if (ageMs > 100) Interlocked.Increment(ref _routeAgeOver100);
                    if (conn.TrySendAudio(packet.Data, streamId)) _sentPackets++;
                    else _droppedPackets++;
                    ReportAudioHealth();
                    // Accumulate sample time rather than timer overshoot: a
                    // coarse Windows timer must not make every packet late.
                    nextPacket += (long)(packet.Data.Length * (double)frequency / bytesPerSecond);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Reports how much audio reached the phone and how much the transport
    /// could not take. A dropped packet is an audible click, so this is the
    /// difference between "the phone is at fault" and "the transport is".
    /// </summary>
    private void ReportAudioHealth()
    {
        var now = Environment.TickCount64;
        if (now - _lastReport < 2000) return;
        _lastReport = now;
        log.Info($"phone audio: {_sentPackets} packets queued, {_droppedPackets} rejected" +
            $" | routeEvict={Interlocked.Read(ref _routeEvicted)} routeReject={Interlocked.Read(ref _routeRejected)}" +
            $" routeDeq={Interlocked.Read(ref _routeDequeued)}" +
            $" ageMaxMs={Interlocked.Exchange(ref _routeAgeMaxMs, 0)}" +
            $" age>50ms={Interlocked.Exchange(ref _routeAgeOver50, 0)}" +
            $" age>100ms={Interlocked.Exchange(ref _routeAgeOver100, 0)}" +
            $" paceReset={Interlocked.Read(ref _paceResets)}" +
            $" delayCalls={Interlocked.Exchange(ref _paceDelayCalls, 0)}" +
            $" oversleep>5ms={Interlocked.Exchange(ref _paceOversleep5, 0)}" +
            $" oversleep>10ms={Interlocked.Exchange(ref _paceOversleep10, 0)}" +
            $" oversleepMaxMs={Interlocked.Exchange(ref _paceOversleepMaxMs, 0)}" +
            " | " + conn.AudioHealth +
            (_capture is null ? "" : " | " + _capture.Health));
    }

    private void OnVolumeChanged(float volume) =>
        Interlocked.Exchange(ref _pendingVolume, VolumePercent(volume));

    private async Task ForwardVolumeChangesAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var level = Interlocked.Exchange(ref _pendingVolume, -1);
                if (level < 0 || level == Volatile.Read(ref _lastForwardedVolume)) continue;
                Volatile.Write(ref _lastForwardedVolume, level);
                conn.SendControl(ServerMessages.AudioVolume(level));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static int VolumePercent(float volume) =>
        (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100f);

    private async Task StopCaptureAsync()
    {
        var cancellation = _paceCancellation;
        _paceCancellation = null;
        cancellation?.Cancel();
        _paced?.Writer.TryComplete();
        _paced = null;
        var pace = _paceTask;
        _paceTask = null;
        var capture = _capture;
        _capture = null;
        try { capture?.Dispose(); }
        catch (Exception ex) { log.Warn("stopping the phone speaker capture: " + ex.Message); }
        try { if (pace is not null) await pace.ConfigureAwait(false); }
        finally { cancellation?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopCaptureAsync().ConfigureAwait(false);
        if (_endpoint is not null)
        {
            _endpoint.VolumeChanged -= OnVolumeChanged;
            var volumeStop = _volumeStop;
            _volumeStop = null;
            volumeStop?.Cancel();
            var volumePump = _volumePump;
            _volumePump = null;
            try { if (volumePump is not null) await volumePump.ConfigureAwait(false); }
            finally { volumeStop?.Dispose(); }
            try { await _endpoint.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.Warn("releasing the phone speaker endpoint: " + ex.Message); }
            _endpoint = null;
            // Windows may still be playing through an endpoint that is about
            // to disappear. Say so plainly rather than leaving the machine
            // pointing at a device that is gone.
            conn.SendControl(ServerMessages.Stage("speakers",
                "The phone's speaker is no longer available. If Windows was playing through it, choose another output."));
        }
    }
}

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
    private System.Threading.Channels.Channel<byte[]>? _paced;
    private CancellationTokenSource? _paceCancellation;
    private Task? _paceTask;
    private long _sentPackets;
    private long _droppedPackets;
    private long _lastReport;
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
        conn.SendControl(ServerMessages.AudioVolume(VolumePercent(endpoint.Volume)));
        return new[] { DeviceCapabilities.Speakers };
    }

    /// <summary>
    /// The phone's own volume slider moved: the endpoint follows it, so the
    /// two controls are one control.
    /// </summary>
    public void HandleVolume(int level)
    {
        if (_endpoint is null) return;
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
            var queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                new System.Threading.Channels.BoundedChannelOptions(16)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                });
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
                var packet = samples.ToArray();
                queue.Writer.TryWrite(packet);
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
    private async Task PaceAsync(System.Threading.Channels.ChannelReader<byte[]> reader,
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
                if (now - nextPacket > frequency / 10) nextPacket = now;
                while (reader.TryRead(out var packet))
                {
                    var remaining = nextPacket - System.Diagnostics.Stopwatch.GetTimestamp();
                    if (remaining > 0)
                        await Task.Delay(TimeSpan.FromSeconds(remaining / (double)frequency), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (conn.TrySendAudio(packet, streamId)) _sentPackets++;
                    else _droppedPackets++;
                    ReportAudioHealth();
                    // Accumulate sample time rather than timer overshoot: a
                    // coarse Windows timer must not make every packet late.
                    nextPacket += (long)(packet.Length * (double)frequency / bytesPerSecond);
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
        log.Info($"phone audio: {_sentPackets} packets sent, {_droppedPackets} dropped" +
            (_capture is null ? "" : " | " + _capture.Health));
    }

    private void OnVolumeChanged(float volume) =>
        conn.SendControl(ServerMessages.AudioVolume(VolumePercent(volume)));

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

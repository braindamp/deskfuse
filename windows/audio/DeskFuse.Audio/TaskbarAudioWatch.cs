using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskFuse.Audio;

/// <summary>
/// Read-only comparison of every active render endpoint while the user operates
/// the *actual* Windows taskbar volume flyout. No tones, default changes, volume
/// writes, broker calls, registry edits or audio files are involved.
/// </summary>
internal static class TaskbarAudioWatch
{
    private const float SignalThreshold = 80f / 32768f;

    internal static async Task<int> RunAsync(int seconds, CancellationToken cancellationToken)
    {
        if (seconds is < 5 or > 120)
            throw new ArgumentException("--seconds must be between 5 and 120.");

        using var enumerator = new MMDeviceEnumerator();
        Console.WriteLine("DeskFuse taskbar audio watch (READ ONLY; no test sounds generated).");
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try
            {
                using var selected = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                Console.WriteLine($"Default {role}: {selected.FriendlyName} [{selected.ID}]");
            }
            catch (COMException ex)
            {
                Console.WriteLine($"Default {role}: unavailable ({ex.HResult:X8})");
            }
        }

        var watches = new List<EndpointWatch>();
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var watch = new EndpointWatch(device);
                    watches.Add(watch); // watch owns the MMDevice
                    watch.Start();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or
                                           NotSupportedException or ArgumentException)
                {
                    // A stale endpoint must not prevent observing the others.
                    Console.WriteLine($"Cannot watch {device.ID}: {ex.Message}");
                    if (!watches.Any(w => ReferenceEquals(w.Device, device)))
                        device.Dispose();
                }
            }
            if (watches.Count == 0)
            {
                Console.Error.WriteLine("No active render endpoints could be observed.");
                return 1;
            }

            Console.WriteLine($"Watching {watches.Count} endpoint(s) for {seconds} seconds.");
            Console.WriteLine("First move/release the TASKBAR popup slider; then repeat with the broad Volume Mixer.");
            Console.WriteLine("Leave music/videos paused. Mark the two actions in your notes using the UTC log timestamps.");
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            duration.CancelAfter(TimeSpan.FromSeconds(seconds));
            while (!duration.IsCancellationRequested)
            {
                try { await Task.Delay(250, duration.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (duration.IsCancellationRequested) { break; }
                var now = DateTimeOffset.UtcNow;
                foreach (var watch in watches) watch.Report(now);
            }

            Console.WriteLine("Observation complete. Signal means loopback PCM exceeded 80/32768;");
            Console.WriteLine("absence means no above-threshold samples, NOT proof Windows created no stream.");
            foreach (var watch in watches) watch.Summary();
            return 0;
        }
        finally
        {
            foreach (var watch in watches) watch.Dispose();
        }
    }

    private sealed class EndpointWatch : IDisposable
    {
        private readonly WasapiLoopbackCapture _capture;
        private readonly WaveFormat _format;
        private readonly WaveFormatEncoding _encoding;
        private readonly int _bytesPerSample;
        private readonly Dictionary<string, bool> _activeSessions = new();
        private long _capturedPackets;
        private long _signalPackets;
        private long _lastReportedPackets;
        private float _lastVolume = -1f;
        private bool _lastMute;
        private DateTimeOffset _lastSessions;
        private Exception? _captureError;
        private bool _started;

        internal MMDevice Device { get; }

        internal EndpointWatch(MMDevice device)
        {
            Device = device;
            _capture = new WasapiLoopbackCapture(device);
            _format = _capture.WaveFormat;
            _encoding = _format is WaveFormatExtensible ext
                ? ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.IeeeFloat
                    : ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                        ? WaveFormatEncoding.Pcm : WaveFormatEncoding.Unknown
                : _format.Encoding;
            _bytesPerSample = _format.BitsPerSample / 8;
            if ((_encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample is not (32 or 64)) ||
                (_encoding == WaveFormatEncoding.Pcm && _format.BitsPerSample is not (8 or 16 or 24 or 32)) ||
                (_encoding is not WaveFormatEncoding.Pcm and not WaveFormatEncoding.IeeeFloat) ||
                _format.BlockAlign <= 0 || _bytesPerSample <= 0)
            {
                _capture.Dispose();
                throw new NotSupportedException($"Unsupported loopback format {_format}.");
            }
            _capture.DataAvailable += OnAudio;
            _capture.RecordingStopped += (_, args) => _captureError = args.Exception;
            Console.WriteLine($"Watching: {Device.FriendlyName} [{Device.ID}], {_format}, {_encoding}");
        }

        internal void Start()
        {
            _capture.StartRecording();
            _started = true;
        }

        private void OnAudio(object? sender, WaveInEventArgs args)
        {
            Interlocked.Increment(ref _capturedPackets);
            float peak = 0;
            var frameBytes = _format.BlockAlign;
            var complete = args.BytesRecorded / frameBytes * frameBytes;
            for (var offset = 0; offset < complete; offset += _bytesPerSample)
            {
                var bytes = args.Buffer.AsSpan(offset, _bytesPerSample);
                var value = _encoding == WaveFormatEncoding.IeeeFloat
                    ? _bytesPerSample == 4
                        ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                        : (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
                    : _bytesPerSample switch
                    {
                        1 => (bytes[0] - 128) / 128f,
                        2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                        3 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
                        4 => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0),
                        _ => 0f
                    };
                if (float.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
            }
            if (peak >= SignalThreshold)
            {
                Interlocked.Increment(ref _signalPackets);
                // Use integer bits: exchange is atomic on all supported architectures.
                var bits = BitConverter.SingleToInt32Bits(peak);
                Interlocked.Exchange(ref _peakBits, bits);
            }
        }

        private int _peakBits;
        internal void Report(DateTimeOffset now)
        {
            try
            {
                var volume = Device.AudioEndpointVolume;
                var level = volume.MasterVolumeLevelScalar;
                var mute = volume.Mute;
                if (Math.Abs(level - _lastVolume) > 0.005 || mute != _lastMute)
                {
                    Console.WriteLine($"{now:HH:mm:ss.fff}Z {Device.FriendlyName}: volume={level:F3} muted={mute}");
                    _lastVolume = level;
                    _lastMute = mute;
                }
            }
            catch (COMException) { }

            var packets = Interlocked.Read(ref _signalPackets);
            if (packets != _lastReportedPackets)
            {
                var max = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
                Console.WriteLine($"{now:HH:mm:ss.fff}Z {Device.FriendlyName}: +{packets - _lastReportedPackets} " +
                    $"signal packets, peak={max:F4}, total={packets}");
                _lastReportedPackets = packets;
            }

            if (now - _lastSessions >= TimeSpan.FromSeconds(1))
            {
                _lastSessions = now;
                ReportSessions(now);
            }
        }

        private void ReportSessions(DateTimeOffset now)
        {
            try
            {
                var manager = Device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        var system = session.IsSystemSoundsSession;
                        var peak = session.AudioMeterInformation.MasterPeakValue;
                        var key = session.GetSessionInstanceIdentifier;
                        var active = peak >= SignalThreshold;
                        var wasActive = _activeSessions.GetValueOrDefault(key);
                        if (active || (system && (active != wasActive)))
                        {
                            var pid = session.GetProcessID;
                            Console.WriteLine($"{now:HH:mm:ss.fff}Z {Device.FriendlyName}: " +
                                $"session={(system ? "SYSTEM" : "APP")} pid={pid} " +
                                $"peak={peak:F4} muted={session.SimpleAudioVolume.Mute}");
                        }
                        _activeSessions[key] = active;
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }

        internal void Summary()
        {
            Console.WriteLine($"{Device.FriendlyName} [{Device.ID}]: " +
                $"{Interlocked.Read(ref _signalPackets)} signal / " +
                $"{Interlocked.Read(ref _capturedPackets)} total loopback packets" +
                (_captureError is null ? "" : $"; capture error: {_captureError.Message}"));
        }

        public void Dispose()
        {
            try { if (_started) _capture.StopRecording(); }
            finally
            {
                _capture.Dispose();
                Device.Dispose();
            }
        }
    }
}

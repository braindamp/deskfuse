using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FocusWindow.Core.Session;
using FocusWindow.Core.Media;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace FocusWindow.Agent;

/// <summary>
/// Explicit default Windows output LOOPBACK (not a Windows per-phone render
/// device). Uses its real WASAPI mix format, including native speaker positions
/// and original sample rate. The phone may request exact 5.1/7.1 layouts.
/// Unsupported paths use a speaker-aware stereo downmix, never channel truncation.
///
/// This is an initial PCM16 transport adapter. Audio is chunked to ~10 ms and
/// the capture callback does no socket writes: PhoneConnection owns one bounded
/// audio queue and the encrypted transport writer.
/// </summary>
internal sealed class WindowsLoopbackAudioCapture : IHostAudioCapture
{
    private readonly WasapiCapture _capture;
    private readonly bool _eventSync;
    private readonly MMDevice? _device;
    private readonly WaveFormat _sourceFormat;
    private readonly int _sourceMask;
    private readonly bool _floatSource;
    private readonly int _bytesPerSourceSample;
    private readonly int _framesPerPacket;
    private bool _started;
    private bool _disposed;
    // How the capture is actually arriving. A hole in the captured stream is
    // an audible dropout, and it is not a dropped packet.
    private long _packets;
    private long _maxGapMs;
    private long _lastArrivalQpc;
    // Reset by the existing 2-second health reporter; callback only increments
    // fixed-size counters. No per-packet logging, socket I/O or allocations.
    private long _windowCallbacks;
    private long _windowGapOver20;
    private long _windowGapOver50;
    private long _windowGapOver100;
    private long _windowMaxGapMs;

    private const int Stereo = 0x3;
    private const int FiveOneBack = 0x3f;
    private const int FiveOneSide = 0x60f;
    private const int SevenOne = 0x63f;
    private const int Center = 0x4;
    private const int Lfe = 0x8;
    private const int BackLeft = 0x10;
    private const int BackRight = 0x20;
    private const int SideLeft = 0x200;
    private const int SideRight = 0x400;

    public int SampleRate => _sourceFormat.SampleRate;
    public int Channels { get; }
    public string Layout { get; }

    public WindowsLoopbackAudioCapture(
        IReadOnlyList<string> supportedPhoneLayouts, int maxPcmBytes = 3840)
        : this(null, supportedPhoneLayouts, maxPcmBytes)
    {
    }

    /// <summary>
    /// Loopback of one explicit render endpoint, for example the phone's own
    /// DeskFuse endpoint. A missing or unavailable endpoint fails closed:
    /// this capture never falls back to the PC's default output or to the
    /// mixed PC sound.
    /// </summary>
    public WindowsLoopbackAudioCapture(
        string? endpointId, IReadOnlyList<string> supportedPhoneLayouts, int maxPcmBytes = 3840)
    {
        // Explicit A/B experiment only. Preserve the empirically reliable
        // polling mode unless the launcher opts into the Windows 10 1703+
        // EVENTCALLBACK + LOOPBACK capture path.
        _eventSync = string.Equals(
            Environment.GetEnvironmentVariable("DESKFUSE_WASAPI_EVENT_SYNC"),
            "1", StringComparison.Ordinal);
        if (endpointId is not null)
        {
            MMDevice device;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(endpointId);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                           or InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException(
                    "That phone speaker endpoint is not available on this PC.", ex);
            }
            // The capture uses the device, so it has to outlive it.
            _device = device;
            _capture = new LowLatencyLoopbackCapture(device, _eventSync);
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new LowLatencyLoopbackCapture(_device, _eventSync);
        }
        try
        {
            _sourceFormat = _capture.WaveFormat;
            if (_sourceFormat.SampleRate is < 8000 or > 192000 ||
                _sourceFormat.Channels is < 1 or > 8 ||
                _sourceFormat.BitsPerSample % 8 != 0)
                throw new NotSupportedException("Windows output mix format is outside supported audio limits");

            _bytesPerSourceSample = _sourceFormat.BitsPerSample / 8;
            _floatSource = _sourceFormat is WaveFormatExtensible ext
                ? ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT
                : _sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            var pcm = _sourceFormat is WaveFormatExtensible wide
                ? wide.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM
                : _sourceFormat.Encoding == WaveFormatEncoding.Pcm;
            if (!(_floatSource && _bytesPerSourceSample == 4) &&
                !(pcm && _bytesPerSourceSample is 2 or 3 or 4))
                throw new NotSupportedException("Unsupported WASAPI mix sample encoding");

            _sourceMask = _sourceFormat is WaveFormatExtensible format
                ? ReadChannelMask(format)
                : _sourceFormat.Channels == 2 ? Stereo
                : _sourceFormat.Channels == 1 ? Center : 0;
            if (System.Numerics.BitOperations.PopCount((uint)_sourceMask) != _sourceFormat.Channels ||
                (_sourceMask & ~SevenOne) != 0)
                throw new NotSupportedException("Windows speaker positions are not supported by the current mapper");

            // Select the best real output layout. A 7.1 Windows mix may be
            // rendered on genuine Android 5.1 hardware with explicit rear
            // fold-down, not thrown all the way back to stereo.
            var negotiated = PcmSpeakerLayout.Select(_sourceMask, supportedPhoneLayouts);
            Layout = negotiated.Name;
            Channels = negotiated.Channels;
            // Older v3 phones only understand 3840-byte PCM chunks. A new
            // client explicitly advertises a larger packet budget. Never
            // issue an oversized packet just because Windows runs at 192kHz.
            _framesPerPacket = Math.Max(1, Math.Min(
                _sourceFormat.SampleRate / 100,
                Math.Clamp(maxPcmBytes, 384, 32768) / (Channels * sizeof(short))));
        }
        catch
        {
            _capture.Dispose();
            throw;
        }
    }

    // Retain the proven 20 ms NAudio loopback buffer for both A/B modes.
    // Windows 10 1703+ supports event-driven loopback with EVENTCALLBACK;
    // changing from polling is OPT-IN and does not alter endpoint flags,
    // sample conversion, packet duration, encryption or Android playback.
    private sealed class LowLatencyLoopbackCapture(MMDevice device, bool eventSync)
        : WasapiCapture(device, eventSync, 20)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    public void Start(Action<ReadOnlyMemory<byte>> onSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("Audio capture already started");
        _capture.DataAvailable += (_, args) =>
        {
            var nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            var previousQpc = _lastArrivalQpc;
            if (previousQpc != 0)
            {
                var gapMs = (long)((nowQpc - previousQpc) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
                if (gapMs > _maxGapMs) _maxGapMs = gapMs;
                if (gapMs > _windowMaxGapMs) _windowMaxGapMs = gapMs;
                if (gapMs > 20) Interlocked.Increment(ref _windowGapOver20);
                if (gapMs > 50) Interlocked.Increment(ref _windowGapOver50);
                if (gapMs > 100) Interlocked.Increment(ref _windowGapOver100);
            }
            _lastArrivalQpc = nowQpc;
            Interlocked.Increment(ref _windowCallbacks);
            Interlocked.Increment(ref _packets);
            var bytesPerFrame = _bytesPerSourceSample * _sourceFormat.Channels;
            var aligned = args.BytesRecorded / bytesPerFrame * bytesPerFrame;
            var readPosition = 0;
            Span<float> source = stackalloc float[8];
            while (readPosition < aligned)
            {
                var frames = Math.Min(_framesPerPacket, (aligned - readPosition) / bytesPerFrame);
                var size = frames * Channels * sizeof(short);
                var buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    var output = buffer.AsSpan(0, size);
                    var input = args.Buffer.AsSpan(readPosition, frames * bytesPerFrame);
                    if (Layout != "stereo")
                    {
                        var outputMask = Layout == "7.1_surround" ? SevenOne
                            : Layout == "5.1_side" ? FiveOneSide : FiveOneBack;
                        for (var frame = 0; frame < frames; frame++)
                        {
                            for (var ch = 0; ch < _sourceFormat.Channels; ch++)
                                source[ch] = ReadSample(input,
                                    (frame * _sourceFormat.Channels + ch) * _bytesPerSourceSample);
                            for (var ch = 0; ch < Channels; ch++)
                            {
                                var speaker = NthSpeakerBit(outputMask, ch);
                                // A native 7.1 route must NEVER fold separate
                                // side channels into back channels. Remap only
                                // when reducing to 5.1.
                                var value = Layout == "7.1_surround"
                                    ? SampleForSpeaker(source, speaker)
                                    : speaker switch
                                    {
                                        BackLeft => FoldRear(source, BackLeft, SideLeft),
                                        BackRight => FoldRear(source, BackRight, SideRight),
                                        SideLeft => FoldRear(source, SideLeft, BackLeft),
                                        SideRight => FoldRear(source, SideRight, BackRight),
                                        _ => SampleForSpeaker(source, speaker),
                                    };
                                WritePcm(output, (frame * Channels + ch) * 2, value);
                            }
                        }
                    }
                    else
                    {
                        for (var frame = 0; frame < frames; frame++)
                        {
                            for (var ch = 0; ch < _sourceFormat.Channels; ch++)
                                source[ch] = ReadSample(input,
                                    (frame * _sourceFormat.Channels + ch) * _bytesPerSourceSample);
                            var l = 0f;
                            var r = 0f;
                            for (var ch = 0; ch < _sourceFormat.Channels; ch++)
                            {
                                var bit = NthSpeakerBit(_sourceMask, ch);
                                var v = source[ch];
                                if (bit == 0x1) l += v;
                                else if (bit == 0x2) r += v;
                                else if (bit == Center) {
                                    var centerGain = _sourceFormat.Channels == 1 ? 1f : 0.707f;
                                    l += centerGain * v; r += centerGain * v;
                                }
                                else if (bit == Lfe) { l += 0.25f * v; r += 0.25f * v; }
                                else if (bit is BackLeft or SideLeft) l += 0.707f * v;
                                else if (bit is BackRight or SideRight) r += 0.707f * v;
                                else throw new NotSupportedException("Unknown Windows speaker position");
                            }
                            // Prevent worst-case multi-speaker summation from
                            // clipping far above original PCM headroom.
                            var gain = _sourceFormat.Channels > 2 ? 0.55f : 1f;
                            WritePcm(output, frame * 4, gain * l);
                            WritePcm(output, frame * 4 + 2, gain * r);
                        }
                    }
                    onSamples(buffer.AsMemory(0, size));
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                readPosition += frames * bytesPerFrame;
            }
        };
        _capture.StartRecording();
        _started = true;
    }

    private static int NthSpeakerBit(int mask, int index)
    {
        for (var bit = 0; bit < 32; bit++)
            if ((mask & (1 << bit)) != 0 && index-- == 0) return 1 << bit;
        return 0;
    }

    private static int ReadChannelMask(WaveFormatExtensible format)
    {
        // NAudio 2.2.1 retains dwChannelMask in its marshalled structure but
        // exposes no public property. WAVEFORMATEXTENSIBLE: 18-byte header,
        // 2-byte valid-bits union, then the native speaker mask at offset 20.
        var native = WaveFormat.MarshalToPtr(format);
        try { return Marshal.ReadInt32(native, 20); }
        finally { Marshal.FreeHGlobal(native); }
    }

    private float SampleForSpeaker(ReadOnlySpan<float> source, int speakerBit)
    {
        if ((_sourceMask & speakerBit) == 0) return 0f;
        var index = System.Numerics.BitOperations.PopCount((uint)(_sourceMask & (speakerBit - 1)));
        return source[index];
    }

    private float FoldRear(ReadOnlySpan<float> source, int primary, int secondary)
    {
        var a = SampleForSpeaker(source, primary);
        var b = SampleForSpeaker(source, secondary);
        return (_sourceMask & primary) != 0 && (_sourceMask & secondary) != 0
            ? (a + b) * 0.70710678f
            : a + b;
    }

    private float ReadSample(ReadOnlySpan<byte> input, int offset)
    {
        var chunk = input.Slice(offset, _bytesPerSourceSample);
        if (_floatSource)
        {
            var f = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(chunk));
            return float.IsFinite(f) ? Math.Clamp(f, -1f, 1f) : 0f;
        }
        return _bytesPerSourceSample switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(chunk) / 32768f,
            3 => ((chunk[0] | chunk[1] << 8 | chunk[2] << 16) << 8 >> 8) / 8388608f,
            4 => BinaryPrimitives.ReadInt32LittleEndian(chunk) / 2147483648f,
            _ => throw new NotSupportedException("Unsupported PCM sample width"),
        };
    }

    private static void WritePcm(Span<byte> output, int position, float v)
    {
        var clipped = Math.Clamp(v, -1f, 1f);
        var sample = (short)Math.Clamp((int)MathF.Round(clipped * 32767), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(output.Slice(position, 2), sample);
    }

    /// <summary>
    /// WASAPI callback arrival timing, not a measure of missing audio samples.
    /// The largest-gap counter is cumulative; window counters are reset every
    /// time the 2-second reporter reads this snapshot.
    /// </summary>
    public string? Health =>
        $"capMode={(_eventSync ? "event" : "poll")} " +
        $"{Interlocked.Read(ref _packets)} capture callbacks, longest hole {Volatile.Read(ref _maxGapMs)} ms" +
        $" | cap2s callbacks={Interlocked.Exchange(ref _windowCallbacks, 0)}" +
        $" maxGapMs={Interlocked.Exchange(ref _windowMaxGapMs, 0)}" +
        $" gap>20ms={Interlocked.Exchange(ref _windowGapOver20, 0)}" +
        $" gap>50ms={Interlocked.Exchange(ref _windowGapOver50, 0)}" +
        $" gap>100ms={Interlocked.Exchange(ref _windowGapOver100, 0)}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_started) _capture.StopRecording(); }
        finally { _capture.Dispose(); _device?.Dispose(); }
    }
}

using System.Diagnostics;
using System.Text;
namespace FocusWindow.Core.Media;
public enum CodecPreference { Auto, H264 }
public sealed record EncoderSettings(
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    string FfmpegPath,
    string EncoderName);
public interface IVideoEncoder : IAsyncDisposable
{
    /// <summary>Wire codec name, as the client's mimeFor() expects ("h264").</summary>
    string CodecName { get; }
    /// <summary>
    /// The colour format actually on the wire. This is reported to the phone verbatim: the
    /// plan promised 4:4:4 where supported, but H.264 4:4:4 is not decodable by Android
    /// MediaCodec, so the honest value here is YUV420.
    /// </summary>
    string PixelFormat { get; }
    string EncoderName { get; }
    bool HasExited { get; }
    /// <summary>Feeds one tightly packed BGRA frame of exactly Width x Height.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, CancellationToken ct);
}
public static class EncoderArgs
{
    /// <summary>
    /// Builds the ffmpeg command line. Pure and unit-tested because a wrong flag here fails
    /// silently as "no output".
    ///   * rawvideo BGRA on stdin, FLV (length-prefixed, per-packet flushed) on stdout
    ///   * wallclock timestamps + vfr: frames are emitted ONLY when the desktop changed, so a
    ///     static document costs 0 fps instead of a stream of duplicate frames (FR-REF-02)
    ///   * no B-frames, no lookahead: one frame in, one access unit out
    ///   * BT.709 limited range tagged in the stream so the phone decodes colours correctly
    /// </summary>
    public static string Build(EncoderSettings s)
    {
        var fps = Math.Clamp(s.Fps, 1, 240);
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostats ");
        sb.Append($"-f rawvideo -pix_fmt bgra -video_size {s.Width}x{s.Height} -framerate {fps} ");
        sb.Append("-use_wallclock_as_timestamps 1 -i pipe:0 -an -sn ");
        sb.Append("-vf scale=out_color_matrix=bt709:out_range=tv,format=yuv420p ");
        sb.Append("-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv ");
        var k = Math.Max(500, s.BitrateKbps);
        switch (s.EncoderName)
        {
            case "libx264":
                // Capped CRF: text stays sharp when idle, motion is bounded by maxrate.
                sb.Append("-c:v libx264 -preset ultrafast -tune zerolatency -bf 0 -g 120 ");
                sb.Append($"-crf 20 -maxrate {k}k -bufsize {Math.Max(250, k / 2)}k ");
                break;
            case "h264_nvenc":
                sb.Append("-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq 21 -bf 0 -g 120 -forced-idr 1 ");
                sb.Append($"-b:v {k}k -maxrate {k}k -bufsize {Math.Max(250, k / 2)}k ");
                break;
            case "h264_qsv":
                sb.Append("-c:v h264_qsv -preset veryfast -look_ahead 0 -bf 0 -g 120 ");
                sb.Append($"-b:v {k}k -maxrate {k}k ");
                break;
            case "h264_amf":
                sb.Append("-c:v h264_amf -usage ultralowlatency -quality speed -bf 0 -g 120 ");
                sb.Append($"-b:v {k}k -maxrate {k}k ");
                break;
            default:
                throw new ArgumentException($"unknown encoder '{s.EncoderName}'");
        }
        sb.Append("-fps_mode vfr -flush_packets 1 -flvflags no_duration_filesize -f flv pipe:1");
        return sb.ToString();
    }
}
/// <summary>
/// H.264 encode by driving an ffmpeg child process.
///
/// This is deliberately the simplest thing that works and is TESTED (see FfmpegEncoderTests).
/// Its known costs, stated plainly:
///   * ffmpeg must be shipped next to the agent (and its licence honoured: libx264 is GPL).
///   * It cannot be told to emit an IDR on demand mid-stream, so "send me a keyframe" is
///     implemented by restarting the process and re-feeding the latest frame.
///   * 4:2:0 only. 4:4:4 is NOT implemented, and Android's AVC decoders do not take it anyway.
/// The production path is an in-process Media Foundation hardware MFT; the IVideoEncoder
/// seam exists so that replacement does not touch the session code.
/// </summary>
public sealed class FfmpegEncoder : IVideoEncoder
{
    private readonly Process _process;
    private readonly EncoderSettings _settings;
    private readonly Func<EncodedAccessUnit, ValueTask> _onAccessUnit;
    private readonly ILog _log;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly Queue<string> _stderrTail = new();
    private readonly object _tailGate = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    public string CodecName => "h264";
    public string PixelFormat => "YUV420";
    public string EncoderName => _settings.EncoderName;
    public bool HasExited => _process.HasExited;
    private FfmpegEncoder(Process process, EncoderSettings settings, Func<EncodedAccessUnit, ValueTask> onAu, ILog log)
    {
        _process = process;
        _settings = settings;
        _onAccessUnit = onAu;
        _log = log;
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }
    public static FfmpegEncoder Start(EncoderSettings settings, Func<EncodedAccessUnit, ValueTask> onAccessUnit, ILog log)
    {
        if (settings.Width % 2 != 0 || settings.Height % 2 != 0)
            throw new ArgumentException($"{settings.Width}x{settings.Height}: 4:2:0 H.264 needs even dimensions");
        var psi = new ProcessStartInfo(settings.FfmpegPath, EncoderArgs.Build(settings))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new EncoderUnavailableException(
                $"Cannot start '{settings.FfmpegPath}': {ex.Message}. Place ffmpeg next to the agent or pass --ffmpeg <path>.", ex);
        }
        return new FfmpegEncoder(p, settings, onAccessUnit, log);
    }
    public string StderrTail
    {
        get { lock (_tailGate) return string.Join("\n", _stderrTail); }
    }
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, CancellationToken ct)
    {
        var expected = _settings.Width * _settings.Height * 4;
        if (bgra.Length != expected)
            throw new ArgumentException($"frame is {bgra.Length} bytes, expected {expected} ({_settings.Width}x{_settings.Height} BGRA)");
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(bgra, ct).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new EncoderUnavailableException($"ffmpeg stopped accepting frames: {ex.Message}\n{StderrTail}", ex);
        }
    }
    private async Task PumpStdoutAsync()
    {
        try
        {
            await foreach (var au in FlvDemuxer.ReadAsync(_process.StandardOutput.BaseStream, _cts.Token).ConfigureAwait(false))
            {
                await _onAccessUnit(au).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposed) == 0) _log.Warn("encoder output ended: " + ex.Message);
        }
    }
    private async Task PumpStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_tailGate)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 40) _stderrTail.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _process.StandardInput.Close(); } catch { /* already closed */ }
        // Closing stdin lets ffmpeg flush and exit on its own; killing is the fallback.
        if (!_process.WaitForExit(2000))
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
        _cts.Cancel();
        try { await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* shutting down */ }
        _process.Dispose();
        _cts.Dispose();
    }
}
public sealed class EncoderUnavailableException : Exception
{
    public EncoderUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
public static class EncoderPicker
{
    private static readonly string[] Candidates = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };

    /// <summary>
    /// Qualify the encoder at the target's REAL native size, rather than trusting a
    /// 320x240 probe or an ffmpeg encoder listing. A GPU can support 320x240 but reject
    /// a portrait 1440x3120 AVC session due to its driver, codec or surface limits.
    /// Preserve native pixels; only the encoder implementation may fall back.
    /// </summary>
    public static async Task<string> PickForTargetAsync(EncoderSettings target, ILog log, CancellationToken ct)
    {
        if (target.Width <= 0 || target.Height <= 0 || (target.Width & 1) != 0 || (target.Height & 1) != 0)
            throw new EncoderUnavailableException($"Cannot encode {target.Width}x{target.Height} as H.264 YUV420: dimensions must be positive and even.");

        foreach (var name in Candidates)
        {
            var settings = target with { EncoderName = name };
            if (await TryEncoderAsync(settings, log, ct).ConfigureAwait(false))
            {
                log.Info($"video encoder: {name} verified at {target.Width}x{target.Height} ({target.Fps} fps requested)");
                return name;
            }
            log.Warn($"video encoder {name} cannot encode {target.Width}x{target.Height}; trying the next encoder without resizing");
        }
        throw new EncoderUnavailableException($"No H.264 encoder could produce a native {target.Width}x{target.Height} keyframe. GPU drivers or FFmpeg may need updating; video resolution was NOT reduced.");
    }

    /// <summary>Legacy startup probe for tool tests; the session always uses PickForTargetAsync.</summary>
    public static async Task<string> PickAsync(string ffmpegPath, CodecPreference pref, ILog log, CancellationToken ct)
    {
        var small = new EncoderSettings(320, 240, 30, 2000, ffmpegPath, "libx264");
        foreach (var name in Candidates)
        {
            if (await TryEncoderAsync(small with { EncoderName = name }, log, ct).ConfigureAwait(false))
            {
                log.Info($"video encoder: {name} (small probe only)");
                return name;
            }
            log.Info($"video encoder {name}: not usable here");
        }
        throw new EncoderUnavailableException("No H.264 encoder produced output (not even libx264). Check the ffmpeg build.");
    }

    public static Task<bool> TryEncoderAsync(string ffmpegPath, string name, ILog log, CancellationToken ct) =>
        TryEncoderAsync(new EncoderSettings(320, 240, 30, 2000, ffmpegPath, name), log, ct);

    public static async Task<bool> TryEncoderAsync(EncoderSettings settings, ILog log, CancellationToken ct)
    {
        var gotKeyframe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FfmpegEncoder? enc = null;
        // Target-native 1440x3120 raw frames are ~18 MB each; bound the entire
        // write+read attempt, not only the wait for the first encoded access unit.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.EncoderName == "libx264" ? 20 : 10));
        try
        {
            enc = FfmpegEncoder.Start(settings, au =>
            {
                if (au.Keyframe) gotKeyframe.TrySetResult(true);
                return ValueTask.CompletedTask;
            }, NullLog.Instance);
            var frame = new byte[checked(settings.Width * settings.Height * 4)];
            for (var i = 0; i < 3 && !gotKeyframe.Task.IsCompleted; i++)
            {
                Array.Fill(frame, (byte)(40 * (i + 1)));
                await enc.WriteFrameAsync(frame, budget.Token).ConfigureAwait(false);
            }
            return await gotKeyframe.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Info($"probe {settings.EncoderName}: timed out at {settings.Width}x{settings.Height}");
            return false;
        }
        catch (Exception ex) when (ex is EncoderUnavailableException or IOException or InvalidOperationException or OverflowException)
        {
            log.Info($"probe {settings.EncoderName}: {ex.Message}");
            return false;
        }
        finally
        {
            if (enc is not null) await enc.DisposeAsync().ConfigureAwait(false);
        }
    }
}

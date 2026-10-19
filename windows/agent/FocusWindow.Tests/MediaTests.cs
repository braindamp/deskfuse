using System.Diagnostics;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;
using Xunit;
using Xunit.Abstractions;
namespace FocusWindow.Tests;
public class EncoderArgsTests
{
    private static EncoderSettings S(string enc = "libx264", int w = 1440, int h = 3120, int fps = 60, int kbps = 20000) =>
        new(w, h, fps, kbps, "ffmpeg", enc);
    [Fact]
    public void The_command_line_encodes_the_properties_the_session_depends_on()
    {
        var a = EncoderArgs.Build(S());
        Assert.Contains("-video_size 1440x3120", a);
        Assert.Contains("-pix_fmt bgra", a);
        Assert.Contains("-use_wallclock_as_timestamps 1", a);   // frames stamped on arrival
        Assert.Contains("-fps_mode vfr", a);                     // no duplicate frames: idle = 0 fps
        Assert.Contains("-bf 0", a);                             // no reordering: 1 frame in, 1 AU out
        Assert.Contains("-tune zerolatency", a);
        Assert.Contains("-flush_packets 1", a);                  // every access unit leaves immediately
        Assert.Contains("-f flv pipe:1", a);                     // length-prefixed framing
        Assert.Contains("-colorspace bt709", a);                 // colours decode correctly on the phone
        Assert.Contains("format=yuv420p", a);                    // honest: 4:2:0
        Assert.DoesNotContain("yuv444", a);
    }
    [Fact]
    public void Each_hardware_encoder_has_its_own_rate_control_and_unknown_ones_are_refused()
    {
        Assert.Contains("h264_nvenc", EncoderArgs.Build(S("h264_nvenc")));
        Assert.Contains("-forced-idr 1", EncoderArgs.Build(S("h264_nvenc")));
        Assert.Contains("h264_qsv", EncoderArgs.Build(S("h264_qsv")));
        Assert.Contains("h264_amf", EncoderArgs.Build(S("h264_amf")));
        Assert.Throws<ArgumentException>(() => EncoderArgs.Build(S("libx265")));
    }
    [Fact]
    public void Bitrate_and_fps_are_clamped_to_sane_values()
    {
        Assert.Contains("-maxrate 500k", EncoderArgs.Build(S(kbps: 10)));
        Assert.Contains("-framerate 240", EncoderArgs.Build(S(fps: 9999)));
        Assert.Contains("-framerate 1 ", EncoderArgs.Build(S(fps: 0)));
    }
    [Fact]
    public void Odd_dimensions_are_refused_up_front_because_420_cannot_carry_them()
    {
        var ex = Assert.Throws<ArgumentException>(() => FfmpegEncoder.Start(S(w: 1439), _ => ValueTask.CompletedTask, FocusWindow.Core.NullLog.Instance));
        Assert.Contains("even", ex.Message);
    }
    [Fact]
    public void A_missing_ffmpeg_is_a_named_error_with_instructions()
    {
        var s = S() with { FfmpegPath = "/definitely/not/here/ffmpeg" };
        var ex = Assert.Throws<EncoderUnavailableException>(() => FfmpegEncoder.Start(s, _ => ValueTask.CompletedTask, FocusWindow.Core.NullLog.Instance));
        Assert.Contains("--ffmpeg", ex.Message);
    }
}
public class FlvDemuxerTests
{
    private static byte[] Tag(byte type, byte[] data)
    {
        var tag = new List<byte> { type, (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, 0, 0, 0, 0, 0, 0, 0 };
        tag.AddRange(data);
        var prev = 11 + data.Length;
        tag.AddRange(new[] { (byte)(prev >> 24), (byte)(prev >> 16), (byte)(prev >> 8), (byte)prev });
        return tag.ToArray();
    }
    private static byte[] Flv(params byte[][] tags)
    {
        var header = new byte[] { (byte)'F', (byte)'L', (byte)'V', 1, 1, 0, 0, 0, 9, 0, 0, 0, 0 };
        return header.Concat(tags.SelectMany(t => t)).ToArray();
    }
    private static readonly byte[] Sps = { 0x67, 0x64, 0x00, 0x1F, 0xAC };
    private static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };
    private static readonly byte[] Idr = { 0x65, 0x88, 0x84, 0x00, 0x10 };
    private static readonly byte[] P = { 0x41, 0x9A, 0x24, 0x6C };
    private static byte[] AvccTag()
    {
        var avcc = new List<byte> { 1, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0, (byte)Sps.Length };
        avcc.AddRange(Sps);
        avcc.Add(1);
        avcc.AddRange(new byte[] { 0, (byte)Pps.Length });
        avcc.AddRange(Pps);
        return Tag(9, new byte[] { 0x17, 0, 0, 0, 0 }.Concat(avcc).ToArray());
    }
    private static byte[] NaluTag(bool key, byte[] nal)
    {
        var body = new List<byte> { (byte)(key ? 0x17 : 0x27), 1, 0, 0, 0, 0, 0, 0, (byte)nal.Length };
        body.AddRange(nal);
        return Tag(9, body.ToArray());
    }
    private static async Task<List<EncodedAccessUnit>> Demux(byte[] flv)
    {
        var list = new List<EncodedAccessUnit>();
        await foreach (var au in FlvDemuxer.ReadAsync(new MemoryStream(flv))) list.Add(au);
        return list;
    }
    [Fact]
    public async Task Keyframes_get_sps_pps_prepended_and_inter_frames_do_not_and_lengths_become_start_codes()
    {
        var aus = await Demux(Flv(AvccTag(), NaluTag(true, Idr), NaluTag(false, P)));
        Assert.Equal(2, aus.Count);
        Assert.True(aus[0].Keyframe);
        Assert.False(aus[1].Keyframe);
        Assert.Equal(new[] { 7, 8, 5 }, FlvDemuxer.NalTypes(aus[0].Data));
        Assert.Equal(new[] { 1 }, FlvDemuxer.NalTypes(aus[1].Data));
        Assert.True(aus[0].Data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 0, 1 }));
        Assert.True(aus[0].Data.AsSpan().IndexOf(Idr) > 0, "IDR payload must survive verbatim");
    }
    [Fact]
    public async Task Non_video_tags_are_skipped_and_a_truncated_stream_ends_cleanly()
    {
        var script = Tag(18, new byte[] { 2, 0, 1, (byte)'x' });
        var aus = await Demux(Flv(script, AvccTag(), NaluTag(true, Idr)));
        Assert.Single(aus);
        var whole = Flv(AvccTag(), NaluTag(true, Idr));
        // Cut exactly at a tag boundary: a clean end, one access unit.
        var avccEnd = 13 + AvccTag().Length;
        Assert.Empty(await Demux(whole[..avccEnd]));
        // Cut in the middle of a tag header, and in the middle of a tag body: encoder crashed.
        await Assert.ThrowsAnyAsync<EndOfStreamException>(async () => await Demux(whole[..(avccEnd + 5)]));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(async () => await Demux(whole[..(whole.Length - 8)]));
    }
    [Fact]
    public async Task A_nal_length_that_overruns_the_access_unit_is_rejected()
    {
        var bad = Tag(9, new byte[] { 0x17, 1, 0, 0, 0, 0, 0, 0, 200, 0x65, 1, 2 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Demux(Flv(AvccTag(), bad)));
    }
    [Fact]
    public async Task Garbage_that_is_not_flv_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Demux(new byte[64]));
    }
}
/// <summary>Real ffmpeg, real frames at the phone's real size. Skipped (not faked) without FOCUS_FFMPEG.</summary>
[Collection("cpu")]
public class FfmpegEncoderTests
{
    private readonly ITestOutputHelper _out;
    public FfmpegEncoderTests(ITestOutputHelper o) => _out = o;
    private static EncoderSettings Settings(int w, int h, int fps = 60) => new(w, h, fps, 20000, ToolLocator.Ffmpeg!, "libx264");
    public static async Task<(int Frames, byte[] First, byte[] Last)> DecodeAsync(string annexBPath, int w, int h)
    {
        var psi = new ProcessStartInfo(ToolLocator.Ffmpeg!,
            $"-loglevel error -f h264 -i \"{annexBPath}\" -f rawvideo -pix_fmt bgra pipe:1")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var frameBytes = w * h * 4;
        byte[] first = Array.Empty<byte>(), last = new byte[frameBytes], scratch = new byte[frameBytes];
        var count = 0;
        var stderr = p.StandardError.ReadToEndAsync();
        while (true)
        {
            var read = 0;
            while (read < frameBytes)
            {
                var n = await p.StandardOutput.BaseStream.ReadAsync(scratch.AsMemory(read, frameBytes - read));
                if (n == 0) break;
                read += n;
            }
            if (read < frameBytes) break;
            if (count == 0) first = (byte[])scratch.Clone();
            (last, scratch) = (scratch, last);
            count++;
        }
        await p.WaitForExitAsync();
        var err = await stderr;
        Assert.True(string.IsNullOrWhiteSpace(err), "ffmpeg decode reported errors: " + err);
        return (count, first, last);
    }
    [FactIfFfmpeg]
    public async Task One_single_frame_is_emitted_on_its_own_without_waiting_for_a_second()
    {
        // The reason for FLV: with raw Annex-B the last NAL of the newest frame is unterminated until
        // the next start code, so a desktop that goes quiet would leave the phone on a stale image.
        var got = new TaskCompletionSource<EncodedAccessUnit>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var enc = FfmpegEncoder.Start(Settings(1440, 3120), au => { got.TrySetResult(au); return ValueTask.CompletedTask; },
            FocusWindow.Core.NullLog.Instance);
        var sw = Stopwatch.StartNew();
        await enc.WriteFrameAsync(TestFrames.Make(1440, 3120, 20, 120, 220), CancellationToken.None);
        var au = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _out.WriteLine($"first access unit after {sw.ElapsedMilliseconds} ms ({au.Data.Length} bytes) with NO second frame sent");
        Assert.True(au.Keyframe);
        var nals = FlvDemuxer.NalTypes(au.Data);
        Assert.Contains(7, nals);   // SPS
        Assert.Contains(8, nals);   // PPS
        Assert.Contains(5, nals);   // IDR slice
        Assert.Equal("h264", enc.CodecName);
        Assert.Equal("YUV420", enc.PixelFormat);
    }
    [FactIfFfmpeg]
    public async Task Frames_fed_at_the_fps_cap_produce_exactly_one_access_unit_each_and_only_the_first_is_a_keyframe()
    {
        const int N = 30, W = 720, H = 1560, Fps = 30;
        var aus = new List<EncodedAccessUnit>();
        await using (var enc = FfmpegEncoder.Start(Settings(W, H, Fps), au => { lock (aus) aus.Add(au); return ValueTask.CompletedTask; },
                   FocusWindow.Core.NullLog.Instance))
        {
            var frame = TestFrames.Make(W, H, 20, 120, 220);
            for (var i = 0; i < N; i++)
            {
                frame[((10 + i) * W + 400) * 4] = (byte)(i * 8); // something different every frame
                await enc.WriteFrameAsync(frame, CancellationToken.None);
                await Task.Delay(1000 / Fps);                    // exactly the pacing the session uses
            }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { lock (aus) if (aus.Count >= N) break; await Task.Delay(50); }
        }
        _out.WriteLine($"fed {N} frames, got {aus.Count} access units, keyframes={aus.Count(a => a.Keyframe)}");
        Assert.Equal(N, aus.Count);
        Assert.True(aus[0].Keyframe);
        Assert.Equal(1, aus.Count(a => a.Keyframe));
    }
    [FactIfFfmpeg]
    public async Task What_the_phone_decodes_is_the_pixel_perfect_1440x3120_frame_that_went_in()
    {
        const int W = 1440, H = 3120;
        var path = Path.Combine(Path.GetTempPath(), $"fw-fidelity-{Guid.NewGuid():N}.h264");
        var frames = new List<EncodedAccessUnit>();
        await using (var enc = FfmpegEncoder.Start(Settings(W, H, 30), au => { lock (frames) frames.Add(au); return ValueTask.CompletedTask; },
                   FocusWindow.Core.NullLog.Instance))
        {
            var src = TestFrames.Make(W, H, 20, 120, 220, squareX: 200, squareY: 300, square: 350);
            for (var i = 0; i < 3; i++)
            {
                await enc.WriteFrameAsync(src, CancellationToken.None);
                await Task.Delay(60);
            }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline) { lock (frames) if (frames.Count >= 3) break; await Task.Delay(50); }
        }
        await File.WriteAllBytesAsync(path, frames.SelectMany(f => f.Data).ToArray());
        var (count, first, last) = await DecodeAsync(path, W, H);
        File.Delete(path);
        _out.WriteLine($"decoded {count} frames of {W}x{H}");
        Assert.Equal(3, count);
        // Background colour survives BT.709 limited-range round trip within a few levels.
        int At(byte[] f, int x, int y, int c) => f[(y * W + x) * 4 + c];
        foreach (var (b, g, r) in new[] { (At(last, W - 30, H - 30, 0), At(last, W - 30, H - 30, 1), At(last, W - 30, H - 30, 2)) })
        {
            _out.WriteLine($"background decoded as BGR=({b},{g},{r}), sent (20,120,220)");
            Assert.InRange(b, 20 - 8, 20 + 8);
            Assert.InRange(g, 120 - 8, 120 + 8);
            Assert.InRange(r, 220 - 8, 220 + 8);
        }
        // Geometry: a 350 px square (100 DIP at 336 DPI) must come back as ~350 px at the same place.
        Assert.True(DensityMeasure.TryFindColorBox(last, W, H, 255, 255, 255, 40, out var box));
        _out.WriteLine($"350 px square decoded at {box}");
        Assert.InRange(box.X, 199, 201);
        Assert.InRange(box.Y, 299, 301);
        Assert.InRange(box.Width, 349, 351);
        Assert.InRange(box.Height, 349, 351);
        Assert.True(DensityMeasure.EvaluateRuler(box, 100, 336).Ok);
    }
    [FactIfFfmpeg]
    public async Task The_picker_falls_through_unavailable_hardware_encoders_to_libx264()
    {
        var log = new ListLog();
        var picked = await EncoderPicker.PickAsync(ToolLocator.Ffmpeg!, CodecPreference.Auto, log, CancellationToken.None);
        _out.WriteLine(string.Join("\n", log.Lines));
        Assert.Equal("libx264", picked);   // this box has no NVENC/QSV/AMF in its ffmpeg
        Assert.Contains(log.Lines, l => l.Contains("h264_nvenc") && l.Contains("not usable"));
    }
    [FactIfFfmpeg]
    public async Task A_wrong_frame_size_is_refused_before_it_can_corrupt_the_stream()
    {
        await using var enc = FfmpegEncoder.Start(Settings(640, 480), _ => ValueTask.CompletedTask, FocusWindow.Core.NullLog.Instance);
        await Assert.ThrowsAsync<ArgumentException>(async () => await enc.WriteFrameAsync(new byte[1234], CancellationToken.None));
    }
}

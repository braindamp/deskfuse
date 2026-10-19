using System.Runtime.InteropServices;
using FocusWindow.Core.Display;
using Xunit;
using Xunit.Abstractions;
namespace FocusWindow.Tests;
/// <summary>
/// Asserts the C# mirror of FocusShared.h against numbers PRODUCED BY THE C++ COMPILER
/// (driver/tests/contract_test.cpp -> windows/tests/contract.generated.txt). If someone edits
/// one side without the other, this fails instead of the two processes misreading each other.
/// </summary>
public class DriverContractTests
{
    private static Dictionary<string, ulong> LoadGenerated()
    {
        var env = Environment.GetEnvironmentVariable("FOCUS_CONTRACT_FILE");
        string? path = !string.IsNullOrEmpty(env) && File.Exists(env) ? env : null;
        for (var dir = AppContext.BaseDirectory; path is null && !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)!)
        {
            var candidate = Path.Combine(dir, "tests", "contract.generated.txt");
            if (File.Exists(candidate)) path = candidate;
            if (Path.GetDirectoryName(dir) is null) break;
        }
        Assert.True(path is not null, "contract.generated.txt not found; run windows/driver/tests/run_tests.sh first");
        return File.ReadAllLines(path!)
            .Where(l => l.Contains('='))
            .ToDictionary(l => l.Split('=')[0], l => ulong.Parse(l.Split('=')[1]));
    }
    [Fact]
    public void Every_cpp_number_matches_the_csharp_mirror()
    {
        var cpp = LoadGenerated();
        var expected = new Dictionary<string, ulong>
        {
            ["IOCTL_SET_TARGET"] = DriverContract.IoctlSetTarget,
            ["IOCTL_GET_STATUS"] = DriverContract.IoctlGetStatus,
            ["IOCTL_REMOVE_MONITOR"] = DriverContract.IoctlRemoveMonitor,
            ["IOCTL_PING"] = DriverContract.IoctlPing,
            ["PROTOCOL_VERSION"] = (ulong)DriverContract.ProtocolVersion,
            ["HEARTBEAT_TIMEOUT_MS"] = (ulong)DriverContract.HeartbeatTimeoutMs,
            ["SIZEOF_SET_TARGET"] = (ulong)Marshal.SizeOf<DriverContract.SetTarget>(),
            ["SIZEOF_STATUS"] = (ulong)Marshal.SizeOf<DriverContract.Status>(),
            ["OFFSET_STATUS_FRAMES_PUBLISHED"] = (ulong)Marshal.OffsetOf<DriverContract.Status>(nameof(DriverContract.Status.FramesPublished)),
            ["OFFSET_STATUS_LAST_ERROR"] = (ulong)Marshal.OffsetOf<DriverContract.Status>(nameof(DriverContract.Status.LastError)),
            ["OFFSET_STATUS_SURFACE_ERROR"] = (ulong)Marshal.OffsetOf<DriverContract.Status>(nameof(DriverContract.Status.SurfaceWin32Error)),
            ["SURFACE_MAGIC"] = DriverContract.SurfaceMagic,
            ["SURFACE_VERSION"] = DriverContract.SurfaceVersion,
            ["HEADER_BYTES"] = DriverContract.HeaderBytes,
            ["SLOT_COUNT"] = DriverContract.SlotCount,
            ["MAX_DIMENSION"] = DriverContract.MaxDimension,
            ["MAX_PIXELS"] = DriverContract.MaxPixels,
            ["SLOT_HEADER_BYTES"] = DriverContract.SlotHeaderBytes,
            ["SLOT_PIXEL_BYTES"] = DriverContract.SlotPixelBytes,
            ["SLOT_STRIDE_BYTES"] = DriverContract.SlotStrideBytes,
            ["SECTION_BYTES"] = DriverContract.SectionBytes,
            ["PIXEL_FORMAT_BGRA8"] = DriverContract.PixelFormatBgra8,
            ["ERR_FRAME_TOO_LARGE"] = DriverContract.ErrFrameTooLarge,
            ["HDR_OFFSET_MAGIC"] = DriverContract.HdrMagic,
            ["HDR_OFFSET_VERSION"] = DriverContract.HdrVersion,
            ["HDR_OFFSET_WIDTH"] = DriverContract.HdrWidth,
            ["HDR_OFFSET_HEIGHT"] = DriverContract.HdrHeight,
            ["HDR_OFFSET_STRIDE"] = DriverContract.HdrStride,
            ["HDR_OFFSET_SLOT_COUNT"] = DriverContract.HdrSlotCount,
            ["HDR_OFFSET_PUBLISHED_SEQ"] = DriverContract.HdrPublishedSeq,
            ["HDR_OFFSET_LATEST_SLOT"] = DriverContract.HdrLatestSlot,
            ["HDR_OFFSET_MAX_PIXELS"] = DriverContract.HdrMaxPixels,
            ["HDR_OFFSET_MONITOR_ARRIVED"] = DriverContract.HdrMonitorArrived,
            ["HDR_OFFSET_LAST_ERROR"] = DriverContract.HdrLastError,
            ["HDR_OFFSET_DROPPED"] = DriverContract.HdrDropped,
            ["SLOT_OFFSET_SEQ"] = DriverContract.SlotSeq,
            ["SLOT_OFFSET_QPC"] = DriverContract.SlotQpc,
            ["SLOT_OFFSET_WIDTH"] = DriverContract.SlotWidth,
            ["SLOT_OFFSET_HEIGHT"] = DriverContract.SlotHeight,
        };
        foreach (var (key, value) in expected)
        {
            Assert.True(cpp.TryGetValue(key, out var fromCpp), $"{key} missing from the C++ output");
            Assert.True(value == fromCpp, $"{key}: C# says {value}, the C++ compiler says {fromCpp}");
        }
        // Nothing the C++ side exports may be silently ignored by the mirror.
        var unmirrored = cpp.Keys.Except(expected.Keys).Where(k => k != "SIZEOF_SURFACE_HEADER" && k != "SIZEOF_SLOT_HEADER").ToList();
        Assert.True(unmirrored.Count == 0, "C++ exports without a C# assertion: " + string.Join(", ", unmirrored));
        Assert.Equal(96UL, cpp["SIZEOF_SURFACE_HEADER"]);
        Assert.Equal(32UL, cpp["SIZEOF_SLOT_HEADER"]);
    }
    [Fact]
    public void A_1440x3120_phone_fits_the_surface_which_the_v1_2560_limit_could_not_carry()
    {
        Assert.True(1440u * 3120u <= DriverContract.MaxPixels);
        Assert.True(3120u <= DriverContract.MaxDimension);
        Assert.True(2160u * 3840u <= DriverContract.MaxPixels);
        Assert.True(1440u * 3120u * 4u <= DriverContract.SlotPixelBytes);
    }
    [Fact]
    public void Ioctl_codes_follow_the_ctl_code_formula()
    {
        // CTL_CODE(0x22, 0x800, BUFFERED, WRITE) = 0x22A000, computed by hand from winioctl.h.
        Assert.Equal(0x22A000u, DriverContract.IoctlSetTarget);
        Assert.Equal(0x226004u, DriverContract.IoctlGetStatus);
        Assert.Equal(0x22A008u, DriverContract.IoctlRemoveMonitor);
        Assert.Equal(0x22200Cu, DriverContract.IoctlPing);
    }
}
[Collection("cpu")]
public class SurfaceReaderTests
{
    private readonly ITestOutputHelper _out;
    public SurfaceReaderTests(ITestOutputHelper output) => _out = output;
    [Fact]
    public void Before_the_driver_initialises_the_surface_there_is_nothing_to_read()
    {
        unsafe
        {
            var raw = new byte[DriverContract.HeaderBytes];
            fixed (byte* p = raw)
            {
                var reader = new SurfaceReader(p, raw.Length);
                var buf = Array.Empty<byte>();
                Assert.False(reader.IsInitialized);
                Assert.Equal(ReadOutcome.NoNewFrame, reader.TryReadNewest(0, ref buf, out _));
            }
        }
    }
    [Fact]
    public void A_1440x3120_frame_round_trips_byte_for_byte()
    {
        using var w = new TestSurfaceWriter(1440, 3120);
        var reader = w.CreateReader();
        var frame = TestFrames.Make(1440, 3120, 20, 120, 220);
        Assert.True(w.Publish(frame, 1440, 3120));
        var buf = Array.Empty<byte>();
        Assert.Equal(ReadOutcome.Ok, reader.TryReadNewest(0, ref buf, out var info));
        Assert.Equal(new FrameInfo(1, 1440, 3120, 0), info with { PresentQpc = 0 });
        Assert.True(frame.AsSpan().SequenceEqual(buf.AsSpan(0, frame.Length)));
    }
    [Fact]
    public void Only_a_newer_frame_is_returned_and_older_ones_are_skipped()
    {
        using var w = new TestSurfaceWriter(64, 64);
        var reader = w.CreateReader();
        var buf = Array.Empty<byte>();
        w.Publish(TestFrames.Make(64, 64, 1, 1, 1, 0, 0, 4), 64, 64);
        w.Publish(TestFrames.Make(64, 64, 2, 2, 2, 0, 0, 4), 64, 64);
        w.Publish(TestFrames.Make(64, 64, 3, 3, 3, 0, 0, 4), 64, 64);
        Assert.Equal(ReadOutcome.Ok, reader.TryReadNewest(0, ref buf, out var info));
        Assert.Equal(3UL, info.Seq);                  // latest-wins
        Assert.Equal(3, buf[(10 * 64 + 10) * 4]);
        Assert.Equal(ReadOutcome.NoNewFrame, reader.TryReadNewest(info.Seq, ref buf, out _));
    }
    [Fact]
    public void A_frame_larger_than_a_slot_is_rejected_as_invalid_not_read_out_of_bounds()
    {
        using var w = new TestSurfaceWriter(64, 64);
        var reader = w.CreateReader();
        var buf = Array.Empty<byte>();
        unsafe
        {
            // Corrupt the slot header after a legitimate publish.
            w.Publish(TestFrames.Make(64, 64, 9, 9, 9, 0, 0, 4), 64, 64);
            var slot = w.Base + DriverContract.HeaderBytes + (long)(*(uint*)(w.Base + DriverContract.HdrLatestSlot)) *
                       *(uint*)(w.Base + DriverContract.HdrSlotStrideBytes);
            *(uint*)(slot + DriverContract.SlotWidth) = 40000;
        }
        Assert.Equal(ReadOutcome.Invalid, reader.TryReadNewest(0, ref buf, out _));
    }
    private (long Written, long Delivered, long GaveUp, long Mixed) Hammer(bool invalidateFirst, int seconds, uint slots = 3, int writerPauseMs = 0)
    {
        // A writer rewriting slots as fast as it can, and a reader copying. Each frame is ONE flat
        // byte value: any mix of two values in a delivered buffer is a torn read.
        const int W = 640, H = 480;
        using var w = new TestSurfaceWriter(W, H, slots);
        var reader = w.CreateReader();
        var stop = false;
        long written = 0;
        var writer = new Thread(() =>
        {
            var frame = new byte[W * H * 4];
            byte v = 0;
            while (!Volatile.Read(ref stop))
            {
                v = (byte)(v % 250 + 1);
                Array.Fill(frame, v);
                w.Publish(frame, W, H, invalidateFirst);
                Interlocked.Increment(ref written);
                if (writerPauseMs > 0) Thread.Sleep(writerPauseMs); // leaves windows, as real 60-120 fps does
            }
        }) { IsBackground = true };
        writer.Start();
        var buf = Array.Empty<byte>();
        ulong last = 0;
        long delivered = 0, gaveUp = 0, mixed = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var outcome = reader.TryReadNewest(last, ref buf, out var info);
            if (outcome == ReadOutcome.Torn) { gaveUp++; continue; }
            if (outcome != ReadOutcome.Ok) continue;
            last = info.Seq;
            delivered++;
            var first = buf[0];
            for (var i = 0; i < W * H * 4; i += 997) // stride through the whole frame
            {
                if (buf[i] != first) { mixed++; break; }
            }
            if (buf[W * H * 4 - 1] != first) mixed++;
        }
        Volatile.Write(ref stop, true);
        writer.Join();
        return (written, delivered, gaveUp, mixed);
    }
    [Fact]
    public void Concurrent_writer_and_reader_never_deliver_a_torn_frame()
    {
        var r = Hammer(invalidateFirst: true, seconds: 4);
        _out.WriteLine($"FIXED protocol : written={r.Written} delivered={r.Delivered} gave-up={r.GaveUp} TORN-ACCEPTED={r.Mixed}");
        Assert.True(r.Delivered > 50, $"reader only delivered {r.Delivered} frames");
        Assert.Equal(0, r.Mixed);
    }
    [Fact]
    public void Worst_case_single_slot_with_the_fixed_protocol_still_never_accepts_a_torn_frame()
    {
        // ONE slot means the writer is rewriting the very slot the reader is copying on every
        // publish: maximum contention. The fixed protocol must turn every overlap into a retry.
        var r = Hammer(invalidateFirst: true, seconds: 4, slots: 1, writerPauseMs: 0);
        _out.WriteLine($"FIXED, 1 slot  : written={r.Written} delivered={r.Delivered} gave-up={r.GaveUp} TORN-ACCEPTED={r.Mixed}");
        Assert.True(r.Delivered > 5, $"reader only delivered {r.Delivered} frames");
        Assert.Equal(0, r.Mixed);
    }
    [Fact]
    public void Negative_control_the_v1_writer_without_invalidation_DOES_deliver_torn_frames_under_contention()
    {
        // The same single-slot hammer against the v1 writer (no Seq=0 before rewriting pixels).
        // If this reported 0, the tests above would prove nothing; it must see torn frames,
        // which is what shows both that the v1 bug was real and that this harness can detect it.
        var r = Hammer(invalidateFirst: false, seconds: 4, slots: 1, writerPauseMs: 0);
        _out.WriteLine($"V1 BUG, 1 slot : written={r.Written} delivered={r.Delivered} TORN-ACCEPTED={r.Mixed}");
        Assert.True(r.Mixed > 0, "the harness failed to reproduce the v1 torn-frame bug, so it cannot vouch for the fix");
    }
}

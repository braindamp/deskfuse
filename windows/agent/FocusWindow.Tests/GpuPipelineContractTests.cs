using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using Xunit;

namespace FocusWindow.Tests;

public sealed class GpuPipelineContractTests
{
    private static readonly VideoPipelineSettings Settings = new(
        new RectI(2560, 0, 2100, 3120), 1440, 3120, 60, 20000, 336);
    private static readonly VideoPipelineInfo Source = new(
        new RectI(2890, 0, 1440, 3120), 336, 1440, 3120,
        "h264", "YUV420", "NVENC / WGC", "BT.709 full range", true);

    [Fact]
    public void Native_geometry_requires_the_real_captured_bounds_dpi_and_cadence()
    {
        Source.RequireNativeGeometry(Settings);
        foreach (var invalid in new[] {
            Source with { SourceBounds = new RectI(2560, 0, 2100, 3120) },
            Source with { SourceBounds = new RectI(100, 0, 1440, 3120) },
            Source with { WindowDpi = 96 },
            Source with { NativePixelSize = false },
            Source with { FramesPerSecond = 120 },
            Source with { Width = 720 },
        }) Assert.Throws<DisplayException>(() => invalid.RequireNativeGeometry(Settings));
    }

    [Fact]
    public void Centered_capture_maps_the_first_and_last_phone_pixel_to_the_actual_window()
    {
        var geometry = new InputGeometry(Source.SourceBounds, Source.Width, Source.Height);
        Assert.Equal((2890, 0), geometry.ToDesktop(0, 0));
        Assert.Equal((4329, 3119), geometry.ToDesktop(1439, 3119));
        Assert.Equal((2890, 3119), geometry.ToDesktop(-100, int.MaxValue));
    }

    [Fact]
    public async Task Microphone_worker_fault_terminates_and_joins_the_video_session()
    {
        var videoStopped = false;
        async Task Video(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { videoStopped = true; }
        }
        static Task Wait(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        var transport = new TaskCompletionSource();
        var session = SessionLifetime.RunUntilFirstStopAsync(Wait, Video, Wait,
            transport.Task, default, _ => Task.FromException(new IOException("mic worker failed")));
        await Assert.ThrowsAsync<IOException>(() => session.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(videoStopped);
    }
}

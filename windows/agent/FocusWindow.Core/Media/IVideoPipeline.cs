using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Media;

/// <summary>
/// Shared session contract at the ENCODED access-unit boundary. WGC/D3D/NVENC
/// stays native. This interface must not require a CPU BGRA image.
/// </summary>
public readonly record struct GpuVideoPacket(byte[] AnnexB, bool Keyframe, ulong CaptureQpc);
public sealed record VideoPipelineSettings(
    RectI BackingMonitorBounds, int Width, int Height, int Fps,
    int BitrateKbps, int RequiredDpi, string? MonitorGdiName = null, string? EnrollmentId = null);
public sealed record VideoPipelineInfo(
    RectI SourceBounds, int WindowDpi, int Width, int Height,
    string Codec, string PixelFormat, string Encoder,
    string ColorRange, bool NativePixelSize, string AppLabel = "Windows application",
    string? SourceId = null, int FramesPerSecond = 60)
{
    public void RequireNativeGeometry(VideoPipelineSettings settings)
    {
        var monitor = settings.BackingMonitorBounds;
        if (!NativePixelSize || Width != settings.Width || Height != settings.Height ||
            SourceBounds.Width != Width || SourceBounds.Height != Height ||
            SourceBounds.X < monitor.X || SourceBounds.Y < monitor.Y ||
            SourceBounds.Right > monitor.Right || SourceBounds.Bottom > monitor.Bottom ||
            WindowDpi != settings.RequiredDpi || FramesPerSecond != settings.Fps)
            throw new FocusWindow.Core.Display.DisplayException("source-geometry",
                "GPU source geometry/DPI/cadence does not match the negotiated native viewport.");
    }
}

public interface IVideoPipeline : IAsyncDisposable
{
    VideoPipelineInfo Info { get; }
    Task StartAsync(VideoPipelineSettings settings, CancellationToken ct);
    IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync(CancellationToken ct);
    void RequestKeyframe();
    /// <summary>Revalidate source ownership, geometry and foreground before input.</summary>
    bool TryPrepareInput(InputEventDto input) => false;
}

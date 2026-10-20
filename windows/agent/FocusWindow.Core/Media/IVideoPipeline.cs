using FocusWindow.Core.Display;

namespace FocusWindow.Core.Media;

/// <summary>
/// Shared session contract at the ENCODED access-unit boundary. WGC/D3D/NVENC
/// stays native. This interface must not require a CPU BGRA image.
/// </summary>
public readonly record struct GpuVideoPacket(byte[] AnnexB, bool Keyframe, ulong CaptureQpc);
public sealed record VideoPipelineSettings(
    RectI BackingMonitorBounds, int Width, int Height, int Fps,
    int BitrateKbps, int RequiredDpi);
public sealed record VideoPipelineInfo(
    RectI SourceBounds, int WindowDpi, int Width, int Height,
    string Codec, string PixelFormat, string Encoder,
    string ColorRange, bool NativePixelSize);

public interface IVideoPipeline : IAsyncDisposable
{
    VideoPipelineInfo Info { get; }
    Task StartAsync(VideoPipelineSettings settings, CancellationToken ct);
    IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync(CancellationToken ct);
    void RequestKeyframe();
}

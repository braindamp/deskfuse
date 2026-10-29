using System.Buffers;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Core.Media;

/// <summary>
/// Shared session contract at the ENCODED access-unit boundary. WGC/D3D/NVENC
/// stays native. This interface must not require a CPU BGRA image.
/// </summary>
/// <summary>
/// One owned encoded access unit. The producer transfers ownership to the
/// consumer; the consumer must dispose it unless the transport accepts it.
/// The header prefix allows the encoder destination to become the frame
/// plaintext without another copy. Only the completed length is exposed.
/// </summary>
public sealed class GpuVideoPacket : IDisposable
{
    public const int PayloadOffset = 5 + 8 + Wire.FrameHeaderBytes;
    public const int TotalOverheadBytes = PayloadOffset + 16;
    private const int PlaintextOffset = 5 + 8;
    private byte[]? _buffer;
    private readonly ArrayPool<byte> _pool;
    private readonly int _capacity;
    private int _length = -1;
    // Writable=0, Complete=1, Accepted=2, Sealed=3, Released=4.
    private int _state;
    public bool Keyframe { get; private set; }
    public ulong CaptureQpc { get; private set; }
    public long CompletedQpc { get; private set; }
    public int RentedBytes => Buffer.Length;

    private GpuVideoPacket(int capacity, ArrayPool<byte> pool)
    {
        if (capacity <= 0 || capacity > Transport.PhoneConnection.MaxVideoAccessUnitBytes)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _pool = pool;
        _buffer = pool.Rent(checked(TotalOverheadBytes + capacity));
        _capacity = Math.Min(_buffer.Length - TotalOverheadBytes, Transport.PhoneConnection.MaxVideoAccessUnitBytes);
    }

    // Convenience for non-native providers; the native path writes directly to WritablePayload.
    public GpuVideoPacket(byte[] annexB, bool keyframe, ulong captureQpc) : this(annexB.Length, ArrayPool<byte>.Shared)
    {
        annexB.CopyTo(WritablePayload);
        Complete(annexB.Length, keyframe, captureQpc);
    }

    public static GpuVideoPacket Rent(int capacity, ArrayPool<byte>? pool = null) => new(capacity, pool ?? ArrayPool<byte>.Shared);
    public Memory<byte> WritablePayload => _length < 0
        ? Buffer.AsMemory(PayloadOffset, _capacity)
        : throw new InvalidOperationException("Access unit is already complete.");
    public int Capacity => _capacity;
    public int Length => _length >= 0 ? _length : throw new InvalidOperationException("Access unit is not complete.");
    public ReadOnlyMemory<byte> AnnexB => CallerBuffer.AsMemory(PayloadOffset, Length);
    internal Memory<byte> Plaintext => CallerBuffer.AsMemory(PlaintextOffset, checked(Wire.FrameHeaderBytes + Length));
    private byte[] CallerBuffer => Volatile.Read(ref _state) < 2 ? Buffer :
        throw new InvalidOperationException("Access unit ownership has transferred to transport.");
    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(GpuVideoPacket));

    public void Complete(int length, bool keyframe, ulong captureQpc)
    {
        _ = Buffer;
        if (_length >= 0 || length <= 0 || length > _capacity) throw new InvalidOperationException("Invalid completed access unit.");
        _length = length;
        Keyframe = keyframe;
        CaptureQpc = captureQpc;
        CompletedQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        _state = 1;
    }

    internal void AcceptByTransport()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            throw new InvalidOperationException("Only a completed caller-owned packet can transfer.");
    }
    internal void CancelTransfer() => Interlocked.CompareExchange(ref _state, 1, 2);
    internal ReadOnlyMemory<byte> SealForTransport(Transport.SecureChannel channel, byte kind)
    {
        if (Interlocked.CompareExchange(ref _state, 3, 2) != 2)
            throw new InvalidOperationException("Packet must be accepted and sealed exactly once.");
        var plainLength = checked(Wire.FrameHeaderBytes + Length);
        var sealedLength = checked(plainLength + Transport.SecureChannel.RecordOverheadBytes);
        var wireLength = checked(5 + sealedLength);
        var buffer = Buffer;
        buffer[0] = kind;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), sealedLength);
        channel.Seal(buffer.AsSpan(PlaintextOffset, plainLength), buffer.AsSpan(5, sealedLength));
        return buffer.AsMemory(0, wireLength);
    }
    internal void DisposeFromTransport()
    {
        Interlocked.Exchange(ref _state, 4);
        ReturnBuffer();
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _state) is 2 or 3)
            throw new InvalidOperationException("Only transport can release an accepted packet.");
        Interlocked.Exchange(ref _state, 4);
        ReturnBuffer();
    }
    private void ReturnBuffer()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) _pool.Return(buffer, clearArray: false);
    }
}
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
    string Diagnostics => string.Empty;
    VideoPipelineInfo Info { get; }
    Task StartAsync(VideoPipelineSettings settings, CancellationToken ct);
    IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync(CancellationToken ct);
    void RequestKeyframe();
    /// <summary>Revalidate source ownership, geometry and foreground before input.</summary>
    bool TryPrepareInput(InputEventDto input) => false;
}

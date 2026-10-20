using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;

namespace FocusWindow.Agent.Media;

/// <summary>
/// The in-process DeskFuse GPU stream. Reuses the proven WGC capture and NVENC
/// rather than shelling out to a POC exe or copying BGRA to FFmpeg.
/// </summary>
internal sealed class NativeGpuPipeline : IVideoPipeline
{
    private const string Library = "FocusWindow.Gpu";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_abi_version")]
    private static extern uint AbiVersion();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_open")]
    private static extern IntPtr Open(IntPtr hwnd, uint w, uint h, uint fps, uint bitrate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_read")]
    private static extern int Read(IntPtr session, uint timeoutMs, [Out] byte[] bytes,
        uint capacity, out uint written, out ulong qpc, out byte keyframe);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_force_idr")]
    private static extern void ForceIdr(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_last_error", CharSet = CharSet.Unicode)]
    private static extern int LastError(IntPtr session, [Out] char[] dest, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_close")]
    private static extern void Close(IntPtr session);

    private WorkspaceWindowHost? _window;
    private IntPtr _native;
    private bool _disposed;
    public VideoPipelineInfo Info { get; private set; } = null!;

    private static string Error(IntPtr handle)
    {
        var chars = new char[1024];
        var size = LastError(handle, chars, (uint)chars.Length);
        return size > 0 ? new string(chars, 0, Math.Min(size, chars.Length - 1)) : "Native GPU engine error";
    }

    public async Task StartAsync(VideoPipelineSettings settings, CancellationToken ct)
    {
        if (_disposed || _native != IntPtr.Zero)
            throw new InvalidOperationException("GPU engine cannot be started twice");
        if (AbiVersion() != 1) throw new InvalidOperationException("GPU native ABI mismatch");
        _window = new WorkspaceWindowHost();
        try
        {
            await _window.StartAsync(settings.BackingMonitorBounds, settings.Width,
                settings.Height, settings.RequiredDpi, ct).ConfigureAwait(false);
            _native = Open(_window.Handle, (uint)settings.Width, (uint)settings.Height,
                (uint)settings.Fps, checked((uint)settings.BitrateKbps * 1000));
            if (_native == IntPtr.Zero) throw new InvalidOperationException(Error(IntPtr.Zero));
            Info = new VideoPipelineInfo(_window.Bounds, _window.Dpi, settings.Width,
                settings.Height, "h264", "YUV420", "NVENC / WGC",
                "BT.709 full range", true);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var handle = _native;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("GPU pipeline not started");
        var packet = new byte[1024 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var sample = await Task.Run(() =>
            {
                var status = Read(handle, 25, packet, (uint)packet.Length,
                    out var size, out var qpc, out var keyframe);
                return (status, size, qpc, keyframe);
            }, ct).ConfigureAwait(false);
            if (sample.status == -2)
            {
                if (sample.size > 8 * 1024 * 1024)
                    throw new IOException("GPU access unit exceeds 8 MiB");
                packet = new byte[sample.size];
                continue;
            }
            if (sample.status < 0) throw new IOException(Error(handle));
            if (sample.status == 0) continue;
            var encoded = packet.AsSpan(0, checked((int)sample.size)).ToArray();
            yield return new GpuVideoPacket(encoded, sample.keyframe != 0, sample.qpc);
        }
    }

    public void RequestKeyframe()
    {
        if (_native != IntPtr.Zero) ForceIdr(_native);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var old = _native;
        _native = IntPtr.Zero;
        if (old != IntPtr.Zero) Close(old); // consumer must stop packet worker first
        if (_window is not null) await _window.DisposeAsync().ConfigureAwait(false);
        _window = null;
    }
}

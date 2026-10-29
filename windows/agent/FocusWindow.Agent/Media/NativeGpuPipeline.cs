using System.Runtime.InteropServices;
using System.Threading.Channels;
using FocusWindow.Core.Display;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;

namespace FocusWindow.Agent.Media;

/// <summary>
/// One MTA worker owns open/read/close, D3D11 and the native apartment for the
/// whole stream. No Task.Run per frame, cross-thread COM or close/read race.
/// Bounded encoded output waits; stale capture is discarded BEFORE encoding.
/// </summary>
internal sealed class NativeGpuPipeline(string? executable = null, string? arguments = null,
    WorkspaceApplicationCatalog? catalog = null) : IVideoPipeline
{
    private const string Library = "FocusWindow.Gpu";
    private readonly string _executable = ValidateExecutable(executable);
    private static string ValidateExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new DisplayException("source", "No supported application source was selected.",
                "Start the host with --workspace-app and an absolute .exe path, plus --workspace-args if a new-instance flag is required.");
        return path;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_abi_version")]
    private static extern uint AbiVersion();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_open")]
    private static extern nint Open(nint hwnd, uint width, uint height, uint fps, uint bitrate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_read")]
    private static extern int Read(nint session, uint timeoutMs, nint bytes,
        uint capacity, out uint written, out ulong qpc, out byte keyframe);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_force_idr")]
    private static extern void ForceIdr(nint session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_last_error", CharSet = CharSet.Unicode)]
    private static extern int LastError(nint session, [Out] char[] dest, uint capacity);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMetrics
    {
        public uint Size, Version;
        public ulong Frames, CaptureArrivalFallbackAus, QpcFrequency, SourceWaitMaxUs;
        public ulong EncodeSubmitMaxUs, BitstreamLockMaxUs, InspectCopyMaxUs;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_get_metrics")]
    private static extern int GetMetrics(nint session, out NativeMetrics metrics, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "fwgpu_close")]
    private static extern void Close(nint session);

    private readonly Channel<GpuVideoPacket> _packets = Channel.CreateBounded<GpuVideoPacket>(
        new BoundedChannelOptions(1) { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkspaceApplicationSource? _source;
    private Thread? _worker;
    private bool _workerStarted;
    private int _wantIdr = 1;
    private int _started;
    private int _disposed;
    public VideoPipelineInfo Info { get; private set; } = null!;
    private string _diagnostics = "nativeMetrics=not-sampled";
    public string Diagnostics => Volatile.Read(ref _diagnostics);

    private static string Error(nint handle)
    {
        var chars = new char[1024];
        var size = LastError(handle, chars, (uint)chars.Length);
        return size > 0 ? new string(chars, 0, Math.Min(size, chars.Length - 1)) : "Native GPU engine error";
    }

    public async Task StartAsync(VideoPipelineSettings settings, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("GPU pipeline already started");
        try
        {
            if (AbiVersion() != 1) throw new DisplayException("gpu-abi", "DeskFuse native GPU ABI mismatch.");
            var source = catalog is null ? new WorkspaceApplicationSource(_executable, arguments) :
                catalog.Get(settings.EnrollmentId ?? throw new DisplayException("source", "An enrolled source identity is required."),
                    _executable, arguments);
            source.Attach();
            _source = source;
            await _source.StartAsync(settings, ct).ConfigureAwait(false);
            Info = new VideoPipelineInfo(_source.Bounds, _source.Dpi, settings.Width, settings.Height,
                "h264", "YUV420", "NVENC / WGC", "BT.709 full range", true,
                _source.AppLabel, _source.SourceId, settings.Fps);
            Info.RequireNativeGeometry(settings);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hwnd = _source.Handle;
            _worker = new Thread(() => Run(hwnd, settings, ready))
                { IsBackground = true, Name = "DeskFuse WGC/NVENC" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
            _workerStarted = true;
            await ready.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private unsafe void Run(nint hwnd, VideoPipelineSettings settings, TaskCompletionSource ready)
    {
        nint native = nint.Zero;
        Exception? failure = null;
        GpuVideoPacket? packet = null;
        try
        {
            native = Open(hwnd, (uint)settings.Width, (uint)settings.Height, (uint)settings.Fps,
                checked((uint)settings.BitrateKbps * 1000));
            if (native == nint.Zero) throw new DisplayException("gpu", Error(nint.Zero),
                "This backend requires Windows Graphics Capture and an NVENC-capable NVIDIA adapter/driver.");
            ready.TrySetResult();
            var capacity = 1024 * 1024 - GpuVideoPacket.TotalOverheadBytes;
            var lastGeometryCheck = Environment.TickCount64;
            var lastMetricsCheck = lastGeometryCheck;
            var metricsAvailable = true;
            long nativeReadMaxUs = 0;
            while (!_stop.IsCancellationRequested)
            {
                // Do not encode a second pending access unit while the channel
                // is occupied; WGC retains the latest source frame upstream.
                if (!_packets.Writer.WaitToWriteAsync(_stop.Token).AsTask().GetAwaiter().GetResult())
                    break;
                if (Environment.TickCount64 - lastGeometryCheck >= 100)
                {
                    if (_source?.IsCurrent() != true)
                        throw new DisplayException("source-geometry", "The captured app was moved, resized, minimized or changed DPI.",
                            "Restore it and reconnect. Stale input geometry is never used.");
                    lastGeometryCheck = Environment.TickCount64;
                }
                if (Interlocked.Exchange(ref _wantIdr, 0) != 0) ForceIdr(native);
                packet ??= GpuVideoPacket.Rent(capacity);
                int status;
                uint size;
                ulong qpc;
                byte keyframe;
                var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                fixed (byte* output = packet.WritablePayload.Span)
                    status = Read(native, 25, (nint)output, (uint)packet.Capacity, out size, out qpc, out keyframe);
                nativeReadMaxUs = Math.Max(nativeReadMaxUs,
                    (long)(System.Diagnostics.Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds * 1000));
                if (metricsAvailable && Environment.TickCount64 - lastMetricsCheck >= 1000)
                {
                    lastMetricsCheck = Environment.TickCount64;
                    try
                    {
                        if (GetMetrics(native, out var metrics, 64) == 1 && metrics.Size == 64 && metrics.Version == 1)
                            Volatile.Write(ref _diagnostics, $"nativeMetricsV=1 nativeAus={metrics.Frames} qpcHz={metrics.QpcFrequency} " +
                                $"captureArrivalFallbackAus={metrics.CaptureArrivalFallbackAus} sourceWaitMaxUs={metrics.SourceWaitMaxUs} " +
                                $"encodeSubmitMaxUs={metrics.EncodeSubmitMaxUs} bitstreamLockMaxUs={metrics.BitstreamLockMaxUs} " +
                                $"inspectCopyMaxUs={metrics.InspectCopyMaxUs} readApiMaxUs={nativeReadMaxUs} gpuCopyCompletion=unmeasured");
                    }
                    catch (EntryPointNotFoundException) { metricsAvailable = false; }
                }
                if (status == -2)
                {
                    if (size <= packet.Capacity || size > 8 * 1024 * 1024) throw new IOException("Invalid GPU access-unit size");
                    capacity = checked((int)size);
                    packet.Dispose();
                    packet = null;
                    continue;
                }
                if (status < 0) throw new DisplayException("gpu", Error(native));
                if (status == 0) continue;
                if (status != 1 || size == 0 || size > packet.Capacity) throw new IOException("Invalid native GPU packet contract");
                packet.Complete(checked((int)size), keyframe != 0, qpc);
                _packets.Writer.WriteAsync(packet, _stop.Token)
                    .AsTask().GetAwaiter().GetResult();
                packet = null; // ownership passed to the channel
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { ready.TrySetCanceled(); }
        catch (Exception ex) { failure = ex; ready.TrySetException(ex); }
        finally
        {
            packet?.Dispose();
            try { if (native != nint.Zero) Close(native); }
            catch (Exception ex) { failure ??= ex; }
            finally
            {
                _packets.Writer.TryComplete(failure);
                _finished.TrySetResult();
            }
        }
    }

    public IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync(CancellationToken ct) => _packets.Reader.ReadAllAsync(ct);
    public void RequestKeyframe() => Interlocked.Exchange(ref _wantIdr, 1);
    public bool TryPrepareInput(InputEventDto input) => _source?.TryPrepareInput(input) == true;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_workerStarted) await _finished.Task.ConfigureAwait(false);
        while (_packets.Reader.TryRead(out var packet)) packet.Dispose();
        if (_source is not null)
        {
            if (catalog is null) await _source.DisposeAsync().ConfigureAwait(false);
            else _source.Detach();
        }
        _source = null;
        _stop.Dispose();
    }
}

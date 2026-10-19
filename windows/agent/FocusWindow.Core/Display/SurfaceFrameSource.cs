using System.Diagnostics;
namespace FocusWindow.Core.Display;
/// <summary>
/// <see cref="IFrameSource"/> over the driver's shared surface. The wait primitive is injected:
/// production passes the named auto-reset event the driver signals per published frame; tests
/// pass whatever their synthetic writer signals.
///
/// Always returns the NEWEST frame and skips anything older (latest-wins). That is what keeps
/// latency bounded when the encoder is slower than the display.
/// </summary>
public sealed class SurfaceFrameSource : IFrameSource
{
    private readonly SurfaceReader _reader;
    private readonly Func<int, bool> _waitForFrame;
    private readonly Action? _onDispose;
    private ulong _lastSeq;
    public SurfaceFrameSource(SurfaceReader reader, Func<int, bool> waitForFrame, Action? onDispose = null)
    {
        _reader = reader;
        _waitForFrame = waitForFrame;
        _onDispose = onDispose;
    }
    public ulong LastSeq => _lastSeq;
    public bool TryRead(int timeoutMs, ref byte[] buffer, out FrameInfo info)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs / 1000.0 * Stopwatch.Frequency);
        while (true)
        {
            switch (_reader.TryReadNewest(_lastSeq, ref buffer, out info))
            {
                case ReadOutcome.Ok:
                    _lastSeq = info.Seq;
                    return true;
                case ReadOutcome.Invalid:
                    throw new DisplayException("capture",
                        "The shared frame surface contains an impossible frame description.",
                        "Restart the host agent; if it persists, reinstall the display driver.");
                case ReadOutcome.Torn:
                    continue; // the writer lapped us: take the next one immediately
                case ReadOutcome.NoNewFrame:
                    break;
            }
            var remainingMs = (int)((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
            if (remainingMs <= 0) return false;
            // Cap each wait so a signal consumed by someone else (or lost) costs at most one
            // poll interval instead of the whole timeout.
            _waitForFrame(Math.Min(remainingMs, 50));
        }
    }
    public void Dispose() => _onDispose?.Invoke();
}

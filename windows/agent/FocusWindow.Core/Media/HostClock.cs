namespace FocusWindow.Core.Media;

/// <summary>
/// One clock for everything the host sends.
///
/// The picture and the sound are captured by different sessions, and a
/// session-relative timestamp cannot be compared with another session's.
/// Every stream therefore carries a timestamp from this one origin, so the
/// phone can measure how long each path took and align them.
///
/// The origin is the host's monotonic clock, not the wall clock: two
/// machines never share a wall clock exactly enough for a 20 ms target.
/// </summary>
public static class HostClock
{
    private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>The host's monotonic microseconds since this process started.</summary>
    public static long NowMicroseconds() => ElapsedMicroseconds(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>Converts a raw timestamp from the same clock.</summary>
    public static long ElapsedMicroseconds(long timestamp) =>
        (long)(System.Diagnostics.Stopwatch.GetElapsedTime(Origin, timestamp).TotalMilliseconds * 1000.0);
}

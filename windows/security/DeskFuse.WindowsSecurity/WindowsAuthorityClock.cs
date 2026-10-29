using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskFuse.WindowsSecurity;

/// <summary>Suspend-inclusive boot clock for authority. Audio source QPC/correlation remains a separate clock.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAuthorityClock
{
    public static ulong Now100Nanoseconds { get { QueryInterruptTimePrecise(out var value); return value; } }
    public static long NowMilliseconds => checked((long)(Now100Nanoseconds / 10000));
    [DllImport("kernel32.dll")] private static extern void QueryInterruptTimePrecise(out ulong value);
}

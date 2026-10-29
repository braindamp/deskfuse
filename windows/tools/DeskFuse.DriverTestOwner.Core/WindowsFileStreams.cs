using Microsoft.Win32.SafeHandles;

namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Compatibility facade for held-handle stream verification.</summary>
public static class WindowsFileStreams
{
    public static void RequireOnlyUnnamedDataStream(SafeFileHandle file)
        => WindowsSecurity.WindowsFileStreams.RequireOnlyUnnamedDataStream(file);
    internal static void RequireOnlyUnnamedDataStream(ReadOnlySpan<byte> buffer)
        => WindowsSecurity.WindowsFileStreams.RequireOnlyUnnamedDataStream(buffer);
}

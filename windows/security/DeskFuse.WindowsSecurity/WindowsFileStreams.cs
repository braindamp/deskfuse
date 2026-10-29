using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.WindowsSecurity;

/// <summary>Enumerate streams from the already held file, never a second path open.</summary>
public static class WindowsFileStreams
{
    private const int FileStreamInfo = 7, MoreData = 234, InsufficientBuffer = 122, HeaderBytes = 24;
    private const int MaxEnumerationBytes = 1024 * 1024;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        [Out] byte[] buffer, uint bufferSize);

    public static void RequireOnlyUnnamedDataStream(SafeFileHandle file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stream verification is required.");
        RequireOnWindows(file);
    }

    [SupportedOSPlatform("windows")]
    private static void RequireOnWindows(SafeFileHandle file)
    {
        for (var size = 4096; size <= MaxEnumerationBytes; size *= 2)
        {
            var buffer = new byte[size];
            if (GetFileInformationByHandleEx(file, FileStreamInfo, buffer, (uint)buffer.Length))
            {
                RequireOnlyUnnamedDataStream(buffer);
                return;
            }
            var error = Marshal.GetLastWin32Error();
            if (error is not (MoreData or InsufficientBuffer))
                throw new Win32Exception(error, "Unable to enumerate package file streams.");
        }
        throw new InvalidDataException("Package file stream enumeration exceeds its bound.");
    }

    // FILE_STREAM_INFO is a chain of variable-size records; the kernel has no
    // explicit bytes-returned field, so every offset/length is bounds-checked.
    internal static void RequireOnlyUnnamedDataStream(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;
        var count = 0;
        while (true)
        {
            if (offset < 0 || buffer.Length - offset < HeaderBytes)
                throw new InvalidDataException("Malformed stream enumeration.");
            var record = buffer[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            if (nameLength == 0 || (nameLength & 1) != 0 || nameLength > (uint)(record.Length - HeaderBytes) ||
                (next != 0 && (next < HeaderBytes + nameLength || (next & 7) != 0 || next > record.Length)))
                throw new InvalidDataException("Malformed stream enumeration.");
            var name = Encoding.Unicode.GetString(record.Slice(HeaderBytes, checked((int)nameLength)));
            if (!string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase) || ++count != 1)
                throw new InvalidDataException("Package payload contains an alternate data stream.");
            if (next == 0) return;
            offset = checked(offset + (int)next);
        }
    }
}

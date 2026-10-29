using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskFuse.DriverTestOwner;

/// <summary>
/// Ask SetupAPI which catalog the current platform selects from the held INF.
/// The caller must hold protected directory and INF identity through this path read.
/// </summary>
internal static class WindowsInfCatalogSelection
{
    private const uint InfNameIsAbsolute = 2;
    private const int InsufficientBuffer = 122, MaxInfInformationBytes = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OriginalFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OriginalInfName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OriginalCatalogName;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfInformationW(string path, uint searchControl, nint buffer, uint capacity, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupQueryInfOriginalFileInformationW(nint information, uint index, nint alternatePlatform,
        ref OriginalFileInfo result);

    [SupportedOSPlatform("windows")]
    internal static string SelectedCatalog(FileStream inf)
    {
        if (!inf.CanRead || !inf.CanSeek || inf.SafeFileHandle.IsInvalid || inf.SafeFileHandle.IsClosed)
            throw new InvalidDataException("INF must remain open under protected custody.");
        if (!SetupGetInfInformationW(inf.Name, InfNameIsAbsolute, nint.Zero, 0, out var required) &&
            Marshal.GetLastWin32Error() != InsufficientBuffer)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read INF metadata.");
        if (required < 8 || required > MaxInfInformationBytes)
            throw new InvalidDataException("Invalid INF metadata size.");
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!SetupGetInfInformationW(inf.Name, InfNameIsAbsolute, buffer, required, out var actual))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read INF metadata.");
            // SP_INF_INFORMATION.InfCount is the second DWORD; never append or
            // silently select a constituent from a multi-INF information set.
            if (actual > required || Marshal.ReadInt32(buffer, 4) != 1)
                throw new InvalidDataException("Ambiguous INF metadata.");
            var original = new OriginalFileInfo
            {
                Size = (uint)Marshal.SizeOf<OriginalFileInfo>(), OriginalInfName = "", OriginalCatalogName = ""
            };
            if (!SetupQueryInfOriginalFileInformationW(buffer, 0, nint.Zero, ref original))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot determine the target catalog selected by SetupAPI.");
            if (string.IsNullOrEmpty(original.OriginalCatalogName)) throw new InvalidDataException("INF has no selected catalog.");
            return original.OriginalCatalogName;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

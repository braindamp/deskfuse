using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.DriverTestOwner;

internal static class SetupApi
{
    internal const uint DifRegisterDevice = 0x19, SpdrpHardwareId = 1, DigcfAllClasses = 4;
    internal const uint SpditCompatDriver = 2, DiEnumSingleInf = 0x00010000, DiQuietInstall = 0x00800000;
    internal const uint DiNeedRestart = 0x80, DiNeedReboot = 0x100, DevpropTypeString = 0x12;
    internal const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122, ErrorNotFound = 1168;
    internal const int ErrorDevinstAlreadyExists = unchecked((int)0xE0000207);
    internal const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
        public static DeviceInfo Empty => new() { CbSize = (uint)Marshal.SizeOf<DeviceInfo>() };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InstallParameters
    {
        public uint CbSize, Flags, FlagsEx;
        public nint HwndParent, InstallMsgHandler, InstallMsgHandlerContext, FileQueue;
        public nuint ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
        public static InstallParameters Empty => new() { CbSize = (uint)Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverInfo
    {
        public uint CbSize, DriverType;
        public nuint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
        public System.Runtime.InteropServices.ComTypes.FILETIME Date;
        public ulong Version;
        public static DriverInfo Empty => new() { CbSize = (uint)Marshal.SizeOf<DriverInfo>(),
            Description = "", Manufacturer = "", Provider = "" };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DriverDetail
    {
        public uint CbSize;
        public System.Runtime.InteropServices.ComTypes.FILETIME InfDate;
        public uint CompatIdsOffset, CompatIdsLength;
        public nuint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SectionName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InfFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] public string HardwareId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    internal sealed class InfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public InfoSet() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern InfoSet SetupDiCreateDeviceInfoList(ref Guid classGuid, nint parent);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiCreateDeviceInfoW(InfoSet set, string name, ref Guid classGuid,
        string? description, nint parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiSetDeviceRegistryPropertyW(InfoSet set, ref DeviceInfo info,
        uint property, byte[] buffer, uint bytes);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(InfoSet set, ref DeviceInfo info,
        uint property, out uint type, byte[]? buffer, uint bytes, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(InfoSet set, ref DeviceInfo info,
        StringBuilder id, uint chars, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiCallClassInstaller(uint function, InfoSet set, ref DeviceInfo info);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiOpenDeviceInfoW(InfoSet set, string id, nint parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern InfoSet SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInfo(InfoSet set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDevicePropertyW(InfoSet set, ref DeviceInfo info,
        ref PropertyKey key, out uint type, byte[]? buffer, uint bytes, out uint required, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstallParamsW(InfoSet set, ref DeviceInfo info, ref InstallParameters parameters);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiSetDeviceInstallParamsW(InfoSet set, ref DeviceInfo info, ref InstallParameters parameters);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiBuildDriverInfoList(InfoSet set, ref DeviceInfo info, uint driverType);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiEnumDriverInfoW(InfoSet set, ref DeviceInfo info, uint driverType, uint index, ref DriverInfo driver);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDriverInfoDetailW(InfoSet set, ref DeviceInfo info,
        ref DriverInfo driver, nint detail, uint bytes, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupCopyOEMInfW(string sourceInf, string? sourceMedia, uint sourceMediaType,
        uint copyStyle, StringBuilder destination, uint chars, out uint required, nint nameComponent);
    [DllImport("newdev.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool DiInstallDevice(nint parent, InfoSet set, ref DeviceInfo info,
        ref DriverInfo driver, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    [DllImport("newdev.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool DiUninstallDevice(nint parent, InfoSet set, ref DeviceInfo info,
        uint flags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern ulong GetTickCount64();
}

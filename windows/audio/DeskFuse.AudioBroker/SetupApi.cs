using System.Text;
using System.Runtime.InteropServices;

namespace DeskFuse.AudioBroker;

/// <summary>
/// The SetupAPI and newdev calls, kept apart from the logic that uses them.
/// The declarations are the contract; the behaviour lives in
/// <see cref="DeviceInstance"/>.
/// </summary>
internal static class SetupApi
{
    internal const uint DifRegisterDevice = 0x19;
    internal const uint SpdrpHardwareId = 1;
    internal const uint SpdrpFriendlyName = 12;
    internal const uint SpdrpService = 3;
    internal const uint SpdrpManufacturer = 11;
    internal const uint RegSz = 1;
    internal const uint RegMultiSz = 7;
    internal const uint DevPropTypeString = 0x00000012;
    internal const uint DigcfAllClasses = 0x4;
    internal const uint DbtDeviceNodesChanged = 0x00000007;

    /// <summary>DEVPKEY_Device_FriendlyName.</summary>
    internal static readonly Guid DevPropKeyDeviceFriendlyName =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
        public static SpDevInfoData Empty => new() { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
    }
    // Enumerate installed media-class devnodes (present or absent) so
    // root-enumerated per-phone devices left by a crashed broker can be pruned.
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SetupDiGetClassDevsW(ref Guid classGuid,
        nint enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SetupDiCreateDeviceInfoList(ref Guid classGuid, nint hwndParent);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiCreateDeviceInfoW(nint set, string name, ref Guid classGuid,
        string? description, nint hwndParent, uint creationFlags, ref SpDevInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId,
        nint hwndParent, uint flags, ref SpDevInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiSetDeviceRegistryPropertyW(nint set, ref SpDevInfoData info,
        uint property, byte[] buffer, uint bytes);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SpDevInfoData info,
        uint property, out uint type, byte[]? buffer, uint bytes, out uint required);

    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SpDevInfoData info,
        StringBuilder instanceId, uint chars, out uint required);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiCallClassInstaller(uint function, nint set, ref SpDevInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiRemoveDevice(nint set, ref SpDevInfoData info);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("newdev.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool DiInstallDevice(nint hwndParent, nint set, ref SpDevInfoData info,
        nint driverInfo, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

}

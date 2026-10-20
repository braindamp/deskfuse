using System.Runtime.InteropServices;
using System.Text;
using DeskFuse.Audio;

namespace DeskFuse.AudioBroker;

/// <summary>
/// One Windows device instance for one phone.
///
/// The device node is created from the installed DeskFuse Audio driver
/// package and the node is given this phone's name, so Windows publishes
/// one render endpoint per phone. Removing the node removes the endpoint
/// again.
///
/// The mechanism is the same one devcon uses: the driver package is applied
/// to a new device node for its hardware id, and the node is then named.
/// </summary>
internal static class DeviceInstance
{
    private const uint SpdrpHardwareId = 1;
    // SPDRP_SERVICE: the driver service the device must use. Without it the
    // class installer has nothing to bind and registration fails.
    private const uint SpdrpService = 3;
    private const uint SpdrpFriendlyName = 12;
    private const uint SpdrpManufacturer = 11;
    private const uint DifRegisterDevice = 24;
    private const uint RegSz = 1;
    private const uint DevPropTypeString = 0x00000012;
    // DEVPKEY_Device_FriendlyName
    private static readonly Guid DevPropKeyDeviceFriendlyName =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint RegMultiSz = 7;
    private const uint DigcfPresent = 0x2;
    // DICD_GENERATE_ID: the system assigns the device instance id.
    private const uint DicdGenerateId = 0x00000001;

    private static readonly Guid MediaClassGuid = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    internal static void Probe()
    {
        var names = new[] { "Root\\DeskFuseAudio", "deskfuse-speakers-abc", "ROOT\\MEDIA\\0007" };
        foreach (var listClass in new string?[] { MediaClassGuid.ToString("B"), null })
        {
            var set = SetupDiCreateDeviceInfoList(listClass, IntPtr.Zero);
            Console.WriteLine($"list={(listClass is null ? "(none)" : "MEDIA")} -> 0x{set.ToInt64():X}");
            if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;
            foreach (var name in names)
            {
                var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
                var guid = MediaClassGuid;
                var ok = SetupDiCreateDeviceInfo(set, name, ref guid, null, IntPtr.Zero, DicdGenerateId, ref info);
                Console.WriteLine($"   '{name}' -> {ok} error {Marshal.GetLastWin32Error()} devInst 0x{info.DevInst:X}");
            }
            SetupDiDestroyDeviceInfoList(set);
        }
        Console.WriteLine($"sizeof(SP_DEVINFO_DATA) = {Marshal.SizeOf<SpDevInfoData>()}");
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(string? classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName,
        ref Guid classGuid, string? deviceDescription, IntPtr hwndParent, uint creationFlags,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsEx(IntPtr classGuid, string? enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, uint propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, uint property, uint propertyRegDataType,
        byte[]? propertyBuffer, int propertyBufferSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiRemoveDevice(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DiInstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, IntPtr filterDriverInfo, uint flags, out int rebootRequired);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string? deviceInstanceId,
        string fullInfPath, uint installFlags, out int rebootRequired);

    /// <summary>
    /// Isolates which argument the device creation call rejects. One run,
    /// one line per case.
    /// </summary>
    internal static void Matrix()
    {
        var cases = new (string Label, string? ListClass, string Name, uint Flags, bool Params)[]
        {
            ("class list, hw id, no params", "M", "Root\\DeskFuseAudio", 0, false),
            ("class list, hw id, with params", "M", "Root\\DeskFuseAudio", 0, true),
            ("classless, full id, no params", null, "Root\\MEDIA\\deskfuse-speakers-y1", 0, false),
            ("classless, full id, with params", null, "Root\\MEDIA\\deskfuse-speakers-y2", 0, true),
            ("class list, full id, no params", "M", "Root\\MEDIA\\deskfuse-speakers-y3", 0, false),
        };
        foreach (var (label, listClass, name, flags, withParams) in cases)
        {
            var set = SetupDiCreateDeviceInfoList(listClass is null ? null : MediaClassGuid.ToString("B"), IntPtr.Zero);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                Console.WriteLine($"{label}: list failed {Marshal.GetLastWin32Error()}");
                continue;
            }
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            var guid = MediaClassGuid;
            if (!SetupDiCreateDeviceInfo(set, name, ref guid, null, IntPtr.Zero, flags, ref info))
            {
                Console.WriteLine($"{label}: create failed {Marshal.GetLastWin32Error()}");
                SetupDiDestroyDeviceInfoList(set);
                continue;
            }
            SetMultiStringProperty(set, ref info, SpdrpHardwareId, "Root\\DeskFuseAudio");
            SetStringProperty(set, ref info, SpdrpService, PhoneEndpointContract.ServiceName);
            SetStringProperty(set, ref info, SpdrpFriendlyName, "DeskFuse Matrix");
            if (withParams)
            {
                SetClassInstallParams(set, ref info, "Root\\DeskFuseAudio", "DeskFuseAudio_Device",
                    PhoneEndpointContract.ServiceName);
            }
            var registered = SetupDiCallClassInstaller(DifRegisterDevice, set, ref info);
            var error = Marshal.GetLastWin32Error();
            Console.WriteLine($"{label}: registered={registered} error {error} " +
                $"instance={ReadInstanceId(set, ref info)}");
            if (registered) TryRemove(set, ref info);
            SetupDiDestroyDeviceInfoList(set);
        }
    }




    // The class install parameters tell the class installer exactly which
    // INF section and driver to use for this device.
    [StructLayout(LayoutKind.Sequential)]
    private struct SpClassInstallHeader
    {
        public uint CbSize;
        public uint ClassInstallFunction;
        public SpClassInstallParams ClassInstallParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpClassInstallParams
    {
        public uint CbSize;
        public IntPtr TargetPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDispInstallParams
    {
        public uint CbSize;
        public uint InstallFlags;
        public IntPtr Hwnd;
        public IntPtr SzDevicePath;
        public IntPtr SzClass;
        public IntPtr SzDriver;
        public IntPtr PRegPath;
        public IntPtr PConfigData;
        public uint CConfigData;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, byte[] classInstallParams, uint classInstallParamsSize);

    /// <summary>
    /// Points the class installer at this driver's install section, so the
    /// registration binds the DeskFuse Audio driver instead of searching for
    /// one that matches the hardware id.
    /// </summary>
    private static void SetClassInstallParams(IntPtr infoSet, ref SpDevInfoData info,
        string devicePath, string installSection, string service)
    {
        var devicePathBuffer = Marshal.StringToHGlobalUni(devicePath);
        var classBuffer = Marshal.StringToHGlobalUni(installSection);
        var driverBuffer = Marshal.StringToHGlobalUni(service);
        try
        {
            var disp = new SpDispInstallParams
            {
                CbSize = (uint)Marshal.SizeOf<SpDispInstallParams>(),
                InstallFlags = 0,
                Hwnd = IntPtr.Zero,
                SzDevicePath = devicePathBuffer,
                SzClass = classBuffer,
                SzDriver = driverBuffer,
                PRegPath = IntPtr.Zero,
                PConfigData = IntPtr.Zero,
                CConfigData = 0,
            };
            var dispSize = (uint)Marshal.SizeOf<SpDispInstallParams>();
            var dispBuffer = Marshal.AllocHGlobal((int)dispSize);
            try
            {
                Marshal.StructureToPtr(disp, dispBuffer, false);
                var headerSize = (uint)Marshal.SizeOf<SpClassInstallHeader>();
                var total = headerSize + dispSize;
                var headerBuffer = Marshal.AllocHGlobal((int)total);
                try
                {
                    var header = new SpClassInstallHeader
                    {
                        CbSize = total,
                        ClassInstallFunction = DifRegisterDevice,
                        ClassInstallParams = new SpClassInstallParams
                        {
                            CbSize = (uint)Marshal.SizeOf<SpClassInstallParams>(),
                            TargetPath = IntPtr.Zero,
                        },
                    };
                    Marshal.StructureToPtr(header, headerBuffer, false);
                    // The dispatch parameters follow the header in the same buffer.
                    var dispBytes = new byte[dispSize];
                    Marshal.Copy(dispBuffer, dispBytes, 0, (int)dispSize);
                    Marshal.Copy(dispBytes, 0, headerBuffer + (int)headerSize, (int)dispSize);
                    var bytes = new byte[total];
                    Marshal.Copy(headerBuffer, bytes, 0, (int)total);
                    SetupDiSetClassInstallParams(infoSet, ref info, bytes, total);
                }
                finally
                {
                    Marshal.FreeHGlobal(headerBuffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dispBuffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(devicePathBuffer);
            Marshal.FreeHGlobal(classBuffer);
            Marshal.FreeHGlobal(driverBuffer);
        }
    }


    /// <summary>
    /// Renames an existing DeskFuse device instance, so the endpoint it
    /// publishes carries the phone's own name. Windows refreshes the
    /// endpoint name from the device after the change notification.
    /// </summary>
    internal static bool RenameDevice(string instancePath, string friendlyName)
    {
        var set = SetupDiGetClassDevsEx(IntPtr.Zero, "ROOT", IntPtr.Zero, 0x4 /* DIGCF_ALLCLASSES */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
        try
        {
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (var index = 0; SetupDiEnumDeviceInfo(set, index++, ref info); )
            {
                var buffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, 0)) continue;
                if (!string.Equals(buffer.ToString(), instancePath, StringComparison.OrdinalIgnoreCase)) continue;
                var key = DevPropKeyDeviceFriendlyName;
                var bytes = Encoding.Unicode.GetBytes(friendlyName + " ");
                var ok = SetupDiSetDeviceProperty(set, ref info, ref key, DevPropTypeString,
                    bytes, bytes.Length, 0);
                if (ok) SetupDiBroadcastDeviceChange(IntPtr.Zero, DbtDeviceNodesChanged, IntPtr.Zero, IntPtr.Zero);
                return ok;
            }
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiBroadcastDeviceChange(IntPtr hwnd, uint notification,
        IntPtr propertyData, IntPtr deviceData);

    private const uint DbtDeviceNodesChanged = 0x00000007;

    /// <summary>
    /// Creates the device node for one phone, names it, and returns its
    /// instance path.
    ///
    /// The node is created the way devcon creates one: a device info set
    /// for the media class, a device info carrying the hardware id, and
    /// DIF_REGISTERDEVICE, which makes the PnP manager install the driver
    /// that matches the hardware id.
    /// </summary>
    internal static PhoneDevice Create(string instanceId, string hardwareId, string friendlyName,
        string manufacturer)
    {
        var inf = FindDriverStoreInf() ?? throw new InvalidOperationException(
            "The DeskFuse Audio driver package is not in the driver store on this PC.");

        // A class-less device information set. A class-bound one is
        // rejected for a caller-chosen instance id.
        var infoSet = SetupDiCreateDeviceInfoList(null, IntPtr.Zero);
        if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
            throw new InvalidOperationException(
                $"Windows refused to open a device information set (error {Marshal.GetLastWin32Error()}).");

        var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
        var classGuid = MediaClassGuid;
        try
        {
            // The device name is the full instance path, and DICD_GENERATE_ID
            // is deliberately omitted: the phone then owns a stable instance
            // id such as Root\DeskFuseAudio\deskfuse-speakers-<hash>.
            if (!SetupDiCreateDeviceInfo(infoSet, instanceId, ref classGuid, null, IntPtr.Zero,
                    0, ref info))
                throw new InvalidOperationException(
                    $"Windows refused to create the device node for this phone (error {Marshal.GetLastWin32Error()}).");

            // These are properties of the *pending* device, so they are set
            // before the device is registered.
            SetMultiStringProperty(infoSet, ref info, SpdrpHardwareId, hardwareId);
            SetStringProperty(infoSet, ref info, SpdrpFriendlyName, friendlyName);
            SetStringProperty(infoSet, ref info, SpdrpManufacturer, manufacturer);
            // The manufacturer string carries the stable per-phone identity
            // into the device tree, so the instance always belongs to the
            // same enrolled phone.

            SetClassInstallParams(infoSet, ref info, hardwareId, "DeskFuseAudio_Device",
                PhoneEndpointContract.ServiceName);

            if (!SetupDiCallClassInstaller(DifRegisterDevice, infoSet, ref info))
            {
                // The class installer could not register the node. The
                // driver install service can do both steps in one call.
                var registerError = Marshal.GetLastWin32Error();
                if (!DiInstallDevice(IntPtr.Zero, infoSet, ref info, IntPtr.Zero, 0, out _))
                    throw new InvalidOperationException(
                        $"Windows refused to register the device node for this phone " +
                        $"(error {registerError}, install {Marshal.GetLastWin32Error()}).");
            }

            var instancePath = ReadInstanceId(infoSet, ref info);
            if (string.IsNullOrEmpty(instancePath))
                throw new InvalidOperationException("Windows did not report the new device instance id.");

            // The device instance now exists, so the endpoint builder can be
            // given the phone's own name.
            SetDeviceFriendlyName(infoSet, ref info, friendlyName);

            return new PhoneDevice(instancePath, friendlyName, inf);
        }
        catch
        {
            TryRemove(infoSet, ref info);
            throw;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData);

    /// <summary>
    /// The per-phone name on the device instance itself. The endpoint
    /// builder takes the name in brackets from this property when the
    /// driver does not put one on the KS interface.
    /// </summary>
    private static void SetDeviceFriendlyName(IntPtr infoSet, ref SpDevInfoData info, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + " ");
        var key = DevPropKeyDeviceFriendlyName;
        SetupDiSetDeviceProperty(infoSet, ref info, ref key,
            DevPropTypeString, bytes, bytes.Length, 0);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        ref Guid propertyKey, uint propertyType, byte[]? propertyBuffer, int propertyBufferSize,
        uint flags);

    private static void SetStringProperty(IntPtr infoSet, ref SpDevInfoData info, uint property, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        SetupDiSetDeviceRegistryProperty(infoSet, ref info, property, RegSz, bytes, bytes.Length);
    }

    /// <summary>
    /// A hardware id list is a REG_MULTI_SZ: every entry is null terminated
    /// and the list ends with a second null.
    /// </summary>
    private static void SetMultiStringProperty(IntPtr infoSet, ref SpDevInfoData info, uint property, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0\0");
        SetupDiSetDeviceRegistryProperty(infoSet, ref info, property, RegMultiSz, bytes, bytes.Length);
    }

    private static string ReadInstanceId(IntPtr infoSet, ref SpDevInfoData info)
    {
        var buffer = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceId(infoSet, ref info, buffer, buffer.Capacity, 0)
            ? buffer.ToString()
            : string.Empty;
    }

    private static void TryRemove(IntPtr infoSet, ref SpDevInfoData info)
    {
        if (info.DevInst == 0) return;
        try { SetupDiRemoveDevice(infoSet, ref info); } catch { /* best effort */ }
    }

    /// <summary>Removes the device node, which removes the phone's endpoint.</summary>
    internal static void Remove(string instancePath)
    {
        var set = SetupDiGetClassDevsEx(IntPtr.Zero, "ROOT", IntPtr.Zero, 0x4 /* DIGCF_ALLCLASSES */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (var index = 0; SetupDiEnumDeviceInfo(set, index++, ref info); )
            {
                var buffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, 0)) continue;
                if (!string.Equals(buffer.ToString(), instancePath, StringComparison.OrdinalIgnoreCase)) continue;
                SetupDiRemoveDevice(set, ref info);
                return;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>The instance paths of the DeskFuse devices on this machine.</summary>
    private static List<string> ExistingDevices(string hardwareId)
    {
        var found = new List<string>();
        var set = SetupDiGetClassDevsEx(IntPtr.Zero, "ROOT", IntPtr.Zero, 0x4 /* DIGCF_ALLCLASSES */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return found;
        try
        {
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (var index = 0; SetupDiEnumDeviceInfo(set, index++, ref info); )
            {
                var buffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, 0)) continue;
                if (HardwareIds(set, ref info).Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                    found.Add(buffer.ToString());
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return found;
    }

    private static string? NewInstance(List<string> before, string hardwareId)
    {
        foreach (var path in ExistingDevices(hardwareId))
            if (!before.Contains(path, StringComparer.OrdinalIgnoreCase)) return path;
        return null;
    }

    private static void Name(string instancePath, string friendlyName, string manufacturer)
    {
        var set = SetupDiGetClassDevsEx(IntPtr.Zero, "ROOT", IntPtr.Zero, 0x4 /* DIGCF_ALLCLASSES */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var info = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (var index = 0; SetupDiEnumDeviceInfo(set, index++, ref info); )
            {
                var buffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, 0)) continue;
                if (!string.Equals(buffer.ToString(), instancePath, StringComparison.OrdinalIgnoreCase)) continue;
                SetStringProperty(set, ref info, SpdrpFriendlyName, friendlyName);
                SetStringProperty(set, ref info, SpdrpManufacturer, manufacturer);
                return;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    

    private static string HardwareIds(IntPtr infoSet, ref SpDevInfoData info)
    {
        var size = 0;
        SetupDiGetDeviceRegistryProperty(infoSet, ref info, SpdrpHardwareId, RegMultiSz, null, 0, out size);
        if (size <= 0) return string.Empty;
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryProperty(infoSet, ref info, SpdrpHardwareId, RegMultiSz, buffer, size, out _))
            return string.Empty;
        return Encoding.Unicode.GetString(buffer);
    }

    /// <summary>
    /// The published copy of the driver INF in the driver store. Using the
    /// store copy keeps the device bound to the package that is actually
    /// installed on this machine.
    /// </summary>
    internal static string? FindDriverStoreInf()
    {
        var repository = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(repository)) return null;
        foreach (var folder in Directory.EnumerateDirectories(repository, "deskfuseaudio.inf_*"))
        {
            var inf = Path.Combine(folder, "DeskFuseAudio.inf");
            if (File.Exists(inf)) return inf;
        }
        return null;
    }
}

/// <summary>One created device node for one phone.</summary>
internal sealed class PhoneDevice
{
    public PhoneDevice(string instancePath, string friendlyName, string driverInf)
    {
        InstancePath = instancePath;
        FriendlyName = friendlyName;
        DriverInf = driverInf;
    }

    /// <summary>e.g. ROOT\MEDIA\0000</summary>
    public string InstancePath { get; }

    /// <summary>The device name Windows shows for this phone.</summary>
    public string FriendlyName { get; }

    /// <summary>The driver INF bound to the device.</summary>
    public string DriverInf { get; }
}

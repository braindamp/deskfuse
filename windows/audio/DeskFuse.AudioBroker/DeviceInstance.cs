using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DeskFuse.Audio;

namespace DeskFuse.AudioBroker;

/// <summary>Creates caller-named root devices using the already installed audio driver.</summary>
internal static class DeviceInstance
{
    private const uint SpdrpHardwareId = 1;
    private const uint SpdrpFriendlyName = 12;
    private const uint DifRegisterDevice = 0x19;
    private const int ErrorDevinstAlreadyExists = unchecked((int)0xE0000207);
    private static readonly Guid MediaClassGuid = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
        public static SpDevInfoData Empty => new() { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
    }

    // SDK GUIDs are binary. Registry property types are implicit in the property;
    // SetupDiSetDeviceRegistryPropertyW takes five arguments, not six.
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(ref Guid classGuid, nint hwndParent);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfoW(nint set, string name, ref Guid classGuid,
        string? description, nint hwndParent, uint creationFlags, ref SpDevInfoData info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId,
        nint hwndParent, uint flags, ref SpDevInfoData info);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(nint set, ref SpDevInfoData info,
        uint property, byte[] buffer, uint bytes);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref SpDevInfoData info,
        uint property, out uint type, byte[]? buffer, uint bytes, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SpDevInfoData info,
        StringBuilder instanceId, uint chars, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, nint set, ref SpDevInfoData info);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiRemoveDevice(nint set, ref SpDevInfoData info);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("newdev.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DiInstallDevice(nint hwndParent, nint set, ref SpDevInfoData info,
        nint driverInfo, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    private static nint OpenSet()
    {
        var guid = MediaClassGuid;
        var set = SetupDiCreateDeviceInfoList(ref guid, nint.Zero);
        if (set == new nint(-1)) throw Error("SetupDiCreateDeviceInfoList");
        return set;
    }

    private static InvalidOperationException Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{operation}: 0x{unchecked((uint)code):X8} ({new Win32Exception(code).Message}).");
    }

    private static void SetProperty(nint set, ref SpDevInfoData info, uint property, string value, bool multi = false)
    {
        var bytes = Encoding.Unicode.GetBytes(value + (multi ? "\0\0" : "\0"));
        if (!SetupDiSetDeviceRegistryPropertyW(set, ref info, property, bytes, (uint)bytes.Length))
            throw Error($"SetupDiSetDeviceRegistryPropertyW({property})");
    }

    private static void SetFriendlyNameIfChanged(nint set, ref SpDevInfoData info, string value)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref info, SpdrpFriendlyName, out _, null, 0, out var size);
        if (size != 0)
        {
            var buffer = new byte[size];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, SpdrpFriendlyName,
                    out _, buffer, size, out _))
                throw Error("SetupDiGetDeviceRegistryPropertyW(friendly name)");
            if (Encoding.Unicode.GetString(buffer).TrimEnd('\0').Equals(value, StringComparison.Ordinal))
                return;
        }
        SetProperty(set, ref info, SpdrpFriendlyName, value);
    }

    private static string[] HardwareIds(nint set, ref SpDevInfoData info)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref info, SpdrpHardwareId, out _, null, 0, out var size);
        if (size == 0) return [];
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, SpdrpHardwareId, out _, buffer, size, out _))
            throw Error("SetupDiGetDeviceRegistryPropertyW");
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void RequireAudioDevice(nint set, ref SpDevInfoData info)
    {
        if (info.ClassGuid != MediaClassGuid ||
            !HardwareIds(set, ref info).Contains(PhoneEndpointContract.HardwareId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The instance does not belong to the DeskFuse Audio driver.");
    }

    private static string ReadInstanceId(nint set, ref SpDevInfoData info)
    {
        var text = new StringBuilder(1024);
        if (!SetupDiGetDeviceInstanceIdW(set, ref info, text, (uint)text.Capacity, out _))
            throw Error("SetupDiGetDeviceInstanceIdW");
        return text.ToString();
    }

    private static void RequirePhonePath(string path)
    {
        var prefix = PhoneEndpointContract.InstanceRoot + "\\" + PhoneEndpointContract.InstancePrefix;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !PhoneEndpointContract.IsPeerId(path[prefix.Length..]))
            throw new ArgumentException("A full enrollment-derived DeskFuse audio instance path is required.");
    }

    internal static PhoneDevice Create(string instanceId, string hardwareId, string friendlyName, string manufacturer)
    {
        // Service and manufacturer are assigned by the installed INF.
        _ = manufacturer;
        RequirePhonePath(instanceId);
        if (!hardwareId.Equals(PhoneEndpointContract.HardwareId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the DeskFuse Audio hardware ID is supported.");
        var inf = FindDriverStoreInf() ?? throw new InvalidOperationException("The DeskFuse Audio driver is not installed.");
        var set = OpenSet();
        var info = SpDevInfoData.Empty;
        var created = false;
        try
        {
            var guid = MediaClassGuid;
            if (!SetupDiCreateDeviceInfoW(set, instanceId, ref guid, friendlyName, nint.Zero, 0, ref info))
            {
                if (Marshal.GetLastWin32Error() != ErrorDevinstAlreadyExists)
                    throw Error("SetupDiCreateDeviceInfoW");
                info = SpDevInfoData.Empty;
                if (!SetupDiOpenDeviceInfoW(set, instanceId, nint.Zero, 0, ref info))
                    throw Error("SetupDiOpenDeviceInfoW");
                RequireAudioDevice(set, ref info);
            }
            else
            {
                created = true;
                SetProperty(set, ref info, SpdrpHardwareId, hardwareId, multi: true);
                SetProperty(set, ref info, SpdrpFriendlyName, friendlyName);
                // 0x18 is DIF_ALLOW_INSTALL. Registration is 0x19 and has NO
                // fabricated class-install parameter payload.
                if (!SetupDiCallClassInstaller(DifRegisterDevice, set, ref info))
                    throw Error("SetupDiCallClassInstaller(DIF_REGISTERDEVICE=0x19)");
            }
            var actual = ReadInstanceId(set, ref info);
            if (!actual.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Windows returned '{actual}' instead of caller-selected '{instanceId}'.");
            var started = false;
            if (!created)
            {
                var result = CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0);
                if (result != 0) throw new InvalidOperationException($"CM_Get_DevNode_Status: 0x{result:X8}");
                started = (status & 0x8) != 0 && problem == 0; // DN_STARTED
            }
            // Reinstalling a healthy node restarts its subtree. Windows can
            // still report its old audio endpoint active for several seconds.
            if (!started)
            {
                if (!DiInstallDevice(nint.Zero, set, ref info, nint.Zero, 0, out var reboot))
                    throw Error("DiInstallDevice");
                if (reboot) throw new InvalidOperationException("This audio node requires a Windows restart; none was performed.");
            }
            SetFriendlyNameIfChanged(set, ref info, friendlyName);
            return new PhoneDevice(actual, friendlyName, inf);
        }
        catch
        {
            if (created && !SetupDiRemoveDevice(set, ref info))
                Console.Error.WriteLine($"Device rollback: 0x{unchecked((uint)Marshal.GetLastWin32Error()):X8}");
            throw;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static bool RenameDevice(string path, string friendlyName)
    {
        var set = OpenSet();
        try
        {
            var info = SpDevInfoData.Empty;
            if (!SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info)) return false;
            RequireAudioDevice(set, ref info);
            SetFriendlyNameIfChanged(set, ref info, friendlyName);
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static void RemoveNode(string path)
    {
        RequirePhonePath(path); // Never remove the existing generic ROOT\MEDIA\0000 baseline.
        var set = OpenSet();
        try
        {
            var info = SpDevInfoData.Empty;
            if (!SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info))
            {
                if (Marshal.GetLastWin32Error() == unchecked((int)0xE000020B)) return;
                throw Error("SetupDiOpenDeviceInfoW(remove)");
            }
            RequireAudioDevice(set, ref info);
            if (!SetupDiRemoveDevice(set, ref info)) throw Error("SetupDiRemoveDevice");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static async Task RemoveAsync(string path)
    {
        RemoveNode(path);
        // PnP removal precedes AudioEndpointBuilder's departure notification.
        // Hold the broker's device gate until the old endpoint is truly gone,
        // so immediate recreation cannot accidentally capture the old GUID.
        var deadline = Environment.TickCount64 + 20_000;
        Exception? lastError = null;
        while (true)
        {
            try
            {
                if (EndpointResolver.FindActiveEndpoint(path) is null)
                {
                    Console.WriteLine($"audio-endpoint-departed=True instance={path}");
                    return;
                }
                lastError = null;
            }
            catch (COMException ex)
            {
                // An unsuccessful enumeration is not proof of departure.
                lastError = ex;
            }
            if (Environment.TickCount64 >= deadline)
            {
                var message = $"Windows did not confirm playback endpoint departure for '{path}' within 20 seconds.";
                Console.Error.WriteLine(message);
                throw new TimeoutException(message, lastError);
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    internal static void Probe() => Console.WriteLine(
        $"SDK contracts: x64={Environment.Is64BitProcess}; SP_DEVINFO_DATA={Marshal.SizeOf<SpDevInfoData>()}; DIF_REGISTERDEVICE=0x{DifRegisterDevice:X2}; property setter args=5; class GUID=binary");

    internal static void Matrix() => MatrixAsync().GetAwaiter().GetResult();

    private static async Task MatrixAsync()
    {
        Probe();
        var devices = new List<PhoneDevice>();
        try
        {
            var ids = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
            var endpoints = new List<string>();
            for (var index = 0; index < ids.Length; index++)
            {
                var requested = PhoneEndpointContract.InstancePath(ids[index]);
                var label = "DeskFuse Matrix Phone " + (index == 0 ? "A" : "B");
                var device = Create(requested, PhoneEndpointContract.HardwareId, label, "DeskFuse");
                devices.Add(device);
                var endpoint = await EndpointResolver.ResolveAsync(device.InstancePath, TimeSpan.FromSeconds(20),
                    CancellationToken.None).ConfigureAwait(false);
                var state = ReadState(device.InstancePath);
                Console.WriteLine($"registered=True installed=True requested={requested} instance={device.InstancePath} " +
                    $"started={(state.Status & 8) != 0} problem={state.Problem} endpoint={endpoint ?? "(missing)"}");
                if (endpoint is null) throw new InvalidOperationException("No matching active playback endpoint for the new node.");
                endpoints.Add(endpoint);
            }
            if (endpoints[0] == endpoints[1]) throw new InvalidOperationException("Two phones resolved to the same playback endpoint.");
            Console.WriteLine("distinct-per-phone-endpoints=True");
            var original = devices[0];
            Create(original.InstancePath, PhoneEndpointContract.HardwareId, original.FriendlyName, "DeskFuse");
            // Diagnostic only: outlast the measured delayed replacement
            // caused by reinstalling an already started device.
            await Task.Delay(5000).ConfigureAwait(false);
            var reopened = await EndpointResolver.ResolveAsync(original.InstancePath, TimeSpan.FromSeconds(20),
                CancellationToken.None).ConfigureAwait(false);
            if (reopened != endpoints[0])
                throw new InvalidOperationException("Reopening a healthy phone changed its playback endpoint.");
            Console.WriteLine($"healthy-reopen-kept-endpoint=True instance={original.InstancePath} endpoint={reopened}");
            await RemoveAsync(original.InstancePath).ConfigureAwait(false);
            devices.RemoveAt(0);
            var recreated = Create(original.InstancePath, PhoneEndpointContract.HardwareId, original.FriendlyName, "DeskFuse");
            devices.Add(recreated);
            var restored = await EndpointResolver.ResolveAsync(recreated.InstancePath, TimeSpan.FromSeconds(20),
                CancellationToken.None).ConfigureAwait(false);
            if (restored is null) throw new InvalidOperationException("No endpoint after recreating the same caller-selected ID.");
            Console.WriteLine($"stable-instance-recreated=True instance={recreated.InstancePath} endpoint={restored} same-endpoint={restored == endpoints[0]}");
        }
        finally
        {
            foreach (var device in devices)
            {
                await RemoveAsync(device.InstancePath).ConfigureAwait(false);
                Console.WriteLine($"removed=True instance={device.InstancePath}");
            }
        }
    }

    private static (uint Status, uint Problem) ReadState(string path)
    {
        var set = OpenSet();
        try
        {
            var info = SpDevInfoData.Empty;
            if (!SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info)) throw Error("SetupDiOpenDeviceInfoW(status)");
            var result = CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0);
            if (result != 0) throw new InvalidOperationException($"CM_Get_DevNode_Status: 0x{result:X8}");
            return (status, problem);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static string? FindDriverStoreInf()
    {
        var repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(repository)) return null;
        return Directory.EnumerateDirectories(repository, "deskfuseaudio.inf_*")
            .Select(folder => Path.Combine(folder, "DeskFuseAudio.inf")).FirstOrDefault(File.Exists);
    }
}

internal sealed class PhoneDevice(string instancePath, string friendlyName, string driverInf)
{
    public string InstancePath { get; } = instancePath;
    public string FriendlyName { get; } = friendlyName;
    public string DriverInf { get; } = driverInf;
}

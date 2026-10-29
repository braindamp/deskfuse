using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DeskFuse.Audio;

namespace DeskFuse.AudioBroker;

/// <summary>Creates caller-named root devices using the already installed audio driver.</summary>
internal static class DeviceInstance
{
    private const int ErrorDevinstAlreadyExists = unchecked((int)0xE0000207);
    private static readonly Guid MediaClassGuid = new("4d36e96c-e325-11ce-bfc1-08002be10318");


    // SDK GUIDs are binary. Registry property types are implicit in the property;
    // SetupApi.SetupDiSetDeviceRegistryPropertyW takes five arguments, not six.
    private static nint OpenSet()
    {
        var guid = MediaClassGuid;
        var set = SetupApi.SetupDiCreateDeviceInfoList(ref guid, nint.Zero);
        if (set == new nint(-1)) throw Error("SetupApi.SetupDiCreateDeviceInfoList");
        return set;
    }

    private static InvalidOperationException Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{operation}: 0x{unchecked((uint)code):X8} ({new Win32Exception(code).Message}).");
    }

    private static void SetProperty(nint set, ref SetupApi.SpDevInfoData info, uint property, string value, bool multi = false)
    {
        var bytes = Encoding.Unicode.GetBytes(value + (multi ? "\0\0" : "\0"));
        if (!SetupApi.SetupDiSetDeviceRegistryPropertyW(set, ref info, property, bytes, (uint)bytes.Length))
            throw Error($"SetupApi.SetupDiSetDeviceRegistryPropertyW({property})");
    }

    private static void SetFriendlyNameIfChanged(nint set, ref SetupApi.SpDevInfoData info, string value)
    {
        SetupApi.SetupDiGetDeviceRegistryPropertyW(set, ref info, SetupApi.SpdrpFriendlyName, out _, null, 0, out var size);
        if (size != 0)
        {
            var buffer = new byte[size];
            if (!SetupApi.SetupDiGetDeviceRegistryPropertyW(set, ref info, SetupApi.SpdrpFriendlyName,
                    out _, buffer, size, out _))
                throw Error("SetupApi.SetupDiGetDeviceRegistryPropertyW(friendly name)");
            if (Encoding.Unicode.GetString(buffer).TrimEnd('\0').Equals(value, StringComparison.Ordinal))
                return;
        }
        SetProperty(set, ref info, SetupApi.SpdrpFriendlyName, value);
    }

    private static string[] HardwareIds(nint set, ref SetupApi.SpDevInfoData info)
    {
        SetupApi.SetupDiGetDeviceRegistryPropertyW(set, ref info, SetupApi.SpdrpHardwareId, out _, null, 0, out var size);
        if (size == 0) return [];
        var buffer = new byte[size];
        if (!SetupApi.SetupDiGetDeviceRegistryPropertyW(set, ref info, SetupApi.SpdrpHardwareId, out _, buffer, size, out _))
            throw Error("SetupApi.SetupDiGetDeviceRegistryPropertyW");
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void RequireAudioDevice(nint set, ref SetupApi.SpDevInfoData info)
    {
        if (info.ClassGuid != MediaClassGuid ||
            !HardwareIds(set, ref info).Contains(PhoneEndpointContract.HardwareId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The instance does not belong to the DeskFuse Audio driver.");
    }

    private static string ReadInstanceId(nint set, ref SetupApi.SpDevInfoData info)
    {
        var text = new StringBuilder(1024);
        if (!SetupApi.SetupDiGetDeviceInstanceIdW(set, ref info, text, (uint)text.Capacity, out _))
            throw Error("SetupApi.SetupDiGetDeviceInstanceIdW");
        return text.ToString();
    }

    private static void RequirePhonePath(string path)
    {
        var prefix = PhoneEndpointContract.InstanceRoot + "\\" + PhoneEndpointContract.InstancePrefix;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !PhoneEndpointContract.IsPeerId(path[prefix.Length..]))
            throw new ArgumentException("A full enrollment-derived DeskFuse audio instance path is required.");
    }

    internal static PhoneDevice Create(string instanceId, string hardwareId, string friendlyName, string manufacturer,
        bool allowExisting = true)
    {
        // Service and manufacturer are assigned by the installed INF.
        _ = manufacturer;
        RequirePhonePath(instanceId);
        if (!hardwareId.Equals(PhoneEndpointContract.HardwareId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the DeskFuse Audio hardware ID is supported.");
        var inf = FindDriverStoreInf() ?? throw new InvalidOperationException("The DeskFuse Audio driver is not installed.");
        var set = OpenSet();
        var info = SetupApi.SpDevInfoData.Empty;
        var created = false;
        try
        {
            var guid = MediaClassGuid;
            if (!SetupApi.SetupDiCreateDeviceInfoW(set, instanceId, ref guid, friendlyName, nint.Zero, 0, ref info))
            {
                if (Marshal.GetLastWin32Error() != ErrorDevinstAlreadyExists)
                    throw Error("SetupApi.SetupDiCreateDeviceInfoW");
                if (!allowExisting)
                    throw new InvalidOperationException("An unowned phone endpoint already exists; explicit recovery is required.");
                info = SetupApi.SpDevInfoData.Empty;
                if (!SetupApi.SetupDiOpenDeviceInfoW(set, instanceId, nint.Zero, 0, ref info))
                    throw Error("SetupApi.SetupDiOpenDeviceInfoW");
                RequireAudioDevice(set, ref info);
            }
            else
            {
                created = true;
                SetProperty(set, ref info, SetupApi.SpdrpHardwareId, hardwareId, multi: true);
                SetProperty(set, ref info, SetupApi.SpdrpFriendlyName, friendlyName);
                // 0x18 is DIF_ALLOW_INSTALL. Registration is 0x19 and has NO
                // fabricated class-install parameter payload.
                if (!SetupApi.SetupDiCallClassInstaller(SetupApi.DifRegisterDevice, set, ref info))
                    throw Error("SetupApi.SetupDiCallClassInstaller(DIF_REGISTERDEVICE=0x19)");
            }
            var actual = ReadInstanceId(set, ref info);
            if (!actual.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Windows returned '{actual}' instead of caller-selected '{instanceId}'.");
            var started = false;
            if (!created)
            {
                var result = SetupApi.CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0);
                if (result != 0) throw new InvalidOperationException($"SetupApi.CM_Get_DevNode_Status: 0x{result:X8}");
                started = (status & 0x8) != 0 && problem == 0; // DN_STARTED
            }
            // Reinstalling a healthy node restarts its subtree. Windows can
            // still report its old audio endpoint active for several seconds.
            if (!started)
            {
                if (!SetupApi.DiInstallDevice(nint.Zero, set, ref info, nint.Zero, 0, out var reboot))
                    throw Error("SetupApi.DiInstallDevice");
                if (reboot) throw new InvalidOperationException("This audio node requires a Windows restart; none was performed.");
            }
            SetFriendlyNameIfChanged(set, ref info, friendlyName);
            return new PhoneDevice(actual, friendlyName, inf);
        }
        catch (Exception ex)
        {
            if (created)
            {
                if (!SetupApi.SetupDiRemoveDevice(set, ref info))
                    Console.Error.WriteLine($"Device rollback: 0x{unchecked((uint)Marshal.GetLastWin32Error()):X8}");
                // A successful remove call is not proof of absence. Preserve the
                // exact created identity for confirmed cleanup or quarantine.
                throw new DeviceCreationIncompleteException(new PhoneDevice(instanceId, friendlyName, inf), ex);
            }
            throw;
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
    }

    internal static bool RenameDevice(string path, string friendlyName)
    {
        var set = OpenSet();
        try
        {
            var info = SetupApi.SpDevInfoData.Empty;
            if (!SetupApi.SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info)) return false;
            RequireAudioDevice(set, ref info);
            SetFriendlyNameIfChanged(set, ref info, friendlyName);
            return true;
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
    }

    private static void RemoveNode(string path)
    {
        RequirePhonePath(path); // Never remove the existing generic ROOT\MEDIA\0000 baseline.
        var set = OpenSet();
        try
        {
            var info = SetupApi.SpDevInfoData.Empty;
            if (!SetupApi.SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info))
            {
                if (Marshal.GetLastWin32Error() == unchecked((int)0xE000020B)) return;
                throw Error("SetupApi.SetupDiOpenDeviceInfoW(remove)");
            }
            RequireAudioDevice(set, ref info);
            // DIF_REMOVE lets class/device co-installers clean up before
            // Windows stops the devnode. Calling SetupDiRemoveDevice here is
            // only valid from a class installer (not this application).
            if (!SetupApi.SetupDiCallClassInstaller(SetupApi.DifRemoveDevice, set, ref info))
                throw Error("SetupApi.SetupDiCallClassInstaller(DIF_REMOVE=0x05)");
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>
    /// A departed PnP node and an MMDevice audio-list refresh are distinct
    /// events. The PnP node is authoritative for safe re-creation; CoreAudio
    /// may briefly report a cached endpoint after that node has gone.
    /// Never block the broker's device gate for 20 seconds on a shell refresh.
    /// </summary>
    internal static async Task RemoveAsync(string path)
    {
        RequirePhonePath(path);
        RemoveNode(path);

        // Confirm actual PnP absence, NOT just a successful remove request.
        // An unexpected Config Manager result is an error, not "removed".
        var pnpDeadline = Environment.TickCount64 + 2_000;
        while (true)
        {
            var code = SetupApi.CM_Locate_DevNodeW(out _, path, 0);
            if (code == SetupApi.CrNoSuchDevnode)
            {
                Console.WriteLine($"speaker-pnp-departed=True instance={path}");
                break;
            }
            if (code != SetupApi.CrSuccess)
                throw new InvalidOperationException(
                    $"CM_Locate_DevNodeW({path}) failed: CR=0x{code:X8}");
            if (Environment.TickCount64 >= pnpDeadline)
                throw new TimeoutException(
                    $"DeskFuse speaker remains in the PnP device tree 2s after DIF_REMOVE: {path}");
            await Task.Delay(100).ConfigureAwait(false);
        }

        // Keep the known prior endpoint GUID out of a new session wherever
        // possible, but do not mistake CoreAudio shell-cache lag for failed
        // PnP removal. The device is already gone from the live PnP tree.
        var audioDeadline = Environment.TickCount64 + 1_500;
        while (true)
        {
            try
            {
                var endpointId = EndpointResolver.FindActiveEndpoint(path);
                if (endpointId is null)
                {
                    Console.WriteLine($"speaker-audio-departed=True instance={path}");
                    return;
                }
                if (Environment.TickCount64 >= audioDeadline)
                {
                    Console.Error.WriteLine(
                        $"speaker-audio-refresh-pending=True instance={path} endpoint={endpointId}");
                    return;
                }
            }
            catch (COMException ex)
            {
                if (Environment.TickCount64 >= audioDeadline)
                {
                    Console.Error.WriteLine(
                        $"speaker-audio-refresh-unverified=True instance={path} error={ex.Message}");
                    return;
                }
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    internal static void Probe() => Console.WriteLine(
        $"SDK contracts: x64={Environment.Is64BitProcess}; SP_DEVINFO_DATA={Marshal.SizeOf<SetupApi.SpDevInfoData>()}; DIF_REGISTERDEVICE=0x{SetupApi.DifRegisterDevice:X2}; property setter args=5; class GUID=binary");

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
            // These temporary identities have no connected phone. Verify the
            // actual render mix without sending diagnostic audio to a user.
            var audioResult = await SpeakerCommands.RunAsync(
                ["audioroute", "--endpoint-id", endpoints[0], "--peer-id", ids[0]]).ConfigureAwait(false);
            if (audioResult != 0)
                throw new InvalidOperationException("The new endpoint did not return genuine application audio and silence.");
            Console.WriteLine("genuine-application-loopback=True");
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
            var info = SetupApi.SpDevInfoData.Empty;
            if (!SetupApi.SetupDiOpenDeviceInfoW(set, path, nint.Zero, 0, ref info)) throw Error("SetupApi.SetupDiOpenDeviceInfoW(status)");
            var result = SetupApi.CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0);
            if (result != 0) throw new InvalidOperationException($"SetupApi.CM_Get_DevNode_Status: 0x{result:X8}");
            return (status, problem);
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
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

internal sealed class DeviceCreationIncompleteException(PhoneDevice device, Exception cause)
    : Exception("Audio device creation did not complete; cleanup must be confirmed.", cause)
{
    public PhoneDevice Device { get; } = device;
}

internal sealed class PhoneDevice(string instancePath, string friendlyName, string driverInf)
{
    public string InstancePath { get; } = instancePath;
    public string FriendlyName { get; } = friendlyName;
    public string DriverInf { get; } = driverInf;
}

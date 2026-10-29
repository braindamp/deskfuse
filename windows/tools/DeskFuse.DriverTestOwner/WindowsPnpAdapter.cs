using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>
/// Exact-instance adapter. It is not a CLI and performs no work at construction.
/// The owner must validate protected bundle/signature/recovery admission before
/// dispatching it through RunCoordinator.Execute. No exception-time PnP cleanup.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPnpAdapter : IDriverNativeOperations, IDisposable
{
    private readonly RunManifest manifest;
    private readonly string sourceInf;
    private SetupApi.InfoSet? set;
    private SetupApi.DeviceInfo info = SetupApi.DeviceInfo.Empty;
    private string? publishedInf;

    internal WindowsPnpAdapter(RunManifest manifest, string protectedRunDirectory)
    {
        manifest.Validate(protectedRunDirectory);
        this.manifest = manifest;
        sourceInf = Path.Combine(protectedRunDirectory, "bundle", "package", "FocusIdd.inf");
    }

    public NativeResult Invoke(NativeOperation operation) => operation switch
    {
        NativeOperation.StagePackage => Stage(),
        NativeOperation.CreateElement => Create(),
        NativeOperation.SetHardwareId => SetHardwareId(),
        NativeOperation.RegisterDevice => Register(),
        NativeOperation.InstallDevice => Install(),
        NativeOperation.RemoveDevice => Remove(),
        _ => throw new InvalidDataException("Unknown native operation.")
    };

    public AbsenceObservation ObserveAbsence() => ObserveAbsence(manifest);

    private NativeResult Stage()
    {
        if (!manifest.BundleSha256.ContainsKey("package/FocusIdd.inf"))
            throw new InvalidDataException("Missing approved FocusIdd INF hash.");
        if (ContractJson.Hash(File.ReadAllBytes(sourceInf)) != manifest.BundleSha256["package/FocusIdd.inf"])
            throw new InvalidDataException("Approved INF changed before staging.");
        var destination = new StringBuilder(1024);
        if (!SetupApi.SetupCopyOEMInfW(sourceInf, null, 1, 0, destination, (uint)destination.Capacity, out _, nint.Zero))
            return Failed();
        publishedInf = Path.GetFullPath(destination.ToString());
        if (ContractJson.Hash(File.ReadAllBytes(publishedInf)) != manifest.BundleSha256["package/FocusIdd.inf"])
            throw new InvalidDataException("Published INF does not match the approved package.");
        return new(true);
    }

    private NativeResult Create()
    {
        if (set is not null) throw new InvalidOperationException("No device recreation or adoption.");
        var classGuid = manifest.ClassGuid;
        set = SetupApi.SetupDiCreateDeviceInfoList(ref classGuid, nint.Zero);
        RequireSet(set);
        // Flags=0: supplied full ID, never DICD_GENERATE_ID.
        if (!SetupApi.SetupDiCreateDeviceInfoW(set, manifest.InstanceId, ref classGuid,
                "DeskFuse temporary display proof", nint.Zero, 0, ref info))
        {
            var error = Marshal.GetLastWin32Error();
            return new(false, error, OwnershipConflict: error == SetupApi.ErrorDevinstAlreadyExists);
        }
        if (!string.Equals(InstanceId(set, ref info), manifest.InstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SetupAPI returned a different device identity.");
        return new(true);
    }

    private NativeResult SetHardwareId()
    {
        var current = RequireCurrent();
        var bytes = Encoding.Unicode.GetBytes(manifest.HardwareId + "\0\0");
        return SetupApi.SetupDiSetDeviceRegistryPropertyW(current, ref info, SetupApi.SpdrpHardwareId,
            bytes, (uint)bytes.Length) ? new(true) : Failed();
    }

    private NativeResult Register()
    {
        var current = RequireCurrent();
        var success = SetupApi.SetupDiCallClassInstaller(SetupApi.DifRegisterDevice, current, ref info);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        return new(success, error, RebootRequired: RestartRequired(current, ref info));
    }

    private NativeResult Install()
    {
        var current = RequireCurrent();
        if (publishedInf is null || publishedInf.Length >= 260) throw new InvalidDataException("Invalid staged INF path.");
        var parameters = Parameters(current, ref info);
        parameters.Flags |= SetupApi.DiEnumSingleInf | SetupApi.DiQuietInstall;
        parameters.DriverPath = publishedInf;
        if (!SetupApi.SetupDiSetDeviceInstallParamsW(current, ref info, ref parameters)) return Failed();
        if (!SetupApi.SetupDiBuildDriverInfoList(current, ref info, SetupApi.SpditCompatDriver)) return Failed();
        SetupApi.DriverInfo? selected = null;
        for (uint index = 0; ; index++)
        {
            var driver = SetupApi.DriverInfo.Empty;
            if (!SetupApi.SetupDiEnumDriverInfoW(current, ref info, SetupApi.SpditCompatDriver, index, ref driver))
            {
                var code = Marshal.GetLastWin32Error();
                if (code == SetupApi.ErrorNoMoreItems) break;
                return new(false, code);
            }
            var detail = Detail(current, ref info, ref driver);
            if (!string.Equals(Path.GetFullPath(detail.InfFileName), publishedInf, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Compatible list escaped the approved single INF.");
            if (selected is not null) throw new InvalidDataException("Ambiguous compatible driver nodes in approved INF.");
            selected = driver;
        }
        if (selected is null) throw new InvalidDataException("Approved package has no compatible driver node.");
        var exact = selected.Value;
        var success = SetupApi.DiInstallDevice(nint.Zero, current, ref info, ref exact, 0, out var reboot);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        return new(success, error, RebootRequired: RestartRequired(current, ref info) || reboot);
    }

    private NativeResult Remove()
    {
        using var devices = SetupApi.SetupDiGetClassDevsW(nint.Zero, null, nint.Zero, SetupApi.DigcfAllClasses);
        RequireSet(devices);
        var target = SetupApi.DeviceInfo.Empty;
        // Never pass DIOD_CANCEL_REMOVE, and never select a target by hardware-ID scan.
        if (!SetupApi.SetupDiOpenDeviceInfoW(devices, manifest.InstanceId, nint.Zero, 0, ref target))
        {
            var code = Marshal.GetLastWin32Error();
            if (code != SetupApi.ErrorNoSuchDevinst) return new(false, code);
        }
        else
        {
            RequireOwned(devices, ref target);
            var parameters = Parameters(devices, ref target);
            parameters.Flags |= SetupApi.DiQuietInstall;
            if (!SetupApi.SetupDiSetDeviceInstallParamsW(devices, ref target, ref parameters)) return Failed();
            var success = SetupApi.DiUninstallDevice(nint.Zero, devices, ref target, 0, out var reboot);
            var error = success ? 0 : Marshal.GetLastWin32Error();
            var restart = RestartRequired(devices, ref target) || reboot;
            if (!success || restart) return new(success, error, RebootRequired: restart);
        }
        var absence = ObserveAbsence(manifest);
        return new(true, ParentAndSubtreeAbsent: absence.ParentAndSubtreeAbsent,
            EnumerationSucceeded: absence.EnumerationSucceeded);
    }

    internal static AbsenceObservation ObserveAbsence(RunManifest manifest)
    {
        // DIGCF_PRESENT deliberately absent: include phantom records. Property
        // failures remain errors rather than fabricated evidence of absence.
        using var devices = SetupApi.SetupDiGetClassDevsW(nint.Zero, null, nint.Zero, SetupApi.DigcfAllClasses);
        RequireSet(devices);
        var entries = new List<(string Id, string? Parent)>();
        for (uint index = 0; ; index++)
        {
            var device = SetupApi.DeviceInfo.Empty;
            if (!SetupApi.SetupDiEnumDeviceInfo(devices, index, ref device))
            {
                if (Marshal.GetLastWin32Error() == SetupApi.ErrorNoMoreItems) break;
                throw Error("SetupDiEnumDeviceInfo");
            }
            var id = InstanceId(devices, ref device);
            entries.Add((id, Parent(devices, ref device, id)));
        }
        var descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifest.InstanceId };
        bool added;
        do
        {
            added = false;
            foreach (var entry in entries)
                if (entry.Parent is not null && descendants.Contains(entry.Parent)) added |= descendants.Add(entry.Id);
        } while (added);
        return new(manifest.RunId, manifest.InstanceId, !entries.Any(e => descendants.Contains(e.Id)), true);
    }

    private void RequireOwned(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device)
    {
        if (device.ClassGuid != manifest.ClassGuid ||
            !string.Equals(InstanceId(current, ref device), manifest.InstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery device identity/class mismatch.");
        _ = SetupApi.SetupDiGetDeviceRegistryPropertyW(current, ref device, SetupApi.SpdrpHardwareId,
            out _, null, 0, out var required);
        if (required is 0 or > 65536) throw Error("Hardware ID size");
        var buffer = new byte[required];
        if (!SetupApi.SetupDiGetDeviceRegistryPropertyW(current, ref device, SetupApi.SpdrpHardwareId,
                out var type, buffer, required, out _) || type != 7) throw Error("Hardware IDs");
        if (!Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Contains(manifest.HardwareId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Exact instance has unexpected hardware IDs; no adoption.");
    }

    private SetupApi.InfoSet RequireCurrent() => set is { IsInvalid: false, IsClosed: false }
        ? set : throw new InvalidOperationException("Device information element has not been created.");
    private static void RequireSet(SetupApi.InfoSet current)
    {
        if (current.IsInvalid) throw Error("Device information set");
    }
    private static NativeResult Failed(bool reboot = false) => new(false, Marshal.GetLastWin32Error(), reboot);
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private static string InstanceId(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device)
    {
        var id = new StringBuilder(512);
        if (!SetupApi.SetupDiGetDeviceInstanceIdW(current, ref device, id, (uint)id.Capacity, out _))
            throw Error("Device instance ID");
        return id.ToString();
    }

    private static SetupApi.InstallParameters Parameters(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device)
    {
        var parameters = SetupApi.InstallParameters.Empty;
        if (!SetupApi.SetupDiGetDeviceInstallParamsW(current, ref device, ref parameters)) throw Error("Install parameters");
        return parameters;
    }
    private static bool RestartRequired(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device) =>
        (Parameters(current, ref device).Flags & (SetupApi.DiNeedRestart | SetupApi.DiNeedReboot)) != 0;

    private static SetupApi.DriverDetail Detail(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device,
        ref SetupApi.DriverInfo driver)
    {
        _ = SetupApi.SetupDiGetDriverInfoDetailW(current, ref device, ref driver, nint.Zero, 0, out var required);
        if (required < Marshal.SizeOf<SetupApi.DriverDetail>() || required > 65536) throw Error("Driver detail size");
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, Marshal.SizeOf<SetupApi.DriverDetail>());
            if (!SetupApi.SetupDiGetDriverInfoDetailW(current, ref device, ref driver, buffer, required, out _))
                throw Error("Driver detail");
            return Marshal.PtrToStructure<SetupApi.DriverDetail>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? Parent(SetupApi.InfoSet current, ref SetupApi.DeviceInfo device, string id)
    {
        var key = new SetupApi.PropertyKey { Format = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), Id = 8 };
        var buffer = new byte[1024];
        if (!SetupApi.SetupDiGetDevicePropertyW(current, ref device, ref key, out var type, buffer,
                (uint)buffer.Length, out var required, 0))
        {
            if (Marshal.GetLastWin32Error() == SetupApi.ErrorNotFound &&
                string.Equals(id, @"HTREE\ROOT\0", StringComparison.OrdinalIgnoreCase)) return null;
            throw Error("Device parent property");
        }
        if (type != SetupApi.DevpropTypeString || required > buffer.Length || required < 2 || required % 2 != 0)
            throw new InvalidDataException("Invalid device parent property.");
        return Encoding.Unicode.GetString(buffer, 0, (int)required).TrimEnd('\0');
    }

    public void Dispose() => set?.Dispose(); // Handle release only; never hidden uninstall.
}

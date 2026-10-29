using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

[SupportedOSPlatform("windows")]
internal static class WindowsMaintenancePreflight
{
    // Read-only inventory in separately authorized trusted setup. No disable,
    // uninstall, package update or property mutation is performed by preflight.
    internal static void RequireNoFocusIddInstances()
    {
        using var devices = SetupApi.SetupDiGetClassDevsW(nint.Zero, null, nint.Zero, SetupApi.DigcfAllClasses);
        if (devices.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inventory maintenance devices.");
        for (uint i = 0; ; i++)
        {
            var device = SetupApi.DeviceInfo.Empty;
            if (!SetupApi.SetupDiEnumDeviceInfo(devices, i, ref device))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == SetupApi.ErrorNoMoreItems) return;
                throw new Win32Exception(error, "Incomplete maintenance inventory.");
            }
            if (i >= 16384) throw new InvalidDataException("Device inventory exceeds its bound.");
            var text = new StringBuilder(512);
            if (!SetupApi.SetupDiGetDeviceInstanceIdW(devices, ref device, text, (uint)text.Capacity, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unknown device instance during preflight.");
            var id = text.ToString();
            if (!NeedsHardwareIdentity(id, device.ClassGuid))
            { RequireNoLegacyIdentity(id, device.ClassGuid, null); continue; }
            _ = SetupApi.SetupDiGetDeviceRegistryPropertyW(devices, ref device, SetupApi.SpdrpHardwareId, out _, null, 0, out var required);
            if (Marshal.GetLastWin32Error() != SetupApi.ErrorInsufficientBuffer || required is < 4 or > 65536 || required % 2 != 0)
                throw new InvalidDataException("Legacy root display identity is unknown; explicit reconciliation is required.");
            var bytes = new byte[required];
            if (!SetupApi.SetupDiGetDeviceRegistryPropertyW(devices, ref device, SetupApi.SpdrpHardwareId,
                    out var type, bytes, required, out var returned) || type != 7 || returned != required)
                throw new InvalidDataException("Legacy root display hardware identity is invalid.");
            var values = Encoding.Unicode.GetString(bytes);
            if (!values.EndsWith("\0\0", StringComparison.Ordinal)) throw new InvalidDataException("Invalid hardware multi-string.");
            RequireNoLegacyIdentity(id, device.ClassGuid, values.TrimEnd('\0').Split('\0'));
        }
    }

    private static bool NeedsHardwareIdentity(string id, Guid classGuid) =>
        classGuid == RunManifest.DisplayClass && id.StartsWith(@"ROOT\DISPLAY\", StringComparison.OrdinalIgnoreCase);

    internal static void RequireNoLegacyIdentity(string id, Guid classGuid, string[]? hardwareIds)
    {
        if (id.StartsWith(@"ROOT\FOCUSIDD\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(@"ROOT\DESKFUSEFOCUSIDD\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Existing FocusIdd instance requires exact legacy/run reconciliation.");
        if (NeedsHardwareIdentity(id, classGuid))
        {
            if (hardwareIds is not { Length: > 0 } || hardwareIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Legacy root display identity is unknown.");
            if (hardwareIds.Any(h => string.Equals(h, @"Root\FocusIdd", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Existing legacy FocusIdd instance requires explicit reconciliation.");
        }
    }
}

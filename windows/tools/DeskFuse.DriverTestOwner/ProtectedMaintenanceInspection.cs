using System.Runtime.Versioning;
using System.Security.AccessControl;
using DeskFuse.WindowsSecurity;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.DriverTestOwner;

/// <summary>Non-authorizing compatibility facade for the shared retained inspection.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedMaintenanceInspection : IDisposable
{
    private readonly ProtectedDirectoryInspection inspection;
    internal ProtectedMaintenanceInspection(ProtectedDirectoryInspection inspection) => this.inspection = inspection;
    internal string DirectoryPath => inspection.DirectoryPath;
    internal static ProtectedMaintenanceInspection OpenExistingForInspection(string path)
        => new(ProtectedDirectoryInspection.OpenExistingForInspection(path));
    internal static ProtectedMaintenanceInspection OpenInstallerAnchorForBootstrap(string path)
        => new(ProtectedDirectoryInspection.OpenInstallerAnchorForBootstrap(path));
    internal void DemandLiveAuthorization() => inspection.DemandLiveAuthorization();
    internal void InspectHeldFile(SafeFileHandle file, string expectedLeaf) => inspection.InspectHeldFile(file, expectedLeaf);
    public void Dispose() => inspection.Dispose();
}

[SupportedOSPlatform("windows")]
internal static class MaintenanceDescriptorPolicy
{
    internal static void Validate(RawSecurityDescriptor descriptor, bool protectedRoot)
        => PrivateStorageDescriptorPolicy.Validate(descriptor, protectedRoot);
}

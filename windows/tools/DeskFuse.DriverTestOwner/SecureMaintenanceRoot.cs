using System.Runtime.Versioning;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.DriverTestOwner;

/// <summary>Compatibility facade for the fixed shared maintenance profile.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SecureMaintenanceRoot : IDisposable
{
    private readonly SecurePrivateStorageRoot storage;
    private SecureMaintenanceRoot(SecurePrivateStorageRoot storage) => this.storage = storage;
    internal string DirectoryPath => storage.DirectoryPath;
    internal Guid NamespaceId => storage.NamespaceId;
    internal static SecureMaintenanceRoot CreateFresh()
        => new(SecurePrivateStorageRoot.CreateFresh(PrivateStorageProfile.Maintenance));
    internal static SecureMaintenanceRoot OpenExisting(Guid namespaceId)
        => new(SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.Maintenance, namespaceId));
    internal void ValidateHeldIdentity() => storage.ValidateHeldIdentity();
    internal ProtectedMaintenanceInspection OpenDirectory(string relativePath) => new(storage.OpenDirectory(relativePath));
    internal ProtectedMaintenanceInspection CreateProtectedDirectory(string relativePath)
        => new(storage.CreateProtectedDirectory(relativePath));
    internal FileStream OpenProtectedRead(string relativePath) => storage.OpenProtectedRead(relativePath);
    internal FileStream CopyProtectedFile(string relativePath, Stream source, string expectedSha256,
        int maximumBytes, CancellationToken cancellationToken = default)
        => storage.CopyProtectedFile(relativePath, source, expectedSha256, maximumBytes, cancellationToken);
    internal static string ResolveRelative(string root, string relativePath)
        => SecurePrivateStorageRoot.ResolveRelative(root, relativePath);
    internal static byte[] ReadBounded(FileStream file, int maximum) => SecurePrivateStorageRoot.ReadBounded(file, maximum);
    public void Dispose() => storage.Dispose();
}

// Pure compatibility seams used by existing source and tests. All policies and
// copy/bootstrap algorithms live in DeskFuse.WindowsSecurity.
internal readonly record struct MaintenanceFileIdentity(ulong Volume, ulong Low, ulong High);

internal static class MaintenanceBootstrapReservation
{
    internal static string DirectoryForAnchor(string anchor)
        => PrivateStorageBootstrapReservation.DirectoryForAnchor(anchor, PrivateStorageProfile.Maintenance);
    internal static (string Directory, Guid NamespaceId) Reserve(string heldAnchor, Action<string> createNewPrivateDirectory)
        => PrivateStorageBootstrapReservation.Reserve(heldAnchor, PrivateStorageProfile.Maintenance, createNewPrivateDirectory);
}

internal static class PrivateFileCopy
{
    internal static void ValidateArguments(Stream source, string expectedSha256, int maximumBytes)
        => WindowsSecurity.PrivateFileCopy.ValidateArguments(source, expectedSha256, maximumBytes);
    internal static void CopyAndVerify(Stream source, Stream destination, string expectedSha256, int maximumBytes,
        CancellationToken cancellationToken = default)
        => WindowsSecurity.PrivateFileCopy.CopyAndVerify(source, destination, expectedSha256, maximumBytes, cancellationToken);
}

internal sealed record MaintenanceBootstrapRecord(int Version, Guid NamespaceId, ulong Volume, ulong FileLow, ulong FileHigh)
{
    internal void Require(Guid expected, MaintenanceFileIdentity identity)
        => new PrivateStorageBootstrapRecord(Version, NamespaceId, Volume, FileLow, FileHigh)
            .Require(expected, new PrivateStorageFileIdentity(identity.Volume, identity.Low, identity.High));
}

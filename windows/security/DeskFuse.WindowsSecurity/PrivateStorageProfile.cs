namespace DeskFuse.WindowsSecurity;

/// <summary>The only machine namespaces supported by private-at-creation storage.</summary>
public enum PrivateStorageProfile { Maintenance = 1, BrokerAuthority = 2 }

internal static class PrivateStorageProfiles
{
    internal static void Require(PrivateStorageProfile profile) => _ = DirectoryName(profile);

    internal static string DirectoryName(PrivateStorageProfile profile) => profile switch
    {
        PrivateStorageProfile.Maintenance => "DeskFuse-Maintenance",
        PrivateStorageProfile.BrokerAuthority => "DeskFuse-Authority",
        _ => throw new InvalidDataException("Unknown private storage profile.")
    };

    internal static IReadOnlyList<string> InitialFiles(PrivateStorageProfile profile) => profile switch
    {
        PrivateStorageProfile.Maintenance => [],
        PrivateStorageProfile.BrokerAuthority => [BrokerOwnerLeasePolicy.FileName],
        _ => throw new InvalidDataException("Unknown private storage profile.")
    };

    internal static IReadOnlyList<string> InitialDirectories(PrivateStorageProfile profile) => profile switch
    {
        PrivateStorageProfile.Maintenance => ["approvals", "runs"],
        PrivateStorageProfile.BrokerAuthority => ["enrollments", "bootstrap", "ceremonies"],
        _ => throw new InvalidDataException("Unknown private storage profile.")
    };
}

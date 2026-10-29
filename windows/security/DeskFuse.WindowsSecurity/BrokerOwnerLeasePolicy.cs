namespace DeskFuse.WindowsSecurity;

// Fixed native open policy for the one broker owner file, not a mutable-file API.
internal static class BrokerOwnerLeasePolicy
{
    internal const string FileName = "owner.lock";
    internal const uint DesiredAccess = 0xc0020080; // GENERIC_READ | GENERIC_WRITE | READ_CONTROL | FILE_READ_ATTRIBUTES
    internal const uint ShareMode = 0; // FileShare.None, including no delete sharing
    internal const uint OpenExisting = 3;
    internal const uint OpenReparsePoint = 0x00200000;

    internal static void RequireProfile(PrivateStorageProfile profile)
    {
        if (profile != PrivateStorageProfile.BrokerAuthority)
            throw new InvalidOperationException("Only the broker authority profile has a service owner lease.");
    }
}

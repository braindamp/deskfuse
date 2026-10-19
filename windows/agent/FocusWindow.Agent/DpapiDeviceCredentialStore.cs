using System.Security.Cryptography;
using FocusWindow.Core.Transport;

namespace FocusWindow.Agent;

/// <summary>
/// Approved phone credentials, encrypted using Windows DPAPI for the current user.
/// The store itself lives in Core with an injectable protector so corruption,
/// tampering and recovery are covered by tests. A damaged store fails CLOSED:
/// no previous device can connect until re-enrolled.
/// </summary>
internal sealed class DpapiDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly FileDeviceCredentialStore _store = new(
        Path.Combine(AppData.Directory, "trusted-phones.dpapi"),
        static plain => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser),
        static sealedBytes => ProtectedData.Unprotect(sealedBytes, null,
            DataProtectionScope.CurrentUser));

    public IReadOnlyList<DeviceCredential> All() => _store.All();
    public void Save(DeviceCredential credential) => _store.Save(credential);
    public void Revoke(string deviceId) => _store.Revoke(deviceId);
}

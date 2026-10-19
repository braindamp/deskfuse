using System.Security.Cryptography;
using System.Text.Json;
using FocusWindow.Core.Transport;

namespace FocusWindow.Agent;

/// <summary>
/// Approved phone credentials, encrypted using Windows DPAPI for the current user.
/// Independent per-device secrets make revocation effective. A damaged store
/// fails CLOSED: no previous device can connect until re-enrolled.
/// </summary>
internal sealed class DpapiDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly string _path = Path.Combine(AppData.Directory, "trusted-phones.dpapi");
    private readonly object _gate = new();

    private Dictionary<string, DeviceCredential> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null,
                DataProtectionScope.CurrentUser);
            try
            {
                var entries = JsonSerializer.Deserialize<List<DeviceCredential>>(plain) ?? new();
                return entries.Where(e => e.DeviceId.Length is >= 8 and <= 128 && e.Secret?.Length == 32)
                    .Take(128)
                    .ToDictionary(e => e.DeviceId, e => e, StringComparer.Ordinal);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException
            or ArgumentException)
        {
            // Never silently restore a reusable pairing credential from plaintext.
            return new(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, DeviceCredential> entries)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries.Values.ToList());
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, protectedBytes);
                File.Move(temp, _path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public IReadOnlyList<DeviceCredential> All()
    {
        lock (_gate) return Read().Values.ToArray();
    }

    public void Save(DeviceCredential credential)
    {
        if (credential.DeviceId.Length is < 8 or > 128 || credential.Secret.Length != 32)
            throw new ArgumentException("Invalid phone credential");
        lock (_gate)
        {
            var entries = Read();
            if (!entries.ContainsKey(credential.DeviceId) && entries.Count >= 128)
                throw new InvalidOperationException("Maximum enrolled device count reached");
            entries[credential.DeviceId] = credential;
            Write(entries);
        }
    }

    public void Revoke(string deviceId)
    {
        lock (_gate)
        {
            var entries = Read();
            if (entries.Remove(deviceId)) Write(entries);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace FocusWindow.Core.Transport;

/// <summary>
/// Encrypted-at-rest credential store with an injectable protector. The Windows agent
/// supplies DPAPI; tests supply a deterministic stand-in. File format, validation,
/// atomic replacement and fail-closed behavior live here so they are exercised.
/// </summary>
public sealed class FileDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly object _gate = new();

    public FileDeviceCredentialStore(string path,
        Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        _path = path;
        _protect = protect;
        _unprotect = unprotect;
    }

    private Dictionary<string, DeviceCredential> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var plain = _unprotect(File.ReadAllBytes(_path));
            try
            {
                var entries = JsonSerializer.Deserialize<List<DeviceCredential>>(plain) ?? new();
                return entries
                    .Where(e => e.DeviceId is { Length: >= 8 and <= 128 } && e.Secret?.Length == 32)
                    .Take(128)
                    .ToDictionary(e => e.DeviceId, e => e, StringComparer.Ordinal);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException
            or ArgumentException)
        {
            // Never silently restore a reusable pairing credential from damaged storage.
            return new(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, DeviceCredential> entries)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries.Values.ToList());
        try
        {
            var protectedBytes = _protect(plain);
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

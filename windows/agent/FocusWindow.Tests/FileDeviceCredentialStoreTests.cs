using System.Security.Cryptography;
using System.Text;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

/// <summary>
/// Windows counterpart of the B07 keystore test: damaged, tampered or partially
/// invalid credential files must fail CLOSED and must never resurrect a phone.
/// </summary>
public sealed class FileDeviceCredentialStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "deskfuse-store-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, "trusted-phones.dpapi");

    public FileDeviceCredentialStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Deterministic stand-in for DPAPI: a marker plus a reversible transform, so the
    // file never stores the credential verbatim and a missing marker is an auth failure.
    private static byte[] Protect(byte[] plain)
    {
        var wire = new byte[plain.Length + 4];
        wire[0] = (byte)'D'; wire[1] = (byte)'S'; wire[2] = (byte)'K'; wire[3] = (byte)'1';
        for (var i = 0; i < plain.Length; i++) wire[i + 4] = (byte)(plain[i] ^ 0x5A);
        return wire;
    }

    private static byte[] Unprotect(byte[] wire)
    {
        if (wire.Length < 4 || wire[0] != (byte)'D' || wire[1] != (byte)'S'
            || wire[2] != (byte)'K' || wire[3] != (byte)'1')
        {
            throw new CryptographicException("Not a DeskFuse test envelope");
        }
        var plain = new byte[wire.Length - 4];
        for (var i = 0; i < plain.Length; i++) plain[i] = (byte)(wire[i + 4] ^ 0x5A);
        return plain;
    }

    private FileDeviceCredentialStore NewStore() => new(StorePath, Protect, Unprotect);

    private static DeviceCredential Credential(string id, byte fill)
        => new(id, Enumerable.Repeat(fill, 32).ToArray());

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    [Fact]
    public void Save_round_trips_and_replaces_the_same_device()
    {
        NewStore().Save(Credential("phone-aaaa-0001", 1));

        var reopened = NewStore();
        reopened.Save(Credential("phone-aaaa-0001", 2));

        var only = Assert.Single(NewStore().All());
        Assert.Equal("phone-aaaa-0001", only.DeviceId);
        Assert.Equal(Enumerable.Repeat((byte)2, 32).ToArray(), only.Secret);
    }

    [Fact]
    public void Revoke_removes_only_the_named_device_and_persists()
    {
        var store = NewStore();
        store.Save(Credential("phone-aaaa-0001", 1));
        store.Save(Credential("phone-bbbb-0002", 2));

        store.Revoke("phone-aaaa-0001");

        var remaining = Assert.Single(NewStore().All());
        Assert.Equal("phone-bbbb-0002", remaining.DeviceId);
    }

    [Fact]
    public void Corrupt_store_fails_closed()
    {
        File.WriteAllBytes(StorePath, Encoding.UTF8.GetBytes("not a sealed credential store"));
        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Tampered_envelope_fails_closed()
    {
        NewStore().Save(Credential("phone-aaaa-0001", 1));
        var wire = File.ReadAllBytes(StorePath);
        wire[0] ^= 0xFF;
        File.WriteAllBytes(StorePath, wire);

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Tampered_payload_fails_closed()
    {
        NewStore().Save(Credential("phone-aaaa-0001", 1));
        var wire = File.ReadAllBytes(StorePath);
        wire[^1] ^= 0xFF;
        File.WriteAllBytes(StorePath, wire);

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Null_device_id_in_a_readable_store_fails_closed()
    {
        var json = Encoding.UTF8.GetBytes(
            $"[{{\"DeviceId\":null,\"Secret\":\"{Convert.ToBase64String(new byte[32])}\"}}]");
        File.WriteAllBytes(StorePath, Protect(json));

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void Invalid_credentials_are_rejected()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Save(Credential("short", 1)));
        Assert.Throws<ArgumentException>(() =>
            store.Save(Credential(new string('a', 129), 1)));
        Assert.Throws<ArgumentException>(() =>
            store.Save(Credential("phone-aaaa-0001", 1) with { Secret = new byte[31] }));
    }

    [Fact]
    public void Store_accepts_128_devices_and_refuses_the_129th()
    {
        var store = NewStore();
        for (var i = 0; i < 128; i++)
            store.Save(Credential($"phone-{i:D4}-0000", (byte)i));
        Assert.Equal(128, store.All().Count);

        Assert.Throws<InvalidOperationException>(() =>
            store.Save(Credential("phone-9999-0000", 9)));

        // Replacing an existing device is still allowed at the cap.
        store.Save(Credential("phone-0000-0000", 9));
        Assert.Equal(128, NewStore().All().Count);
    }

    [Fact]
    public void Recovery_after_corruption_starts_from_an_empty_store()
    {
        File.WriteAllBytes(StorePath, Encoding.UTF8.GetBytes("garbage"));
        var store = NewStore();
        Assert.Empty(store.All());

        store.Save(Credential("phone-aaaa-0001", 1));

        var only = Assert.Single(NewStore().All());
        Assert.Equal("phone-aaaa-0001", only.DeviceId);
    }

    [Fact]
    public void Sealed_file_does_not_contain_the_plaintext_secret_or_device_id()
    {
        var credential = Credential("phone-aaaa-0001", 0xA5);
        NewStore().Save(credential);

        var wire = File.ReadAllBytes(StorePath);
        Assert.False(ContainsSequence(wire, credential.Secret));
        Assert.False(ContainsSequence(wire, Encoding.UTF8.GetBytes(credential.DeviceId)));
    }
}

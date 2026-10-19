using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace FocusWindow.Core.Transport;
/// <summary>The persistent half of a pairing: a long-lived host key + salt.</summary>
public sealed record PairingState(byte[] Pkcs8PrivateKey, byte[] Secret, DateTime CreatedUtc);
public interface IPairingStore
{
    PairingState? Load();
    void Save(PairingState state);
}
public sealed class MemoryPairingStore : IPairingStore
{
    private PairingState? _state;
    public PairingState? Load() => _state;
    public void Save(PairingState state) => _state = state;
}
/// <summary>
/// One QR offer: the host public key + salt rendered as the URI the Android client's
/// PairingPayload.parse expects:
///   focuswindow://pair?h=&lt;ip&gt;&amp;p=&lt;port&gt;&amp;k=&lt;b64url SPKI&gt;&amp;s=&lt;b64url salt&gt;&amp;n=&lt;host&gt;
/// </summary>
public sealed class PairingOffer : IDisposable
{
    public required ECDiffieHellman KeyPair { get; init; }
    public required byte[] Secret { get; init; }
    public required byte[] PublicSpki { get; init; }
    public required string Uri { get; init; }
    public required string HostAddress { get; init; }
    public required int Port { get; init; }
    public void Dispose() => KeyPair.Dispose();
}
public static class Pairing
{
    public static PairingState NewState()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingState(key.ExportPkcs8PrivateKey(), RandomNumberGenerator.GetBytes(32), DateTime.UtcNow);
    }
    public static PairingState LoadOrCreate(IPairingStore store, bool forceNew)
    {
        if (!forceNew && store.Load() is { } existing) return existing;
        var created = NewState();
        store.Save(created);
        return created;
    }
    public static PairingOffer BuildOffer(PairingState state, string address, int port, string hostName)
    {
        var keyPair = ECDiffieHellman.Create();
        keyPair.ImportPkcs8PrivateKey(state.Pkcs8PrivateKey, out _);
        var spki = keyPair.ExportSubjectPublicKeyInfo();
        var uri =
            $"focuswindow://pair?h={System.Uri.EscapeDataString(address)}" +
            $"&p={port}" +
            $"&k={System.Uri.EscapeDataString(Base64Url(spki))}" +
            $"&s={System.Uri.EscapeDataString(Base64Url(state.Secret))}" +
            $"&n={System.Uri.EscapeDataString(hostName)}";
        return new PairingOffer
        {
            KeyPair = keyPair,
            Secret = state.Secret,
            PublicSpki = spki,
            Uri = uri,
            HostAddress = address,
            Port = port,
        };
    }
    /// <summary>
    /// The client decodes with Base64.URL_SAFE: the standard alphabet ('+', '/') would be
    /// rejected, so base64url it is. Padding is kept (Android's decoder is not guaranteed to
    /// tolerate its absence).
    /// </summary>
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    /// <summary>
    /// FR-TRUST-03: prefer a private LAN address on a physical adapter. Virtual adapters
    /// (VPN, Hyper-V, WSL, VMware) are deprioritised because the phone usually cannot route
    /// to them. Returns null if nothing sensible exists.
    /// </summary>
    public static string? PickLanAddress()
    {
        var candidates = new List<(int score, string address)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var score = 0;
            var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (name.Contains("virtual") || name.Contains("hyper-v") || name.Contains("wsl") ||
                name.Contains("vmware") || name.Contains("virtualbox") || name.Contains("vpn") ||
                name.Contains("tap") || name.Contains("tun") || name.Contains("docker") || name.Contains("vethernet"))
            {
                score -= 50;
            }
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet) score += 10;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var s = score;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 192 && b[1] == 168) s += 30;
                else if (b[0] == 10) s += 25;
                else if (b[0] == 172 && b[1] is >= 16 and <= 31) s += 20;
                else if (b[0] == 169 && b[1] == 254) s -= 40;
                else s -= 20;
                candidates.Add((s, ua.Address.ToString()));
            }
        }
        return candidates.OrderByDescending(c => c.score).Select(c => c.address).FirstOrDefault();
    }
}

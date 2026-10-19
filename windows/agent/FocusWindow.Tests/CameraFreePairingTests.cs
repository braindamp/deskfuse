using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

public sealed class CameraFreePairingTests
{
    [Fact]
    public void Enrollment_key_schedule_matches_android_reference_vector()
    {
        var shared = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var clientNonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var serverNonce = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        var clientPublic = Enumerable.Range(0, 65).Select(i => (byte)i).ToArray();
        var hostPublic = Enumerable.Range(65, 65).Select(i => (byte)i).ToArray();
        var transcript = EnrollmentCrypto.Transcript(clientNonce, serverNonce,
            clientPublic, hostPublic);
        Assert.Equal("0804AD7746FFA2C7A38ED838A2FC04F716104858917DDB26673C19C723EA30CD",
            Convert.ToHexString(transcript));
        var (code, key) = EnrollmentCrypto.Derive(shared, transcript);
        Assert.Equal("306824", code);
        Assert.Equal("B5CD3C517FEABBFEFE513A0048BBF1610D44B853CDECD8E2172158815557E9A5",
            Convert.ToHexString(key));
    }

    [Fact]
    public void Credentials_are_independent_and_revocable()
    {
        var store = new MemoryDeviceCredentialStore();
        var one = new DeviceCredential("phone-0001", RandomNumberGenerator.GetBytes(32));
        var two = new DeviceCredential("phone-0002", RandomNumberGenerator.GetBytes(32));
        store.Save(one);
        store.Save(two);
        Assert.Equal(2, store.All().Count);
        store.Revoke(one.DeviceId);
        Assert.Single(store.All());
        Assert.Equal(two.DeviceId, store.All()[0].DeviceId);
    }

    [Fact]
    public async Task First_encrypted_packet_selects_the_matching_device_identity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var tcp = new TcpClient();
        var dialing = tcp.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();
        await dialing;
        using var hostKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var credentials = new[]
        {
            new DeviceCredential("phone-0001", RandomNumberGenerator.GetBytes(32)),
            new DeviceCredential("phone-0002", RandomNumberGenerator.GetBytes(32))
        };
        var hostTask = SecureChannel.AcceptAnyAsync(accepted.GetStream(), hostKey,
            credentials, CancellationToken.None);
        var clientTask = SecureChannel.ConnectAsync(tcp.GetStream(),
            hostKey.ExportSubjectPublicKeyInfo(), credentials[1].Secret, CancellationToken.None);
        using var host = await hostTask;
        using var phone = await clientTask;
        Assert.Null(host.CredentialDeviceId); // nothing trusted until first authenticated packet
        var sealedPacket = phone.Seal(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(host.Open(sealedPacket)));
        Assert.Equal("phone-0002", host.CredentialDeviceId);
    }
}

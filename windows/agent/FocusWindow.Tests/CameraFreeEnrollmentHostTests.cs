using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusWindow.Core;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

/// <summary>
/// Executed coverage for the camera-free enrollment channel itself:
/// a denial must never mint or transmit a credential, an approval must deliver
/// only the sealed envelope, and repeated attempts from one address are limited.
/// </summary>
public sealed class CameraFreeEnrollmentHostTests
{
    private sealed class ScriptedApproval : IApprovalPolicy, IEnrollmentApprovalPolicy
    {
        private readonly bool _allow;
        public ScriptedApproval(bool allow) => _allow = allow;

        public string? DeviceId { get; private set; }
        public string? DeviceLabel { get; private set; }
        public string? Code { get; private set; }

        public Task<bool> ApproveNewPairingAsync(string deviceId, string deviceLabel,
            string verificationCode, CancellationToken ct)
        {
            DeviceId = deviceId;
            DeviceLabel = deviceLabel;
            Code = verificationCode;
            return Task.FromResult(_allow);
        }

        public Task<bool> ApproveAsync(string deviceId, string deviceLabel,
            string verificationCode, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class CapturingLog : ILog
    {
        public List<string> Lines { get; } = new();
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add("WARN " + message);
        public void Error(string message) => Lines.Add("ERR " + message);
    }

    private sealed record EnrollmentSession(TcpClient Client, string Code, byte[] Key, byte[] Transcript)
        : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private static (CameraFreePairingHost Host, PairingOffer Offer) StartHost(
        ScriptedApproval approval, IDeviceCredentialStore store, ILog? log = null)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            // Releasing an ephemeral-port probe can repeatedly return the same
            // base port whose +1/+2 is reserved on hosted Windows runners.
            // Choose independent candidates and let the REAL sockets bind them.
            var port = RandomNumberGenerator.GetInt32(12000, 45000);
            PairingOffer? offer = null;
            try
            {
                offer = Pairing.BuildOffer(Pairing.NewState(), "127.0.0.1", port, "deskfuse-test");
                var host = new CameraFreePairingHost(offer, "deskfuse-test", store, approval,
                    log ?? NullLog.Instance);
                return (host, offer);
            }
            catch (SocketException)
            {
                offer?.Dispose();
                // Adjacent ports were taken; try another trio.
            }
        }
        throw new InvalidOperationException("no free port trio for the enrollment host");
    }

    private static async Task<EnrollmentSession> EnrollAsync(int enrollmentPort, string deviceId,
        string label, CancellationToken ct)
    {
        using var keys = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPublic = keys.ExportSubjectPublicKeyInfo();
        var clientNonce = RandomNumberGenerator.GetBytes(16);

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, enrollmentPort, ct);
        var stream = client.GetStream();
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            deviceId,
            deviceLabel = label,
            publicKey = Convert.ToBase64String(clientPublic),
            clientNonce = Convert.ToBase64String(clientNonce),
        });
        await WriteFramedAsync(stream, request, ct);

        using var challenge = JsonDocument.Parse(await ReadFramedAsync(stream, ct));
        var root = challenge.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        var hostPublic = Convert.FromBase64String(root.GetProperty("publicKey").GetString()!);
        var serverNonce = Convert.FromBase64String(root.GetProperty("serverNonce").GetString()!);

        using var hostKeys = ECDiffieHellman.Create();
        hostKeys.ImportSubjectPublicKeyInfo(hostPublic, out _);
        var shared = keys.DeriveRawSecretAgreement(hostKeys.PublicKey);
        var transcript = EnrollmentCrypto.Transcript(clientNonce, serverNonce, clientPublic, hostPublic);
        var (code, key) = EnrollmentCrypto.Derive(shared, transcript);
        CryptographicOperations.ZeroMemory(shared);
        return new EnrollmentSession(client, code, key, transcript);
    }

    private static async Task<JsonElement> ReadDecisionAsync(EnrollmentSession session,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(
            await ReadFramedAsync(session.Client.GetStream(), ct));
        return document.RootElement.Clone();
    }

    private static async Task WriteFramedAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFramedAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    [Fact]
    public async Task Denied_enrollment_sends_no_credential_and_stores_no_device()
    {
        var approval = new ScriptedApproval(allow: false);
        var store = new MemoryDeviceCredentialStore();
        var log = new CapturingLog();
        var (host, offer) = StartHost(approval, store, log);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var session = await EnrollAsync(host.EnrollmentPort, "phone-test-0001",
            "Denied phone", cts.Token);
        Assert.Equal(session.Code, approval.Code);
        Assert.Equal("phone-test-0001", approval.DeviceId);
        Assert.Equal("Denied phone", approval.DeviceLabel);

        var decision = await ReadDecisionAsync(session, cts.Token);
        Assert.Equal("denied", decision.GetProperty("status").GetString());
        Assert.False(decision.TryGetProperty("ciphertext", out _));
        Assert.False(decision.TryGetProperty("nonce", out _));
        Assert.Empty(store.All());
        Assert.Contains(log.Lines, line => line.Contains("Denied a new phone: Denied phone"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("Approved a new phone"));
    }

    [Fact]
    public async Task Denial_log_neutralises_control_characters_in_the_phone_label()
    {
        var approval = new ScriptedApproval(allow: false);
        var log = new CapturingLog();
        var (host, offer) = StartHost(approval, new MemoryDeviceCredentialStore(), log);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var session = await EnrollAsync(host.EnrollmentPort, "phone-test-0004",
            "Evil\nINFO forged", cts.Token);
        var decision = await ReadDecisionAsync(session, cts.Token);
        Assert.Equal("denied", decision.GetProperty("status").GetString());
        Assert.Contains(log.Lines, line => line.Contains("Denied a new phone: Evil?INFO forged"));
        Assert.All(log.Lines, line =>
        {
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
        });
    }

    [Fact]
    public async Task Approved_enrollment_delivers_only_the_encrypted_credential()
    {
        var approval = new ScriptedApproval(allow: true);
        var store = new MemoryDeviceCredentialStore();
        var (host, offer) = StartHost(approval, store);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var session = await EnrollAsync(host.EnrollmentPort, "phone-test-0002",
            "Approved phone", cts.Token);
        Assert.Equal(session.Code, approval.Code);

        var decision = await ReadDecisionAsync(session, cts.Token);
        Assert.Equal("approved", decision.GetProperty("status").GetString());
        var nonce = Convert.FromBase64String(decision.GetProperty("nonce").GetString()!);
        var sealedBytes = Convert.FromBase64String(decision.GetProperty("ciphertext").GetString()!);
        Assert.Equal(12, nonce.Length);
        Assert.True(sealedBytes.Length > 16);

        var plain = new byte[sealedBytes.Length - 16];
        using (var gcm = new AesGcm(session.Key, 16))
        {
            gcm.Decrypt(nonce, sealedBytes.AsSpan(0, sealedBytes.Length - 16),
                sealedBytes.AsSpan(sealedBytes.Length - 16), plain, session.Transcript);
        }
        using var approved = JsonDocument.Parse(plain);
        var secret = Convert.FromBase64String(
            approved.RootElement.GetProperty("secret").GetString()!);
        Assert.Equal(32, secret.Length);
        Assert.Equal(offer.Port, approved.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("deskfuse-test",
            approved.RootElement.GetProperty("hostName").GetString());

        var stored = Assert.Single(store.All());
        Assert.Equal("phone-test-0002", stored.DeviceId);
        Assert.Equal(secret, stored.Secret);
    }

    [Fact]
    public async Task Denied_re_enrollment_leaves_an_existing_credential_untouched()
    {
        var existing = new DeviceCredential("phone-test-0005",
            RandomNumberGenerator.GetBytes(32));
        var store = new MemoryDeviceCredentialStore();
        store.Save(existing);
        var approval = new ScriptedApproval(allow: false);
        var (host, offer) = StartHost(approval, store);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var session = await EnrollAsync(host.EnrollmentPort, "phone-test-0005",
            "Spoofed label", cts.Token);
        var decision = await ReadDecisionAsync(session, cts.Token);
        Assert.Equal("denied", decision.GetProperty("status").GetString());

        var stored = Assert.Single(store.All());
        Assert.Equal(existing.DeviceId, stored.DeviceId);
        Assert.Equal(existing.Secret, stored.Secret);
    }

    [Fact]
    public async Task Malformed_request_never_asks_the_owner_or_touches_the_store()
    {
        var approval = new ScriptedApproval(allow: true);
        var store = new MemoryDeviceCredentialStore();
        var (host, offer) = StartHost(approval, store);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.EnrollmentPort, cts.Token);
        var stream = client.GetStream();
        await WriteFramedAsync(stream,
            JsonSerializer.SerializeToUtf8Bytes(new { v = 2 }), cts.Token);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await ReadFramedAsync(stream, cts.Token));

        Assert.Null(approval.Code);
        Assert.Empty(store.All());
    }

    [Fact]
    public async Task Second_attempt_from_the_same_address_is_dropped_by_the_rate_limit()
    {
        var approval = new ScriptedApproval(allow: false);
        var store = new MemoryDeviceCredentialStore();
        var (host, offer) = StartHost(approval, store);
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using (var first = await EnrollAsync(host.EnrollmentPort, "phone-test-0003",
            "First attempt", cts.Token))
        {
            var decision = await ReadDecisionAsync(first, cts.Token);
            Assert.Equal("denied", decision.GetProperty("status").GetString());
        }

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, host.EnrollmentPort, cts.Token);
        var stream = second.GetStream();
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await WriteFramedAsync(stream,
                JsonSerializer.SerializeToUtf8Bytes(new { v = 1 }), cts.Token);
            await ReadFramedAsync(stream, cts.Token);
        });
        Assert.Empty(store.All());
    }

    [Fact]
    public async Task Discovery_reply_carries_the_host_name_and_both_ports()
    {
        var (host, offer) = StartHost(new ScriptedApproval(allow: false),
            new MemoryDeviceCredentialStore());
        using var offerScope = offer;
        using var hostScope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync(Encoding.ASCII.GetBytes(CameraFreePairingHost.Probe),
            new IPEndPoint(IPAddress.Loopback, host.DiscoveryPort), cts.Token);
        var reply = await udp.ReceiveAsync(cts.Token);
        var parts = Encoding.UTF8.GetString(reply.Buffer).Split('|');

        Assert.Equal(CameraFreePairingHost.ReplyPrefix, parts[0]);
        Assert.Equal("deskfuse-test", Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
        Assert.Equal(offer.Port, int.Parse(parts[2]));
        Assert.Equal(host.EnrollmentPort, int.Parse(parts[3]));
    }
}

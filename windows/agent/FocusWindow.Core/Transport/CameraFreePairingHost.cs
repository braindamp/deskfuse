using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusWindow.Core.Session;

namespace FocusWindow.Core.Transport;

/// <summary>
/// Camera-free, owner-approved enrollment. The existing protocol-v3 media/control
/// connection is unchanged: enrollment issues an independent 32-byte credential per phone.
/// Discovery intentionally advertises NO secret and provides NO authentication.
/// </summary>
public sealed record DeviceCredential(string DeviceId, byte[] Secret);
public interface IDeviceCredentialStore
{
    IReadOnlyList<DeviceCredential> All();
    void Save(DeviceCredential credential);
    void Revoke(string deviceId);
}
public sealed class MemoryDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly Dictionary<string, DeviceCredential> _devices = new(StringComparer.Ordinal);
    public IReadOnlyList<DeviceCredential> All() { lock (_devices) return _devices.Values.ToList(); }
    public void Save(DeviceCredential credential) { lock (_devices) _devices[credential.DeviceId] = credential; }
    public void Revoke(string deviceId) { lock (_devices) _devices.Remove(deviceId); }
}

/// <summary>
/// Runs on sessionPort+1 (UDP discovery) and sessionPort+2 (TCP enrollment).
/// Both ports must be free. Requests are bounded; an approved credential is
/// encrypted to the phone using an ephemeral ECDH-derived AES-GCM key.
/// First enrollment MUST require a physical Windows approval comparing the same
/// 6-digit code visible on the phone. A matching code is the MITM defense.
/// </summary>
public sealed class CameraFreePairingHost : IDisposable
{
    public const string Probe = "DESKFUSE_DISCOVER_V1";
    public const string ReplyPrefix = "DESKFUSE_HOST_V1";
    private const int MaxMessageBytes = 4096;
    private readonly PairingOffer _offer;
    private readonly IDeviceCredentialStore _credentials;
    private readonly IApprovalPolicy _approval;
    private readonly ILog _log;
    private readonly TcpListener _tcp;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    // Do not stack multiple approval dialogs or let unauthenticated LAN peers flood the desktop.
    private readonly SemaphoreSlim _pending = new(1, 1);
    private readonly Dictionary<IPAddress, DateTime> _lastAttempt = new();
    private readonly object _attemptGate = new();
    private readonly Task _discoveryTask;
    private readonly Task _enrollmentTask;
    private readonly string _hostName;

    public int DiscoveryPort { get; }
    public int EnrollmentPort { get; }

    public CameraFreePairingHost(PairingOffer offer, string hostName,
        IDeviceCredentialStore credentials, IApprovalPolicy approval, ILog log)
    {
        if (offer.Port > 65533) throw new ArgumentOutOfRangeException(nameof(offer), "Need two adjacent ports");
        _offer = offer;
        _credentials = credentials;
        _approval = approval;
        _log = log;
        _hostName = hostName;
        DiscoveryPort = offer.Port + 1;
        EnrollmentPort = offer.Port + 2;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        try
        {
            _tcp = new TcpListener(IPAddress.Any, EnrollmentPort);
            _tcp.Start(4);
        }
        catch
        {
            _udp.Dispose();
            throw;
        }
        _discoveryTask = Task.Run(DiscoverAsync);
        _enrollmentTask = Task.Run(AcceptAsync);
    }

    private async Task DiscoverAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var request = await _udp.ReceiveAsync(token).ConfigureAwait(false);
                if (request.Buffer.Length != Probe.Length ||
                    Encoding.ASCII.GetString(request.Buffer) != Probe) continue;
                // Hostname is base64 encoded to keep the UDP format delimiter-safe.
                var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(_hostName));
                var reply = Encoding.UTF8.GetBytes($"{ReplyPrefix}|{name}|{_offer.Port}|{EnrollmentPort}");
                await _udp.SendAsync(reply, request.RemoteEndPoint, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException ex) { _log.Warn("LAN discovery: " + ex.Message); }
        }
    }

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pending.WaitAsync(token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch { _pending.Release(); throw; }
                var remoteAddress = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address;
                var permitted = remoteAddress is not null;
                if (remoteAddress is not null)
                {
                    lock (_attemptGate)
                    {
                        // One enrollment attempt per source each 15 seconds.
                        if (_lastAttempt.TryGetValue(remoteAddress, out var last) &&
                            DateTime.UtcNow - last < TimeSpan.FromSeconds(15))
                            permitted = false;
                        if (permitted) _lastAttempt[remoteAddress] = DateTime.UtcNow;
                        if (_lastAttempt.Count > 512) _lastAttempt.Clear();
                    }
                }
                if (!permitted)
                {
                    client.Dispose();
                    _pending.Release();
                    continue;
                }
                // The approval dialog is deliberately not on the listener thread.
                _ = Task.Run(async () =>
                {
                    try { await EnrollAsync(client, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or SocketException or CryptographicException
                        or InvalidDataException or JsonException or OperationCanceledException)
                    {
                        _log.Warn("LAN enrollment refused: " + ex.GetType().Name);
                    }
                    finally { client.Dispose(); _pending.Release(); }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task EnrollAsync(TcpClient client, CancellationToken shutdown)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var stream = client.GetStream();
        using var request = JsonDocument.Parse(await ReadAsync(stream, ct).ConfigureAwait(false));
        var root = request.RootElement;
        if (root.GetProperty("v").GetInt32() != 1) throw new InvalidDataException("bad enrollment version");
        var id = root.GetProperty("deviceId").GetString() ?? "";
        var label = root.GetProperty("deviceLabel").GetString() ?? "";
        if (id.Length is < 8 or > 128 || label.Length is < 1 or > 100)
            throw new InvalidDataException("invalid phone identity");
        var clientPub = Convert.FromBase64String(root.GetProperty("publicKey").GetString() ?? "");
        var clientNonce = Convert.FromBase64String(root.GetProperty("clientNonce").GetString() ?? "");
        if (clientNonce.Length != 16 || clientPub.Length > 512) throw new InvalidDataException("bad enrollment key");
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(clientPub, out _);
        var serverNonce = RandomNumberGenerator.GetBytes(16);
        var hostPublic = _offer.PublicSpki;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, hostName = _hostName, publicKey = Convert.ToBase64String(hostPublic),
            serverNonce = Convert.ToBase64String(serverNonce)
        });
        await WriteAsync(stream, response, ct).ConfigureAwait(false);

        byte[] shared;
        lock (_offer.KeyPair) shared = _offer.KeyPair.DeriveRawSecretAgreement(peer.PublicKey);
        var transcript = EnrollmentCrypto.Transcript(clientNonce, serverNonce, clientPub, hostPublic);
        var (code, key) = EnrollmentCrypto.Derive(shared, transcript);
        CryptographicOperations.ZeroMemory(shared);
        try
        {
            // Force a NEW approval even if the presented label/id matches an older device.
            // Otherwise a spoofed id would silently mint a fresh credential.
            var approved = await _approval.ApproveNewPairingAsync(id, label, code, ct)
                .ConfigureAwait(false);
            if (!approved)
            {
                await WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { status = "denied" }), ct)
                    .ConfigureAwait(false);
                return;
            }
            var secret = RandomNumberGenerator.GetBytes(32);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new
            {
                secret = Convert.ToBase64String(secret),
                port = _offer.Port,
                hostName = _hostName
            });
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length + 16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), transcript);
            _credentials.Save(new DeviceCredential(id, secret));
            await WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
            {
                status = "approved",
                nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(cipher),
            }), ct).ConfigureAwait(false);
            _log.Info($"Approved a new phone: {label} (credential stored securely by the host)");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is < 2 or > MaxMessageBytes) throw new InvalidDataException("enrollment message too large");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }
    private static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is < 2 or > MaxMessageBytes) throw new InvalidDataException("enrollment response too large");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        _stop.Cancel();
        _tcp.Stop();
        _udp.Dispose();
        _stop.Dispose();
        // Outstanding enrollment tasks release their slots after shutdown.
    }
}

public static class EnrollmentCrypto
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("DeskFuse-enroll-v1");

    public static byte[] Transcript(byte[] clientNonce, byte[] serverNonce, byte[] clientPub, byte[] hostPub)
    {
        if (clientNonce.Length != 16 || serverNonce.Length != 16 ||
            clientPub.Length > 512 || hostPub.Length > 512)
            throw new ArgumentException("invalid enrollment transcript");
        return SHA256.HashData(Domain.Concat(clientNonce).Concat(serverNonce)
            .Concat(clientPub).Concat(hostPub).ToArray());
    }

    public static (string Code, byte[] Key) Derive(byte[] sharedSecret, byte[] transcript)
    {
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, transcript);
        try
        {
            var sas = HKDF.Expand(HashAlgorithmName.SHA256, prk, 4,
                Encoding.ASCII.GetBytes("deskfuse:enroll:sas:v1"));
            var number = BinaryPrimitives.ReadInt32BigEndian(sas) & 0x7FFFFFFF;
            var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32,
                Encoding.ASCII.GetBytes("deskfuse:enroll:credential:v1"));
            return ((number % 1_000_000).ToString("D6"), key);
        }
        finally { CryptographicOperations.ZeroMemory(prk); }
    }
}

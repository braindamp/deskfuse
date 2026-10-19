using System.Buffers.Binary;
using System.Security.Cryptography;
namespace FocusWindow.Core.Transport;
/// <summary>
/// Host half of the FR-TRUST-02 handshake — a byte-for-byte mirror of the Android client's
/// SecureChannel.kt:
///
///   client -> [int32 BE len][client SPKI public key]
///   host   -> [int32 BE len][host SPKI public key]   (MUST equal the key bytes in the QR)
///   shared  = ECDH P-256, RAW secret (x coordinate)
///   prk     = HKDF-Extract(salt = pairing secret from the QR, ikm = shared)
///   c2s/s2c = HKDF-Expand(prk, "focuswindow:c2s:v3" / "...:s2c:v3", 32)
///   code    = (BE-int32(HKDF-Expand(prk, "focuswindow:confirm:v3", 4)) AND 0x7FFFFFFF) % 1e6
///   packet  = [8-byte BE counter] || AES-256-GCM(ciphertext || 16-byte tag)
///   nonce   = 4 zero bytes || 8-byte BE counter
///
/// Two interoperability traps, both fixed here:
///
///  1. .NET's <c>ECDiffieHellman.DeriveKeyMaterial</c> does NOT return the raw shared
///     secret — by default it returns SHA-256(secret). Java's <c>KeyAgreement.generateSecret()</c>
///     returns the raw secret. The first version of this file used DeriveKeyMaterial, so the
///     two sides would have derived different keys every single time.
///     <c>DeriveRawSecretAgreement</c> (.NET 7+) is the matching primitive.
///
///  2. Some Java providers have returned the shared secret with leading zero bytes stripped,
///     while .NET returns the full 32 bytes. In ~1 of 256 handshakes the secrets differ in
///     length. We therefore derive BOTH candidates and lock in whichever authenticates the
///     first inbound packet (the client's Hello). No retry, no user-visible failure.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    // Envelope: 8-byte monotonic record counter + 16-byte AES-GCM tag.
    public const int RecordOverheadBytes = 8 + 16;
    private sealed record KeySet(byte[] SendKey, byte[] RecvKey, string Code, string? DeviceId = null);
    private readonly List<KeySet> _candidates;
    private KeySet? _locked;
    private long _sendCounter;
    private long _recvCounter = -1;
    private readonly object _gate = new();
    private SecureChannel(List<KeySet> candidates) => _candidates = candidates;
    /// <summary>The 6-digit short authentication string the user compares on both screens.</summary>
    public string VerificationCode => (_locked ?? _candidates[0]).Code;
    /// <summary>Only populated after a valid first AES-GCM packet unlocks this credential.</summary>
    public string? CredentialDeviceId => _locked?.DeviceId;
    public static async Task<SecureChannel> AcceptAsync(
        Stream stream,
        ECDiffieHellman hostKeyPair,
        byte[] pairingSecret,
        CancellationToken ct)
    {
        return await AcceptAnyAsync(stream, hostKeyPair,
            new[] { new DeviceCredential(string.Empty, pairingSecret) }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Authenticate a phone against per-device secrets, without exposing those secrets during
    /// discovery or enrollment. Only the candidate authenticating the first GCM packet wins.
    /// </summary>
    public static async Task<SecureChannel> AcceptAnyAsync(
        Stream stream, ECDiffieHellman hostKeyPair,
        IReadOnlyList<DeviceCredential> credentials, CancellationToken ct)
    {
        if (credentials.Count == 0) throw new CryptographicException("No enrolled phones");
        if (credentials.Count > 128) throw new CryptographicException("Too many pairing candidates");
        var clientKeyBytes = await ReadLengthPrefixedAsync(stream, 4096, ct).ConfigureAwait(false);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(clientKeyBytes, out _);
        var ourPublic = hostKeyPair.ExportSubjectPublicKeyInfo();
        await WriteLengthPrefixedAsync(stream, ourPublic, ct).ConfigureAwait(false);
        var shared = hostKeyPair.DeriveRawSecretAgreement(peer.PublicKey);
        var candidates = new List<KeySet>();
        var trimmed = TrimLeadingZeros(shared);
        foreach (var credential in credentials)
        {
            if (credential.Secret.Length != 32) continue;
            var deviceId = credential.DeviceId.Length == 0 ? null : credential.DeviceId;
            candidates.Add(Derive(shared, credential.Secret, deviceId));
            if (trimmed.Length != shared.Length)
                candidates.Add(Derive(trimmed, credential.Secret, deviceId));
        }
        if (candidates.Count == 0) throw new CryptographicException("No valid enrolled credentials");
        CryptographicOperations.ZeroMemory(shared);
        return new SecureChannel(candidates);
    }
    /// <summary>
    /// Client side of the same handshake. The product only ever needs the host half; this
    /// exists so unit tests can run both ends in-process. The independent cross-check is the
    /// Node client in tests/interop, which follows SecureChannel.kt rather than this file.
    /// </summary>
    public static async Task<SecureChannel> ConnectAsync(
        Stream stream,
        byte[] hostPublicSpki,
        byte[] pairingSecret,
        CancellationToken ct)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        await WriteLengthPrefixedAsync(stream, ephemeral.ExportSubjectPublicKeyInfo(), ct).ConfigureAwait(false);
        var wireKey = await ReadLengthPrefixedAsync(stream, 4096, ct).ConfigureAwait(false);
        if (!wireKey.AsSpan().SequenceEqual(hostPublicSpki))
            throw new CryptographicException("host key mismatch - possible interception");
        using var host = ECDiffieHellman.Create();
        host.ImportSubjectPublicKeyInfo(hostPublicSpki, out _);
        var shared = ephemeral.DeriveRawSecretAgreement(host.PublicKey);
        var keys = Derive(shared, pairingSecret);
        // The client sends on c2s and receives on s2c: the mirror image of the host.
        var mirrored = new KeySet(SendKey: keys.RecvKey, RecvKey: keys.SendKey, Code: keys.Code);
        CryptographicOperations.ZeroMemory(shared);
        return new SecureChannel(new List<KeySet> { mirrored });
    }
    private static KeySet Derive(ReadOnlySpan<byte> shared, byte[] pairingSecret, string? deviceId = null)
    {
        static byte[] Info(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared.ToArray(), pairingSecret);
        var c2s = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Info("focuswindow:c2s:v3"));
        var s2c = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Info("focuswindow:s2c:v3"));
        var confirm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 4, Info("focuswindow:confirm:v3"));
        var value = BinaryPrimitives.ReadInt32BigEndian(confirm) & 0x7FFFFFFF;
        return new KeySet(SendKey: s2c, RecvKey: c2s, Code: (value % 1_000_000).ToString("D6"), DeviceId: deviceId);
    }
    private static byte[] TrimLeadingZeros(byte[] value)
    {
        var i = 0;
        while (i < value.Length - 1 && value[i] == 0) i++;
        return i == 0 ? value : value[i..];
    }
    private static void FillNonce(long counter, Span<byte> destination)
    {
        destination.Clear();
        BinaryPrimitives.WriteInt64BigEndian(destination[4..], counter);
    }
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            // Before lock-in we send with candidate 0. That is safe because the host only
            // sends AFTER it has opened the client's Hello, which is what locks us in.
            var keys = _locked ?? _candidates[0];
            var counter = _sendCounter++;
            Span<byte> nonce = stackalloc byte[12];
            FillNonce(counter, nonce);
            var output = new byte[8 + plaintext.Length + 16];
            BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(0, 8), counter);
            using var gcm = new AesGcm(keys.SendKey, 16);
            gcm.Encrypt(nonce, plaintext, output.AsSpan(8, plaintext.Length), output.AsSpan(8 + plaintext.Length, 16));
            return output;
        }
    }
    public byte[] Open(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= 8 + 16)
            throw new CryptographicException("ciphertext too short");
        var counter = BinaryPrimitives.ReadInt64BigEndian(packet[..8]);
        lock (_gate)
        {
            if (_locked is not null)
            {
                return OpenWith(_locked, packet, counter);
            }
            CryptographicException? last = null;
            foreach (var candidate in _candidates)
            {
                try
                {
                    var plain = OpenWith(candidate, packet, counter);
                    _locked = candidate;
                    return plain;
                }
                catch (CryptographicException ex)
                {
                    last = ex;
                }
            }
            throw last ?? new CryptographicException("no key candidate authenticated the packet");
        }
    }
    private byte[] OpenWith(KeySet keys, ReadOnlySpan<byte> packet, long counter)
    {
        if (counter <= _recvCounter)
            throw new CryptographicException($"replayed or reordered packet: {counter} <= {_recvCounter}");
        Span<byte> nonce = stackalloc byte[12];
        FillNonce(counter, nonce);
        var cipherAndTag = packet[8..];
        var plain = new byte[cipherAndTag.Length - 16];
        using var gcm = new AesGcm(keys.RecvKey, 16);
        gcm.Decrypt(nonce, cipherAndTag[..^16], cipherAndTag[^16..], plain);
        _recvCounter = counter; // advanced only AFTER authentication succeeds
        return plain;
    }
    private static async Task<byte[]> ReadLengthPrefixedAsync(Stream stream, int max, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > max)
            throw new InvalidDataException($"bad length prefix: {len}");
        var payload = new byte[len];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
    private static async Task WriteLengthPrefixedAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf, 4);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        foreach (var k in _candidates)
        {
            CryptographicOperations.ZeroMemory(k.SendKey);
            CryptographicOperations.ZeroMemory(k.RecvKey);
        }
    }
}

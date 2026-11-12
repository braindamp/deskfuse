using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskFuse.AudioBroker;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcHash = Org.BouncyCastle.Tls.HashAlgorithm;

namespace DeskFuse.EnrollmentWorker;

/// <summary>Pairing-only identity. No PFX, persisted key, certificate-store entry or authority signing key.</summary>
internal sealed class EnrollmentTlsIdentity
{
    private readonly AsymmetricKeyParameter privateKey;
    internal byte[] CertificateDer { get; }

    private EnrollmentTlsIdentity(byte[] certificateDer, AsymmetricKeyParameter privateKey)
    { CertificateDer = certificateDer; this.privateKey = privateKey; }

    internal static EnrollmentTlsIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=DeskFuse Enrollment", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
        var encoded = key.ExportPkcs8PrivateKey();
        try { return new(certificate.RawData, PrivateKeyFactory.CreateKey(encoded)); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    internal DefaultTlsServer Server() => new PairingServer(this);

    private sealed class PairingServer(EnrollmentTlsIdentity identity) : DefaultTlsServer(new BcTlsCrypto())
    {
        // Same Windows 10 reference profile: TLS 1.2, forward secrecy and AEAD only.
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv12];
        protected override int[] GetSupportedCipherSuites() =>
        [
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        ];
        public override int GetHandshakeTimeoutMillis() => 5000;
        public override int GetMaxHandshakeMessageSize() => 16384;
        public override TlsSession GetSessionToResume(byte[] sessionID) => null!;
        public override bool AllowLegacyResumption() => false;
        protected override TlsCredentialedSigner GetECDsaSignerCredentials()
        {
            var chain = new Org.BouncyCastle.Tls.Certificate([Crypto.CreateCertificate(identity.CertificateDer)]);
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto,
                identity.privateKey, chain, new SignatureAndHashAlgorithm(BcHash.sha256, SignatureAlgorithm.ecdsa));
        }
    }
}

/// <summary>
/// One contained setup socket. TLS has its own read/write serialization; no SYSTEM TLS parser.
/// Cancellation closes this terminal connection, interrupting blocking TLS I/O in either direction.
/// </summary>
internal sealed class EnrollmentTlsChannel(Stream network, EnrollmentTlsIdentity identity) : IEnrollmentChannel
{
    private readonly TlsServerProtocol protocol = new(network);

    internal async Task AuthenticateAsync(CancellationToken ct)
    {
        using var registration = ct.Register(network.Dispose);
        await Task.Run(() => protocol.Accept(identity.Server()), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;
        var bytes = new byte[Math.Min(buffer.Length, EnrollmentProtocol.MaxFrame)];
        using var registration = ct.Register(network.Dispose);
        var count = await Task.Run(() => protocol.ReadApplicationData(bytes, 0, bytes.Length), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        bytes.AsMemory(0, count).CopyTo(buffer);
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length > EnrollmentProtocol.MaxFrame + 4) throw new InvalidDataException("Oversized enrollment write.");
        var bytes = buffer.ToArray();
        using var registration = ct.Register(network.Dispose);
        await Task.Run(() => protocol.WriteApplicationData(bytes, 0, bytes.Length), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }
}

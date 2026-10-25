using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;

namespace FocusWindow.Core.Session;

public sealed record NativeAudioLaunch(string PeerId, string RemoteIp, string CaPem,
    string ServerPem, string ServerKeyPem, byte[] ClientDer);
public interface INativeAudioProcess : IAsyncDisposable
{
    int Port { get; }
    Task Completion { get; }
}

// This is control only. The approved SecureChannel authenticates a single CSR;
// its session issuer never becomes a machine/global trust anchor.
internal sealed class NativePhoneSpeakerRoute(PhoneConnection conn, HostServices services,
    string peerId, CancellationToken session) : IAsyncDisposable
{
    private readonly string _grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly DateTimeOffset _deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    private readonly ECDsa _issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private X509Certificate2? _issuer;
    private INativeAudioProcess? _process;
    private int _used;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;
    public void Begin()
    {
        if (services.CreateNativeAudio is null) throw new InvalidOperationException("Native audio unavailable.");
        var issuer = new CertificateRequest("CN=DeskFuse session " + _grant, _issuerKey, HashAlgorithmName.SHA256);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        _issuer = issuer.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(31));
        conn.SendControl(new JsonObject { ["type"]="native_audio_challenge", ["grant"]=_grant,
            ["version"]=1, ["expiresUnixMs"]=_deadline.ToUnixTimeMilliseconds() });
    }
    public async Task AuthorizeAsync(ClientNativeAudio message)
    {
        session.ThrowIfCancellationRequested();
        if (message.Grant != _grant || message.CsrPem.Length > 8192 || DateTimeOffset.UtcNow > _deadline ||
            Interlocked.CompareExchange(ref _used, 1, 0) != 0 || _issuer is null)
            throw new InvalidOperationException("Expired/replayed native audio authorization.");
        // Default options VERIFY the CSR signature and do not import requested extensions.
        var csr = CertificateRequest.LoadSigningRequestPem(message.CsrPem, HashAlgorithmName.SHA256);
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(csr.PublicKey.ExportSubjectPublicKeyInfo(), out _);
        if (publicKey.KeySize != 256) throw new CryptographicException("Native audio requires P-256.");
        var client = new CertificateRequest("CN=DeskFuse phone " + _grant, publicKey, HashAlgorithmName.SHA256);
        client.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        client.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        client.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        var now = DateTimeOffset.UtcNow;
        using var clientCert = client.Create(_issuer, now.AddSeconds(-30), now.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new CertificateRequest("CN=deskfuse-session.local", serverKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("deskfuse-session.local");
        server.CertificateExtensions.Add(names.Build());
        server.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        server.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        server.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var serverCert = server.Create(_issuer, now.AddSeconds(-30), now.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        _process = await services.CreateNativeAudio!(new NativeAudioLaunch(peerId, conn.RemoteIp,
            _issuer.ExportCertificatePem(), serverCert.ExportCertificatePem(), serverKey.ExportPkcs8PrivateKeyPem(),
            clientCert.RawData), session).ConfigureAwait(false);
        _ = ObserveExitAsync(_process.Completion);
        conn.SendControl(new JsonObject { ["type"]="native_audio_grant", ["version"]=1, ["grant"]=_grant,
            ["port"]=_process.Port, ["caPem"]=_issuer.ExportCertificatePem(),
            ["clientPem"]=clientCert.ExportCertificatePem(), ["serverDer"]=Convert.ToBase64String(serverCert.RawData),
            ["expiresUnixMs"]=now.AddMinutes(30).ToUnixTimeMilliseconds() });
    }
    private async Task ObserveExitAsync(Task exit)
    { try { await exit.ConfigureAwait(false); _completion.TrySetResult(); }
      catch (Exception error) { _completion.TrySetException(error); } }
    public async ValueTask DisposeAsync()
    {
        if (_process is not null) await _process.DisposeAsync().ConfigureAwait(false);
        _issuer?.Dispose(); _issuerKey.Dispose();
    }
}

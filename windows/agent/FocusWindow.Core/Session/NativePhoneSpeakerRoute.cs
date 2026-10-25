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
    Task<string> EndpointReady { get; }
    Task EnableMediaAsync(CancellationToken ct);
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
    private readonly NativeObservationFence _observationFence=new();
    private readonly object _observationGate=new();
    private string? _requestId;
    private TaskCompletionSource<bool>? _requestedObservation;
    public void Observe(ClientNativeAudioObservation message) {
        lock(_observationGate) {
            if (message.Grant!=_grant || _controlStop.IsCancellationRequested) return;
            bool requested=message.RequestId is not null && message.RequestId==_requestId;
            if (message.RequestId is not null&&!requested) return;
            if (!_observationFence.Accept(message.Sequence,message.Snapshot,requested))return;
            services.AudioLab?.Report(new JsonObject { ["version"]=1,["run_id"]=services.AudioLab.RunId,
                ["sequence"]=message.Sequence,["end_snapshot"]=requested,["snapshot"]=message.Snapshot.DeepClone() });
            if (requested) _requestedObservation?.TrySetResult(true);
        }
    }
    private async Task<bool> RequestObservationAsync(CancellationToken ct) {
        Task<bool> result;
        lock(_observationGate) {
            if(_controlStop.IsCancellationRequested || _process is null)return false;
            _requestId=Guid.NewGuid().ToString("N");_requestedObservation=new(TaskCreationOptions.RunContinuationsAsynchronously);
            result=_requestedObservation.Task;
            conn.SendControl(new JsonObject {["type"]="native_audio_observation_request",["grant"]=_grant,["requestId"]=_requestId});
        }
        try {return await result.WaitAsync(TimeSpan.FromMilliseconds(800),ct).ConfigureAwait(false);}
        catch(TimeoutException){return false;}
        catch(OperationCanceledException){return false;}
        finally {lock(_observationGate){_requestId=null;_requestedObservation=null;}}
    }
    private int _begun;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly object _stopGate=new();
    private Task? _stop;
    private readonly CancellationTokenSource _controlStop=CancellationTokenSource.CreateLinkedTokenSource(session);
    private readonly object _volumeGate=new();
    private INativeEndpointVolume? _volume;
    private Task? _volumeTask;
    private long _volumeSequence;
    private int _volumeLevel;
    private bool _mediaEnabled;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;
    public void Begin()
    {
        lock (_stopGate) {
            if (_stop is not null) throw new ObjectDisposedException(nameof(NativePhoneSpeakerRoute));
            if (Interlocked.Exchange(ref _begun,1)!=0) return;
        }
        if (services.CreateNativeAudio is null) throw new InvalidOperationException("Native audio unavailable.");
        var issuer = new CertificateRequest("CN=DeskFuse session " + _grant, _issuerKey, HashAlgorithmName.SHA256);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        _issuer = issuer.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(31));
        conn.SendControl(new JsonObject { ["type"]="native_audio_challenge", ["grant"]=_grant,
            ["version"]=2, ["expiresUnixMs"]=_deadline.ToUnixTimeMilliseconds() });
    }
    public async Task AuthorizeAsync(ClientNativeAudio message)
    {
        await _serial.WaitAsync(session).ConfigureAwait(false);
        try {
            lock (_stopGate) {if (_stop is not null) throw new ObjectDisposedException(nameof(NativePhoneSpeakerRoute));}
            await AuthorizeCoreAsync(message).ConfigureAwait(false);
        } finally {_serial.Release();}
    }
    private async Task AuthorizeCoreAsync(ClientNativeAudio message)
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
        conn.SendControl(new JsonObject { ["type"]="native_audio_grant", ["version"]=2, ["grant"]=_grant,
            ["port"]=_process.Port, ["caPem"]=_issuer.ExportCertificatePem(),
            ["clientPem"]=clientCert.ExportCertificatePem(), ["serverDer"]=Convert.ToBase64String(serverCert.RawData),
            ["expiresUnixMs"]=now.AddMinutes(30).ToUnixTimeMilliseconds(),
            ["lab"]=services.AudioLab?.Policy.ToJson(), ["runId"]=services.AudioLab?.RunId });
        services.AudioLab?.Bind(RequestObservationAsync);
        _volumeTask=ObserveVolumeAsync(_process,_controlStop.Token);
    }
    private async Task ObserveVolumeAsync(INativeAudioProcess process,CancellationToken ct)
    {
        try {
            var endpoint=await process.EndpointReady.WaitAsync(TimeSpan.FromSeconds(30),ct).ConfigureAwait(false);
            var factory=services.ObserveNativeEndpointVolume ?? throw new InvalidOperationException("Native endpoint volume observer unavailable.");
            using var observer=factory(endpoint); // attach only; Rust retains the sole endpoint lease
            lock (_volumeGate) {_volume=observer;observer.Changed+=SendVolume;SendVolume(observer.Volume);}
            try {await Task.Delay(Timeout.Infinite,ct).ConfigureAwait(false);}
            finally {lock (_volumeGate) {observer.Changed-=SendVolume;_volume=null;}}
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) {_completion.TrySetException(error);}
    }
    private void SendVolume(float value)
    {
        lock (_volumeGate) {
            _volumeLevel=(int)Math.Round(Math.Clamp(value,0f,1f)*100f);
            conn.SendControl(new JsonObject { ["type"]="native_audio_volume",["grant"]=_grant,
                ["sequence"]=++_volumeSequence,["level"]=_volumeLevel });
        }
    }
    public void HandleVolume(int level)
    {
        INativeEndpointVolume? observer;lock (_volumeGate) observer=_volume;
        observer?.SetVolume(Math.Clamp(level,0,100)/100f);
    }
    public async Task ApplyVolumeAsync(ClientNativeAudioVolume message)
    {
        bool enable;
        lock (_volumeGate) {
            if (message.Grant!=_grant || message.Sequence<=0) throw new InvalidOperationException("Wrong native volume authorization.");
            if (message.Sequence!=_volumeSequence) return; // superseded reliable control update
            if (message.Level!=_volumeLevel || !message.Applied) throw new InvalidOperationException("Phone cannot enforce native volume/mute.");
            enable=!_mediaEnabled;_mediaEnabled=true;
        }
        if (enable) await (_process ?? throw new InvalidOperationException("Native owner missing.")).EnableMediaAsync(session).ConfigureAwait(false);
    }
    private async Task ObserveExitAsync(Task exit)
    { try { await exit.ConfigureAwait(false); _completion.TrySetResult(); }
      catch (Exception error) { _completion.TrySetException(error); } }
    public ValueTask DisposeAsync()
    {
        lock (_stopGate) return new ValueTask(_stop ??= StopCoreAsync());
    }
    private async Task StopCoreAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try {
            services.AudioLab?.Unbind(RequestObservationAsync);
            _controlStop.Cancel();
            if (_volumeTask is not null) await _volumeTask.ConfigureAwait(false);
            if (_process is not null) await _process.DisposeAsync().ConfigureAwait(false);
            _completion.TrySetResult(); // also completes stop before authorization
        } catch (Exception error) {_completion.TrySetException(error);throw;}
        finally {_controlStop.Dispose();_issuer?.Dispose();_issuerKey.Dispose();_serial.Release();}
    }
}

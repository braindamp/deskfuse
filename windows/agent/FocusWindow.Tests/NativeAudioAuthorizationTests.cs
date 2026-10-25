using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using Xunit;

namespace FocusWindow.Tests;
public sealed class NativeAudioAuthorizationTests
{
    private sealed class Owner : INativeAudioProcess
    {
        public int Port=>47811;
        public readonly TaskCompletionSource<string> Source=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Enabled,Disposed;
        public Task<string> EndpointReady=>Source.Task;
        public Task EnableMediaAsync(CancellationToken ct) {Enabled++;return Task.CompletedTask;}
        public Task Completion=>_done.Task;
        public ValueTask DisposeAsync() {Disposed++;_done.TrySetResult();return ValueTask.CompletedTask;}
    }
    [Fact]
    public async Task Authenticated_control_binds_exact_endpoint_CSR_and_rejects_replay()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer=await ConnectedPeer.CreateAsync(timeout.Token);
        peer.Host.StartPumps();
        NativeAudioLaunch? launched=null;
        var services=new HostServices(()=>null!,()=>null!,()=>null!,new AutoApprove(),null!,
            CreateNativeAudio:(launch,_)=>{launched=launch;return Task.FromResult<INativeAudioProcess>(new Owner());});
        var registry=new DevicePeerRegistry("known-host");
        using var lease=await registry.AttachAsync("enrolled-phone","Phone",timeout.Token,[DeviceCapabilities.Speakers]);
        await using var route=new NativePhoneSpeakerRoute(peer.Host,services,lease.Peer.Endpoints.Speakers,timeout.Token);
        route.Begin();
        var challenge=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        var grant=challenge["grant"]!.GetValue<string>();
        using var phoneKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr=new CertificateRequest("CN=Phone",phoneKey,HashAlgorithmName.SHA256).CreateSigningRequestPem();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>route.AuthorizeAsync(new("wrong-session",csr)));
        Assert.Null(launched);
        await route.AuthorizeAsync(new(grant,csr));
        Assert.NotNull(launched);
        Assert.Equal(lease.Peer.Endpoints.Speakers,launched.PeerId);
        using var cert=new X509Certificate2(launched.ClientDer);
        using var certKey=cert.GetECDsaPublicKey();
        Assert.Equal(phoneKey.ExportSubjectPublicKeyInfo(),certKey!.ExportSubjectPublicKeyInfo());
        using var issuer=X509Certificate2.CreateFromPem(launched.CaPem);
        using var chain=new X509Chain();
        chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuer);
        chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
        Assert.True(chain.Build(cert));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>route.AuthorizeAsync(new(grant,csr)));
    }
    [Fact]
    public async Task Altered_CSR_signature_cannot_authorize_a_native_owner()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer=await ConnectedPeer.CreateAsync(timeout.Token);peer.Host.StartPumps();
        var called=false;
        var services=new HostServices(()=>null!,()=>null!,()=>null!,new AutoApprove(),null!,
            CreateNativeAudio:(_,_)=>{called=true;return Task.FromResult<INativeAudioProcess>(new Owner());});
        await using var route=new NativePhoneSpeakerRoute(peer.Host,services,"00112233445566778899aabbccddeeff",timeout.Token);
        route.Begin();var challenge=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=Phone",key,HashAlgorithmName.SHA256).CreateSigningRequest();
        request[^1]^=1;
        var pem=PemEncoding.WriteString("CERTIFICATE REQUEST",request);
        await Assert.ThrowsAsync<CryptographicException>(()=>route.AuthorizeAsync(new(challenge["grant"]!.GetValue<string>(),pem)));
        Assert.False(called);
    }
    [Fact]
    public async Task Takeover_waits_for_old_owner_disposal_before_admitting_replacement()
    {
        var peers=new DevicePeerRegistry("host");
        using var old=await peers.AttachAsync("phone","Old",default,[DeviceCapabilities.Speakers]);
        var replacement=peers.AttachAsync("phone","New",default,[DeviceCapabilities.Speakers]);
        Assert.True(old.Cancellation.IsCancellationRequested);
        Assert.False(replacement.IsCompleted);
        old.Dispose();
        using var current=await replacement;
        Assert.Equal("New",Assert.Single(peers.Online).Label);
        Assert.Equal(old.Peer.Endpoints.Speakers,current.Peer.Endpoints.Speakers);
    }
    private sealed class FakeVolume : INativeEndpointVolume {
        public float VolumeLevel=0.6f;
        public float Volume=>VolumeLevel;
        public event Action<float>? Changed;
        public void SetVolume(float level) {VolumeLevel=level;Changed?.Invoke(level);}
        public void Dispose() { }
    }
    [Fact]
    public async Task Native_route_start_is_idempotent_and_stop_joins_actual_owner() {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer=await ConnectedPeer.CreateAsync(timeout.Token);peer.Host.StartPumps();
        var owner=new Owner();var starts=0;var captures=0;
        var services=new HostServices(()=>null!,()=>null!,()=>null!,new AutoApprove(),null!,
            CreateNativeAudio:(_,_)=>{starts++;return Task.FromResult<INativeAudioProcess>(owner);},
            CreateEndpointAudioCapture:(_,_,_)=>{captures++;return null;});
        await using var route=new PhoneSpeakerRoute(peer.Host,services,"00112233445566778899aabbccddeeff","Phone",FocusWindow.Core.NullLog.Instance);
        await route.ProvisionAsync([DeviceCapabilities.Speakers],timeout.Token);
        var challenge=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        await route.HandleAsync(new ClientAudio("start_phone_speakers"));
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr=new CertificateRequest("CN=Phone",key,HashAlgorithmName.SHA256).CreateSigningRequestPem();
        await route.AuthorizeNativeAsync(new(challenge["grant"]!.GetValue<string>(),csr));
        await route.HandleAsync(new ClientAudio("start_phone_speakers"));
        Assert.Equal(1,starts);Assert.Equal(0,captures);
        await route.HandleAsync(new ClientAudio("stop_phone_speakers"));
        await route.NativeCompletion.WaitAsync(timeout.Token);
        Assert.Equal(1,owner.Disposed);
    }
    [Fact]
    public async Task Native_volume_uses_exact_attached_endpoint_and_requires_applied_ack_before_media() {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer=await ConnectedPeer.CreateAsync(timeout.Token);peer.Host.StartPumps();
        var owner=new Owner();var volume=new FakeVolume();string? observed=null;
        var services=new HostServices(()=>null!,()=>null!,()=>null!,new AutoApprove(),null!,
            CreateNativeAudio:(_,_)=>Task.FromResult<INativeAudioProcess>(owner),
            ObserveNativeEndpointVolume:id=>{observed=id;return volume;});
        await using var route=new NativePhoneSpeakerRoute(peer.Host,services,"00112233445566778899aabbccddeeff",timeout.Token);
        route.Begin();var challenge=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        var grant=challenge["grant"]!.GetValue<string>();
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr=new CertificateRequest("CN=Phone",key,HashAlgorithmName.SHA256).CreateSigningRequestPem();
        await route.AuthorizeAsync(new(grant,csr));
        owner.Source.SetResult("explicit-endpoint");
        var issued=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        Assert.Equal("native_audio_grant",issued["type"]!.GetValue<string>());
        var control=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        Assert.Equal("explicit-endpoint",observed);Assert.Equal(0,owner.Enabled);
        var sequence=control["sequence"]!.GetValue<long>();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>route.ApplyVolumeAsync(new("other-grant",sequence,60,true)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>route.ApplyVolumeAsync(new(grant,sequence,60,false)));
        await route.ApplyVolumeAsync(new(grant,sequence,60,true));
        await route.ApplyVolumeAsync(new(grant,sequence,60,true));
        Assert.Equal(1,owner.Enabled);
        volume.SetVolume(0);
        control=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        Assert.Equal(0,control["level"]!.GetValue<int>());
        await route.ApplyVolumeAsync(new(grant,control["sequence"]!.GetValue<long>(),0,true));
        Assert.Equal(1,owner.Enabled);
    }
    [Fact]
    public async Task Stop_before_CSR_completes_retirement_and_refuses_late_authorization() {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer=await ConnectedPeer.CreateAsync(timeout.Token);peer.Host.StartPumps();
        var services=new HostServices(()=>null!,()=>null!,()=>null!,new AutoApprove(),null!,
            CreateNativeAudio:(_,_)=>throw new InvalidOperationException("must not launch"));
        await using var route=new NativePhoneSpeakerRoute(peer.Host,services,"00112233445566778899aabbccddeeff",timeout.Token);
        route.Begin();var challenge=JsonNode.Parse((await peer.ReadAsync(timeout.Token)).Plain)!;
        await route.DisposeAsync();
        await route.Completion.WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>route.AuthorizeAsync(new(challenge["grant"]!.GetValue<string>(),"unused")));
    }
}

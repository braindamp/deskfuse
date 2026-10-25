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
        public Task Completion=>Task.Delay(Timeout.Infinite);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
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
}

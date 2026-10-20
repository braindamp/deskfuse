using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FocusWindow.Core;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;
using Xunit;

namespace FocusWindow.Tests;

public sealed class MediaTransportTests
{
    [Fact]
    public async Task Control_and_audio_bypass_bulk_queues_without_reordering_encrypted_records()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer = await ConnectedPeer.CreateAsync(deadline.Token);
        for (var i = 0; i < PhoneConnection.MaxFrameBacklog; i++)
            Assert.True(peer.Host.TrySendFrame([0, 0, 1, 0x65], true, i + 1, 1));
        Assert.False(peer.Host.TrySendFrame([0, 0, 1, 0x65], true, 99, 1));
        var capacity = peer.Host.WaitForVideoCapacityAsync(deadline.Token);
        Assert.False(capacity.IsCompleted);
        await peer.Host.SendFileControlAsync(new JsonObject { ["type"] = "file_data", ["action"] = "begin" }, deadline.Token);
        await peer.Host.SendFileControlAsync(new JsonObject { ["type"] = "file_data", ["action"] = "end" }, deadline.Token);
        Assert.True(peer.Host.TrySendAudio([1, 0, 2, 0], 1));
        Assert.True(peer.Host.TrySendAudio([3, 0, 4, 0], 1));
        peer.Host.SendControl(ServerMessages.Stage("ready", "target metadata before video"));
        peer.Host.StartPumps();
        var kinds = new List<byte>();
        for (var i = 0; i < 7; i++) kinds.Add((await peer.ReadAsync(deadline.Token)).Kind);
        Assert.Equal(new byte[] { 1, 5, 5, 2, 1, 2, 1 }, kinds);
        await capacity;
        Assert.Equal(0, peer.Host.FrameBacklog);
    }

    [Fact]
    public async Task Audio_queue_keeps_recent_pcm_and_microphone_wire_payload_is_authenticated()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var peer = await ConnectedPeer.CreateAsync(deadline.Token);
        for (byte i = 0; i < 12; i++) Assert.True(peer.Host.TrySendAudio([i, 0, 0, 0], 7));
        peer.Host.StartPumps();
        for (byte i = 4; i < 12; i++)
        {
            var packet = await peer.ReadAsync(deadline.Token);
            Assert.Equal(Wire.KindAudio, packet.Kind);
            Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(packet.Plain));
            Assert.Equal(i, packet.Plain[4]);
        }
        var microphone = new byte[644];
        BinaryPrimitives.WriteInt32BigEndian(microphone, 123);
        microphone[4] = 42;
        await peer.WriteAsync(Wire.KindPhoneMic, microphone, deadline.Token);
        var received = await peer.Host.MicrophonePackets.ReadAsync(deadline.Token);
        Assert.Equal(123, received.StreamId);
        Assert.Equal(640, received.Pcm16Mono.Length);
        Assert.Equal(42, received.Pcm16Mono[0]);
        await peer.WriteAsync(Wire.KindPhoneMic, new byte[646], deadline.Token);
        await peer.Host.Completion.WaitAsync(deadline.Token);
    }
}

/// <summary>Independent encrypted TCP peer; does not pretend to decode H.264.</summary>
internal sealed class ConnectedPeer(TcpClient tcp, SecureChannel channel, PhoneConnection? host = null) : IAsyncDisposable
{
    public PhoneConnection Host => host ?? throw new InvalidOperationException();
    public static async Task<ConnectedPeer> CreateAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = RandomNumberGenerator.GetBytes(32);
        var tcp = new TcpClient { NoDelay = true };
        var dial = tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var accepted = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();
        await dial;
        var accepting = SecureChannel.AcceptAsync(accepted.GetStream(), key, secret, ct);
        var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), key.ExportSubjectPublicKeyInfo(), secret, ct);
        return new ConnectedPeer(tcp, channel, new PhoneConnection(accepted, accepted.GetStream(), await accepting, NullLog.Instance));
    }
    public static async Task<ConnectedPeer> ConnectAsync(PairingOffer offer, byte[] secret, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, offer.Port, ct);
        var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), offer.PublicSpki, secret, ct);
        return new ConnectedPeer(tcp, channel);
    }
    public Task ControlAsync(JsonObject message, CancellationToken ct) => WriteAsync(Wire.KindControl, ServerMessages.Serialize(message), ct);
    public async Task WriteAsync(byte kind, byte[] plain, CancellationToken ct)
    {
        var sealedBytes = channel.Seal(plain);
        var prefix = new byte[5];
        prefix[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(1), sealedBytes.Length);
        await tcp.GetStream().WriteAsync(prefix, ct);
        await tcp.GetStream().WriteAsync(sealedBytes, ct);
    }
    public async Task<(byte Kind, byte[] Plain)> ReadAsync(CancellationToken ct)
    {
        var prefix = new byte[5];
        await tcp.GetStream().ReadExactlyAsync(prefix.AsMemory(0, 1), ct);
        if (prefix[0] == Wire.KindPing) return (prefix[0], []);
        await tcp.GetStream().ReadExactlyAsync(prefix.AsMemory(1, 4), ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(1));
        Assert.InRange(size, 24, Wire.MaxFrameBytes);
        var body = new byte[size];
        await tcp.GetStream().ReadExactlyAsync(body, ct);
        return (prefix[0], channel.Open(body));
    }
    public async Task<JsonObject> UntilAsync(string type, CancellationToken ct)
    {
        while (true)
        {
            var packet = await ReadAsync(ct);
            if (packet.Kind != Wire.KindControl) continue;
            var obj = JsonNode.Parse(packet.Plain)!.AsObject();
            if (obj["type"]!.GetValue<string>() == type) return obj;
            if (obj["fatal"]?.GetValue<bool>() == true)
                throw new InvalidOperationException(obj.ToJsonString());
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (host is not null) await host.DisposeAsync();
        tcp.Dispose();
        channel.Dispose();
    }
}

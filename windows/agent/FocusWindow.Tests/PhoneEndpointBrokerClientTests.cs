using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskFuse.Audio;
using Xunit;

namespace FocusWindow.Tests;

public sealed class PhoneEndpointBrokerClientTests
{
    private const string PeerA = "00112233445566778899aabbccddeeff";
    private const string PeerB = "ffeeddccbbaa99887766554433221100";
    private const string EndpointA = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}";
    private const string EndpointB = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000002}";

    [Fact]
    public async Task Provision_retains_its_connection_and_disposal_releases_it()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var name = UniquePipeName();
        await using var server = CreateServer(name);
        await using var client = new PhoneEndpointBrokerClient(name);
        var connected = server.WaitForConnectionAsync(ct);
        var provision = client.ProvisionAsync(PeerA, "Phone A", ct);
        await connected;
        using var reader = CreateReader(server);

        var request = await ReadRequestAsync(reader, ct);
        Assert.Equal(BrokerOps.Create, request.Op);
        Assert.Equal(PeerA, request.PeerId);
        Assert.Equal("Phone A", request.Label);
        var endpoint = PhoneEndpoint(PeerA, EndpointA, "DeskFuse Phone A");
        await ReplyAsync(server, endpoint, ct);
        var result = await provision;
        Assert.Equal(EndpointA, result.EndpointId);
        Assert.Null(result.Error);

        // A second request must arrive on this same connection. The broker
        // removes a device when this pipe reaches EOF, not after each reply.
        var list = client.ListAsync(ct);
        Assert.Equal(BrokerOps.List, (await ReadRequestAsync(reader, ct)).Op);
        await ReplyAsync(server, new BrokerResponse(true, Endpoints: [endpoint]), ct);
        var listed = Assert.Single(await list);
        Assert.Equal(PeerA, listed.PeerId);
        Assert.Equal(EndpointA, listed.EndpointId);
        Assert.Equal("DeskFuse Phone A", listed.DeviceFriendlyName);
        Assert.Equal(PhoneEndpointContract.InstancePath(PeerA), listed.DeviceInstanceId);

        var disconnected = reader.ReadLineAsync(ct).AsTask();
        await client.DisposeAsync();
        Assert.Null(await disconnected);
    }

    [Fact]
    public async Task Independent_lease_connections_survive_another_leases_disposal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var name = UniquePipeName();
        await using var factory = new PhoneEndpointBrokerClient(name);
        await using var first = factory.CreateLeaseClient();
        await using var second = factory.CreateLeaseClient();
        await using var serverA = CreateServer(name);
        var connectedA = serverA.WaitForConnectionAsync(ct);
        var provisionA = first.ProvisionAsync(PeerA, "Phone A", ct);
        await connectedA;
        using var readerA = CreateReader(serverA);
        Assert.Equal(PeerA, (await ReadRequestAsync(readerA, ct)).PeerId);
        await ReplyAsync(serverA, PhoneEndpoint(PeerA, EndpointA, "DeskFuse Phone A"), ct);
        Assert.Equal(EndpointA, (await provisionA).EndpointId);

        await using var serverB = CreateServer(name);
        var connectedB = serverB.WaitForConnectionAsync(ct);
        var provisionB = second.ProvisionAsync(PeerB, "Phone B", ct);
        await connectedB;
        using var readerB = CreateReader(serverB);
        Assert.Equal(PeerB, (await ReadRequestAsync(readerB, ct)).PeerId);
        await ReplyAsync(serverB, PhoneEndpoint(PeerB, EndpointB, "DeskFuse Phone B"), ct);
        Assert.Equal(EndpointB, (await provisionB).EndpointId);

        var disconnectedA = readerA.ReadLineAsync(ct).AsTask();
        await first.DisposeAsync();
        await factory.DisposeAsync();
        Assert.Null(await disconnectedA);

        var releaseB = second.ReleaseAsync(PeerB, ct);
        var request = await ReadRequestAsync(readerB, ct);
        Assert.Equal(BrokerOps.Release, request.Op);
        Assert.Equal(PeerB, request.PeerId);
        await ReplyAsync(serverB, new BrokerResponse(true, PeerId: PeerB), ct);
        await releaseB;
        var disconnectedB = readerB.ReadLineAsync(ct).AsTask();
        await second.DisposeAsync();
        Assert.Null(await disconnectedB);
    }

    [Fact]
    public async Task Concurrent_requests_wait_for_the_previous_reply_on_a_retained_pipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var name = UniquePipeName();
        await using var server = CreateServer(name);
        await using var client = new PhoneEndpointBrokerClient(name);
        var connected = server.WaitForConnectionAsync(ct);
        var first = client.ProvisionAsync(PeerA, "Phone A", ct);
        await connected;
        using var reader = CreateReader(server);
        Assert.Equal(PeerA, (await ReadRequestAsync(reader, ct)).PeerId);

        var second = client.ProvisionAsync(PeerB, "Phone B", ct);
        var nextRequest = reader.ReadLineAsync(ct).AsTask();
        await Task.Delay(100, ct);
        Assert.False(nextRequest.IsCompleted, "A second request escaped before the first reply.");
        Assert.False(second.IsCompleted);

        await ReplyAsync(server, PhoneEndpoint(PeerA, EndpointA, "DeskFuse Phone A"), ct);
        Assert.Equal(EndpointA, (await first).EndpointId);
        var secondRequest = JsonSerializer.Deserialize<BrokerRequest>((await nextRequest)!);
        Assert.Equal(PeerB, secondRequest!.PeerId);
        await ReplyAsync(server, PhoneEndpoint(PeerB, EndpointB, "DeskFuse Phone B"), ct);
        Assert.Equal(EndpointB, (await second).EndpointId);
    }

    private static string UniquePipeName() => "DeskFuse.AudioBroker.Test." + Guid.NewGuid().ToString("N");

    private static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut,
        maxNumberOfServerInstances: 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static StreamReader CreateReader(Stream stream) =>
        new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

    private static async Task<BrokerRequest> ReadRequestAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<BrokerRequest>(line)!;
    }

    private static BrokerResponse PhoneEndpoint(string peer, string endpoint, string label) =>
        new(true, PeerId: peer, EndpointId: endpoint, DeviceFriendlyName: label,
            DeviceInstanceId: PhoneEndpointContract.InstancePath(peer));

    private static async Task ReplyAsync(Stream server, BrokerResponse response, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");
        await server.WriteAsync(bytes, ct);
        await server.FlushAsync(ct);
    }
}

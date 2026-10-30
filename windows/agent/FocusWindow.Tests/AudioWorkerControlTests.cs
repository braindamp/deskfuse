using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioWorkerControlTests
{
    [Fact]
    public async Task Pipe_replies_keep_a_negative_slot_when_eight_regular_writes_are_retained()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); local.WriteCompletion = barrier.Task;
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { });
        byte[] Reply(ulong request, byte kind) => AudioWorkerNetworkProtocol.Encode(new(request, Guid.Parse("11111111-1111-1111-1111-111111111111"), kind, [1]), reply: true);
        var work = Enumerable.Range(1, 8).Select(i => pump.SendReplyAsync(Reply((ulong)i, 129), false)).ToList();
        _ = await remote.ReadAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => { _ = pump.SendReplyAsync(Reply(9, 129), false); });
        work.Add(pump.SendReplyAsync(Reply(9, 255), true));
        Assert.Throws<InvalidOperationException>(() => { _ = pump.SendReplyAsync(Reply(10, 255), true); });
        barrier.SetResult(true);
        for (var i = 0; i < 8; i++) Assert.Equal(AudioWorkerFrameKind.NetworkReply, (await remote.ReadAsync(CancellationToken.None)).Kind);
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(3)); await pump.StopAndJoinAsync();
    }
    private static AudioControlGrant Grant()
    {
        using var f = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-authority-v1.json")));
        return AudioAuthorityProtocol.ParseControl(Convert.FromHexString(f.RootElement.GetProperty("controlHex").GetString()!));
    }
    [Fact]
    public async Task Caller_cancellation_keeps_actual_projection_owned_until_its_ack()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { });
        var operation = pump.PublishAsync(AudioAuthorityProtocol.Control(Grant()));
        var frame = await remote.ReadAsync(CancellationToken.None);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(caller.Token));
        Assert.False(operation.IsCompleted);
        await remote.WriteAsync(AudioWorkerFrameKind.AuthorityObserved, frame.Body, CancellationToken.None);
        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(3))); await pump.StopAndJoinAsync();
    }
    [Fact]
    public async Task Shutdown_does_not_release_an_actual_write_that_completes_after_cancellation()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); local.WriteCompletion = barrier.Task;
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { });
        var operation = pump.PublishAsync(AudioAuthorityProtocol.Control(Grant()));
        _ = await remote.ReadAsync(CancellationToken.None); // Bytes submitted; completion is still genuinely owned.
        var joined = pump.StopAndJoinAsync(); Assert.False(joined.IsCompleted); Assert.False(operation.IsCompleted);
        barrier.SetResult(true); await joined.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(() => operation);
    }
    [Fact]
    public async Task Sole_reader_routes_events_while_a_projection_acknowledgement_is_pending()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var routed = new TaskCompletionSource<AudioWorkerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        var pump = new AudioWorkerControlPump(new(local), frame => { routed.TrySetResult(frame); return true; }, _ => Interlocked.Increment(ref failures));
        var bytes = AudioAuthorityProtocol.Control(Grant());
        var projection = pump.PublishAsync(bytes); var request = await remote.ReadAsync(CancellationToken.None);
        Assert.Equal(AudioWorkerFrameKind.Authority, request.Kind);
        await remote.WriteAsync(AudioWorkerFrameKind.ConnectionObserved, new byte[112], CancellationToken.None);
        Assert.Equal(AudioWorkerFrameKind.ConnectionObserved, (await routed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Kind);
        Assert.False(projection.IsCompleted);
        await remote.WriteAsync(AudioWorkerFrameKind.AuthorityObserved, request.Body, CancellationToken.None);
        Assert.True(await projection.WaitAsync(TimeSpan.FromSeconds(3)));
        await pump.StopAndJoinAsync(); Assert.Equal(1, local.MaxReaders); Assert.Equal(0, failures);
    }
    [Fact]
    public async Task Restrictive_projection_and_Stop_do_not_wait_for_a_positive_ack()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { }); var grant = Grant();
        var bytes = AudioAuthorityProtocol.Control(grant);
        var regular = pump.PublishAsync(bytes); Assert.Same(regular, pump.PublishAsync(bytes));
        _ = await remote.ReadAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => { _ = pump.PublishAsync(AudioAuthorityProtocol.Control(grant with { Revision = grant.Revision + 1 })); });
        var closedBytes = AudioAuthorityProtocol.Control(grant with { Revision = grant.Revision + 1, Closed = true, OutputReady = false });
        var restrictive = pump.PublishAsync(closedBytes);
        var closed = await remote.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(closedBytes, closed.Body); Assert.False(regular.IsCompleted);
        await remote.WriteAsync(AudioWorkerFrameKind.AuthorityObserved, closed.Body, CancellationToken.None);
        Assert.True(await restrictive.WaitAsync(TimeSpan.FromSeconds(3)));
        await pump.SendStopAsync(); Assert.Equal(AudioWorkerFrameKind.Stop, (await remote.ReadAsync(CancellationToken.None)).Kind);
        Assert.False(regular.IsCompleted); Assert.Throws<IOException>(() => { _ = pump.PublishAsync(bytes); });
        await remote.WriteAsync(AudioWorkerFrameKind.AuthorityObserved, bytes, CancellationToken.None);
        Assert.True(await regular.WaitAsync(TimeSpan.FromSeconds(3)));
        await pump.StopAndJoinAsync(); Assert.Equal(1, local.MaxReaders);
    }
    [Fact]
    public async Task Wrong_acknowledgement_fails_all_retained_operations_and_cannot_reconnect()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer); var failures = 0;
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => Interlocked.Increment(ref failures));
        var grant = Grant(); var bytes = AudioAuthorityProtocol.Control(grant); var operation = pump.PublishAsync(bytes);
        _ = await remote.ReadAsync(CancellationToken.None);
        await remote.WriteAsync(AudioWorkerFrameKind.AuthorityObserved,
            AudioAuthorityProtocol.Control(grant with { AuthorityUntil = grant.AuthorityUntil + 1 }), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
        Assert.Throws<IOException>(() => { _ = pump.PublishAsync(bytes); });
        await pump.StopAndJoinAsync(); Assert.Equal(1, failures);
    }
    [Fact]
    public async Task Bootstrap_matches_shared_Rust_fixture_and_handles_fragmented_control()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-worker-control-v1.json")));
        var expected = Convert.FromHexString(fixture.RootElement.GetProperty("helloHex").GetString()!);
        var hello = AudioWorkerControl.Hello(Guid.ParseExact("00112233445566778899aabbccddeeff", "N"), Enumerable.Repeat((byte)3, 32).ToArray(), 17, 31);
        Assert.Equal(expected, hello);
        var channel = new Channel(); var output = new AudioWorkerControl(channel);
        await output.WriteAsync(AudioWorkerFrameKind.Hello, hello, CancellationToken.None);
        channel.Input = channel.Output.ToArray();
        var frame = await new AudioWorkerControl(channel).ReadAsync(CancellationToken.None);
        Assert.Equal(AudioWorkerFrameKind.Hello, frame.Kind); Assert.Equal(hello, frame.Body);
        Assert.Throws<InvalidDataException>(() => AudioWorkerControl.Hello(Guid.Empty, new byte[32], 0, 0));
    }

    [Fact]
    public async Task Malformed_or_closed_control_is_terminal_and_cannot_reconnect()
    {
        foreach (var input in new byte[][] { [0, 0, 0, 0], [0, 0, 0x20, 1], [0, 0, 0, 1, 255], [0, 0, 0, 2, 1] })
        {
            var channel = new Channel { Input = input }; var frames = new AudioWorkerControl(channel);
            var failure = await Record.ExceptionAsync(async () => await frames.ReadAsync(CancellationToken.None));
            Assert.True(failure is IOException or InvalidDataException);
            channel.Input = [0, 0, 0, 1, 3]; channel.Position = 0;
            await Assert.ThrowsAsync<IOException>(async () => await frames.ReadAsync(CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(async () => await frames.WriteAsync(AudioWorkerFrameKind.Stop, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        }
    }

    private sealed class Channel : IEnrollmentChannel
    {
        internal byte[] Input = []; internal int Position; internal MemoryStream Output = new();
        public ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var length = Math.Min(1, Math.Min(bytes.Length, Input.Length - Position));
            Input.AsSpan(Position, length).CopyTo(bytes.Span); Position += length; return ValueTask.FromResult(length);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => Output.WriteAsync(bytes, ct);
    }
    private sealed class Duplex : IEnrollmentChannel
    {
        private readonly System.Threading.Channels.Channel<byte> input = System.Threading.Channels.Channel.CreateBounded<byte>(new System.Threading.Channels.BoundedChannelOptions(32768) { AllowSynchronousContinuations = false });
        private Duplex other = null!;
        private int readers;
        internal int MaxReaders;
        internal Task? WriteCompletion;
        internal static (Duplex, Duplex) Pair() { var a = new Duplex(); var b = new Duplex(); a.other = b; b.other = a; return (a, b); }
        public async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref readers); MaxReaders = Math.Max(MaxReaders, count);
            try { var value = await input.Reader.ReadAsync(ct); bytes.Span[0] = value; return 1; }
            finally { Interlocked.Decrement(ref readers); }
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            foreach (var value in bytes.ToArray()) await other.input.Writer.WriteAsync(value, ct);
            if (WriteCompletion is not null) await WriteCompletion; // Synthetic delayed real completion, not an abandoned wait.
        }
    }
}

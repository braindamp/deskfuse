using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class AudioWorkerControlTests
{
    [Theory]
    [InlineData("PCM_OPEN")]
    [InlineData("PCM_BIND")]
    [InlineData("PCM_DUPLICATE")]
    [InlineData("PCM_REVOKE")]
    public void Pcm_native_failure_keeps_captured_error_and_operation_when_last_error_changes(string operation)
    {
        var previous = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        try
        {
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(5);
            var captured = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(87);
            var failure = PcmGrant.NativeFailure(operation, captured);
            Assert.IsType<System.ComponentModel.Win32Exception>(failure);
            Assert.Equal(5, failure.NativeErrorCode);
            Assert.StartsWith($"{operation} failed: native error 5 (0x00000005): ", failure.Message);
            Assert.EndsWith(new System.ComponentModel.Win32Exception(5).Message, failure.Message);
        }
        finally { System.Runtime.InteropServices.Marshal.SetLastPInvokeError(previous); }
    }

    [Fact]
    public void Partial_kernel_bind_retains_broker_file_until_revoke_succeeds()
    {
        using var file = new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)4, ownsHandle: false);
        var attempts = 0;
        var grant = new PcmGrant(file, _ =>
        {
            if (++attempts == 1) throw new IOException("Revoke unconfirmed.");
        });
        var size = System.Runtime.InteropServices.Marshal.SizeOf<PcmGrant.Reply>();
        var bound = new PcmGrant.Reply { Size = (uint)size, Abi = 2, Section = 8, Event = 12, Generation = 1 };
        Assert.Throws<IOException>(() => grant.BindAndDuplicate(() => (bound, size),
            () => throw new IOException("Duplicate failed.")));
        Assert.Throws<IOException>(grant.Dispose);
        Assert.False(file.IsClosed);
        grant.Dispose();
        Assert.True(file.IsClosed);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Source_exchange_requires_actual_write_and_exact_preparation_context()
    {
        var (local, peer) = Duplex.Pair();
        var remote = new AudioWorkerControl(peer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        local.WriteCompletion = barrier.Task;
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { });
        byte[] context = [1, 2, 3];
        try
        {
            var open = pump.ExchangeSourceAsync(AudioWorkerFrameKind.OpenSource, context, [4], TimeSpan.FromSeconds(3));
            var request = await remote.ReadAsync(deadline.Token);
            Assert.Equal(AudioWorkerFrameKind.OpenSource, request.Kind);
            Assert.Equal(new byte[] {1, 2, 3, 4}, request.Body);
            await remote.WriteAsync(AudioWorkerFrameKind.SourceOpened, new byte[] {1, 2, 3, 5}, deadline.Token);
            Assert.False(open.IsCompleted);
            barrier.SetResult(true);
            Assert.Equal(new byte[] {5}, await open.WaitAsync(deadline.Token));

            var map = pump.ExchangeSourceAsync(AudioWorkerFrameKind.AttachPcm, context, [6], TimeSpan.FromSeconds(3));
            Assert.Equal(AudioWorkerFrameKind.AttachPcm, (await remote.ReadAsync(deadline.Token)).Kind);
            await remote.WriteAsync(AudioWorkerFrameKind.SourceMapped, new byte[] {1, 2, 9, 7}, deadline.Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => map.WaitAsync(deadline.Token));
        }
        finally { barrier.TrySetResult(true); await pump.StopAndJoinAsync(); }
    }

    [Fact]
    public async Task Pipe_replies_keep_a_negative_slot_when_eight_regular_writes_are_retained()
    {
        var (local, peer) = Duplex.Pair(); var remote = new AudioWorkerControl(peer);
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); local.WriteCompletion = barrier.Task;
        var pump = new AudioWorkerControlPump(new(local), _ => false, _ => { });
        byte[] Reply(ulong request, byte kind) => AudioWorkerNetworkProtocol.Encode(new(request, Guid.Parse("11111111-1111-1111-1111-111111111111"), kind, [1]), reply: true);
        async Task Send(ulong request, byte kind, bool negative)
        {
            AudioWorkerControlPump.ReplyPermit? permit = null;
            try { await pump.SendReplyAsync(Reply(request, kind), negative, reserved => permit = reserved); }
            finally { permit?.Dispose(); }
        }
        var work = Enumerable.Range(1, 8).Select(i => Send((ulong)i, 129, false)).ToList();
        _ = await remote.ReadAsync(CancellationToken.None);
        var regularWaiter = Send(9, 129, false);
        Assert.False(regularWaiter.IsCompleted);
        work.Add(Send(9, 255, true));
        var negativeWaiter = Send(10, 255, true);
        Assert.False(negativeWaiter.IsCompleted);
        barrier.SetResult(true);
        for (var i = 0; i < 10; i++) Assert.Equal(AudioWorkerFrameKind.NetworkReply, (await remote.ReadAsync(CancellationToken.None)).Kind);
        await Task.WhenAll(work.Append(regularWaiter).Append(negativeWaiter)).WaitAsync(TimeSpan.FromSeconds(3));
        await pump.StopAndJoinAsync();
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
    internal sealed class Duplex : IEnrollmentChannel
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

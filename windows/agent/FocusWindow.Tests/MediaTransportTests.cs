using System.Buffers.Binary;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using FocusWindow.Core;
using FocusWindow.Core.Media;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Session;
using FocusWindow.Core.Transport;
using Xunit;
using Xunit.Abstractions;

namespace FocusWindow.Tests;

public sealed class MediaTransportTests(ITestOutputHelper output)
{
    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Returns;
        public byte[]? LastRented;
        public override byte[] Rent(int minimumLength)
        {
            var bytes = new byte[minimumLength + 91];
            Array.Fill(bytes, (byte)0xe7);
            LastRented = bytes;
            return bytes;
        }
        public override void Return(byte[] array, bool clearArray = false) => Interlocked.Increment(ref Returns);
    }

    private sealed class GatedWriteStream(Stream inner) : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public byte[]? InFlightArray;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment);
            InFlightArray = segment.Array;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Fail) throw new IOException("Simulated write failure.");
            await inner.WriteAsync(buffer, ct);
        }
    }

    private static GpuVideoPacket Packet(TrackingPool pool)
    {
        var packet = GpuVideoPacket.Rent(128, pool);
        new byte[] { 0, 0, 1, 0x65 }.CopyTo(packet.WritablePayload);
        packet.Complete(4, true, 123);
        return packet;
    }

    private sealed class CountingVideo : IVideoPipeline
    {
        public TaskCompletionSource SecondAdvance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnumeratorDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VideoPipelineInfo Info => throw new NotSupportedException();
        public Task StartAsync(VideoPipelineSettings settings, CancellationToken ct) => throw new NotSupportedException();
        public void RequestKeyframe() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<GpuVideoPacket> ReadAccessUnitsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                yield return new GpuVideoPacket([0, 0, 1, 0x65], true, 1);
                SecondAdvance.TrySetResult();
                yield return new GpuVideoPacket([0, 0, 1, 0x41], false, 2);
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { EnumeratorDisposed.TrySetResult(); }
        }
    }

    [Fact]
    public async Task Session_does_not_dequeue_a_second_access_unit_while_first_write_is_blocked()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        GatedWriteStream? gate = null;
        await using var peer = await ConnectedPeer.CreateAsync(deadline.Token, s => gate = new(s));
        peer.Host.StartPumps();
        var video = new CountingVideo();
        var services = new HostServices(() => throw new NotSupportedException(), () => new RecordingInputSink(),
            () => new StaticGuard(), new AutoApprove(), new MemoryPairingStore());
        var session = new PhoneSession(new HostOptions(), services, "test", NullLog.Instance);
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(PhoneSession).GetField("_video", instance)!.SetValue(session, video);
        typeof(PhoneSession).GetField("_conn", instance)!.SetValue(session, peer.Host);
        var pump = (Task)typeof(PhoneSession).GetMethod("PumpEncodedGpuVideoAsync", instance)!
            .Invoke(session, [stop.Token])!;
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        var early = await Task.WhenAny(video.SecondAdvance.Task, Task.Delay(150, deadline.Token));
        Assert.NotSame(video.SecondAdvance.Task, early);
        gate.Release.SetResult();
        await video.SecondAdvance.Task.WaitAsync(deadline.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);
        await video.EnumeratorDisposed.Task.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task Owned_frame_survives_async_write_and_only_its_completed_bytes_reach_peer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GatedWriteStream? gate = null;
        await using var peer = await ConnectedPeer.CreateAsync(deadline.Token, s => gate = new(s));
        var pool = new TrackingPool();
        var frame = Packet(pool);
        Assert.True(peer.Host.TrySendFrame(frame, 73, 9));
        peer.Host.StartPumps();
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, pool.Returns);
        Assert.Same(pool.LastRented, gate.InFlightArray); // encoder destination is the final socket buffer
        Assert.Throws<InvalidOperationException>(() => frame.AnnexB);
        Assert.Throws<InvalidOperationException>(() => frame.Dispose());
        gate.Release.SetResult();
        var received = await peer.ReadAsync(deadline.Token);
        Assert.Equal(Wire.KindFrame, received.Kind);
        Assert.Equal(Wire.FrameHeaderBytes + 4, received.Plain.Length);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(received.Plain.AsSpan(16)));
        Assert.Equal(new byte[] { 0, 0, 1, 0x65 }, received.Plain[Wire.FrameHeaderBytes..]);
        await peer.Host.WaitForVideoCapacityAsync(deadline.Token);
        // The single writer returns the packet only after WriteAsync and FlushAsync.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref pool.Returns) == 1, 2000));
        frame.Dispose();
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public async Task Failed_write_and_unstarted_disposal_return_each_owned_frame_once()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GatedWriteStream? gate = null;
        await using var peer = await ConnectedPeer.CreateAsync(deadline.Token, s => gate = new(s) { Fail = true });
        var pool = new TrackingPool();
        var first = Packet(pool);
        var rejected = Packet(pool);
        Assert.True(peer.Host.TrySendFrame(first, 1, 1));
        Assert.False(peer.Host.TrySendFrame(rejected, 3, 1));
        rejected.Dispose(); // failure retains caller ownership
        peer.Host.StartPumps();
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        gate.Release.SetResult();
        await peer.Host.Completion.WaitAsync(deadline.Token);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref pool.Returns) == 2, 2000));
        Assert.Equal(0, peer.Host.FrameBacklog);

        var noWriter = await ConnectedPeer.CreateAsync(deadline.Token);
        var queued = Packet(pool);
        Assert.True(noWriter.Host.TrySendFrame(queued, 4, 1));
        await noWriter.DisposeAsync();
        Assert.Equal(3, pool.Returns);
    }

    [Fact]
    public async Task Cancelling_an_inflight_write_returns_its_frame_after_write_unwinds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GatedWriteStream? gate = null;
        var peer = await ConnectedPeer.CreateAsync(deadline.Token, s => gate = new(s));
        var pool = new TrackingPool();
        Assert.True(peer.Host.TrySendFrame(Packet(pool), 1, 1));
        peer.Host.StartPumps();
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, pool.Returns);
        await peer.DisposeAsync();
        Assert.Equal(1, pool.Returns);
        Assert.Equal(0, peer.Host.FrameBacklog);
        Assert.Equal(0, peer.Host.FrameBacklogBytes);
    }

    [Fact]
    public async Task Concurrent_enqueue_and_shutdown_returns_every_packet_and_rejects_oversize()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GatedWriteStream? gate = null;
        var peer = await ConnectedPeer.CreateAsync(deadline.Token, s => gate = new(s));
        var pool = new TrackingPool();
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuVideoPacket.Rent(PhoneConnection.MaxVideoAccessUnitBytes + 1, pool));
        Assert.False(peer.Host.TrySendFrame(new byte[PhoneConnection.MaxVideoAccessUnitBytes + 1], true, 1, 1));
        Assert.Equal(1, peer.Host.FramesRejected);
        Assert.True(peer.Host.TrySendFrame(Packet(pool), 1, 1));
        peer.Host.StartPumps();
        await gate!.Entered.Task.WaitAsync(deadline.Token);
        const int attempts = 200;
        var senders = Enumerable.Range(0, attempts).Select(_ => Task.Run(() =>
        {
            var packet = Packet(pool);
            if (!peer.Host.TrySendFrame(packet, 1, 1)) packet.Dispose();
        })).ToArray();
        var shutdown = peer.DisposeAsync().AsTask();
        await Task.WhenAll(senders.Append(shutdown)).WaitAsync(deadline.Token);
        Assert.Equal(attempts + 1, pool.Returns);
        Assert.Equal(0, peer.Host.FrameBacklog);
        Assert.Equal(0, peer.Host.FrameBacklogBytes);
    }

    [Fact]
    [Trait("Category", "ManagedMediaMeasurement")]
    public async Task Measure_owned_and_extra_copy_paths_at_matched_access_unit_size()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = new byte[256 * 1024];
        new Random(42).NextBytes(source);
        // Both paths use the same encrypted loopback transport and existing receiver.
        // The comparison isolates the extra managed access-unit copy only; it is
        // not a WGC/NVENC or physical end-to-end performance measurement.
        async Task<(long Allocated, double Ms)> Run(bool owned)
        {
            await using var peer = await ConnectedPeer.CreateAsync(deadline.Token);
            peer.Host.StartPumps();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 24; i++)
            {
                await peer.Host.WaitForVideoCapacityAsync(deadline.Token);
                if (owned)
                {
                    var packet = GpuVideoPacket.Rent(source.Length);
                    source.CopyTo(packet.WritablePayload);
                    packet.Complete(source.Length, true, 1);
                    if (!peer.Host.TrySendFrame(packet, i + 1, 1))
                    {
                        packet.Dispose();
                        throw new InvalidOperationException("Measurement frame rejected.");
                    }
                }
                else
                {
                    var extraCopy = source.AsSpan().ToArray();
                    Assert.True(peer.Host.TrySendFrame(extraCopy, true, i + 1, 1));
                }
                var received = await peer.ReadAsync(deadline.Token);
                Assert.Equal(source.Length + Wire.FrameHeaderBytes, received.Plain.Length);
            }
            timer.Stop();
            return (GC.GetTotalAllocatedBytes(true) - allocated, timer.Elapsed.TotalMilliseconds);
        }
        _ = await Run(true); // pool/JIT warm-up
        var copied = await Run(false);
        var direct = await Run(true);
        output.WriteLine($"24 x {source.Length} bytes; extra-copy {copied.Allocated} allocated bytes, {copied.Ms:F1} ms; owned {direct.Allocated} allocated bytes, {direct.Ms:F1} ms. Loopback only.");
    }
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
        for (var i = 0; i < 6; i++) kinds.Add((await peer.ReadAsync(deadline.Token)).Kind);
        Assert.Equal(new byte[] { 1, 5, 5, 2, 1, 1 }, kinds);
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
    public static async Task<ConnectedPeer> CreateAsync(CancellationToken ct, Func<Stream, Stream>? wrapHost = null)
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
        var hostStream = accepted.GetStream();
        return new ConnectedPeer(tcp, channel, new PhoneConnection(accepted, wrapHost?.Invoke(hostStream) ?? hostStream, await accepting, NullLog.Instance));
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

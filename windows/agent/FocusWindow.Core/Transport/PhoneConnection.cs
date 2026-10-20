using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using FocusWindow.Core.Protocol;
namespace FocusWindow.Core.Transport;
/// <summary>
/// One authenticated phone connection, speaking exactly the framing in the Android client's
/// LanTransport.kt:
///
///   control: [1][int32 BE len][sealed JSON]
///   frame:   [2][int32 BE len][sealed (20-byte header + Annex-B access unit)]
///   ping:    [3]                     (host -> client, no body)
///   pong:    [4]                     (client -> host, no body)
///
/// Two design points that are easy to get wrong:
///
///  * SEAL ORDER == WRITE ORDER. The receiver rejects any packet whose counter is not
///    greater than the last one ("replayed or reordered"). If two threads sealed packets A
///    and B and then wrote them in the opposite order, the client would drop the
///    connection. All sealing therefore happens inside the single writer loop.
///
///  * IDLE PINGS ARE MANDATORY. The client's socket has a 20 s read timeout. A static
///    desktop sends 0 frames/s (FR-REF-02), so without host pings every idle session would
///    be torn down and "reconnected" every 20 s.
/// </summary>
public sealed class PhoneConnection : IAsyncDisposable
{
    public sealed record PhoneMicrophonePacket(int StreamId, byte[] Pcm16Mono);
    private readonly Channel<PhoneMicrophonePacket> _phoneMicrophone =
        Channel.CreateBounded<PhoneMicrophonePacket>(new BoundedChannelOptions(8) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
    public const int MaxFrameBacklog = 2;
    // Wire.MaxFrameBytes limits the SEALED packet body, not the Annex-B access unit.
    // The 20-byte video header and 24-byte AES-GCM record envelope also count.
    public const int MaxVideoAccessUnitBytes =
        Wire.MaxFrameBytes - Wire.FrameHeaderBytes - SecureChannel.RecordOverheadBytes;

    public static bool CanSendAccessUnit(int byteCount) =>
        byteCount > 0 && byteCount <= MaxVideoAccessUnitBytes;
    // Audio chunks are independent PCM; dropping stale chunks is safe and helps latency.
    private sealed record Outbound(byte Kind, byte[]? Plain, bool IsFrame,
        bool ReleasesSlot = false);
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SecureChannel _chan;
    private readonly ILog _log;
    private readonly Channel<ClientMessage> _incoming =
        Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<Outbound> _outgoing =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outbound> _videoQueue = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outbound> _fileQueue = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outbound> _audioQueue = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<bool> _queued = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _videoSlots = new(MaxFrameBacklog, MaxFrameBacklog);
    // Streaming file chunks should not fill an unbounded outgoing channel.
    private readonly SemaphoreSlim _fileSlots = new(16, 16);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private Task? _writer;
    private int _frameBacklog;
    private const int MaxAudioPayload = 32768;
    private long _pingSentTimestamp;
    private double _rttMs = -1;
    private long _framesSent;
    private long _framesRejected;
    private long _wireBytesSent;
    private int _disposed;
    public PhoneConnection(TcpClient tcp, Stream stream, SecureChannel chan, ILog log)
    {
        _tcp = tcp;
        _stream = stream;
        _chan = chan;
        _log = log;
    }
    public string VerificationCode => _chan.VerificationCode;
    public ChannelReader<ClientMessage> Messages => _incoming.Reader;
    public ChannelReader<PhoneMicrophonePacket> MicrophonePackets => _phoneMicrophone.Reader;
    public Task Completion => _completion.Task;
    public int FrameBacklog => Volatile.Read(ref _frameBacklog);
    /// <summary>Last measured ping round trip, or null before the first pong.</summary>
    public double? RttMs => _rttMs < 0 ? null : _rttMs;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesRejected => Interlocked.Read(ref _framesRejected);
    public long WireBytesSent => Interlocked.Read(ref _wireBytesSent);
    /// <summary>
    /// Reads and AUTHENTICATES the first packet. A peer that completed the (anonymous) ECDH
    /// but does not hold the pairing secret derives different keys, so decryption fails
    /// here with CryptographicException and the caller drops the socket.
    /// </summary>
    public async Task<ClientMessage> ReadFirstMessageAsync(CancellationToken ct)
    {
        var kind = new byte[1];
        await _stream.ReadExactlyAsync(kind, ct).ConfigureAwait(false);
        if (kind[0] != Wire.KindControl)
            throw new InvalidDataException($"first packet must be control, got kind {kind[0]}");
        var plain = await ReadSealedControlAsync(ct).ConfigureAwait(false);
        return ClientMessageParser.Parse(plain);
    }
    public void StartPumps()
    {
        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
    }
    /// <summary>Control messages are never dropped.</summary>
    public void SendControl(System.Text.Json.Nodes.JsonObject message) =>
        Enqueue(_outgoing, new Outbound(Wire.KindControl, ServerMessages.Serialize(message), false));
    private bool Enqueue(Channel<Outbound> queue, Outbound packet)
    {
        if (!queue.Writer.TryWrite(packet)) return false;
        _queued.Writer.TryWrite(true); // coalesced wakeup, even when stale PCM is replaced
        return true;
    }
    public async Task SendFileControlAsync(System.Text.Json.Nodes.JsonObject message, CancellationToken ct)
    {
        await _fileSlots.WaitAsync(ct).ConfigureAwait(false);
        if (!Enqueue(_fileQueue, new Outbound(Wire.KindControl, ServerMessages.Serialize(message), false, true)))
        {
            _fileSlots.Release();
            throw new IOException("File transport closed");
        }
    }
    public void SendPing() => Enqueue(_outgoing, new Outbound(Wire.KindPing, null, false));
    /// <summary>
    /// Backpressure the encoded consumer while keeping capture latest-wins.
    /// This avoids dropping reference frames and repeatedly sending large IDRs
    /// merely because the network is temporarily busy.
    /// </summary>
    public async Task WaitForVideoCapacityAsync(CancellationToken ct)
    {
        await _videoSlots.WaitAsync(ct).ConfigureAwait(false);
        _videoSlots.Release();
    }
    /// <summary>
    /// Queues one access unit. Returns false when the phone is not keeping up (more than
    /// <see cref="MaxFrameBacklog"/> frames queued): the caller must treat the stream as
    /// damaged and restart it from a keyframe, because dropping one inter frame silently
    /// corrupts everything until the next IDR.
    /// </summary>
    public bool TrySendFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long ptsUs, long targetVersion)
    {
        // A late rejection in WriteLoopAsync silently lost an H.264 frame: the
        // caller thought it was accepted and never requested an IDR. Reject
        // before allocating or queuing it so PhoneSession can restart the GOP.
        if (!CanSendAccessUnit(accessUnit.Length))
        {
            Interlocked.Increment(ref _framesRejected);
            _log.Warn($"H.264 access unit length {accessUnit.Length} exceeds wire budget {MaxVideoAccessUnitBytes}; resync required");
            return false;
        }
        if (!_videoSlots.Wait(0)) return false;
        var plain = new byte[Wire.FrameHeaderBytes + accessUnit.Length];
        var span = plain.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], (ushort)Wire.FrameMagic);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(keyframe ? Wire.FlagKeyframe : 0));
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)targetVersion);
        BinaryPrimitives.WriteInt64BigEndian(span[8..], ptsUs);
        BinaryPrimitives.WriteInt32BigEndian(span[16..], accessUnit.Length);
        accessUnit.CopyTo(span[Wire.FrameHeaderBytes..]);
        Interlocked.Increment(ref _frameBacklog);
        if (Enqueue(_videoQueue, new Outbound(Wire.KindFrame, plain, true))) return true;
        Interlocked.Decrement(ref _frameBacklog);
        _videoSlots.Release();
        return false;
    }
    /// <summary>
    /// Queue at most eight audio chunks so a stalled video/file connection cannot
    /// accumulate seconds of speech. The first 4 plaintext bytes are the stream ID.
    /// Sealing happens in the same ordered writer as video/control.
    /// </summary>
    public bool TrySendAudio(ReadOnlySpan<byte> pcm16le, int streamId)
    {
        if (pcm16le.Length is < 4 or > MaxAudioPayload || (pcm16le.Length & 1) != 0 || streamId <= 0)
            return false;
        var plain = new byte[4 + pcm16le.Length];
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(0, 4), streamId);
        pcm16le.CopyTo(plain.AsSpan(4));
        return Enqueue(_audioQueue, new Outbound(Wire.KindAudio, plain, false));
    }
    private async Task<byte[]> ReadSealedControlAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await _stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len is < 1 or > Wire.MaxControlBytes)
            throw new InvalidDataException($"control frame length out of range: {len}");
        var sealedBytes = new byte[len];
        await _stream.ReadExactlyAsync(sealedBytes, ct).ConfigureAwait(false);
        return _chan.Open(sealedBytes);
    }
    private async Task<byte[]> ReadSealedMicrophoneAsync(CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await _stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (size < SecureChannel.RecordOverheadBytes + 6 ||
            size > SecureChannel.RecordOverheadBytes + 644)
            throw new InvalidDataException("microphone packet exceeds PCM wire budget");
        var packet = new byte[size];
        await _stream.ReadExactlyAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var kind = new byte[1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(kind, ct).ConfigureAwait(false);
                switch (kind[0])
                {
                    case Wire.KindControl:
                    {
                        var plain = await ReadSealedControlAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await _incoming.Writer.WriteAsync(ClientMessageParser.Parse(plain), ct).ConfigureAwait(false);
                        }
                        catch (FormatException ex)
                        {
                            // An authenticated peer sent something we cannot parse. Ignoring
                            // one message is safer than killing a live session over it.
                            _log.Warn("ignoring malformed control message: " + ex.Message);
                        }
                        break;
                    }
                    case Wire.KindPhoneMic:
                    {
                        // Strictly bounded 16 kHz mono PCM16, <=20 ms samples.
                        // Audio is still encrypted/authenticated by SecureChannel.
                        // Any unconsumed packets are dropped rather than building
                        // stale audio or backpressuring control/video.
                        var sealedBytes = await ReadSealedMicrophoneAsync(ct).ConfigureAwait(false);
                        var plain = _chan.Open(sealedBytes);
                        if (plain.Length < 6 || plain.Length > 644 ||
                            (plain.Length & 1) != 0)
                            throw new InvalidDataException("invalid microphone PCM length");
                        var streamId = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(0, 4));
                        if (streamId <= 0) throw new InvalidDataException("invalid microphone stream ID");
                        _phoneMicrophone.Writer.TryWrite(new PhoneMicrophonePacket(
                            streamId, plain.AsSpan(4).ToArray()));
                        break;
                    }
                    case Wire.KindPong:
                    {
                        var sent = Interlocked.Read(ref _pingSentTimestamp);
                        if (sent != 0)
                            _rttMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                        break;
                    }
                    default:
                        throw new InvalidDataException($"unexpected packet kind {kind[0]}");
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException
                                       or CryptographicException or InvalidDataException or SocketException)
        {
            if (!ct.IsCancellationRequested) _log.Info("phone connection closed: " + ex.Message);
        }
        finally
        {
            _incoming.Writer.TryComplete();
            _phoneMicrophone.Writer.TryComplete();
            Finish();
        }
    }
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var preferFile = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Select BEFORE sealing: AES-GCM counter order remains wire
                // order across lanes. Target/control precedes dependent media.
                // Audio bypasses queued video/files; alternate bulk work so
                // an active screen cannot starve an explicit file download.
                Outbound? o;
                if (!_outgoing.Reader.TryRead(out o) && !_audioQueue.Reader.TryRead(out o))
                {
                    if (preferFile && _fileQueue.Reader.TryRead(out o)) preferFile = false;
                    else if (_videoQueue.Reader.TryRead(out o)) preferFile = true;
                    else if (_fileQueue.Reader.TryRead(out o)) preferFile = false;
                    else
                    {
                        await _queued.Reader.ReadAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                }
                byte[] packet;
                if (o.Kind == Wire.KindPing)
                {
                    Interlocked.Exchange(ref _pingSentTimestamp, Stopwatch.GetTimestamp());
                    packet = new[] { Wire.KindPing };
                }
                else
                {
                    // Seal HERE, in dequeue order, so counter order == wire order.
                    var sealedBytes = _chan.Seal(o.Plain!);
                    if (o.IsFrame)
                    {
                        if (sealedBytes.Length > Wire.MaxFrameBytes)
                        {
                            // Defensive invariant: TrySendFrame never queues one of
                            // these. A future protocol change must fail the entire
                            // connection rather than silently break inter-frame decode.
                            throw new InvalidDataException(
                                $"sealed H.264 frame of {sealedBytes.Length} bytes exceeds wire maximum");
                        }
                    }
                    packet = new byte[5 + sealedBytes.Length];
                    packet[0] = o.Kind;
                    BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), sealedBytes.Length);
                    sealedBytes.CopyTo(packet.AsSpan(5));
                }
                try
                {
                    await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    if (o.ReleasesSlot) _fileSlots.Release();
                    if (o.IsFrame)
                    {
                        Interlocked.Decrement(ref _frameBacklog);
                        _videoSlots.Release();
                    }
                }
                if (o.IsFrame) Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _wireBytesSent, packet.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            if (!ct.IsCancellationRequested) _log.Info("phone connection write ended: " + ex.Message);
        }
        finally
        {
            Finish();
        }
    }
    private void Finish()
    {
        _completion.TrySetResult();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _videoQueue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();
        _fileQueue.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _tcp.Close(); } catch { /* already closed */ }
        foreach (var t in new[] { _reader, _writer })
        {
            if (t is null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* shutting down */ }
        }
        _chan.Dispose();
        _cts.Dispose();
    }
}

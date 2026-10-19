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
    public const int MaxFrameBacklog = 4;
    // Wire.MaxFrameBytes limits the SEALED packet body, not the Annex-B access unit.
    // The 20-byte video header and 24-byte AES-GCM record envelope also count.
    public const int MaxVideoAccessUnitBytes =
        Wire.MaxFrameBytes - Wire.FrameHeaderBytes - SecureChannel.RecordOverheadBytes;

    public static bool CanSendAccessUnit(int byteCount) =>
        byteCount > 0 && byteCount <= MaxVideoAccessUnitBytes;
    // Audio chunks are independent PCM; dropping stale chunks is safe and helps latency.
    private sealed record Outbound(byte Kind, byte[]? Plain, bool IsFrame,
        bool ReleasesSlot = false, bool IsAudio = false);
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SecureChannel _chan;
    private readonly ILog _log;
    private readonly Channel<ClientMessage> _incoming =
        Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<Outbound> _outgoing =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    // Streaming file chunks should not fill an unbounded outgoing channel.
    private readonly SemaphoreSlim _fileSlots = new(16, 16);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private Task? _writer;
    private int _frameBacklog;
    private int _audioBacklog;
    private const int MaxAudioBacklog = 8;
    private const int MaxAudioPayload = 3840;
    private long _pingSentTimestamp;
    private double _rttMs = -1;
    private long _framesSent;
    private long _framesRejected;
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
    public Task Completion => _completion.Task;
    public int FrameBacklog => Volatile.Read(ref _frameBacklog);
    /// <summary>Last measured ping round trip, or null before the first pong.</summary>
    public double? RttMs => _rttMs < 0 ? null : _rttMs;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesRejected => Interlocked.Read(ref _framesRejected);
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
        _outgoing.Writer.TryWrite(new Outbound(Wire.KindControl, ServerMessages.Serialize(message), false));
    public async Task SendFileControlAsync(System.Text.Json.Nodes.JsonObject message, CancellationToken ct)
    {
        await _fileSlots.WaitAsync(ct).ConfigureAwait(false);
        if (!_outgoing.Writer.TryWrite(new Outbound(Wire.KindControl, ServerMessages.Serialize(message), false, true)))
        {
            _fileSlots.Release();
            throw new IOException("File transport closed");
        }
    }
    public void SendPing() => _outgoing.Writer.TryWrite(new Outbound(Wire.KindPing, null, false));
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
        if (Volatile.Read(ref _frameBacklog) >= MaxFrameBacklog) return false;
        var plain = new byte[Wire.FrameHeaderBytes + accessUnit.Length];
        var span = plain.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], (ushort)Wire.FrameMagic);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(keyframe ? Wire.FlagKeyframe : 0));
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)targetVersion);
        BinaryPrimitives.WriteInt64BigEndian(span[8..], ptsUs);
        BinaryPrimitives.WriteInt32BigEndian(span[16..], accessUnit.Length);
        accessUnit.CopyTo(span[Wire.FrameHeaderBytes..]);
        Interlocked.Increment(ref _frameBacklog);
        if (_outgoing.Writer.TryWrite(new Outbound(Wire.KindFrame, plain, true))) return true;
        Interlocked.Decrement(ref _frameBacklog);
        return false;
    }
    /// <summary>
    /// Queue at most eight audio chunks so a stalled video/file connection cannot
    /// accumulate seconds of speech. The first 4 plaintext bytes are the stream ID.
    /// Sealing happens in the same ordered writer as video/control.
    /// </summary>
    public bool TrySendAudio(ReadOnlySpan<byte> pcm16le, int streamId)
    {
        if (pcm16le.Length is < 4 or > MaxAudioPayload || (pcm16le.Length & 3) != 0 ||
            Volatile.Read(ref _audioBacklog) >= MaxAudioBacklog)
            return false;
        var plain = new byte[4 + pcm16le.Length];
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(0, 4), streamId);
        pcm16le.CopyTo(plain.AsSpan(4));
        Interlocked.Increment(ref _audioBacklog);
        if (_outgoing.Writer.TryWrite(new Outbound(Wire.KindAudio, plain, false, IsAudio: true)))
            return true;
        Interlocked.Decrement(ref _audioBacklog);
        return false;
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
            Finish();
        }
    }
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var o in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
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
                    if (o.IsAudio) Interlocked.Decrement(ref _audioBacklog);
                    if (o.IsFrame)
                    {
                        Interlocked.Decrement(ref _frameBacklog);
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
                finally { if (o.ReleasesSlot) _fileSlots.Release(); }
                if (o.IsFrame) Interlocked.Increment(ref _framesSent);
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

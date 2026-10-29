using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using FocusWindow.Core.Media;
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
    // One write in flight; the GPU pipeline owns the single pending unit.
    public const int MaxFrameBacklog = 1;
    // Wire.MaxFrameBytes limits the SEALED packet body, not the Annex-B access unit.
    // The 20-byte video header and 24-byte AES-GCM record envelope also count.
    public const int MaxVideoAccessUnitBytes =
        Wire.MaxFrameBytes - Wire.FrameHeaderBytes - SecureChannel.RecordOverheadBytes;
    public const long MaxFrameBacklogBytes = (long)MaxFrameBacklog * MaxVideoAccessUnitBytes;

    public static bool CanSendAccessUnit(int byteCount) =>
        byteCount > 0 && byteCount <= MaxVideoAccessUnitBytes;
    // Audio chunks are independent PCM; dropping stale chunks is safe and helps latency.
    private sealed record Outbound(byte Kind, byte[]? Plain, bool IsFrame,
        bool ReleasesSlot = false, long AudioEnqueueQpc = 0, GpuVideoPacket? Frame = null,
        long VideoEnqueueQpc = 0);
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SecureChannel _chan;
    private readonly ILog _log;
    private readonly Channel<ClientMessage> _incoming =
        Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<Outbound> _outgoing =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outbound> _videoQueue = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { SingleReader = false });
    private readonly Channel<Outbound> _fileQueue = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Outbound> _audioQueue;
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
    private long _frameBacklogBytes;
    private long _videoQueueMaxUs;
    private long _videoNativePendingMaxUs;
    private long _videoRentedMaxBytes;
    private long _videoSealMaxUs;
    private long _videoWriteMaxUs;
    // These counters distinguish accepted PCM from true DropOldest evictions
    // and completed local socket writes. A completed WriteAsync is NOT an ACK.
    private long _audioEvicted;
    private long _audioAccepted;
    private long _audioWritten;
    private long _audioQueueAgeMaxMs;
    private long _audioQueueAgeOver50;
    private long _audioWriteMaxMs;
    private long _audioWriteOver20;
    private long _audioWriteOver50;
    private int _disposed;
    public PhoneConnection(TcpClient tcp, Stream stream, SecureChannel chan, ILog log)
    {
        _tcp = tcp;
        _stream = stream;
        _chan = chan;
        _log = log;
        _audioQueue = Channel.CreateBounded<Outbound>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            itemDropped: _ => Interlocked.Increment(ref _audioEvicted));
    }
    public string VerificationCode => _chan.VerificationCode;
    public string RemoteIp => ((System.Net.IPEndPoint)_tcp.Client.RemoteEndPoint!).Address.ToString();
    public string? AuthenticatedDeviceId => _chan.CredentialDeviceId;
    public ChannelReader<ClientMessage> Messages => _incoming.Reader;
    public ChannelReader<PhoneMicrophonePacket> MicrophonePackets => _phoneMicrophone.Reader;
    public Task Completion => _completion.Task;
    public int FrameBacklog => Volatile.Read(ref _frameBacklog);
    public long FrameBacklogBytes => Interlocked.Read(ref _frameBacklogBytes);
    /// <summary>Maxima are local stage durations, not end-to-end latency or a presentation ACK.</summary>
    public string VideoHealth =>
        $"txBytesPending={FrameBacklogBytes}" +
        $" queueMaxUs={Interlocked.Exchange(ref _videoQueueMaxUs, 0)}" +
        $" nativePendingMaxUs={Interlocked.Exchange(ref _videoNativePendingMaxUs, 0)}" +
        $" ownedRentMaxBytes={Interlocked.Read(ref _videoRentedMaxBytes)}" +
        $" sealMaxUs={Interlocked.Exchange(ref _videoSealMaxUs, 0)}" +
        $" writeMaxUs={Interlocked.Exchange(ref _videoWriteMaxUs, 0)}" +
        $" sealLockMaxUs={_chan.SendLockWaitMaxUs} openLockMaxUs={_chan.ReceiveLockWaitMaxUs}";
    /// <summary>Last measured ping round trip, or null before the first pong.</summary>
    public double? RttMs => _rttMs < 0 ? null : _rttMs;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesRejected => Interlocked.Read(ref _framesRejected);
    public long WireBytesSent => Interlocked.Read(ref _wireBytesSent);
    /// <summary>
    /// Host-side diagnostics only. "written" means WriteAsync + FlushAsync
    /// completed locally, not acknowledged or played by the Android phone.
    /// No protocol / network format change.
    /// </summary>
    public string AudioHealth =>
        $"txAccept={Interlocked.Read(ref _audioAccepted)}" +
        $" txEvict={Interlocked.Read(ref _audioEvicted)}" +
        $" txWritten={Interlocked.Read(ref _audioWritten)}" +
        $" txAgeMaxMs={Interlocked.Exchange(ref _audioQueueAgeMaxMs, 0)}" +
        $" txAge>50ms={Interlocked.Exchange(ref _audioQueueAgeOver50, 0)}" +
        $" txWriteMaxMs={Interlocked.Exchange(ref _audioWriteMaxMs, 0)}" +
        $" txWrite>20ms={Interlocked.Exchange(ref _audioWriteOver20, 0)}" +
        $" txWrite>50ms={Interlocked.Exchange(ref _audioWriteOver50, 0)}";
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
        var packet = GpuVideoPacket.Rent(accessUnit.Length);
        try
        {
            accessUnit.CopyTo(packet.WritablePayload.Span);
            packet.Complete(accessUnit.Length, keyframe, 0);
            if (!TrySendFrame(packet, ptsUs, targetVersion)) return false;
            packet = null!; // accepted: ownership passed to the writer
            return true;
        }
        finally { packet?.Dispose(); }
    }

    /// <summary>On success takes ownership; on failure the caller retains ownership.</summary>
    public bool TrySendFrame(GpuVideoPacket packet, long ptsUs, long targetVersion)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!CanSendAccessUnit(packet.Length))
        {
            Interlocked.Increment(ref _framesRejected);
            return false;
        }
        if (!_videoSlots.Wait(0)) return false;
        var queued = false;
        var transferred = false;
        try
        {
            UpdateMaximum(ref _videoNativePendingMaxUs, ElapsedUs(packet.CompletedQpc));
            UpdateMaximum(ref _videoRentedMaxBytes, packet.RentedBytes);
            var span = packet.Plaintext.Span;
            BinaryPrimitives.WriteUInt16BigEndian(span[0..], (ushort)Wire.FrameMagic);
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(packet.Keyframe ? Wire.FlagKeyframe : 0));
            BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)targetVersion);
            BinaryPrimitives.WriteInt64BigEndian(span[8..], ptsUs);
            BinaryPrimitives.WriteInt32BigEndian(span[16..], packet.Length);
            var outbound = new Outbound(Wire.KindFrame, null, true, Frame: packet,
                VideoEnqueueQpc: Stopwatch.GetTimestamp());
            packet.AcceptByTransport();
            transferred = true;
            Interlocked.Increment(ref _frameBacklog);
            Interlocked.Add(ref _frameBacklogBytes, packet.Length);
            queued = Enqueue(_videoQueue, outbound);
            if (queued) return true;
            Interlocked.Decrement(ref _frameBacklog);
            Interlocked.Add(ref _frameBacklogBytes, -packet.Length);
            return false;
        }
        finally
        {
            if (!queued)
            {
                if (transferred) packet.CancelTransfer();
                _videoSlots.Release();
            }
        }
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
        var accepted = Enqueue(_audioQueue,
            new Outbound(Wire.KindAudio, plain, false,
                AudioEnqueueQpc: Stopwatch.GetTimestamp()));
        if (accepted) Interlocked.Increment(ref _audioAccepted);
        return accepted;
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
                if (o.Kind == Wire.KindAudio && o.AudioEnqueueQpc != 0)
                {
                    var queueAgeMs = (long)((Stopwatch.GetTimestamp() -
                        o.AudioEnqueueQpc) * 1000.0 / Stopwatch.Frequency);
                    if (queueAgeMs > _audioQueueAgeMaxMs) _audioQueueAgeMaxMs = queueAgeMs;
                    if (queueAgeMs > 50) Interlocked.Increment(ref _audioQueueAgeOver50);
                }
                try
                {
                    if (o.IsFrame)
                        UpdateMaximum(ref _videoQueueMaxUs, ElapsedUs(o.VideoEnqueueQpc));
                    ReadOnlyMemory<byte> packet;
                    int packetLength;
                    if (o.Kind == Wire.KindPing)
                    {
                        Interlocked.Exchange(ref _pingSentTimestamp, Stopwatch.GetTimestamp());
                        packet = new byte[] { Wire.KindPing };
                        packetLength = 1;
                    }
                    else if (o.Frame is not null)
                    {
                        var sealStarted = Stopwatch.GetTimestamp();
                        packet = o.Frame.SealForTransport(_chan, o.Kind);
                        packetLength = packet.Length;
                        UpdateMaximum(ref _videoSealMaxUs, ElapsedUs(sealStarted));
                    }
                    else
                    {
                        // Seal HERE, in dequeue order, so counter order == wire order.
                        ReadOnlyMemory<byte> plain = o.Plain!;
                        var sealedLength = checked(plain.Length + SecureChannel.RecordOverheadBytes);
                        if (o.IsFrame && sealedLength > Wire.MaxFrameBytes)
                            throw new InvalidDataException($"sealed H.264 frame of {sealedLength} bytes exceeds wire maximum");
                        packetLength = checked(5 + sealedLength);
                        var bytes = new byte[packetLength];
                        packet = bytes;
                        bytes[0] = o.Kind;
                        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), sealedLength);
                        var sealStarted = o.IsFrame ? Stopwatch.GetTimestamp() : 0;
                        _chan.Seal(plain.Span, bytes.AsSpan(5, sealedLength));
                        if (o.IsFrame) UpdateMaximum(ref _videoSealMaxUs, ElapsedUs(sealStarted));
                    }
                    var writeStartedQpc = o.Kind == Wire.KindAudio
                        ? Stopwatch.GetTimestamp() : 0L;
                    var videoWriteStarted = o.IsFrame ? Stopwatch.GetTimestamp() : 0L;
                    await _stream.WriteAsync(packet[..packetLength], ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                    if (o.IsFrame) UpdateMaximum(ref _videoWriteMaxUs, ElapsedUs(videoWriteStarted));
                    if (o.Kind == Wire.KindAudio)
                    {
                        Interlocked.Increment(ref _audioWritten);
                        var writeMs = (long)((Stopwatch.GetTimestamp() -
                            writeStartedQpc) * 1000.0 / Stopwatch.Frequency);
                        if (writeMs > _audioWriteMaxMs) _audioWriteMaxMs = writeMs;
                        if (writeMs > 20) Interlocked.Increment(ref _audioWriteOver20);
                        if (writeMs > 50) Interlocked.Increment(ref _audioWriteOver50);
                    }
                    if (o.IsFrame) Interlocked.Increment(ref _framesSent);
                    Interlocked.Add(ref _wireBytesSent, packetLength);
                }
                finally
                {
                    if (o.ReleasesSlot) _fileSlots.Release();
                    if (o.IsFrame) ReleaseFrame(o);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            if (!ct.IsCancellationRequested) _log.Info("phone connection write ended: " + ex.Message);
        }
        finally
        {
            Finish();
            DrainFrames();
        }
    }
    private void ReleaseFrame(Outbound outbound)
    {
        if (outbound.Frame is not null) Interlocked.Add(ref _frameBacklogBytes, -outbound.Frame.Length);
        outbound.Frame?.DisposeFromTransport();
        Interlocked.Decrement(ref _frameBacklog);
        _videoSlots.Release();
    }
    private static long ElapsedUs(long start) => (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);
    private static void UpdateMaximum(ref long location, long sample)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref location);
            if (sample <= previous) return;
        } while (Interlocked.CompareExchange(ref location, sample, previous) != previous);
    }
    private void DrainFrames()
    {
        while (_videoQueue.Reader.TryRead(out var outbound)) ReleaseFrame(outbound);
    }
    private void Finish()
    {
        _videoQueue.Writer.TryComplete();
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
        DrainFrames();
        _chan.Dispose();
        _cts.Dispose();
    }
}

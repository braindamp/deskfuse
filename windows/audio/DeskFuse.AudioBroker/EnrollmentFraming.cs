using System.Buffers.Binary;

namespace DeskFuse.AudioBroker;

internal enum EnrollmentFrameKind : byte
{
    BeginSetup = 1, SetupLaunched = 2, ShowOffer = 3, ShowFrozen = 4, Confirm = 5, Cancel = 6, Result = 7,
    SetupChoices = 8, SelectSetup = 9, SetupLocation = 10,
    WorkerReady = 16, PublishOffer = 17, NetworkRequest = 18, NetworkReply = 19, NetworkClosed = 20, Stop = 21, NetworkReplySent = 22,
    Offer = 32, Proposal = 33, Frozen = 34, PhoneApproval = 35, Receipt = 36, StatusRequest = 37, Status = 38,
}
internal sealed record EnrollmentFrame(EnrollmentFrameKind Kind, byte[] Body);
internal interface IEnrollmentChannel
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}
internal sealed class EnrollmentStreamChannel(Stream stream) : IEnrollmentChannel
{
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadAsync(buffer, ct);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => stream.WriteAsync(buffer, ct);
}

/// <summary>Setup control only. One bounded frame allocation; direction locks keep records atomic.</summary>
internal sealed class EnrollmentFraming(IEnrollmentChannel channel)
{
    private readonly SemaphoreSlim send = new(1, 1), receive = new(1, 1);
    private int failed;
    internal async ValueTask<EnrollmentFrame> ReadAsync(CancellationToken ct)
    {
        await receive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireLive();
            var header = new byte[4]; await ReadExactly(header, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is < 1 or > EnrollmentProtocol.MaxFrame) throw new InvalidDataException("Invalid enrollment frame length.");
            var record = new byte[length]; await ReadExactly(record, ct).ConfigureAwait(false);
            var kind = (EnrollmentFrameKind)record[0];
            if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown enrollment frame kind.");
            return new(kind, record.AsSpan(1).ToArray());
        }
        catch { Interlocked.Exchange(ref failed, 1); throw; }
        finally { receive.Release(); }
    }
    internal async ValueTask WriteAsync(EnrollmentFrameKind kind, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || body.Length >= EnrollmentProtocol.MaxFrame) throw new InvalidDataException("Invalid enrollment frame.");
        // Snapshot before awaiting so callers cannot mutate an outstanding control record.
        var record = new byte[body.Length + 5]; BinaryPrimitives.WriteUInt32BigEndian(record, (uint)body.Length + 1);
        record[4] = (byte)kind; body.Span.CopyTo(record.AsSpan(5));
        await send.WaitAsync(ct).ConfigureAwait(false);
        try { RequireLive(); await channel.WriteAsync(record, ct).ConfigureAwait(false); }
        catch { Interlocked.Exchange(ref failed, 1); throw; }
        finally { send.Release(); }
    }
    private async ValueTask ReadExactly(Memory<byte> bytes, CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        { var count = await channel.ReadAsync(bytes, ct).ConfigureAwait(false); if (count <= 0) throw new EndOfStreamException("Enrollment control closed."); bytes = bytes[count..]; }
    }
    private void RequireLive() { if (Volatile.Read(ref failed) != 0) throw new IOException("Enrollment framing is terminal; fresh launch required."); }

    internal static byte[] Signed(byte[] message, byte[] signature)
    {
        if (message.Length > EnrollmentProtocol.MaxTranscript || signature.Length is < 8 or > 72) throw new InvalidDataException("Invalid signed setup record.");
        var output = new byte[message.Length + signature.Length + 3];
        BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)message.Length); message.CopyTo(output, 2);
        output[message.Length + 2] = (byte)signature.Length; signature.CopyTo(output, message.Length + 3); return output;
    }
    internal static (byte[] Message, byte[] Signature) ParseSigned(byte[] bytes)
    {
        if (bytes.Length < 3) throw new InvalidDataException("Truncated signed setup record.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (length > EnrollmentProtocol.MaxTranscript || bytes.Length < length + 3) throw new InvalidDataException("Invalid signed setup length.");
        var count = bytes[length + 2];
        if (count is < 8 or > 72 || bytes.Length != length + 3 + count) throw new InvalidDataException("Invalid setup signature envelope.");
        return (bytes.AsSpan(2, length).ToArray(), bytes.AsSpan(length + 3, count).ToArray());
    }
    internal static byte[] Network(ulong connectionId, EnrollmentFrame frame)
    {
        if (connectionId == 0 || frame.Body.Length > EnrollmentProtocol.MaxFrame - 10 ||
            !IsNetworkKind(frame.Kind)) throw new InvalidDataException("Invalid network setup envelope.");
        var bytes = new byte[frame.Body.Length + 9]; BinaryPrimitives.WriteUInt64BigEndian(bytes, connectionId);
        bytes[8] = (byte)frame.Kind; frame.Body.CopyTo(bytes, 9); return bytes;
    }
    internal static (ulong ConnectionId, EnrollmentFrame Frame) ParseNetwork(byte[] bytes)
    {
        if (bytes.Length is < 9 or >= EnrollmentProtocol.MaxFrame) throw new InvalidDataException("Invalid network setup envelope.");
        var id = BinaryPrimitives.ReadUInt64BigEndian(bytes); var kind = (EnrollmentFrameKind)bytes[8];
        if (id == 0 || !IsNetworkKind(kind)) throw new InvalidDataException("Invalid network setup role.");
        return (id, new(kind, bytes.AsSpan(9).ToArray()));
    }
    private static bool IsNetworkKind(EnrollmentFrameKind kind) => kind is EnrollmentFrameKind.Offer or EnrollmentFrameKind.Proposal or
        EnrollmentFrameKind.Frozen or EnrollmentFrameKind.PhoneApproval or EnrollmentFrameKind.Receipt or
        EnrollmentFrameKind.StatusRequest or EnrollmentFrameKind.Status or EnrollmentFrameKind.Cancel;
}

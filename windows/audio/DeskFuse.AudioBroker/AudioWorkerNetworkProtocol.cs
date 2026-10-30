using System.Buffers.Binary;

namespace DeskFuse.AudioBroker;

internal enum AudioNetworkKind : byte
{
    Info = 1, Prepare = 2, Issue = 3, Proof = 4, Status = 5, Output = 6,
    Detach = 7, Stop = 8, SignedSourceStop = 9, SourceStopStatus = 10, AppliedResult = 11,
}
internal sealed record AudioNetworkMessage(ulong Request, Guid Connection, byte Kind, byte[] Body);
internal sealed record AudioConnectionObservation(Guid Connection, byte[] Native, byte[] Phone, byte[] Nonce);

/// <summary>Bounded native pipe RPC. Identity comes from the bound pipe, never these bytes.</summary>
internal static class AudioWorkerNetworkProtocol
{
    internal const int HeaderBytes = 25, MaxBody = AudioWorkerControl.MaxFrame - 1 - HeaderBytes;
    internal static bool Negative(byte kind) => kind is (byte)AudioNetworkKind.Stop or (byte)AudioNetworkKind.SignedSourceStop;
    internal static byte[] Encode(AudioNetworkMessage value, bool reply = false)
    {
        if (value.Request == 0 || value.Connection == Guid.Empty || value.Body.Length > MaxBody ||
            (reply ? value.Kind is not (>= 129 and <= 139) and not 255 : value.Kind is < 1 or > 11)) throw Invalid();
        if (!reply) ValidateRequest(value.Kind, value.Body);
        var bytes = new byte[HeaderBytes + value.Body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value.Request);
        value.Connection.TryWriteBytes(bytes.AsSpan(8, 16), bigEndian: true, out _);
        bytes[24] = value.Kind; value.Body.CopyTo(bytes, HeaderBytes); return bytes;
    }
    internal static AudioNetworkMessage Parse(byte[] bytes, bool reply = false)
    {
        if (bytes.Length < HeaderBytes || bytes.Length >= AudioWorkerControl.MaxFrame) throw Invalid();
        var value = new AudioNetworkMessage(BinaryPrimitives.ReadUInt64BigEndian(bytes),
            new(bytes.AsSpan(8, 16), bigEndian: true), bytes[24], bytes.AsSpan(HeaderBytes).ToArray());
        _ = Encode(value, reply); return value;
    }
    internal static AudioConnectionObservation Observation(byte[] bytes)
    {
        if (bytes.Length != 112) throw Invalid();
        var value = new AudioConnectionObservation(new(bytes.AsSpan(0, 16), bigEndian: true),
            bytes.AsSpan(16, 32).ToArray(), bytes.AsSpan(48, 32).ToArray(), bytes.AsSpan(80, 32).ToArray());
        if (value.Connection == Guid.Empty || new[] { value.Native, value.Phone, value.Nonce }.Any(v => v.All(b => b == 0))) throw Invalid();
        return value;
    }
    internal static Guid Closed(byte[] bytes)
    {
        if (bytes.Length != 16) throw Invalid();
        var id = new Guid(bytes, bigEndian: true); if (id == Guid.Empty) throw Invalid(); return id;
    }
    internal static void ValidateRequest(byte kind, ReadOnlySpan<byte> b)
    {
        switch ((AudioNetworkKind)kind)
        {
            case AudioNetworkKind.Info:
                if (!(b.IsEmpty || b.Length == 1 && b[0] == 1)) throw Invalid(); break;
            case AudioNetworkKind.Prepare:
                // Delivery generation, complete N2 RequestId, actual target incarnation.
                if (b.Length != 56 || U64(b) == 0 || new Guid(b[40..], bigEndian: true) == Guid.Empty) throw Invalid();
                _ = new AudioRequestId(b.Slice(8, 32).ToArray()); break;
            case AudioNetworkKind.Issue:
                if (b.Length != 53 || b[0] is < 1 or > 4 || BinaryPrimitives.ReadUInt32BigEndian(b[33..]) is < 1 or > BrokerProofChallenges.MaxChallengeLifetimeMs)
                    throw Invalid();
                _ = new AudioRequestId(b.Slice(1, 32).ToArray());
                var noTarget = new Guid(b[37..], bigEndian: true) == Guid.Empty;
                if (noTarget != (b[0] is 1 or 3)) throw Invalid(); break;
            case AudioNetworkKind.Proof: Proof(b); break;
            case AudioNetworkKind.Status: Hash(b); break;
            case AudioNetworkKind.Output:
                if (b.Length != 49 || U64(b[32..]) == 0 || U64(b[40..]) == 0 || b[48] > 1) throw Invalid(); Hash(b[..32]); break;
            case AudioNetworkKind.Detach:
                if (b.Length != 40 || U64(b[32..]) == 0) throw Invalid(); Hash(b[..32]); break;
            case AudioNetworkKind.Stop:
                if (b.Length != 88 || new Guid(b[..16], bigEndian: true) == Guid.Empty || U64(b[16..]) == 0) throw Invalid();
                Hash(b.Slice(24, 32)); Hash(b.Slice(56, 32)); break;
            case AudioNetworkKind.SignedSourceStop: _ = AudioSourceStopProtocol.ParseSignedStop(b.ToArray()); break;
            case AudioNetworkKind.SourceStopStatus: _ = AudioSourceStopProtocol.ParseStatusQuery(b.ToArray()); break;
            case AudioNetworkKind.AppliedResult:
                if (b.Length < 73) throw Invalid(); _ = new AudioRequestId(b[..32].ToArray()); Proof(b[32..]); break;
            default: throw Invalid();
        }
    }
    internal static (byte[] Nonce, byte[] Signature) Proof(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 41 or > 105 || bytes[32] is < 8 or > 72 || bytes.Length != bytes[32] + 33) throw Invalid();
        Hash(bytes[..32]); return (bytes[..32].ToArray(), bytes[33..].ToArray());
    }
    private static ulong U64(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt64BigEndian(b);
    private static void Hash(ReadOnlySpan<byte> b) { if (b.Length != 32 || b.IndexOfAnyExcept((byte)0) < 0) throw Invalid(); }
    private static InvalidDataException Invalid() => new("Invalid native audio RPC.");
}

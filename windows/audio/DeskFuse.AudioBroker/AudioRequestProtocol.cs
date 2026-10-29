using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeskFuse.AudioBroker;

internal sealed class AudioRequestId
{
    private readonly byte[] bytes;
    internal ulong Sequence { get; }
    internal byte[] Bytes => bytes.ToArray();
    internal AudioRequestId(byte[] value)
    {
        if (value is not { Length: 32 }) throw new InvalidDataException("Invalid audio request identity.");
        bytes = value.ToArray(); Sequence = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        if (Sequence == 0 || bytes.AsSpan(8).IndexOfAnyExcept((byte)0) < 0) throw new InvalidDataException("Invalid audio request sequence/entropy.");
    }
    internal bool Matches(byte[] other) => bytes.AsSpan().SequenceEqual(other);
}

// Created only from the retained owner reservation. It is never reconstructed
// from parameters accompanying a phone signature at dispatch.
internal sealed class AudioProofBinding
{
    private readonly byte[] boot, sourceDigest, request;
    internal byte[] Boot => boot.ToArray();
    internal Guid Grant { get; }
    internal ulong Generation { get; }
    internal byte[] SourceDigest => sourceDigest.ToArray();
    internal AudioOperationKind Kind { get; }
    internal byte[] RequestId => request.ToArray();
    internal ulong Revision { get; }
    internal ulong Until { get; }
    internal AudioProofBinding(byte[] boot, Guid grant, ulong generation, byte[] sourceDigest,
        AudioOperationKind kind, byte[] request, ulong revision, ulong until)
    {
        if (boot is not { Length: 32 } || boot.All(b => b == 0) || grant == Guid.Empty || generation == 0 ||
            sourceDigest is not { Length: 32 } || sourceDigest.All(b => b == 0) || !Enum.IsDefined(kind) || until == 0)
            throw new InvalidDataException("Invalid frozen audio proof binding.");
        this.boot = boot.ToArray(); Grant = grant; Generation = generation; this.sourceDigest = sourceDigest.ToArray();
        Kind = kind; this.request = new AudioRequestId(request).Bytes; Revision = revision; Until = until;
    }
    internal byte[] Wrap(byte[] core) => AudioRequestProtocol.Proof(core, this);
}
internal sealed record AudioCoreFacts(byte[] Nonce, AudioAuthorityIdentity Identity, byte[] Resource,
    BrokerProofOperation Operation, ulong Generation, long Expiry);
internal sealed record ParsedAudioProof(byte[] Core, AudioCoreFacts Facts, AudioProofBinding Binding);
internal enum AudioRequestStage : byte { Empty, Issuing, AwaitingProof, Verifying, Applied, ReceiptSigning, Retiring }
internal sealed record AudioRequestStatus(byte[] Query, AudioAuthorityIdentity Identity, byte[] Boot, Guid Source,
    ulong SourceGeneration, byte[] SourceDigest, AudioAuthorityPhase Phase, byte[] Binding, ulong DeliveryGeneration,
    byte[] DeliveryDigest, ulong Revision, ulong Until, ulong IssuedThrough, byte[] ActiveRequest,
    AudioRequestStage Stage, ulong ProofExpiryMilliseconds, AudioOperationKind ActiveKind);

internal static class AudioRequestProtocol
{
    internal const int MaxProof = 1024;
    internal static byte[] ChallengeMessage(byte[] proof)
    {
        _ = ParseProof(proof);
        return "DeskFuse.Audio.Challenge.v1\0"u8.ToArray().Concat(SHA256.HashData(proof)).ToArray();
    }
    internal static BrokerProofOperation GenericOperation(AudioOperationKind kind) => kind switch
    {
        AudioOperationKind.AcquireSource or AudioOperationKind.AttachDelivery => BrokerProofOperation.Acquire,
        AudioOperationKind.Renew => BrokerProofOperation.Renew,
        AudioOperationKind.SupersedeDelivery => BrokerProofOperation.Supersede,
        _ => throw Invalid()
    };
    internal static byte[] Proof(byte[] core, AudioProofBinding binding)
    {
        var facts = ParseCore(core);
        if (facts.Operation != GenericOperation(binding.Kind) || (ulong)facts.Expiry > binding.Until / 10000 ||
            binding.Kind == AudioOperationKind.AcquireSource &&
            (!facts.Resource.AsSpan().SequenceEqual(binding.SourceDigest) || facts.Generation != binding.Generation)) throw Invalid();
        using var w = new Writer("Proof"); w.U16(checked((ushort)core.Length)); w.Bytes(core);
        w.Bytes(binding.Boot); w.Id(binding.Grant); w.U64(binding.Generation); w.Bytes(binding.SourceDigest);
        w.Byte((byte)binding.Kind); w.Bytes(binding.RequestId); w.U64(binding.Revision); w.U64(binding.Until);
        return w.Finish();
    }
    internal static ParsedAudioProof ParseProof(byte[] bytes)
    {
        var r = new Reader(bytes, "Proof"); var core = r.Bytes(r.U16());
        var binding = new AudioProofBinding(r.Bytes(32), r.Id(), r.U64(), r.Bytes(32), (AudioOperationKind)r.Byte(), r.Bytes(32), r.U64(), r.U64());
        r.End(); var canonical = Proof(core, binding);
        if (!canonical.AsSpan().SequenceEqual(bytes)) throw Invalid();
        return new(core, ParseCore(core), binding);
    }
    internal static AudioCoreFacts ParseCore(byte[] core)
    {
        if (core.Length is < 252 or > 384) throw Invalid();
        var r = new Reader(core, "DeskFuse.BrokerProof.v2\0", true);
        var nonce = r.Bytes(32); var host = r.Bytes(32); var ns = r.Id(); var enrollment = r.Id();
        var peer = r.Bytes(32); var resource = r.Bytes(32); var operation = (BrokerProofOperation)r.Byte();
        if ((BrokerProofScope)r.Byte() != BrokerProofScope.Speaker) throw Invalid();
        var worker = r.Id(); var user = r.Bytes(r.Byte()); var logon = r.Bytes(r.Byte());
        var authentication = r.U64(); var session = r.U32(); var process = r.U32(); var created = r.U64();
        var generation = r.U64(); var expiryValue = r.U64(); if (expiryValue > long.MaxValue) throw Invalid();
        var expiry = (long)expiryValue; r.End();
        var context = new BrokerProofContext(peer, resource, operation, BrokerProofScope.Speaker, worker, user, logon,
            authentication, session, process, created, generation, new(host, ns, enrollment, user));
        if (!context.Transcript(nonce, expiry).AsSpan().SequenceEqual(core) || host.All(b => b == 0) || generation == 0) throw Invalid();
        return new(nonce, new(host, ns, enrollment, peer, user, worker, logon, authentication, session, process, created), resource, operation, generation, expiry);
    }
    internal static byte[] Status(AudioRequestStatus s)
    {
        if (s.Identity.Host.Length != 32 || s.Identity.Host.All(b => b == 0) || s.Identity.Namespace == Guid.Empty ||
            s.Identity.Enrollment == Guid.Empty || s.Identity.Worker == Guid.Empty ||
            s.Query.Length != 32 || s.Query.All(b => b == 0) || s.Boot.Length != 32 || s.Boot.All(b => b == 0) ||
            s.Source == Guid.Empty || s.SourceGeneration == 0 || s.SourceDigest.Length != 32 || s.SourceDigest.All(b => b == 0) ||
            !Enum.IsDefined(s.Phase) || !Enum.IsDefined(s.Stage) || s.Binding.Length != 32 || s.DeliveryDigest.Length != 32 ||
            (s.DeliveryGeneration == 0) != s.DeliveryDigest.All(b => b == 0) || s.ActiveRequest.Length != 32) throw Invalid();
        if (s.Stage == AudioRequestStage.Empty)
        { if (s.ActiveRequest.Any(b => b != 0) || s.ProofExpiryMilliseconds != 0 || (byte)s.ActiveKind != 0) throw Invalid(); }
        else if (new AudioRequestId(s.ActiveRequest).Sequence > s.IssuedThrough || s.ProofExpiryMilliseconds == 0 || !Enum.IsDefined(s.ActiveKind)) throw Invalid();
        if (s.Phase == AudioAuthorityPhase.Idle && (s.Revision != 0 || s.Until != 0 || s.Binding.Any(b => b != 0) || s.DeliveryGeneration != 0) ||
            s.Phase != AudioAuthorityPhase.Idle && s.Phase != AudioAuthorityPhase.Closed && (s.Revision == 0 || s.Until == 0) ||
            s.Phase == AudioAuthorityPhase.Preparing && s.Binding.Any(b => b != 0) ||
            s.Phase == AudioAuthorityPhase.Prepared && s.Binding.All(b => b == 0) ||
            s.DeliveryGeneration != 0 && s.Binding.All(b => b == 0)) throw Invalid();
        using var w = new Writer("RequestStatus"); w.Bytes(s.Query); w.Bytes(s.Identity.Host); w.Id(s.Identity.Namespace); w.Id(s.Identity.Enrollment);
        w.Bytes(s.Boot); w.Id(s.Identity.Worker); w.Id(s.Source); w.U64(s.SourceGeneration); w.Bytes(s.SourceDigest); w.Byte((byte)s.Phase);
        w.Bytes(s.Binding); w.U64(s.DeliveryGeneration); w.Bytes(s.DeliveryDigest); w.U64(s.Revision); w.U64(s.Until); w.U64(s.IssuedThrough);
        w.Bytes(s.ActiveRequest); w.Byte((byte)s.Stage); w.U64(s.ProofExpiryMilliseconds); w.Byte((byte)s.ActiveKind); return w.Finish();
    }
    internal static AudioRequestStatus ParseStatus(byte[] bytes)
    {
        var r = new Reader(bytes, "RequestStatus"); var query = r.Bytes(32); var host = r.Bytes(32); var ns = r.Id(); var enrollment = r.Id();
        var boot = r.Bytes(32); var worker = r.Id(); var source = r.Id(); var generation = r.U64(); var digest = r.Bytes(32);
        var phase = (AudioAuthorityPhase)r.Byte(); var binding = r.Bytes(32); var deliveryGeneration = r.U64(); var delivery = r.Bytes(32);
        var revision = r.U64(); var until = r.U64(); var floor = r.U64(); var active = r.Bytes(32); var stage = (AudioRequestStage)r.Byte();
        var expiry = r.U64(); var kind = (AudioOperationKind)r.Byte(); r.End();
        // Status carries only the identity projection listed in the wire format.
        var result = new AudioRequestStatus(query, new(host, ns, enrollment, [], [], worker, [], 0, 0, 0, 0), boot,
            source, generation, digest, phase, binding, deliveryGeneration, delivery, revision, until, floor, active, stage, expiry, kind);
        if (!Status(result).AsSpan().SequenceEqual(bytes)) throw Invalid(); return result;
    }
    internal static InvalidDataException Invalid() => new("Invalid canonical audio request record.");
    private sealed class Writer : IDisposable
    {
        private readonly MemoryStream output = new(640);
        internal Writer(string kind) => output.Write(Encoding.ASCII.GetBytes($"DeskFuse.Audio.{kind}.v1\0"));
        internal void Bytes(byte[] b) => output.Write(b);
        internal void Byte(byte b) => output.WriteByte(b);
        internal void U16(ushort n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b,n); output.Write(b); }
        internal void U64(ulong n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b,n); output.Write(b); }
        internal void Id(Guid id) { if (id == Guid.Empty) throw Invalid(); Span<byte> b = stackalloc byte[16]; id.TryWriteBytes(b,true,out _); output.Write(b); }
        internal byte[] Finish() { if (output.Length > MaxProof) throw Invalid(); return output.ToArray(); }
        public void Dispose() => output.Dispose();
    }
    internal ref struct Reader
    {
        private ReadOnlySpan<byte> bytes;
        internal Reader(byte[] bytes, string domain, bool exact = false)
        {
            if (bytes.Length > MaxProof) throw Invalid(); this.bytes = bytes;
            if (!Take(Encoding.ASCII.GetByteCount(exact ? domain : $"DeskFuse.Audio.{domain}.v1\0")).SequenceEqual(
                Encoding.ASCII.GetBytes(exact ? domain : $"DeskFuse.Audio.{domain}.v1\0"))) throw Invalid();
        }
        private ReadOnlySpan<byte> Take(int n) { if (n < 0 || bytes.Length < n) throw Invalid(); var value = bytes[..n]; bytes = bytes[n..]; return value; }
        internal byte[] Bytes(int n) => Take(n).ToArray();
        internal byte Byte() => Take(1)[0];
        internal ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal Guid Id() { var id = new Guid(Take(16),true); if (id == Guid.Empty) throw Invalid(); return id; }
        internal void End() { if (!bytes.IsEmpty) throw Invalid(); }
    }
}

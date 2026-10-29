using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeskFuse.AudioBroker;

internal enum AudioSourceBackend : byte { Committed = 1, OwnedLoopback = 2 }
internal enum AudioDeliveryMode : byte { Game = 1, Interactive = 2, Streaming = 3 }
internal static class AudioDeliveryModes
{
    internal static void RequireSupported(AudioDeliveryMode mode)
    {
        if (mode != AudioDeliveryMode.Game) throw new NotSupportedException("Unsupported audio delivery mode.");
    }
}
internal enum AudioOperationKind : byte { AcquireSource = 1, AttachDelivery = 2, Renew = 3, SupersedeDelivery = 4 }
internal sealed record AudioLeasePolicy(uint DurationMilliseconds, uint ReconnectGraceMilliseconds)
{
    internal ulong Duration100ns => checked((ulong)DurationMilliseconds * 10000);
    internal ulong Grace100ns => checked((ulong)ReconnectGraceMilliseconds * 10000);
    internal void Validate()
    {
        if (DurationMilliseconds == 0 || ReconnectGraceMilliseconds > DurationMilliseconds)
            throw new InvalidDataException("Invalid explicit audio lease policy.");
    }
}
internal sealed record AudioWireContext(uint Epoch, uint Stream, uint Format)
{
    internal void Validate() { if (Epoch == 0 || Stream == 0 || Format == 0) throw new InvalidDataException("Missing source wire context."); }
}
internal sealed record AudioAuthorityIdentity(byte[] Host, Guid Namespace, Guid Enrollment, byte[] Peer, byte[] Subject,
    Guid Worker, byte[] Logon, ulong AuthenticationId, uint Session, uint Process, ulong Created);
internal sealed record AudioSourceIntent(Guid Grant, ulong Generation, Guid DeviceKey, AudioSourceBackend Backend,
    byte[] ProfileSha256, bool MinimumPeriod, AudioLeasePolicy Policy, AudioWireContext Wire);
internal sealed record AudioObservedSource(Guid DeviceKey, string Endpoint, Guid Source, uint Abi, ulong Build,
    ulong Incarnation, ulong PcmGeneration);
internal sealed record AudioDeliveryIntent(ulong Generation, byte[] NativeCertificate, byte[] PhoneCertificate,
    byte[] PhoneRequestNonce, byte[] NativeNonce);

/// <summary>Immutable canonical resource, encoded before entering authority locks.</summary>
internal sealed class AudioSourceDescriptor
{
    // Canonical Source.v1 is Game-only; mode selection never enters the media path.
    internal AudioDeliveryMode Mode => AudioDeliveryMode.Game;
    private readonly byte[] bytes, digest;
    private readonly AudioAuthorityIdentity identity;
    private readonly AudioSourceIntent intent;
    internal AudioAuthorityIdentity Identity => identity with { Host = identity.Host.ToArray(), Peer = identity.Peer.ToArray(), Subject = identity.Subject.ToArray(), Logon = identity.Logon.ToArray() };
    internal AudioSourceIntent Intent => intent with { ProfileSha256 = intent.ProfileSha256.ToArray() };
    internal byte[] Bytes => bytes.ToArray();
    internal byte[] Digest => digest.ToArray();
    internal AudioSourceDescriptor(AudioAuthorityIdentity identity, AudioSourceIntent intent)
    {
        this.identity = identity with { Host = identity.Host.ToArray(), Peer = identity.Peer.ToArray(), Subject = identity.Subject.ToArray(), Logon = identity.Logon.ToArray() };
        this.intent = intent with { ProfileSha256 = intent.ProfileSha256.ToArray() };
        bytes = AudioAuthorityProtocol.Source(this.identity, this.intent); digest = SHA256.HashData(bytes);
    }
}
internal sealed class AudioDeliveryDescriptor
{
    internal BrokerAudioOwner.Connection? Connection { get; }
    private readonly byte[] bytes, digest, binding;
    internal AudioSourceDescriptor Parent { get; }
    private readonly AudioDeliveryIntent intent;
    internal AudioDeliveryIntent Intent => intent with { NativeCertificate = intent.NativeCertificate.ToArray(), PhoneCertificate = intent.PhoneCertificate.ToArray(),
        PhoneRequestNonce = intent.PhoneRequestNonce.ToArray(), NativeNonce = intent.NativeNonce.ToArray() };
    internal byte[] Bytes => bytes.ToArray();
    internal byte[] Digest => digest.ToArray();
    internal byte[] BindingDigest => binding.ToArray();
    internal AudioDeliveryDescriptor(AudioSourceDescriptor source, byte[] bindingDigest, AudioDeliveryIntent intent, BrokerAudioOwner.Connection? connection = null)
    {
        Connection = connection;
        Parent = source; binding = bindingDigest.ToArray();
        this.intent = intent with { NativeCertificate = intent.NativeCertificate.ToArray(), PhoneCertificate = intent.PhoneCertificate.ToArray(),
            PhoneRequestNonce = intent.PhoneRequestNonce.ToArray(), NativeNonce = intent.NativeNonce.ToArray() };
        bytes = AudioAuthorityProtocol.Delivery(source, binding, this.intent); digest = SHA256.HashData(bytes);
    }
}
internal sealed record AudioGrantReceipt(AudioOperationKind Operation, byte[] Host, Guid Namespace, Guid Enrollment,
    byte[] Boot, Guid Worker, Guid SourceGrant, ulong SourceGeneration, byte[] SourceDigest, byte[] DeliveryDigest,
    ulong DeliveryGeneration, ulong Revision, ulong AuthorityUntil, byte[] ProofSha256, byte[] RequestNonce, ulong Remaining100ns);
internal sealed record AudioControlGrant(byte[] Host, Guid Namespace, Guid Enrollment, byte[] Boot, Guid Worker,
    Guid SourceGrant, ulong SourceGeneration, byte[] SourceDigest, byte[] BindingDigest, ulong DeliveryGeneration,
    byte[] DeliveryDigest, ulong Revision, ulong AuthorityUntil, ulong OutputGeneration, bool OutputReady, bool Closed);

internal static class AudioAuthorityProtocol
{
    internal const int MaxDescriptor = 2048;
    internal const string Alpn = "df-audio-authority-2";
    internal static byte[] Source(AudioAuthorityIdentity identity, AudioSourceIntent intent)
    {
        intent.Policy.Validate(); intent.Wire.Validate();
        if (!Enum.IsDefined(intent.Backend)) throw Invalid();
        using var w = new Writer("Source");
        w.Hash(identity.Host); w.Id(identity.Namespace); w.Id(identity.Enrollment); w.Hash(identity.Peer); w.Sid(identity.Subject);
        w.Id(identity.Worker); w.Sid(identity.Logon, true); w.U64(identity.AuthenticationId); w.U32(identity.Session); w.U32(identity.Process); w.Nonzero(identity.Created);
        if (identity.Session == 0 || identity.Process == 0) throw Invalid();
        w.Id(intent.Grant); w.Nonzero(intent.Generation); w.Id(intent.DeviceKey); w.Byte((byte)intent.Backend); w.Hash(intent.ProfileSha256);
        // Current product source format; this does not authorize a physical/default endpoint.
        w.U32(48000); w.U16(2); w.U16(16); w.U16(240); w.Byte(intent.MinimumPeriod ? (byte)1 : (byte)0);
        w.U32(intent.Policy.DurationMilliseconds); w.U32(intent.Policy.ReconnectGraceMilliseconds);
        w.Wire(intent.Wire); return w.Finish();
    }
    internal static byte[] Binding(AudioSourceDescriptor source, AudioObservedSource observed)
    {
        if (observed.DeviceKey != source.Intent.DeviceKey || observed.Abi == 0 || observed.Build == 0 ||
            source.Intent.Backend == AudioSourceBackend.Committed && observed.PcmGeneration == 0 ||
            source.Intent.Backend == AudioSourceBackend.OwnedLoopback && observed.PcmGeneration != 0) throw Invalid();
        using var w = new Writer("Binding"); w.Hash(source.Digest); w.Id(observed.DeviceKey); w.Text(observed.Endpoint, 1024);
        w.Id(observed.Source); w.U32(observed.Abi); w.Nonzero(observed.Build); w.Nonzero(observed.Incarnation); w.U64(observed.PcmGeneration); return w.Finish();
    }
    internal static byte[] Delivery(AudioSourceDescriptor source, byte[] binding, AudioDeliveryIntent intent)
    {
        using var w = new Writer("Delivery"); w.Id(source.Intent.Grant); w.Hash(source.Digest); w.Nonzero(source.Intent.Generation);
        w.Hash(binding); w.Id(source.Identity.Worker); w.Nonzero(intent.Generation); w.Wire(source.Intent.Wire);
        w.Hash(intent.NativeCertificate); w.Hash(intent.PhoneCertificate); w.Hash(intent.PhoneRequestNonce); w.Hash(intent.NativeNonce);
        w.Text(Alpn, 64); w.Byte(1); // Destination policy 1: this exact observed/approved TLS connection only.
        return w.Finish();
    }
    internal static byte[] Receipt(AudioGrantReceipt receipt)
    {
        if (!Enum.IsDefined(receipt.Operation) || receipt.Remaining100ns == 0 || receipt.Remaining100ns > receipt.AuthorityUntil ||
            (receipt.DeliveryGeneration == 0) != (receipt.Operation == AudioOperationKind.AcquireSource)) throw Invalid();
        using var w = new Writer("GrantReceipt"); w.Byte((byte)receipt.Operation); w.Hash(receipt.Host); w.Id(receipt.Namespace); w.Id(receipt.Enrollment);
        w.Hash(receipt.Boot); w.Id(receipt.Worker); w.Id(receipt.SourceGrant); w.Nonzero(receipt.SourceGeneration); w.Hash(receipt.SourceDigest);
        w.Bytes(receipt.DeliveryDigest, 32);
        if (receipt.DeliveryGeneration == 0 && receipt.DeliveryDigest.Any(b => b != 0) || receipt.DeliveryGeneration != 0 && receipt.DeliveryDigest.All(b => b == 0)) throw Invalid();
        w.U64(receipt.DeliveryGeneration); w.Nonzero(receipt.Revision); w.Nonzero(receipt.AuthorityUntil);
        w.Hash(receipt.ProofSha256); w.Hash(receipt.RequestNonce); w.Nonzero(receipt.Remaining100ns); return w.Finish();
    }
    internal static AudioGrantReceipt ParseReceipt(byte[] bytes)
    {
        var r = new Reader(bytes, "GrantReceipt");
        var result = new AudioGrantReceipt((AudioOperationKind)r.Byte(), r.Bytes(32), r.Id(), r.Id(), r.Bytes(32), r.Id(), r.Id(),
            r.U64(), r.Bytes(32), r.Bytes(32), r.U64(), r.U64(), r.U64(), r.Bytes(32), r.Bytes(32), r.U64());
        r.End(); if (!Receipt(result).AsSpan().SequenceEqual(bytes)) throw Invalid(); return result;
    }
    internal static byte[] Control(AudioControlGrant grant)
    {
        if (grant.OutputReady && (grant.Closed || grant.DeliveryGeneration == 0 || grant.OutputGeneration == 0) ||
            !grant.Closed && grant.AuthorityUntil == 0 ||
            (grant.DeliveryGeneration == 0) != grant.DeliveryDigest.All(b => b == 0) ||
            grant.DeliveryGeneration != 0 && grant.BindingDigest.All(b => b == 0)) throw Invalid();
        using var w = new Writer("Control"); w.Hash(grant.Host); w.Id(grant.Namespace); w.Id(grant.Enrollment); w.Hash(grant.Boot);
        w.Id(grant.Worker); w.Id(grant.SourceGrant); w.Nonzero(grant.SourceGeneration); w.Hash(grant.SourceDigest); w.Bytes(grant.BindingDigest, 32);
        w.U64(grant.DeliveryGeneration); w.Bytes(grant.DeliveryDigest, 32); w.Nonzero(grant.Revision); w.U64(grant.AuthorityUntil);
        w.U64(grant.OutputGeneration); w.Byte(grant.OutputReady ? (byte)1 : (byte)0); w.Byte(grant.Closed ? (byte)1 : (byte)0); return w.Finish();
    }
    internal static AudioControlGrant ParseControl(byte[] bytes)
    {
        var r = new Reader(bytes, "Control");
        var result = new AudioControlGrant(r.Bytes(32), r.Id(), r.Id(), r.Bytes(32), r.Id(), r.Id(), r.U64(), r.Bytes(32), r.Bytes(32),
            r.U64(), r.Bytes(32), r.U64(), r.U64(), r.U64(), r.Bool(), r.Bool());
        r.End(); if (!Control(result).AsSpan().SequenceEqual(bytes)) throw Invalid(); return result;
    }
    private static InvalidDataException Invalid() => new("Invalid canonical audio authority record.");
    private static byte[] Domain(string kind) => Encoding.ASCII.GetBytes($"DeskFuse.Audio.{kind}.v1\0");
    private sealed class Writer : IDisposable
    {
        private readonly MemoryStream output = new(512);
        internal Writer(string kind) => output.Write(Domain(kind));
        internal void Byte(byte value) => output.WriteByte(value);
        internal void Bytes(byte[] value, int count) { if (value.Length != count) throw Invalid(); output.Write(value); }
        internal void Hash(byte[] value) { if (value.All(b => b == 0)) throw Invalid(); Bytes(value, 32); }
        internal void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); output.Write(b); }
        internal void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); output.Write(b); }
        internal void U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); output.Write(b); }
        internal void Nonzero(ulong value) { if (value == 0) throw Invalid(); U64(value); }
        internal void Id(Guid value) { if (value == Guid.Empty) throw Invalid(); Span<byte> b = stackalloc byte[16]; value.TryWriteBytes(b, true, out _); output.Write(b); }
        internal void Sid(byte[] value, bool logon = false) { EnrollmentProtocol.ValidateSid(value, logon); Byte((byte)value.Length); output.Write(value); }
        internal void Text(string value, int maximum)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length is < 1 || bytes.Length > maximum || value.Any(char.IsControl)) throw Invalid();
            U16(checked((ushort)bytes.Length)); output.Write(bytes);
        }
        internal void Wire(AudioWireContext context) { context.Validate(); U32(context.Epoch); U32(context.Stream); U32(context.Format); }
        internal byte[] Finish() { if (output.Length > MaxDescriptor) throw Invalid(); return output.ToArray(); }
        public void Dispose() => output.Dispose();
    }
    private ref struct Reader
    {
        private ReadOnlySpan<byte> remaining;
        internal Reader(byte[] bytes, string kind)
        {
            if (bytes.Length > MaxDescriptor) throw Invalid(); remaining = bytes;
            var domain = Domain(kind); if (!Take(domain.Length).SequenceEqual(domain)) throw Invalid();
        }
        private ReadOnlySpan<byte> Take(int count) { if (count < 0 || remaining.Length < count) throw Invalid(); var b = remaining[..count]; remaining = remaining[count..]; return b; }
        internal byte Byte() => Take(1)[0];
        internal bool Bool() => Byte() switch { 0 => false, 1 => true, _ => throw Invalid() };
        internal byte[] Bytes(int count) => Take(count).ToArray();
        internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal Guid Id() => new(Take(16), true);
        internal void End() { if (!remaining.IsEmpty) throw Invalid(); }
    }
}

using System.Buffers.Binary;
using System.Text;

namespace DeskFuse.AudioBroker;

internal enum AudioSourceStopState : byte { Unknown = 0, Denied = 1, RundownComplete = 2, Quarantined = 3 }
internal sealed record AudioStopTarget(byte[] Host, Guid Namespace, Guid Enrollment, byte[] Boot, Guid Worker,
    Guid SourceGrant, ulong SourceGeneration, byte[] SourceDigest, Guid DeviceKey);
internal sealed record AudioSourceStop(AudioStopTarget Target, byte[] Intent, Guid Connection,
    byte[] NativeCertificate, byte[] PhoneCertificate, byte[] NativeNonce);
internal sealed record AudioSourceStopStatus(byte[] Query, AudioStopTarget Target, ulong Revision, AudioSourceStopState State);

/// <summary>Negative-only setup/control records; never a grant or runtime receipt.</summary>
internal static class AudioSourceStopProtocol
{
    internal const int MaxRecord = 1024;
    internal static AudioStopTarget Target(AudioSourceDescriptor source, byte[] boot)
    {
        var id = source.Identity; var intent = source.Intent;
        return new(id.Host, id.Namespace, id.Enrollment, boot.ToArray(), id.Worker, intent.Grant,
            intent.Generation, source.Digest, intent.DeviceKey);
    }
    internal static byte[] Stop(AudioSourceStop value)
    {
        using var w = new Writer("SourceStop"); w.Target(value.Target); w.Hash(value.Intent); w.Id(value.Connection);
        w.Hash(value.NativeCertificate); w.Hash(value.PhoneCertificate); w.Hash(value.NativeNonce); return w.Finish();
    }
    internal static AudioSourceStop ParseStop(byte[] bytes)
    {
        var r = new Reader(bytes, "SourceStop"); var value = new AudioSourceStop(r.Target(), r.Hash(), r.Id(), r.Hash(), r.Hash(), r.Hash());
        r.End(); _ = Stop(value); return value;
    }
    internal static byte[] Status(AudioSourceStopStatus value)
    {
        if (!Enum.IsDefined(value.State) || (value.Revision == 0) != (value.State == AudioSourceStopState.Unknown)) throw Invalid();
        using var w = new Writer("SourceStopStatus"); w.Hash(value.Query); w.Target(value.Target); w.U64(value.Revision); w.Byte((byte)value.State); return w.Finish();
    }
    internal static AudioSourceStopStatus ParseStatus(byte[] bytes)
    {
        var r = new Reader(bytes, "SourceStopStatus"); var query = r.Hash(); var target = r.Target();
        var value = new AudioSourceStopStatus(query, target, r.U64(), (AudioSourceStopState)r.Byte()); r.End(); _ = Status(value); return value;
    }
    internal static byte[] StatusQuery(byte[] query, AudioStopTarget target)
    { using var w = new Writer(null); w.Hash(query); w.Target(target); return w.Finish(); }
    internal static (byte[] Query, AudioStopTarget Target) ParseStatusQuery(byte[] bytes)
    { var r = new Reader(bytes, null); var value = (r.Hash(), r.Target()); r.End(); return value; }
    internal static byte[] SignedStop(byte[] transcript, byte[] signature)
    {
        _ = ParseStop(transcript); if (signature is not { Length: >= 8 and <= 72 }) throw Invalid();
        var bytes = new byte[3 + transcript.Length + signature.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)transcript.Length)); transcript.CopyTo(bytes, 2);
        bytes[2 + transcript.Length] = (byte)signature.Length; signature.CopyTo(bytes, 3 + transcript.Length); return bytes;
    }
    internal static (byte[] Transcript, byte[] Signature) ParseSignedStop(byte[] bytes)
    {
        if (bytes.Length < 3 || bytes.Length > MaxRecord + 75) throw Invalid();
        var n = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (n > MaxRecord || n + 3 > bytes.Length) throw Invalid();
        var count = bytes[n + 2]; if (count is < 8 or > 72 || bytes.Length != n + count + 3) throw Invalid();
        var transcript = bytes.AsSpan(2, n).ToArray(); _ = ParseStop(transcript);
        return (transcript, bytes.AsSpan(n + 3, count).ToArray());
    }
    internal static bool SameTarget(AudioStopTarget a, AudioStopTarget b)
    {
        using var wa = new Writer("SourceStop"); wa.Target(a);
        using var wb = new Writer("SourceStop"); wb.Target(b);
        return wa.Finish().AsSpan().SequenceEqual(wb.Finish());
    }
    private static InvalidDataException Invalid() => new("Invalid canonical audio Stop record.");
    private static byte[] Domain(string kind) => Encoding.ASCII.GetBytes($"DeskFuse.Audio.{kind}.v1\0");
    private sealed class Writer(string? kind) : IDisposable
    {
        private readonly MemoryStream output = Init(kind);
        private static MemoryStream Init(string? kind) { var s = new MemoryStream(512); if (kind is not null) s.Write(Domain(kind)); return s; }
        internal void Byte(byte b) => output.WriteByte(b);
        internal void Hash(byte[] b) { if (b is not { Length: 32 } || b.All(v => v == 0)) throw Invalid(); output.Write(b); }
        internal void Id(Guid id) { if (id == Guid.Empty) throw Invalid(); Span<byte> b = stackalloc byte[16]; id.TryWriteBytes(b, true, out _); output.Write(b); }
        internal void U64(ulong n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, n); output.Write(b); }
        internal void Target(AudioStopTarget t)
        {
            Hash(t.Host); Id(t.Namespace); Id(t.Enrollment); Byte((byte)BrokerProofScope.Speaker); Hash(t.Boot); Id(t.Worker);
            Id(t.SourceGrant); if (t.SourceGeneration == 0) throw Invalid(); U64(t.SourceGeneration); Hash(t.SourceDigest); Id(t.DeviceKey);
        }
        internal byte[] Finish() { if (output.Length > MaxRecord) throw Invalid(); return output.ToArray(); }
        public void Dispose() => output.Dispose();
    }
    private ref struct Reader
    {
        private ReadOnlySpan<byte> remaining;
        internal Reader(byte[] bytes, string? kind)
        {
            if (bytes.Length > MaxRecord) throw Invalid(); remaining = bytes;
            if (kind is not null) { var domain = Domain(kind); if (!Take(domain.Length).SequenceEqual(domain)) throw Invalid(); }
        }
        private ReadOnlySpan<byte> Take(int n) { if (n < 0 || remaining.Length < n) throw Invalid(); var b = remaining[..n]; remaining = remaining[n..]; return b; }
        internal byte Byte() => Take(1)[0];
        internal byte[] Hash() { var b = Take(32).ToArray(); if (b.All(v => v == 0)) throw Invalid(); return b; }
        internal Guid Id() { var id = new Guid(Take(16), true); if (id == Guid.Empty) throw Invalid(); return id; }
        internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal AudioStopTarget Target()
        {
            var host = Hash(); var ns = Id(); var enrollment = Id(); if (Byte() != (byte)BrokerProofScope.Speaker) throw Invalid();
            var value = new AudioStopTarget(host, ns, enrollment, Hash(), Id(), Id(), U64(), Hash(), Id());
            if (value.SourceGeneration == 0) throw Invalid(); return value;
        }
        internal void End() { if (!remaining.IsEmpty) throw Invalid(); }
    }
}

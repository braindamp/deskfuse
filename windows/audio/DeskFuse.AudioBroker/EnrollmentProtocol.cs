using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeskFuse.AudioBroker;

internal enum EnrollmentAction : byte { New = 1, Replace = 2 }
internal enum EnrollmentOutcome : byte { Committed = 1, Pending = 2, Aborted = 3, Unknown = 4, Revoked = 5 }

internal sealed record EnrollmentOffer(byte[] HostSpki, Guid AuthorityNamespace, Guid TicketId, Guid EnrollmentId,
    byte[] PeerIdentity, EnrollmentAction Action, Guid OldEnrollmentId, byte[] BootNonce, byte[] HostNonce,
    Guid WorkerRegistration, byte[] TlsLeafSha256, byte[] SubjectUserSid, byte[] SubjectLogonSid,
    ulong LogonAuthenticationId, uint SessionId, byte[] ApproverUserSid, Guid UiRegistration,
    uint UiProcessId, ulong UiCreationFileTime, BrokerProofScope[] Scopes, ulong ExpiresAtMilliseconds);
internal sealed record EnrollmentProposal(byte[] OfferSha256, byte[] PhoneSpki, byte[] PhoneNonce, BrokerProofScope[] Scopes);
internal sealed record EnrollmentTranscript(EnrollmentOffer Offer, EnrollmentProposal Proposal, byte[] FreezeNonce);
internal sealed record EnrollmentBinding(byte[] HostSha256, Guid AuthorityNamespace, Guid TicketId, Guid EnrollmentId, byte[] TranscriptSha256);
internal sealed record EnrollmentReceipt(EnrollmentBinding Binding, byte[] PeerIdentity, byte[] SubjectUserSid,
    BrokerProofScope[] Scopes, byte[] PhoneSpkiSha256, byte[] RecordSha256);
internal sealed record EnrollmentStatus(EnrollmentBinding Binding, byte[] RequestNonce, EnrollmentOutcome Outcome);
internal sealed record EnrollmentStatusRequest(EnrollmentBinding Binding, byte[] RequestNonce, byte[] PhoneSpki);

/// <summary>Bounded canonical setup messages. These values carry no local approval authority.</summary>
internal static class EnrollmentProtocol
{
    internal const int MaxFrame = 8192, MaxTranscript = 2048;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    internal static byte[] Offer(EnrollmentOffer value)
    {
        using var w = new Writer("Offer");
        w.Spki(value.HostSpki); w.Id(value.AuthorityNamespace); w.Id(value.TicketId); w.Id(value.EnrollmentId);
        w.Bytes(value.PeerIdentity, 32);
        if (!Enum.IsDefined(value.Action) || (value.Action == EnrollmentAction.New) != (value.OldEnrollmentId == Guid.Empty))
            throw Invalid();
        w.Byte((byte)value.Action); w.Id(value.OldEnrollmentId, allowEmpty: true);
        w.Bytes(value.BootNonce, 32); w.Bytes(value.HostNonce, 32); w.Id(value.WorkerRegistration); w.Bytes(value.TlsLeafSha256, 32);
        w.Sid(value.SubjectUserSid); w.Sid(value.SubjectLogonSid, logon: true);
        w.U64(value.LogonAuthenticationId); w.U32(value.SessionId);
        w.Sid(value.ApproverUserSid); w.Id(value.UiRegistration); w.U32(value.UiProcessId); w.U64(value.UiCreationFileTime);
        if (value.SessionId == 0 || value.UiProcessId == 0 || value.UiCreationFileTime == 0) throw Invalid();
        w.Scopes(value.Scopes); w.U64(value.ExpiresAtMilliseconds);
        return w.Finish();
    }

    internal static EnrollmentOffer ParseOffer(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes, "Offer");
        var value = new EnrollmentOffer(r.Bytes(91), r.Id(), r.Id(), r.Id(), r.Bytes(32), (EnrollmentAction)r.Byte(),
            r.Id(true), r.Bytes(32), r.Bytes(32), r.Id(), r.Bytes(32), r.Sid(), r.Sid(true), r.U64(), r.U32(),
            r.Sid(), r.Id(), r.U32(), r.U64(), r.Scopes(), r.U64());
        r.End(); RequireCanonical(bytes, Offer(value)); return value;
    }

    internal static byte[] Proposal(EnrollmentProposal value)
    {
        using var w = new Writer("Proposal");
        w.Bytes(value.OfferSha256, 32); w.Spki(value.PhoneSpki); w.Bytes(value.PhoneNonce, 32); w.Scopes(value.Scopes);
        return w.Finish();
    }

    internal static EnrollmentProposal ParseProposal(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes, "Proposal");
        var value = new EnrollmentProposal(r.Bytes(32), r.Bytes(91), r.Bytes(32), r.Scopes());
        r.End(); RequireCanonical(bytes, Proposal(value)); return value;
    }

    internal static byte[] Transcript(EnrollmentTranscript value)
    {
        var offer = Offer(value.Offer); var proposal = Proposal(value.Proposal);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(offer), value.Proposal.OfferSha256) ||
            value.Proposal.Scopes.Any(scope => !value.Offer.Scopes.Contains(scope))) throw Invalid();
        using var w = new Writer("Transcript");
        w.Blob(offer); w.Blob(proposal); w.Bytes(value.FreezeNonce, 32);
        var bytes = w.Finish(); if (bytes.Length > MaxTranscript) throw Invalid(); return bytes;
    }

    internal static EnrollmentTranscript ParseTranscript(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxTranscript) throw Invalid();
        var r = new Reader(bytes, "Transcript");
        var value = new EnrollmentTranscript(ParseOffer(r.Blob()), ParseProposal(r.Blob()), r.Bytes(32));
        r.End(); RequireCanonical(bytes, Transcript(value)); return value;
    }

    internal static byte[] Frozen(byte[] hash)
    { using var w = new Writer("Frozen"); w.Bytes(hash, 32); return w.Finish(); }

    internal static EnrollmentBinding Binding(EnrollmentTranscript transcript)
        => new(SHA256.HashData(transcript.Offer.HostSpki), transcript.Offer.AuthorityNamespace, transcript.Offer.TicketId,
            transcript.Offer.EnrollmentId, SHA256.HashData(Transcript(transcript)));

    internal static byte[] PhoneApproval(EnrollmentBinding value)
    { using var w = new Writer("PhoneApproval"); w.Binding(value); return w.Finish(); }

    internal static EnrollmentBinding ParsePhoneApproval(ReadOnlySpan<byte> bytes)
    { var r = new Reader(bytes, "PhoneApproval"); var value = r.Binding(); r.End(); return value; }

    internal static byte[] Receipt(EnrollmentReceipt value)
    {
        using var w = new Writer("Committed");
        // Receipt order places the transcript hash after the immutable consent/key fields.
        w.Identity(value.Binding); w.Bytes(value.PeerIdentity, 32); w.Sid(value.SubjectUserSid); w.Scopes(value.Scopes);
        w.Bytes(value.PhoneSpkiSha256, 32); w.Bytes(value.Binding.TranscriptSha256, 32); w.Bytes(value.RecordSha256, 32);
        return w.Finish();
    }

    internal static EnrollmentReceipt ParseReceipt(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes, "Committed");
        var host = r.Bytes(32); var ns = r.Id(); var ticket = r.Id(); var enrollment = r.Id();
        var peer = r.Bytes(32); var subject = r.Sid(); var scopes = r.Scopes(); var phone = r.Bytes(32);
        var value = new EnrollmentReceipt(new(host, ns, ticket, enrollment, r.Bytes(32)), peer, subject, scopes, phone, r.Bytes(32));
        r.End(); RequireCanonical(bytes, Receipt(value)); return value;
    }

    internal static byte[] Status(EnrollmentStatus value)
    {
        if (!Enum.IsDefined(value.Outcome)) throw Invalid();
        using var w = new Writer("Status"); w.Binding(value.Binding); w.Bytes(value.RequestNonce, 32); w.Byte((byte)value.Outcome);
        return w.Finish();
    }

    internal static EnrollmentStatus ParseStatus(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes, "Status"); var value = new EnrollmentStatus(r.Binding(), r.Bytes(32), (EnrollmentOutcome)r.Byte());
        r.End(); RequireCanonical(bytes, Status(value)); return value;
    }
    internal static byte[] StatusRequest(EnrollmentStatusRequest value)
    { using var w = new Writer("StatusRequest"); w.Binding(value.Binding); w.Bytes(value.RequestNonce, 32); w.Spki(value.PhoneSpki); return w.Finish(); }
    internal static EnrollmentStatusRequest ParseStatusRequest(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes, "StatusRequest"); var value = new EnrollmentStatusRequest(r.Binding(), r.Bytes(32), r.Bytes(91));
        r.End(); RequireCanonical(bytes, StatusRequest(value)); return value;
    }

    internal static string VerificationQr(Guid ticket, byte[] hash)
    {
        if (ticket == Guid.Empty || hash is not { Length: 32 }) throw Invalid();
        return $"DESKFUSE-ENROLL-VERIFY-V1:{ticket:N}:{Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    internal static string VerificationText(byte[] hash)
    {
        if (hash is not { Length: 32 }) throw Invalid();
        Span<char> output = stackalloc char[52];
        for (var symbol = 0; symbol < 52; symbol++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++)
            { var position = symbol * 5 + bit; value = (value << 1) | (position < 256 ? (hash[position / 8] >> (7 - position % 8)) & 1 : 0); }
            output[symbol] = Alphabet[value];
        }
        return new string(output);
    }

    internal static byte[] ParseVerificationText(string text)
    {
        if (text is null || text.Length > 128) throw Invalid();
        var output = new byte[32]; var symbol = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-') continue;
            var value = Alphabet.IndexOf(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
            if (value < 0 || symbol >= 52 || (symbol == 51 && (value & 15) != 0)) throw Invalid();
            for (var bit = 0; bit < 5; bit++)
            { var position = symbol * 5 + bit; if (position < 256) output[position / 8] |= (byte)(((value >> (4 - bit)) & 1) << (7 - position % 8)); }
            symbol++;
        }
        if (symbol != 52) throw Invalid(); return output;
    }

    internal static void ValidateSpki(byte[] spki)
    {
        if (spki is not { Length: 91 }) throw Invalid();
        try
        {
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != 91 || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki)) throw Invalid();
        }
        catch (CryptographicException error) { throw new InvalidDataException("Invalid enrollment public key.", error); }
    }

    internal static void ValidateSid(byte[] sid, bool logon = false)
    {
        if (sid is not { Length: >= 8 and <= 68 } || sid[0] != 1 || sid[1] > 15 || sid.Length != 8 + 4 * sid[1] ||
            (logon && (sid.Length != 20 || !sid.AsSpan(2, 6).SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 5 }) ||
                BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan(8, 4)) != 5))) throw Invalid();
    }

    internal static bool Verify(byte[] spki, byte[] bytes, byte[] signature)
    {
        if (bytes is null || bytes.Length > MaxFrame || signature is not { Length: >= 8 and <= 72 }) return false;
        try
        {
            ValidateSpki(spki); using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException) { return false; }
    }

    private static void RequireCanonical(ReadOnlySpan<byte> actual, byte[] expected)
    { if (!actual.SequenceEqual(expected)) throw Invalid(); }
    private static InvalidDataException Invalid() => new("Invalid canonical enrollment message.");
    private static byte[] Domain(string kind) => Encoding.ASCII.GetBytes($"DeskFuse.Enrollment.{kind}.v1\0");

    private sealed class Writer : IDisposable
    {
        private readonly MemoryStream stream = new(512);
        internal Writer(string kind) => stream.Write(Domain(kind));
        internal void Byte(byte value) => stream.WriteByte(value);
        internal void Bytes(byte[] value, int count)
        { if (value is null || value.Length != count) throw Invalid(); stream.Write(value); }
        internal void U32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
        internal void U64(ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); stream.Write(bytes); }
        internal void Id(Guid value, bool allowEmpty = false)
        { if (!allowEmpty && value == Guid.Empty) throw Invalid(); Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, true, out _); stream.Write(bytes); }
        internal void Spki(byte[] value) { ValidateSpki(value); Bytes(value, 91); }
        internal void Sid(byte[] value, bool logon = false) { ValidateSid(value, logon); Byte((byte)value.Length); stream.Write(value); }
        internal void Scopes(BrokerProofScope[] scopes)
        {
            if (scopes is not { Length: >= 1 and <= 8 }) throw Invalid(); Byte((byte)scopes.Length); byte previous = 0;
            foreach (var scope in scopes) { if (!Enum.IsDefined(scope) || (byte)scope <= previous) throw Invalid(); Byte(previous = (byte)scope); }
        }
        internal void Blob(byte[] bytes)
        { if (bytes.Length > MaxTranscript) throw Invalid(); Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length); stream.Write(length); stream.Write(bytes); }
        internal void Identity(EnrollmentBinding value)
        { Bytes(value.HostSha256, 32); Id(value.AuthorityNamespace); Id(value.TicketId); Id(value.EnrollmentId); }
        internal void Binding(EnrollmentBinding value) { Identity(value); Bytes(value.TranscriptSha256, 32); }
        internal byte[] Finish() { if (stream.Length > MaxFrame) throw Invalid(); return stream.ToArray(); }
        public void Dispose() => stream.Dispose();
    }

    private ref struct Reader
    {
        private ReadOnlySpan<byte> remaining;
        internal Reader(ReadOnlySpan<byte> bytes, string kind)
        {
            if (bytes.Length > MaxFrame) throw Invalid(); remaining = bytes;
            var domain = Domain(kind); if (!Take(domain.Length).SequenceEqual(domain)) throw Invalid();
        }
        private ReadOnlySpan<byte> Take(int count)
        { if (count < 0 || remaining.Length < count) throw Invalid(); var result = remaining[..count]; remaining = remaining[count..]; return result; }
        internal byte Byte() => Take(1)[0];
        internal byte[] Bytes(int count) => Take(count).ToArray();
        internal uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal Guid Id(bool allowEmpty = false)
        { var value = new Guid(Take(16), true); if (!allowEmpty && value == Guid.Empty) throw Invalid(); return value; }
        internal byte[] Sid(bool logon = false) { var value = Bytes(Byte()); ValidateSid(value, logon); return value; }
        internal BrokerProofScope[] Scopes()
        {
            var count = Byte(); if (count is < 1 or > 8) throw Invalid(); var scopes = new BrokerProofScope[count]; byte previous = 0;
            for (var i = 0; i < count; i++) { var value = Byte(); if (value <= previous || value > 8) throw Invalid(); scopes[i] = (BrokerProofScope)(previous = value); }
            return scopes;
        }
        internal byte[] Blob() { var length = BinaryPrimitives.ReadUInt16BigEndian(Take(2)); if (length > MaxTranscript) throw Invalid(); return Bytes(length); }
        internal EnrollmentBinding Binding() => new(Bytes(32), Id(), Id(), Id(), Bytes(32));
        internal void End() { if (!remaining.IsEmpty) throw Invalid(); }
    }
}

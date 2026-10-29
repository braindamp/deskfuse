using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DeskFuse.AudioBroker;

internal sealed class BrokerProofAuthorityIdentity
{
    private readonly byte[] host, subject;
    internal Guid NamespaceId { get; }
    internal Guid EnrollmentId { get; }
    internal BrokerProofAuthorityIdentity(byte[] host, Guid namespaceId, Guid enrollmentId, byte[] subjectUserSid)
    {
        if (host is not { Length: 32 } || namespaceId == Guid.Empty || enrollmentId == Guid.Empty)
            throw new InvalidDataException("Missing v2 authority identity.");
        EnrollmentProtocol.ValidateSid(subjectUserSid);
        this.host = host.ToArray(); subject = subjectUserSid.ToArray(); NamespaceId = namespaceId; EnrollmentId = enrollmentId;
    }
    internal bool MatchesSubject(ReadOnlySpan<byte> sid) => CryptographicOperations.FixedTimeEquals(subject, sid);
    internal void WriteTo(Stream stream)
    {
        stream.Write(host); Span<byte> id = stackalloc byte[16];
        NamespaceId.TryWriteBytes(id, true, out _); stream.Write(id);
        EnrollmentId.TryWriteBytes(id, true, out _); stream.Write(id);
    }
}

/// <summary>
/// Broker-derived facts, not claims from a pipe request. The host registration,
/// SID/logon/session and process incarnation must have already been verified by
/// the protected broker before constructing this context.
/// </summary>
internal sealed class BrokerProofContext
{
    private readonly byte[] peer, resource, userSid, logonSid;
    private readonly BrokerProofAuthorityIdentity? authorityIdentity;
    internal BrokerProofOperation Operation { get; }
    internal BrokerProofScope Scope { get; }
    internal Guid Registration { get; }
    internal ulong LogonAuthenticationId { get; }
    internal uint SessionId { get; }
    internal uint ProcessId { get; }
    internal ulong ProcessCreationFileTime { get; }
    internal ulong ExpectedOwnerGeneration { get; }
    internal bool IsAuthorityBound => authorityIdentity is not null;
    internal bool MatchesResource(ReadOnlySpan<byte> value) => CryptographicOperations.FixedTimeEquals(resource, value);

    internal BrokerProofContext(byte[] peer, byte[] resource, BrokerProofOperation operation, BrokerProofScope scope,
        Guid registration, byte[] userSid, byte[] logonSid, ulong logonAuthenticationId, uint sessionId,
        uint processId, ulong processCreationFileTime, ulong expectedOwnerGeneration,
        BrokerProofAuthorityIdentity? authorityIdentity = null)
    {
        if (peer is not { Length: 32 } || resource is not { Length: 32 } || registration == Guid.Empty ||
            !Enum.IsDefined(operation) || !Enum.IsDefined(scope) || processId == 0 || processCreationFileTime == 0 ||
            !ValidSid(userSid) || !ValidSid(logonSid) || !IsLogonSid(logonSid) ||
            (authorityIdentity is not null && !authorityIdentity.MatchesSubject(userSid)))
            throw new InvalidDataException("Invalid protected broker proof context.");
        this.peer = peer.ToArray(); this.resource = resource.ToArray();
        this.userSid = userSid.ToArray(); this.logonSid = logonSid.ToArray();
        Operation = operation; Scope = scope; Registration = registration; LogonAuthenticationId = logonAuthenticationId;
        SessionId = sessionId; ProcessId = processId; ProcessCreationFileTime = processCreationFileTime;
        ExpectedOwnerGeneration = expectedOwnerGeneration;
        this.authorityIdentity = authorityIdentity;
    }

    private static bool ValidSid(byte[]? sid) => sid is { Length: >= 8 and <= 68 } &&
        sid[0] == 1 && sid[1] <= 15 && sid.Length == 8 + 4 * sid[1];
    private static bool IsLogonSid(byte[] sid) => sid.Length == 20 && sid[1] == 3 &&
        sid.AsSpan(2, 6).SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 5 }) &&
        BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan(8, 4)) == 5;

    internal byte[] Transcript(byte[] nonce, long expiryMilliseconds)
    {
        if (nonce is not { Length: 32 } || expiryMilliseconds < 0)
            throw new InvalidDataException("Invalid broker challenge.");
        using var output = new MemoryStream(256);
        if (authorityIdentity is null) output.Write("DeskFuse.BrokerProof.v1\0"u8);
        else output.Write("DeskFuse.BrokerProof.v2\0"u8);
        output.Write(nonce);
        authorityIdentity?.WriteTo(output);
        output.Write(peer);
        output.Write(resource);
        output.WriteByte((byte)Operation);
        output.WriteByte((byte)Scope);
        Span<byte> scratch = stackalloc byte[16];
        if (!Registration.TryWriteBytes(scratch, bigEndian: true, out var written) || written != 16)
            throw new InvalidDataException("Cannot encode registration identity.");
        output.Write(scratch);
        output.WriteByte(checked((byte)userSid.Length)); output.Write(userSid);
        output.WriteByte(checked((byte)logonSid.Length)); output.Write(logonSid);
        BinaryPrimitives.WriteUInt64BigEndian(scratch, LogonAuthenticationId); output.Write(scratch[..8]);
        BinaryPrimitives.WriteUInt32BigEndian(scratch, SessionId); output.Write(scratch[..4]);
        BinaryPrimitives.WriteUInt32BigEndian(scratch, ProcessId); output.Write(scratch[..4]);
        BinaryPrimitives.WriteUInt64BigEndian(scratch, ProcessCreationFileTime); output.Write(scratch[..8]);
        BinaryPrimitives.WriteUInt64BigEndian(scratch, ExpectedOwnerGeneration); output.Write(scratch[..8]);
        BinaryPrimitives.WriteInt64BigEndian(scratch, expiryMilliseconds); output.Write(scratch[..8]);
        return output.ToArray();
    }
}

internal sealed record IssuedBrokerChallenge(byte[] Nonce, byte[] Transcript, long ExpiresAtMilliseconds, byte[]? HostSignature = null);

/// <summary>
/// Bounded, in-memory, single-use device proof verification. This is not an
/// enrollment store, process authenticator or capability grant. The caller must
/// obtain and recheck the key and context from independently protected live broker
/// state, and serialize revocation against the final privileged admission.
/// </summary>
internal sealed class BrokerProofChallenges(Func<long> monotonicMilliseconds)
{
    private sealed record Pending(Guid Registration, byte[] Transcript, byte[] Spki, long Issued, long Expiry);
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private const int MaxPending = 256, MaxPerRegistration = 16;
    internal const int MaxChallengeLifetimeMs = 120000;

    internal IssuedBrokerChallenge Issue(BrokerProofContext context, byte[] protectedEnrolledSpki, int lifetimeMs,
        AudioProofBinding? audio = null, long? absoluteExpiry = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (lifetimeMs is < 1 or > MaxChallengeLifetimeMs)
            throw new InvalidDataException("Unbounded broker challenge lifetime.");
        if (protectedEnrolledSpki is null) throw new InvalidDataException("Missing enrolled verification key.");
        var enrolledKey = protectedEnrolledSpki.ToArray();
        ValidateP256PublicKey(enrolledKey);
        lock (gate)
        {
            var now = monotonicMilliseconds();
            if (now < 0) throw new InvalidOperationException("Invalid monotonic broker clock.");
            foreach (var key in pending.Where(item => item.Value.Expiry <= now).Select(item => item.Key).ToArray())
                pending.Remove(key);
            if (pending.Count >= MaxPending || pending.Values.Count(item => item.Registration == context.Registration) >= MaxPerRegistration)
                throw new InvalidOperationException("Broker challenge capacity is exhausted.");
            var expiry = absoluteExpiry ?? checked(now + lifetimeMs);
            if (expiry <= now || expiry > checked(now + MaxChallengeLifetimeMs)) throw new InvalidDataException("Frozen proof expiry is unavailable.");
            var nonce = RandomNumberGenerator.GetBytes(32);
            var core = context.Transcript(nonce, expiry);
            var transcript = audio is null ? core : audio.Wrap(core);
            pending.Add(Convert.ToHexString(nonce), new(context.Registration, transcript, enrolledKey, now, expiry));
            return new(nonce, transcript.ToArray(), expiry);
        }
    }

    internal void Retire(byte[] nonce) { lock (gate) pending.Remove(Convert.ToHexString(nonce)); }
    internal int PendingCount { get { lock (gate) return pending.Count; } }

    internal bool Consume(byte[] nonce, byte[] signatureDer, BrokerProofContext liveContext, byte[] protectedLiveSpki)
        => Consume(nonce, signatureDer, liveContext, protectedLiveSpki, out _);

    // The authority owner uses the verified expiry for the final short admission gate.
    internal bool Consume(byte[] nonce, byte[] signatureDer, BrokerProofContext liveContext, byte[] protectedLiveSpki,
        out long verifiedExpiry, AudioProofBinding? audio = null)
    {
        verifiedExpiry = 0;
        if (nonce is not { Length: 32 }) return false;
        var nonceSnapshot = nonce.ToArray();
        Pending? challenge;
        lock (gate)
        {
            if (!pending.Remove(Convert.ToHexString(nonceSnapshot), out challenge)) return false;
        }
        // Even bad/expired/foreign proofs consume the nonce. No retry or weaker profile.
        if (signatureDer is not { Length: >= 8 and <= 72 } || liveContext is null ||
            protectedLiveSpki is not { Length: 91 }) return false;
        var signature = signatureDer.ToArray();
        var liveKey = protectedLiveSpki.ToArray();
        var now = monotonicMilliseconds();
        var core = liveContext.Transcript(nonceSnapshot, challenge.Expiry);
        var liveTranscript = audio is null ? core : audio.Wrap(core);
        if (now < challenge.Issued || now >= challenge.Expiry ||
            !CryptographicOperations.FixedTimeEquals(challenge.Spki, liveKey) ||
            !CryptographicOperations.FixedTimeEquals(challenge.Transcript, liveTranscript) ||
            !ValidSignatureDer(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(challenge.Spki, out var read);
            if (read != challenge.Spki.Length || !key.VerifyData(challenge.Transcript, signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return false;
            var afterVerification = monotonicMilliseconds();
            if (afterVerification < now || afterVerification >= challenge.Expiry) return false;
            verifiedExpiry = challenge.Expiry;
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    private static void ValidateP256PublicKey(byte[] spki)
    {
        // Canonical DER SubjectPublicKeyInfo with id-ecPublicKey, prime256v1,
        // and an uncompressed point. Arbitrary curves/alternate DER are denied.
        ReadOnlySpan<byte> prefix = [0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04];
        if (spki is not { Length: 91 } || !spki.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            throw new InvalidDataException("Expected a canonical uncompressed P-256 public key.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.KeySize != 256) throw new InvalidDataException("Invalid P-256 public key.");
        }
        catch (CryptographicException ex) { throw new InvalidDataException("Invalid P-256 public key.", ex); }
    }

    private static bool ValidSignatureDer(byte[] signature)
    {
        try
        {
            var reader = new AsnReader(signature, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadInteger();
            var s = sequence.ReadInteger();
            sequence.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
            var order = new BigInteger(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"), isUnsigned: true, isBigEndian: true);
            return r > BigInteger.Zero && r < order && s > BigInteger.Zero && s < order;
        }
        catch (AsnContentException) { return false; }
    }
}

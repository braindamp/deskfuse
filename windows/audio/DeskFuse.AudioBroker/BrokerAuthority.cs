using System.Security.Cryptography;

namespace DeskFuse.AudioBroker;

internal sealed record BrokerAuthorityRequest(Guid Registration, byte[] Resource, BrokerProofOperation Operation,
    BrokerProofScope Scope, ulong ExpectedOwnerGeneration);

/// <summary>
/// Implemented by the protected broker owner, using its retained worker objects and
/// independently approved enrollment store. No pipe request may implement or nominate
/// these facts. The callback is short and serialized against enrollment/worker revocation.
/// </summary>
internal interface IBrokerAuthorityState
{
    T WithAuthorizedFacts<T>(BrokerAuthorityRequest request, Func<BrokerProofContext, byte[], ulong, T> commit);
    void RevokeEnrollment(Guid enrollmentId);
}

/// <summary>One bounded control operation. A token cannot be copied or dispatched twice.</summary>
internal sealed class BrokerOperationAdmission
{
    private readonly byte[] resource;
    private readonly Func<long> clock;
    private readonly long admittedAt, expiresAt;
    private readonly Func<bool> requireCurrentAuthority;
    private readonly Func<Action, bool>? commitWhileCurrent;
    private int used;
    internal Guid Registration { get; }
    internal BrokerProofOperation Operation { get; }
    internal BrokerProofScope Scope { get; }
    internal ulong OwnerGeneration { get; }
    internal byte[] ResourceIdentity => resource.ToArray();

    internal BrokerOperationAdmission(BrokerAuthorityRequest request, Func<long> clock, long admittedAt, long expiresAt,
        Func<bool> requireCurrentAuthority, Func<Action, bool>? commitWhileCurrent = null)
    {
        Registration = request.Registration; Operation = request.Operation; Scope = request.Scope;
        OwnerGeneration = request.ExpectedOwnerGeneration; resource = request.Resource.ToArray();
        this.clock = clock; this.admittedAt = admittedAt; this.expiresAt = expiresAt;
        this.requireCurrentAuthority = requireCurrentAuthority;
        this.commitWhileCurrent = commitWhileCurrent;
    }

    // N2 uses this only for a bounded memory-state publication. Native work,
    // encoding, signatures, persistence and joins must remain outside it.
    internal T CommitOnce<T>(Func<T> publish)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("Broker operation was already consumed.");
        var now = clock();
        if (now < admittedAt || now >= expiresAt || commitWhileCurrent is null)
            throw new InvalidOperationException("Broker operation cannot publish current authority.");
        T result = default!;
        if (!commitWhileCurrent(() => result = publish())) throw new UnauthorizedAccessException("Authority changed before publication.");
        return result;
    }

    internal T ExecuteOnce<T>(Func<BrokerOperationAdmission, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("Broker operation was already consumed.");
        var now = clock();
        if (now < admittedAt || now >= expiresAt) throw new InvalidOperationException("Broker operation expired before dispatch.");
        // This is the dispatch linearization point. The short authority gate
        // rechecks the live worker, enrollment, key, scope, revision and expiry.
        // Native work runs afterwards, outside that gate, so revocation cannot
        // become stuck behind a native call. The owner must hold its resource
        // rundown lease for this already admitted exact unit.
        if (!requireCurrentAuthority()) throw new UnauthorizedAccessException("Broker authority changed before dispatch.");
        return operation(this);
    }
}

/// <summary>
/// Production proof-to-admission flow. Expensive signature verification is outside
/// the authority lock; final admission rechecks the protected enrollment revision,
/// actual worker/logon facts, key and expiry under the same gate used by revocation.
/// This component does not establish the protected state's origin or launch workers.
/// </summary>
internal sealed class BrokerAuthority(IBrokerAuthorityState state, Func<long> monotonicMilliseconds,
    Func<IssuedBrokerChallenge, byte[]>? signIssuedChallenge = null)
{
    private readonly BrokerProofChallenges challenges = new(monotonicMilliseconds);

    internal IssuedBrokerChallenge Issue(BrokerAuthorityRequest request, int lifetimeMs)
        => IssueCore(request, lifetimeMs, null, null, null);

    internal IssuedBrokerChallenge IssueAudio(BrokerAuthorityRequest request, int lifetimeMs, AudioProofBinding audio,
        long expiry, Action<IssuedBrokerChallenge> allocated)
        => IssueCore(request, lifetimeMs, audio, expiry, allocated);

    internal void Retire(byte[] nonce) => challenges.Retire(nonce);
    internal int PendingCount => challenges.PendingCount;
    private IssuedBrokerChallenge IssueCore(BrokerAuthorityRequest request, int lifetimeMs, AudioProofBinding? audio,
        long? expiry, Action<IssuedBrokerChallenge>? allocated)
    {
        var snapshot = SnapshotRequest(request);
        var facts = state.WithAuthorizedFacts(snapshot, (context, spki, _) =>
        {
            RequireContextMatchesRequest(context, snapshot);
            if (context.IsAuthorityBound && signIssuedChallenge is null)
                throw new InvalidOperationException("Production v2 challenges require the installed host signer.");
            return (Context: context, Key: spki.ToArray());
        });
        var issued = challenges.Issue(facts.Context, facts.Key, lifetimeMs, audio, expiry);
        try
        {
            allocated?.Invoke(issued);
            if (signIssuedChallenge is null) return issued; // Explicit unconnected development v1 only.
            var signature = signIssuedChallenge(issued); // CNG work does not hold the authority gate.
            if (signature is not { Length: >= 8 and <= 72 }) throw new CryptographicException("Invalid host challenge signature.");
            return issued with { HostSignature = signature };
        }
        catch { challenges.Retire(issued.Nonce); throw; }
    }

    internal BrokerOperationAdmission? Admit(BrokerAuthorityRequest request, byte[] nonce, byte[] signatureDer)
        => AdmitCore(request, nonce, signatureDer, null);
    internal BrokerOperationAdmission? AdmitAudio(BrokerAuthorityRequest request, byte[] nonce, byte[] signatureDer, AudioProofBinding audio)
        => AdmitCore(request, nonce, signatureDer, audio);
    private BrokerOperationAdmission? AdmitCore(BrokerAuthorityRequest request, byte[] nonce, byte[] signatureDer, AudioProofBinding? audio)
    {
        var snapshot = SnapshotRequest(request);
        if (nonce is not { Length: 32 }) return null;
        var started = monotonicMilliseconds();
        if (started < 0) return null;
        var nonceCopy = nonce.ToArray();
        // Malformed proofs are consumed too; never copy an unbounded signature.
        var signatureCopy = signatureDer is { Length: >= 8 and <= 72 } ? signatureDer.ToArray() : [];
        var before = state.WithAuthorizedFacts(snapshot, (context, spki, revision) =>
        {
            RequireContextMatchesRequest(context, snapshot);
            return (Context: context, Key: spki.ToArray(), Revision: revision);
        });
        if (!challenges.Consume(nonceCopy, signatureCopy, before.Context, before.Key, out var expiry, audio)) return null;
        byte[] Transcript(BrokerProofContext context)
        { var core = context.Transcript(nonceCopy, expiry); return audio is null ? core : audio.Wrap(core); }
        var expectedContext = Transcript(before.Context);
        return state.WithAuthorizedFacts<BrokerOperationAdmission?>(snapshot, (context, spki, revision) =>
        {
            RequireContextMatchesRequest(context, snapshot);
            var now = monotonicMilliseconds();
            if (now < started || now >= expiry || revision != before.Revision ||
                !CryptographicOperations.FixedTimeEquals(before.Key, spki) ||
                !CryptographicOperations.FixedTimeEquals(expectedContext, Transcript(context))) return null;
            bool Current(BrokerProofContext dispatchContext, byte[] dispatchKey, ulong dispatchRevision)
            {
                RequireContextMatchesRequest(dispatchContext, snapshot);
                var dispatchTime = monotonicMilliseconds();
                return dispatchTime >= now && dispatchTime < expiry && dispatchRevision == before.Revision &&
                    CryptographicOperations.FixedTimeEquals(before.Key, dispatchKey) &&
                    CryptographicOperations.FixedTimeEquals(expectedContext, Transcript(dispatchContext));
            }
            return new BrokerOperationAdmission(snapshot, monotonicMilliseconds, now, expiry, () =>
                state.WithAuthorizedFacts(snapshot, (dispatchContext, dispatchKey, dispatchRevision) =>
                {
                    return Current(dispatchContext, dispatchKey, dispatchRevision);
                }), publish => state.WithAuthorizedFacts(snapshot, (dispatchContext, dispatchKey, dispatchRevision) =>
                {
                    if (!Current(dispatchContext, dispatchKey, dispatchRevision)) return false;
                    publish(); return true;
                }));
        });
    }

    internal void RevokeEnrollment(Guid enrollmentId)
    {
        if (enrollmentId == Guid.Empty) throw new InvalidDataException("Missing enrollment identity.");
        // The protected store must persist denial before acknowledging this call.
        // A failed persistence operation never grants a fallback authorization.
        state.RevokeEnrollment(enrollmentId);
    }

    private static BrokerAuthorityRequest SnapshotRequest(BrokerAuthorityRequest request)
    {
        if (request is null || request.Registration == Guid.Empty || request.Resource is not { Length: 32 } ||
            !Enum.IsDefined(request.Operation) || !Enum.IsDefined(request.Scope))
            throw new InvalidDataException("Invalid broker authority request.");
        return request with { Resource = request.Resource.ToArray() };
    }

    private static void RequireContextMatchesRequest(BrokerProofContext context, BrokerAuthorityRequest request)
    {
        // These fields are cheap broker-owned observations. The resource/peer/SID
        // binding is checked by protected state and by the full proof transcript.
        if (context.Registration != request.Registration || context.Operation != request.Operation ||
            context.Scope != request.Scope || context.ExpectedOwnerGeneration != request.ExpectedOwnerGeneration ||
            !context.MatchesResource(request.Resource))
            throw new InvalidDataException("Protected broker facts do not match the requested operation.");
    }
}

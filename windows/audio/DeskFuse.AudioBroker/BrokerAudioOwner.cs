using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace DeskFuse.AudioBroker;

internal interface IAudioGrantSigner
{
    byte[] SignAudioGrant(BrokerAudioOwner.Committed operation);
}
internal enum AudioAuthorityPhase { Idle, Preparing, Prepared, Closed }
internal sealed record AudioOwnerSnapshot(AudioAuthorityPhase Phase, ulong Revision, ulong AuthorityUntil,
    ulong DeliveryGeneration, bool OutputReady, byte[]? BindingDigest, byte[]? DeliveryDigest);
internal sealed record AudioOperationChallenge(byte[] Nonce, byte[] Transcript, byte[] HostSignature, ulong ProposedUntil, ulong ExpectedRevision);

/// <summary>
/// One concrete source parent and zero/one delivery child. Only bounded memory
/// publications run under authority locks. This is authorization state, not a
/// certificate of native setup, transport retirement or kernel rundown.
/// </summary>
internal sealed class BrokerAudioOwner
{
    internal sealed class Pending(AudioOperationKind kind, BrokerAuthorityRequest request, byte[] phoneNonce,
        ulong revision, ulong until, AudioDeliveryDescriptor? delivery)
    {
        internal readonly AudioOperationKind Kind = kind;
        internal readonly BrokerAuthorityRequest Request = request;
        internal readonly byte[] PhoneNonce = phoneNonce;
        internal readonly ulong Revision = revision, Until = until;
        internal readonly AudioDeliveryDescriptor? Delivery = delivery;
        internal IssuedBrokerChallenge? Challenge;
        internal bool Consuming, Failed, Issuing;
        internal required AudioProofBinding Proof;
        internal required long Expiry;
        internal required Connection Origin;
        internal AudioRequestStage Stage = AudioRequestStage.Issuing;
        internal Applied? Result;
    }
    internal sealed class Applied(Pending pending, byte[] signature, ulong revision)
    {
        internal readonly Pending Pending = pending;
        internal readonly byte[] Signature = signature;
        internal readonly ulong Revision = revision;
        internal readonly object ReceiptGate = new();
        internal byte[]? ReceiptBytes, SignedReceipt;
        internal bool Attempted;
        internal bool SigningMaterialTaken;
        internal Committed? Capability;
        internal bool PreparationClaimed;
    }
    private sealed record Candidate(BrokerAuthorityRequest Request, ulong Revision, ulong Until);
    private sealed record View(AudioAuthorityPhase Phase, ulong Revision, ulong Until, byte[]? Delivery, ulong DeliveryGeneration, Candidate[] Candidates);
    private readonly object gate = new(), clockGate = new();
    private readonly BrokerAuthorityState registry;
    private readonly IBrokerObservedCaller caller;
    private readonly IBrokerAudioWorker worker;
    private readonly BrokerAuthority authority;
    private readonly Func<ulong> clock;
    private readonly byte[] boot;
    private Pending? active;
    private Applied? acquisitionRecord;
    private ulong issuedThrough;
    private readonly Dictionary<Guid, Connection> connections = [];
    private Connection? deliveryOrigin;
    private bool statusSigning;
    private readonly Queue<string> replyOrder = new();
    private readonly Dictionary<string, Applied> applied = [];
    private AudioAuthorityPhase phase;
    private ulong revision, until, lastDeliveryGeneration, outputGeneration, lastClock;
    private bool ready, outputStopped, clockFailed;
    private byte[]? binding;
    private AudioDeliveryDescriptor? delivery;
    private View published = new(AudioAuthorityPhase.Idle, 0, 0, null, 0, []);
    internal AudioSourceDescriptor Source { get; }

    [SupportedOSPlatform("windows")]
    internal BrokerAudioOwner(BrokerAuthorityState registry, IBrokerAudioWorker worker, IBrokerObservedCaller caller,
        Guid enrollment, TrustedBrokerEnrollmentStore.SpeakerAssociation association, AudioSourceBackend backend,
        AudioLeasePolicy policy, bool minimumPeriod, AudioWireContext wire, byte[] serviceBoot,
        Func<ulong> suspendInclusive100ns, Func<IssuedBrokerChallenge, byte[]> signChallenge)
    {
        if (serviceBoot is not { Length: 32 } || serviceBoot.All(b => b == 0)) throw new InvalidDataException("Missing service boot identity.");
        this.registry = registry; this.caller = caller; this.worker = worker; clock = suspendInclusive100ns; boot = serviceBoot.ToArray();
        var identity = registry.CaptureAudioIdentity(worker, enrollment); association.RequireMatch(identity);
        Source = new(identity, new(Guid.NewGuid(), 1, association.DeviceKey, backend, worker.InstalledProfileSha256, minimumPeriod, policy, wire));
        registry.RegisterAudioOwner(worker, enrollment, this);
        authority = new(registry.BindCaller(caller), () => checked((long)(Now() / 10000)), signChallenge);
    }
    internal bool IsWorker(IBrokerRegisteredWorker expected) => ReferenceEquals(worker, expected);

    private ulong Now()
    {
        lock (clockGate)
        {
            var value = clock();
            if (clockFailed || value < lastClock) { clockFailed = true; throw new InvalidOperationException("Audio authority clock moved backwards."); }
            lastClock = value; return value;
        }
    }
    private void RequireLive(ulong now)
    {
        if (phase == AudioAuthorityPhase.Closed || phase != AudioAuthorityPhase.Idle && now >= until)
            throw new UnauthorizedAccessException("Audio source is terminal or expired.");
    }
    private ulong NextRevision() => checked(revision + 1);
    private void Publish()
    {
        if (active is { Result: null, Failed: false } obsolete && obsolete.Revision != revision) Retire(obsolete, false);
        var candidates = active is { Failed: false } p && p.Revision == revision
            ? new[] { new Candidate(p.Request, p.Revision, p.Until) } : [];
        Volatile.Write(ref published, new(phase, revision, until, delivery?.Digest, delivery?.Intent.Generation ?? 0, candidates));
    }

    // Called under the registry gate. Reads an immutable snapshot and never
    // takes the owner gate (owner -> registry is the sole mutation lock order).
    internal (BrokerWorkerResource Resource, ulong Revision) ResourceForAuthority(BrokerAuthorityRequest request)
    {
        var view = Volatile.Read(ref published); var now = Now();
        if (view.Phase == AudioAuthorityPhase.Closed || view.Phase != AudioAuthorityPhase.Idle && now >= view.Until ||
            request.Scope != BrokerProofScope.Speaker || request.Registration != Source.Identity.Worker)
            throw new UnauthorizedAccessException("Audio parent authority is unavailable.");
        var source = Source.Digest; var generation = Source.Intent.Generation;
        if (view.Phase != AudioAuthorityPhase.Idle && request.Resource.AsSpan().SequenceEqual(source) && request.ExpectedOwnerGeneration == generation)
            return (new(source, BrokerProofScope.Speaker, generation), view.Revision);
        if (view.Delivery is not null && request.Resource.AsSpan().SequenceEqual(view.Delivery) && request.ExpectedOwnerGeneration == view.DeliveryGeneration)
            return (new(view.Delivery, BrokerProofScope.Speaker, view.DeliveryGeneration), view.Revision);
        if (view.Candidates.Any(c => c.Revision == view.Revision && now < c.Until && c.Request.Operation == request.Operation &&
                c.Request.ExpectedOwnerGeneration == request.ExpectedOwnerGeneration && c.Request.Resource.AsSpan().SequenceEqual(request.Resource)))
            return (new(request.Resource, BrokerProofScope.Speaker, request.ExpectedOwnerGeneration), view.Revision);
        throw new UnauthorizedAccessException("Delivery is outside its live parent or frozen operation.");
    }

    internal sealed class Connection
    {
        internal readonly BrokerAudioOwner Owner;
        internal readonly Guid Incarnation;
        private readonly byte[] native, phone, nonce;
        internal bool Closed;
        internal Connection(BrokerAudioOwner owner, Guid incarnation, byte[] native, byte[] phone, byte[] nonce)
        { Owner = owner; Incarnation = incarnation; this.native = native.ToArray(); this.phone = phone.ToArray(); this.nonce = nonce.ToArray(); }
        internal bool Matches(AudioDeliveryIntent intent) => native.AsSpan().SequenceEqual(intent.NativeCertificate) &&
            phone.AsSpan().SequenceEqual(intent.PhoneCertificate) && nonce.AsSpan().SequenceEqual(intent.NativeNonce);
    }
    // Called by the registered worker's bound control observer, never from a
    // network-supplied authenticated flag. N3 supplies actual TLS observations.
    internal Connection ObserveConnection(IBrokerAudioWorker observedWorker, Guid incarnation, byte[] native, byte[] phone, byte[] nonce)
    {
        native = native.ToArray(); phone = phone.ToArray(); nonce = nonce.ToArray();
        if (!ReferenceEquals(observedWorker, worker) || incarnation == Guid.Empty ||
            new[] { native, phone, nonce }.Any(v => v is not { Length: 32 } || v.All(b => b == 0)))
            throw new UnauthorizedAccessException("Invalid worker connection observation.");
        lock (gate)
        {
            registry.RequireAudioRegistration(this, caller); RequireLive(Now());
            if (connections.ContainsKey(incarnation) || connections.Count >= 16) throw new InvalidOperationException("Connection observation capacity/conflict.");
            var origin = new Connection(this, incarnation, native, phone, nonce); connections.Add(incarnation, origin); return origin;
        }
    }
    private void RequireOrigin(Connection origin)
    {
        registry.RequireAudioRegistration(this, caller);
        RequireObserved(origin);
        if (delivery is not null && !ReferenceEquals(origin, deliveryOrigin))
            throw new UnauthorizedAccessException("Connection cannot enter this source's control lane.");
    }
    private void RequireObserved(Connection origin)
    {
        if (!ReferenceEquals(origin.Owner, this) || origin.Closed || !connections.TryGetValue(origin.Incarnation, out var actual) ||
            !ReferenceEquals(actual, origin)) throw new UnauthorizedAccessException("Connection is not a retained observation.");
    }
    internal void ConnectionClosed(Connection origin)
    {
        lock (gate)
        {
            if (!ReferenceEquals(origin.Owner, this)) throw new UnauthorizedAccessException();
            if (origin.Closed) return;
            RequireObserved(origin);
            origin.Closed = true; connections.Remove(origin.Incarnation);
            if (delivery is not null && ReferenceEquals(origin, deliveryOrigin)) Detach(delivery.Intent.Generation);
            if (active is { Result: null } p && (ReferenceEquals(p.Origin, origin) || ReferenceEquals(p.Delivery?.Connection, origin))) Retire(p);
        }
    }
    private void Retire(Pending p, bool publish = true)
    {
        p.Failed = true; p.Stage = AudioRequestStage.Retiring;
        if (p.Challenge is not null) authority.Retire(p.Challenge.Nonce);
        if (!p.Issuing && !p.Consuming && p.Result is null && ReferenceEquals(active, p)) active = null;
        if (publish) Publish();
    }
    private void Prune()
    {
        if (active is { Result: null } p && (p.Failed || p.Revision != revision || Now() / 10000 >= (ulong)p.Expiry)) Retire(p);
    }
    internal void Abandon(Connection origin, byte[] requestId)
    {
        var id = new AudioRequestId(requestId);
        lock (gate)
        {
            RequireOrigin(origin); RequireLive(Now());
            if (delivery is null || active is not { Result: null } p || !id.Matches(p.PhoneNonce))
                throw new UnauthorizedAccessException("No owned pre-admission request to abandon.");
            Retire(p);
        }
    }
    internal (ulong IssuedThrough, int AppliedReplies, int Pending, int BrokerNonces) RequestCounts
    { get { lock (gate) { Prune(); return (issuedThrough, applied.Count, active is null ? 0 : 1, authority.PendingCount); } } }

    internal AudioOperationChallenge Issue(Connection origin, AudioOperationKind kind, byte[] phoneRequestNonce, int challengeLifetimeMs, AudioDeliveryDescriptor? candidate = null)
    {
        var id = new AudioRequestId(phoneRequestNonce); var requestNonce = id.Bytes; Pending reservation;
        if (challengeLifetimeMs is < 1 or > BrokerProofChallenges.MaxChallengeLifetimeMs) throw new InvalidDataException("Invalid proof lifetime.");
        lock (gate)
        {
            var now = Now(); RequireOrigin(origin); RequireLive(now); Prune();
            var old = active is not null && new AudioRequestId(active.PhoneNonce).Sequence == id.Sequence ? active :
                applied.Values.Select(v => v.Pending).SingleOrDefault(p => new AudioRequestId(p.PhoneNonce).Sequence == id.Sequence);
            if (old is not null)
            {
                if (!id.Matches(old.PhoneNonce) || old.Kind != kind || (old.Delivery is null) != (candidate is null) ||
                    candidate is not null && !candidate.Digest.AsSpan().SequenceEqual(old.Delivery!.Digest))
                    throw new UnauthorizedAccessException("Audio request identity conflict.");
                if (old.Result is { } result) _ = Committed.FromRecord(this, result).RequireReplyCurrent();
                else if (old.Failed || old.Revision != revision || now / 10000 >= (ulong)old.Expiry)
                    throw new UnauthorizedAccessException("Audio request is stale.");
                return Challenge(old); // InProgress while the same signing call is retained.
            }
            if (id.Sequence <= issuedThrough) throw new UnauthorizedAccessException("Audio request is stale.");
            if (issuedThrough == ulong.MaxValue) { CloseAdmission(); throw new UnauthorizedAccessException("Audio sequence exhausted."); }
            if (id.Sequence != issuedThrough + 1) throw new InvalidDataException("Audio request is out of order.");
            if (active is not null) throw new InvalidOperationException("Audio operation is busy.");
            ValidateOperation(kind, candidate);
            if (candidate is not null && (!candidate.Intent.PhoneRequestNonce.AsSpan().SequenceEqual(requestNonce) ||
                candidate.Connection is null || candidate.Connection.Closed || !ReferenceEquals(candidate.Connection.Owner, this) ||
                !candidate.Connection.Matches(candidate.Intent) || delivery is null && !ReferenceEquals(origin, candidate.Connection)))
                throw new UnauthorizedAccessException("Delivery connection/request changed.");
            var resource = kind == AudioOperationKind.AcquireSource ? Source.Digest : candidate?.Digest ?? delivery!.Digest;
            var generation = kind == AudioOperationKind.AcquireSource ? Source.Intent.Generation : candidate?.Intent.Generation ?? delivery!.Intent.Generation;
            var proposed = checked(now + Source.Intent.Policy.Duration100ns);
            var bound = phase == AudioAuthorityPhase.Idle ? proposed : Math.Min(proposed, until);
            var expiry = checked((long)Math.Min(bound / 10000, checked(now / 10000 + (ulong)challengeLifetimeMs)));
            if ((ulong)expiry <= now / 10000) throw new UnauthorizedAccessException("No remaining proof time.");
            reservation = new(kind, new(Source.Identity.Worker, resource, AudioRequestProtocol.GenericOperation(kind), BrokerProofScope.Speaker, generation),
                requestNonce, revision, proposed, candidate) { Proof = new(boot, Source.Intent.Grant, Source.Intent.Generation, Source.Digest, kind, requestNonce, revision, proposed),
                Expiry = expiry, Origin = origin, Issuing = true };
            issuedThrough = id.Sequence; active = reservation; Publish();
        }
        try
        {
            var issued = authority.IssueAudio(reservation.Request, challengeLifetimeMs, reservation.Proof, reservation.Expiry, allocated =>
            {
                lock (gate)
                {
                    reservation.Challenge = allocated;
                    if (reservation.Failed || !ReferenceEquals(active, reservation)) throw new UnauthorizedAccessException("Audio issuance retired.");
                }
            });
            lock (gate)
            {
                RequireLive(Now());
                if (reservation.Failed || revision != reservation.Revision || Now() / 10000 >= (ulong)reservation.Expiry)
                    throw new UnauthorizedAccessException("Audio changed while signing its challenge.");
                reservation.Challenge = issued with { Nonce = issued.Nonce.ToArray(), Transcript = issued.Transcript.ToArray(), HostSignature = issued.HostSignature!.ToArray() };
                reservation.Stage = AudioRequestStage.AwaitingProof; return Challenge(reservation);
            }
        }
        catch { lock (gate) Retire(reservation); throw; }
        finally { lock (gate) { reservation.Issuing = false; if (reservation.Failed) Retire(reservation); } }
    }
    private static AudioOperationChallenge Challenge(Pending p)
    {
        var challenge = p.Challenge;
        if (challenge?.HostSignature is null) throw new InvalidOperationException("Audio challenge is in progress.");
        return new(challenge.Nonce.ToArray(), challenge.Transcript.ToArray(), challenge.HostSignature.ToArray(), p.Until, p.Revision);
    }
    private void ValidateOperation(AudioOperationKind kind, AudioDeliveryDescriptor? candidate)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown audio operation.");
        if (kind == AudioOperationKind.AcquireSource)
        { if (phase != AudioAuthorityPhase.Idle || candidate is not null) throw new UnauthorizedAccessException("Source acquisition cannot replace a parent."); return; }
        if (phase != AudioAuthorityPhase.Prepared || binding is null) throw new UnauthorizedAccessException("A prepared live parent is required.");
        if (kind == AudioOperationKind.Renew)
        { if (delivery is null || candidate is not null) throw new UnauthorizedAccessException("Disconnected sources cannot renew."); return; }
        if (candidate is null || !ReferenceEquals(candidate.Parent, Source) || !candidate.BindingDigest.AsSpan().SequenceEqual(binding) ||
            candidate.Intent.Generation <= lastDeliveryGeneration) throw new UnauthorizedAccessException("Delivery parent/binding/generation changed.");
        if (candidate.Connection is null) throw new UnauthorizedAccessException("Delivery lacks a connection observation.");
        RequireObserved(candidate.Connection);
        if (kind == AudioOperationKind.AttachDelivery && (delivery is not null || lastDeliveryGeneration != 0) ||
            kind == AudioOperationKind.SupersedeDelivery && lastDeliveryGeneration == 0)
            throw new UnauthorizedAccessException("Attach and supersede have different lifecycle semantics.");
    }

    internal Committed? Admit(Connection origin, byte[] nonce, byte[] signature)
    {
        if (nonce is not { Length: 32 }) return null;
        var nonceSnapshot = nonce.ToArray(); var key = Convert.ToHexString(nonceSnapshot);
        var proof = signature is { Length: >= 8 and <= 72 } ? signature.ToArray() : []; Pending p;
        lock (gate)
        {
            RequireLive(Now()); RequireOrigin(origin); Prune();
            if (applied.TryGetValue(key, out var old))
            { var result = Committed.FromRecord(this, old); _ = result.RequireReplyCurrent(); return old.Signature.AsSpan().SequenceEqual(proof) ? result : null; }
            if (active is not { Failed: false, Issuing: false, Result: null } current || current.Challenge is null ||
                !current.Challenge.Nonce.AsSpan().SequenceEqual(nonceSnapshot)) return null;
            if (!ReferenceEquals(origin, current.Origin)) throw new UnauthorizedAccessException("Proof came from another candidate connection.");
            if (current.Consuming) throw new InvalidOperationException("Exact audio proof is in progress.");
            p = current; p.Consuming = true; p.Stage = AudioRequestStage.Verifying;
        }
        try
        {
            var admission = authority.AdmitAudio(p.Request, nonceSnapshot, proof, p.Proof);
            if (admission is null) return null;
            lock (gate)
            {
                RequireLive(Now()); ValidateOperation(p.Kind, p.Delivery);
                if (p.Failed || !ReferenceEquals(active, p) || revision != p.Revision || Now() >= p.Until ||
                    p.Kind == AudioOperationKind.Renew && p.Until <= until) throw new UnauthorizedAccessException("Frozen audio operation is stale.");
                var next = NextRevision(); var record = new Applied(p, proof, next);
                admission.CommitOnce(() =>
                {
                    if (p.Kind == AudioOperationKind.AcquireSource) { phase = AudioAuthorityPhase.Preparing; acquisitionRecord = record; }
                    if (p.Delivery is not null)
                    { delivery = p.Delivery; deliveryOrigin = delivery.Connection; lastDeliveryGeneration = delivery.Intent.Generation; outputGeneration = 0; ready = false; outputStopped = false; }
                    until = p.Until; revision = next; p.Result = record; p.Stage = AudioRequestStage.Applied;
                    applied.Add(key, record); replyOrder.Enqueue(key);
                    while (replyOrder.Count > 8) applied.Remove(replyOrder.Dequeue());
                    Publish(); return true;
                });
                return Committed.FromRecord(this, record);
            }
        }
        finally
        {
            lock (gate) { p.Consuming = false; if (p.Result is null) Retire(p); Publish(); }
        }
    }

    internal void RequirePreparation(Committed acquisition)
    {
        lock (gate)
        {
            acquisition.RequireAcquisition(this); RequireLive(Now());
            if (phase != AudioAuthorityPhase.Preparing) throw new UnauthorizedAccessException("Source setup is no longer current.");
            registry.WithAudioCurrent(this, caller, () => true);
        }
    }
    internal void ClaimPreparation(Committed acquisition)
    {
        lock (gate) { RequirePreparation(acquisition); acquisition.ClaimPreparation(this); }
    }
    internal bool CompletePreparation(Committed acquisition, AudioObservedSource observed)
    {
        var encoded = AudioAuthorityProtocol.Binding(Source, observed); var digest = SHA256.HashData(encoded);
        lock (gate)
        {
            acquisition.RequireAcquisition(this); RequireLive(Now());
            if (phase != AudioAuthorityPhase.Preparing) return false;
            acquisition.RequirePreparationClaimed(this);
            return registry.WithAudioCurrent(this, caller, () =>
            { var next = NextRevision(); binding = digest; phase = AudioAuthorityPhase.Prepared; revision = next; Publish(); return true; });
        }
    }
    internal AudioDeliveryDescriptor PrepareDelivery(AudioDeliveryIntent intent, Connection observed)
    {
        byte[] bindingSnapshot;
        lock (gate) { RequireLive(Now()); bindingSnapshot = binding?.ToArray() ?? throw new InvalidOperationException("Source binding is not prepared."); }
        lock (gate)
        {
            RequireObserved(observed);
            if (!observed.Matches(intent)) throw new UnauthorizedAccessException("Unobserved delivery candidate.");
            return new(Source, bindingSnapshot, intent, observed);
        }
    }
    internal void OutputChanged(ulong deliveryGeneration, ulong newOutputGeneration, bool outputReady)
    {
        lock (gate)
        {
            RequireLive(Now());
            if (delivery is null || delivery.Intent.Generation != deliveryGeneration || newOutputGeneration == 0 || newOutputGeneration < outputGeneration ||
                newOutputGeneration == outputGeneration && outputStopped && outputReady)
                throw new UnauthorizedAccessException("Output signal names a stale delivery/output.");
            if (newOutputGeneration == outputGeneration && ready == outputReady) return;
            registry.WithAudioCurrent(this, caller, () =>
            {
                var next = NextRevision();
                outputStopped = newOutputGeneration == outputGeneration && ready && !outputReady;
                outputGeneration = newOutputGeneration; ready = outputReady; revision = next; Publish(); return true;
            });
        }
    }
    internal void Detach(ulong deliveryGeneration)
    {
        lock (gate)
        {
            var now = Now(); RequireLive(now);
            if (delivery is null || delivery.Intent.Generation != deliveryGeneration) throw new UnauthorizedAccessException("Stale delivery detach.");
            registry.WithAudioCurrent(this, caller, () =>
            {
                var next = NextRevision(); until = Math.Min(until, checked(now + Source.Intent.Policy.Grace100ns));
                delivery = null; deliveryOrigin = null; ready = false; revision = next; if (now >= until) phase = AudioAuthorityPhase.Closed; Publish(); return true;
            });
        }
    }
    internal void CloseAdmission()
    {
        lock (gate)
        {
            if (phase == AudioAuthorityPhase.Closed) return;
            phase = AudioAuthorityPhase.Closed; ready = false;
            if (revision != ulong.MaxValue) revision++;
            if (active is { Result: null } p) Retire(p);
            Publish(); // Restrictive local fence; this does not assert physical cleanup.
        }
    }
    internal AudioOwnerSnapshot Snapshot()
    {
        lock (gate)
        {
            var now = Now(); if (phase != AudioAuthorityPhase.Idle && now >= until) CloseAdmission();
            return new(phase, revision, until, delivery?.Intent.Generation ?? 0, ready, binding?.ToArray(), delivery?.Digest);
        }
    }
    internal AudioControlGrant ControlSnapshot()
    {
        lock (gate)
        {
            var now = Now();
            if (phase == AudioAuthorityPhase.Idle) throw new UnauthorizedAccessException("No admitted source grant.");
            if (now >= until) CloseAdmission();
            if (phase != AudioAuthorityPhase.Closed) registry.WithAudioCurrent(this, caller, () => true);
            var id = Source.Identity; var intent = Source.Intent;
            return new(id.Host, id.Namespace, id.Enrollment, boot.ToArray(), id.Worker, intent.Grant, intent.Generation, Source.Digest,
                binding?.ToArray() ?? new byte[32], delivery?.Intent.Generation ?? 0, delivery?.Digest ?? new byte[32], revision, until,
                delivery is null ? 0 : outputGeneration, ready, phase == AudioAuthorityPhase.Closed);
        }
    }
    internal void RequireControlCurrent(AudioControlGrant expected)
    {
        if (!IsControlCurrent(expected))
            throw new UnauthorizedAccessException("Audio control publication was superseded.");
    }
    internal bool IsControlCurrent(AudioControlGrant expected)
    {
        var current = ControlSnapshot();
        return current.SourceGrant == expected.SourceGrant && current.Revision == expected.Revision && current.Closed == expected.Closed;
    }

    internal sealed class RequestStatus
    {
        private readonly byte[] bytes;
        private RequestStatus(byte[] bytes) => this.bytes = bytes;
        internal AudioRequestStatus Snapshot => AudioRequestProtocol.ParseStatus(bytes);
        internal static RequestStatus FromOwner(BrokerAudioOwner owner, byte[] query)
        {
            var p = owner.active; var now = owner.Now();
            var stage = p?.Stage ?? AudioRequestStage.Empty;
            if (p is { Result: null } && (p.Failed || p.Revision != owner.revision || now / 10000 >= (ulong)p.Expiry))
                stage = AudioRequestStage.Retiring; // Report dispatch eligibility without mutating the read-only snapshot.
            return new(AudioRequestProtocol.Status(new(query.ToArray(), owner.Source.Identity, owner.boot.ToArray(), owner.Source.Intent.Grant,
                owner.Source.Intent.Generation, owner.Source.Digest,
                owner.phase != AudioAuthorityPhase.Idle && now >= owner.until ? AudioAuthorityPhase.Closed : owner.phase,
                owner.binding?.ToArray() ?? new byte[32], owner.delivery?.Intent.Generation ?? 0, owner.delivery?.Digest ?? new byte[32],
                owner.revision, owner.until, owner.issuedThrough, p?.PhoneNonce.ToArray() ?? new byte[32],
                stage, p is null ? 0 : checked((ulong)p.Expiry), p?.Kind ?? 0)));
        }
    }
    internal byte[] SignedRequestStatus(Connection origin, byte[] query, Func<RequestStatus, byte[]> signer)
    {
        query = query.ToArray();
        if (query is not { Length: 32 } || query.All(b => b == 0)) throw new InvalidDataException("Missing status query identity.");
        RequestStatus status;
        lock (gate)
        {
            RequireOrigin(origin);
            if (statusSigning) throw new InvalidOperationException("Audio status signing is busy.");
            status = RequestStatus.FromOwner(this, query); statusSigning = true;
        }
        try
        {
            var signature = signer(status);
            lock (gate) RequireOrigin(origin);
            return EnrollmentFraming.Signed(AudioRequestProtocol.Status(status.Snapshot), signature);
        }
        finally { lock (gate) statusSigning = false; }
    }

    internal sealed class Committed
    {
        private readonly BrokerAudioOwner owner;
        private readonly Applied record;
        private readonly string key;
        private Committed(BrokerAudioOwner owner, string key, Applied record) { this.owner = owner; this.key = key; this.record = record; }
        internal static Committed FromOwner(BrokerAudioOwner owner, string key)
        {
            lock (owner.gate)
            {
                if (!owner.applied.TryGetValue(key, out var record)) throw new UnauthorizedAccessException("No committed audio operation.");
                return FromRecord(owner, record);
            }
        }
        internal static Committed FromRecord(BrokerAudioOwner owner, Applied record)
        {
            lock (owner.gate)
            {
                var key = Convert.ToHexString(record.Pending.Challenge!.Nonce);
                if ((!owner.applied.TryGetValue(key, out var actual) || !ReferenceEquals(actual, record)) &&
                    !ReferenceEquals(owner.acquisitionRecord, record)) throw new UnauthorizedAccessException("Unowned audio commit record.");
                return record.Capability ??= new(owner, key, record);
            }
        }
        internal void RequireAcquisition(BrokerAudioOwner expected)
        {
            if (!ReferenceEquals(owner, expected) || record.Pending.Kind != AudioOperationKind.AcquireSource ||
                !ReferenceEquals(owner.acquisitionRecord, record))
                throw new UnauthorizedAccessException("Foreign source preparation.");
        }
        internal void ClaimPreparation(BrokerAudioOwner expected)
        {
            RequireAcquisition(expected);
            if (record.PreparationClaimed) throw new InvalidOperationException("This source preparation is already owned.");
            record.PreparationClaimed = true;
        }
        internal void RequirePreparationClaimed(BrokerAudioOwner expected)
        { RequireAcquisition(expected); if (!record.PreparationClaimed) throw new InvalidOperationException("Source preparation has not been claimed."); }
        internal ulong RequireReplyCurrent()
        {
            lock (owner.gate)
            {
                var now = owner.Now(); owner.RequireLive(now);
                if (!owner.applied.TryGetValue(key, out var current) || !ReferenceEquals(current, record) || now >= record.Pending.Until ||
                    owner.until < record.Pending.Until || record.Pending.Kind != AudioOperationKind.AcquireSource &&
                    (owner.delivery is null || !owner.delivery.Digest.AsSpan().SequenceEqual(record.Pending.Request.Resource)))
                    throw new UnauthorizedAccessException("Audio receipt is outside its current binding/deadline.");
                owner.registry.WithAudioCurrent(owner, owner.caller, () => true); return now;
            }
        }
        internal byte[] TakeReceiptBytesForSigning()
        {
            lock (owner.gate)
            {
                _ = RequireReplyCurrent();
                if (!ReferenceEquals(owner.active, record.Pending) || record.Pending.Stage != AudioRequestStage.ReceiptSigning ||
                    !record.Attempted || record.SigningMaterialTaken || record.ReceiptBytes is null)
                    throw new UnauthorizedAccessException("Audio signing material is unavailable or already consumed.");
                record.SigningMaterialTaken = true; return record.ReceiptBytes.ToArray();
            }
        }
        internal byte[] SignedReceipt(IAudioGrantSigner signer)
        {
            lock (record.ReceiptGate)
            {
                try
                {
                    ulong now;
                    lock (owner.gate)
                    {
                        now = RequireReplyCurrent();
                        if (record.SignedReceipt is not null) return record.SignedReceipt.ToArray();
                        if (record.Attempted) throw new IOException("Audio receipt signing is unconfirmed; no replacement signature is generated.");
                        if (!ReferenceEquals(owner.active, record.Pending)) throw new UnauthorizedAccessException("Audio reply is no longer active.");
                        record.Attempted = true; record.Pending.Stage = AudioRequestStage.ReceiptSigning;
                    }
                    var source = owner.Source; var identity = source.Identity; var intent = source.Intent; var pending = record.Pending;
                    record.ReceiptBytes = AudioAuthorityProtocol.Receipt(new(pending.Kind, identity.Host, identity.Namespace, identity.Enrollment,
                        owner.boot, identity.Worker, intent.Grant, intent.Generation, source.Digest,
                        pending.Kind == AudioOperationKind.AcquireSource ? new byte[32] : pending.Request.Resource,
                        pending.Kind == AudioOperationKind.AcquireSource ? 0 : pending.Request.ExpectedOwnerGeneration,
                        record.Revision, pending.Until, SHA256.HashData(pending.Challenge!.Transcript), pending.PhoneNonce, pending.Until - now));
                    var signature = signer.SignAudioGrant(this);
                    record.SignedReceipt = EnrollmentFraming.Signed(record.ReceiptBytes, signature);
                    _ = RequireReplyCurrent(); return record.SignedReceipt.ToArray();
                }
                finally
                {
                    lock (owner.gate)
                    {
                        if (record.Attempted && ReferenceEquals(owner.active, record.Pending))
                        { record.Pending.Stage = AudioRequestStage.Applied; owner.active = null; owner.Publish(); }
                    }
                }
            }
        }
    }
}

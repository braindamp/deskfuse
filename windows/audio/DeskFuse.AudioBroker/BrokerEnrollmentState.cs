using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

internal sealed record BrokerEnrollmentRecord(int Version, Guid StorageNamespace, Guid EnrollmentId,
    byte[] HostIdentitySha256, byte[] PeerIdentity, byte[] SigningSpki, BrokerProofScope[] Scopes,
    byte[] ApprovedTranscriptSha256, string RecordedByWindowsSid)
{
    internal byte[]? SubjectUserSid { get; init; }
    internal byte[]? SigningSpkiSha256 { get; init; }
    internal byte[]? ImmutableRecordSha256 { get; init; }
    internal byte[]? ApproverUserSid { get; init; }
    internal void Validate(Guid storageNamespace, ReadOnlySpan<byte> host)
    {
        if (Version is not (1 or 2) || StorageNamespace == Guid.Empty || StorageNamespace != storageNamespace || EnrollmentId == Guid.Empty ||
            HostIdentitySha256 is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(HostIdentitySha256, host) ||
            PeerIdentity is not { Length: 32 } || SigningSpki is not { Length: 91 } || ApprovedTranscriptSha256 is not { Length: 32 } ||
            Scopes is not { Length: >= 1 and <= 8 } || Scopes.Any(s => !Enum.IsDefined(s)) || Scopes.Distinct().Count() != Scopes.Length ||
            RecordedByWindowsSid != "S-1-5-18")
            throw new InvalidDataException("Invalid protected broker enrollment.");
        if (Version == 2)
        {
            if (SubjectUserSid is null || ApproverUserSid is null || SigningSpkiSha256 is not { Length: 32 } ||
                ImmutableRecordSha256 is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(SigningSpki), SigningSpkiSha256))
                throw new InvalidDataException("Incomplete committed v2 enrollment.");
            EnrollmentProtocol.ValidateSid(SubjectUserSid); EnrollmentProtocol.ValidateSid(ApproverUserSid);
        }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(SigningSpki, out var read);
        if (read != SigningSpki.Length || key.KeySize != 256 ||
            !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(SigningSpki) ||
            key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new InvalidDataException("Broker enrollment must contain a canonical P-256 verification key.");
    }
}

internal interface IBrokerEnrollments
{
    BrokerEnrollmentRecord ReadActive(Guid enrollmentId);
    void Revoke(Guid enrollmentId);
}

/// <summary>
/// Durable product enrollment store. Its capability is created by the SYSTEM
/// installer under the fixed BrokerAuthority profile; arbitrary paths, legacy
/// pairing files and candidate-supplied keys cannot nominate this store.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedBrokerEnrollments : IBrokerEnrollments, IDisposable
{
    private sealed record Revocation(int Version, Guid Namespace, Guid EnrollmentId);
    private readonly SecurePrivateStorageRoot root;
    private readonly byte[] host;
    private readonly IDisposable ownerLease;
    private bool disposed;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal ProtectedBrokerEnrollments(SecurePrivateStorageRoot root, byte[] protectedHostIdentity)
    {
        if (root.Profile != PrivateStorageProfile.BrokerAuthority || protectedHostIdentity is not { Length: 32 })
            throw new InvalidDataException("The broker requires its own protected authority namespace and host identity.");
        this.root = root;
        host = protectedHostIdentity.ToArray();
        root.ValidateHeldIdentity();
        ownerLease = root.AcquireBrokerOwnerLease();
    }

    // Called exclusively by trusted local setup AFTER the independent key
    // approval ceremony. SYSTEM identity proves the writer, not human consent;
    // the ceremony and its digest must not be supplied by the network protocol.
    // This method is intentionally not connected to the legacy broker pipe.
    private BrokerEnrollmentRecord AddFromTrustedSetup(Guid enrollmentId, byte[] peerIdentity, byte[] signingSpki,
        BrokerProofScope[] scopes, byte[] approvedTranscriptSha256)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (enrollmentId == Guid.Empty || peerIdentity is not { Length: 32 } || signingSpki is not { Length: 91 } ||
            scopes is not { Length: >= 1 and <= 8 } || approvedTranscriptSha256 is not { Length: 32 })
            throw new InvalidDataException("Invalid bounded broker enrollment approval.");
        root.ValidateHeldIdentity();
        using var identity = WindowsIdentity.GetCurrent();
        var record = new BrokerEnrollmentRecord(1, root.NamespaceId, enrollmentId, host.ToArray(), peerIdentity.ToArray(),
            signingSpki.ToArray(), scopes.Order().ToArray(), approvedTranscriptSha256.ToArray(), identity.User?.Value ?? "");
        record.Validate(root.NamespaceId, host);
        using var directory = root.OpenDirectory("enrollments");
        RequireNotRevoked(enrollmentId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        using var file = root.CopyProtectedFile(RecordPath(enrollmentId), new MemoryStream(bytes, writable: false), Hash(bytes), 4096);
        return record;
    }

    public BrokerEnrollmentRecord ReadActive(Guid enrollmentId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireId(enrollmentId);
        root.ValidateHeldIdentity();
        using var directory = root.OpenDirectory("enrollments");
        RequireNotRevoked(enrollmentId);
        using var file = root.OpenProtectedRead(RecordPath(enrollmentId));
        var bytes = SecurePrivateStorageRoot.ReadBounded(file, 4096);
        var record = JsonSerializer.Deserialize<BrokerEnrollmentRecord>(bytes, Json)
            ?? throw new InvalidDataException("Missing protected enrollment.");
        if (!JsonSerializer.SerializeToUtf8Bytes(record, Json).AsSpan().SequenceEqual(bytes) || record.EnrollmentId != enrollmentId)
            throw new InvalidDataException("Ambiguous broker enrollment record.");
        record.Validate(root.NamespaceId, host);
        // Revocation is serialized by the owner; checking again also catches a
        // different trusted instance persisting denial while this read occurred.
        RequireNotRevoked(enrollmentId);
        return record;
    }

    public void Revoke(Guid enrollmentId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireId(enrollmentId);
        root.ValidateHeldIdentity();
        using var directory = root.OpenDirectory("enrollments");
        if (HasRevocation(enrollmentId)) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Revocation(1, root.NamespaceId, enrollmentId), Json);
        // Create-new and Flush(true) happen inside the protected copy API. Any
        // torn/present marker denies later admission; it never restores a grant.
        using var marker = root.CopyProtectedFile(RevokedPath(enrollmentId), new MemoryStream(bytes, writable: false), Hash(bytes), 4096);
    }

    private void RequireNotRevoked(Guid id)
    {
        if (HasRevocation(id)) throw new UnauthorizedAccessException("Broker enrollment is revoked.");
    }
    private bool HasRevocation(Guid id)
    {
        try { using var marker = root.OpenProtectedRead(RevokedPath(id)); return true; }
        catch (FileNotFoundException) { return false; }
        // Access, sharing, malformed/reparse and directory errors are denials.
    }
    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new InvalidDataException("Missing enrollment identity."); }
    private static string RecordPath(Guid id) => $"enrollments/{id:N}.json";
    private static string RevokedPath(Guid id) => $"enrollments/{id:N}.revoked.json";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownerLease.Dispose();
    }
}

internal interface IBrokerRegisteredWorker
{
    Guid Registration { get; }
    BrokerLogonIdentity Logon { get; }
    uint ProcessId { get; }
    ulong CreationFileTime { get; }
    bool IsAlive();
}

internal interface IBrokerAudioWorker : IBrokerRegisteredWorker
{
    byte[] InstalledProfileSha256 { get; }
}

internal sealed record BrokerWorkerResource(byte[] Identity, BrokerProofScope Scope, ulong OwnerGeneration);

/// <summary>
/// Concrete service-owned registry. Only trusted launch can supply retained
/// worker facts; resources are delegated by trusted setup/owner policy. Pipe
/// requests may refer to a registration but cannot register a PID or public key.
/// The native resource owner separately serializes/rundowns admitted native work.
/// </summary>
internal sealed class BrokerAuthorityState(IBrokerEnrollments enrollments)
{
    private sealed class Registration(IBrokerRegisteredWorker worker, Guid enrollment, BrokerWorkerResource[] resources)
    {
        internal readonly IBrokerRegisteredWorker Worker = worker;
        internal readonly uint ProcessId = worker.ProcessId;
        internal readonly ulong CreationFileTime = worker.CreationFileTime;
        internal readonly BrokerLogonIdentity Logon = worker.Logon;
        internal readonly Guid Enrollment = enrollment;
        internal readonly BrokerWorkerResource[] Resources = resources;
        internal ulong Revision = 1;
        internal bool Closed;
        internal BrokerAudioOwner? Audio;
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Registration> registrations = [];
    private readonly HashSet<Guid> deniedEnrollments = [];
    private readonly Dictionary<Guid, BrokerAudioOwner> audioSources = [], completedAudioSources = [];
    private BrokerAudioOwner.StopStatus? unknownAudioStatus;

    [SupportedOSPlatform("windows")]
    internal BrokerRegisteredWorker LaunchAndRegister(BrokerWorkerLauncher launcher, IInstalledBrokerWorkerImage image,
        BrokerLogonIdentity approvedLogon, Guid enrollmentId, BrokerWorkerResource[] resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Length is < 1 or > 16 || resources.Any(r => r is null || r.Identity is not { Length: 32 } || !Enum.IsDefined(r.Scope)))
            throw new InvalidDataException("Invalid bounded worker delegation.");
        // Snapshot before launch; RegisterLaunchedWorker performs the fresh
        // protected enrollment/scope checks after the launch returns too.
        var snapshot = resources.Select(r => r is not null && r.Identity is not null
            ? r with { Identity = r.Identity.ToArray() } : throw new InvalidDataException("Invalid worker delegation.")).ToArray();
        lock (gate)
        {
            var enrollment = ReadEnrolled(enrollmentId);
            RequireSubject(enrollment, approvedLogon);
            if (snapshot.Any(r => !enrollment.Scopes.Contains(r.Scope)))
                throw new UnauthorizedAccessException("Worker resource scope is not enrolled.");
        }
        var worker = launcher.Launch(image, approvedLogon);
        try
        {
            RegisterLaunchedWorker(worker, enrollmentId, snapshot);
            return worker;
        }
        catch (Exception failure)
        {
            try { worker.StopAndJoin(); worker.Dispose(); }
            catch (Exception cleanup) { throw new BrokerWorkerLaunchIncompleteException(worker, failure, cleanup); }
            throw;
        }
    }

    // A production authority session can only be obtained from a service-observed
    // connected pipe caller. The registration GUID is never a bearer credential.
    internal IBrokerAuthorityState BindCaller(IBrokerObservedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new CallerState(this, caller);
    }

    private sealed class CallerState(BrokerAuthorityState owner, IBrokerObservedCaller caller) : IBrokerAuthorityState
    {
        public T WithAuthorizedFacts<T>(BrokerAuthorityRequest request, Func<BrokerProofContext, byte[], ulong, T> commit)
            => owner.WithAuthorizedFacts(caller, request, commit);
        public void RevokeEnrollment(Guid id)
        {
            lock (owner.gate)
            {
                if (!owner.registrations.Values.Any(r => r.Enrollment == id && caller.Matches(r.Worker)))
                    throw new UnauthorizedAccessException("This caller cannot revoke another enrollment.");
                owner.DenyEnrollmentLocked(id);
            }
            owner.PersistRevocation(id); // Flush outside the short caller/resource gate.
        }
    }

    internal void RegisterLaunchedWorker(IBrokerRegisteredWorker worker, Guid enrollmentId, BrokerWorkerResource[] resources)
    {
        if (OperatingSystem.IsWindows() && worker is BrokerRegisteredWorker launched && launched.Kind != BrokerWorkerKind.NativeResource)
            throw new UnauthorizedAccessException("Setup/transport roles cannot acquire native resource authority.");
        ArgumentNullException.ThrowIfNull(worker);
        if (worker.Registration == Guid.Empty || worker.ProcessId == 0 || worker.CreationFileTime == 0 ||
            resources is not { Length: >= 1 and <= 16 } || resources.Any(r => r is null || r.Identity is not { Length: 32 } || !Enum.IsDefined(r.Scope)))
            throw new InvalidDataException("Invalid trusted worker delegation.");
        var snapshot = resources.Select(r => r with { Identity = r.Identity.ToArray() }).ToArray();
        if (snapshot.Select(r => (Convert.ToHexString(r.Identity), r.Scope)).Distinct().Count() != snapshot.Length)
            throw new InvalidDataException("Duplicate worker resource delegation.");
        lock (gate)
        {
            var enrollment = ReadEnrolled(enrollmentId);
            RequireSubject(enrollment, worker.Logon);
            if (registrations.Count >= 128 || registrations.ContainsKey(worker.Registration) || !worker.IsAlive() ||
                snapshot.Any(r => !enrollment.Scopes.Contains(r.Scope)))
                throw new UnauthorizedAccessException("Worker registration is unavailable or out of enrollment scope.");
            registrations.Add(worker.Registration, new(worker, enrollmentId, snapshot));
        }
    }

    private T WithAuthorizedFacts<T>(IBrokerObservedCaller caller, BrokerAuthorityRequest request, Func<BrokerProofContext, byte[], ulong, T> commit)
    {
        lock (gate)
        {
            if (!registrations.TryGetValue(request.Registration, out var registration) || registration.Closed ||
                !caller.Matches(registration.Worker) || !registration.Worker.IsAlive() ||
                registration.Worker.ProcessId != registration.ProcessId || registration.Worker.CreationFileTime != registration.CreationFileTime ||
                registration.Worker.Registration != request.Registration || !registration.Worker.Logon.Matches(registration.Logon))
                throw new UnauthorizedAccessException("The retained worker registration is not active.");
            var record = ReadEnrolled(registration.Enrollment);
            RequireSubject(record, registration.Logon);
            var audioFacts = registration.Audio?.ResourceForAuthority(request);
            var resource = registration.Audio is null ? registration.Resources.SingleOrDefault(r => r.Scope == request.Scope &&
                CryptographicOperations.FixedTimeEquals(r.Identity, request.Resource)) : audioFacts!.Value.Resource;
            if (resource is null || resource.OwnerGeneration != request.ExpectedOwnerGeneration || !record.Scopes.Contains(request.Scope))
                throw new UnauthorizedAccessException("The operation is outside the registered resource or generation.");
            var worker = registration.Worker;
            var logon = registration.Logon;
            var context = new BrokerProofContext(record.PeerIdentity, resource.Identity, request.Operation, request.Scope,
                worker.Registration, logon.UserSid, logon.LogonSid, logon.AuthenticationId, logon.SessionId,
                worker.ProcessId, worker.CreationFileTime, resource.OwnerGeneration,
                record.Version == 2 ? new BrokerProofAuthorityIdentity(record.HostIdentitySha256, record.StorageNamespace, record.EnrollmentId, record.SubjectUserSid!) : null);
            if (registration.Audio is not null && record.Version != 2) throw new UnauthorizedAccessException("Audio authority requires committed v2 enrollment.");
            return commit(context, record.SigningSpki.ToArray(), checked(registration.Revision + (audioFacts?.Revision ?? 0)));
        }
    }

    internal AudioAuthorityIdentity CaptureAudioIdentity(IBrokerAudioWorker worker, Guid enrollment)
    {
        lock (gate)
        {
            var record = ReadEnrolled(enrollment); RequireSubject(record, worker.Logon);
            if (record.Version != 2 || !record.Scopes.Contains(BrokerProofScope.Speaker) || !worker.IsAlive())
                throw new UnauthorizedAccessException("Audio ownership requires a live v2 speaker enrollment.");
            var logon = worker.Logon;
            return new(record.HostIdentitySha256.ToArray(), record.StorageNamespace, record.EnrollmentId, record.PeerIdentity.ToArray(),
                record.SubjectUserSid!.ToArray(), worker.Registration, logon.LogonSid, logon.AuthenticationId, logon.SessionId, worker.ProcessId, worker.CreationFileTime);
        }
    }
    internal void RequireAudioRegistration(BrokerAudioOwner owner, IBrokerObservedCaller caller)
    {
        lock (gate)
        {
            var identity = owner.Source.Identity;
            if (!registrations.TryGetValue(identity.Worker, out var r) || r.Closed || !ReferenceEquals(r.Audio, owner) ||
                !caller.Matches(r.Worker) || !r.Worker.IsAlive() || r.Worker.ProcessId != r.ProcessId ||
                r.Worker.CreationFileTime != r.CreationFileTime || !r.Worker.Logon.Matches(r.Logon))
                throw new UnauthorizedAccessException("Audio worker registration is no longer current.");
            var enrolled = ReadEnrolled(r.Enrollment); RequireSubject(enrolled, r.Logon);
            if (enrolled.Version != 2 || !enrolled.Scopes.Contains(BrokerProofScope.Speaker)) throw new UnauthorizedAccessException();
        }
    }
    internal void RegisterAudioOwner(IBrokerAudioWorker worker, Guid enrollment, BrokerAudioOwner owner)
    {
        lock (gate)
        {
            var current = CaptureAudioIdentity(worker, enrollment);
            var source = owner.Source;
            if (audioSources.ContainsKey(source.Intent.DeviceKey))
                throw new InvalidOperationException("Previous speaker source still owns native rundown custody.");
            if (!AudioAuthorityProtocol.Source(current, source.Intent).AsSpan().SequenceEqual(source.Bytes) ||
                !CryptographicOperations.FixedTimeEquals(worker.InstalledProfileSha256, source.Intent.ProfileSha256))
                throw new UnauthorizedAccessException("Audio source is outside the actual worker/enrollment profile.");
            RegisterLaunchedWorker(worker, enrollment, [new(source.Digest, BrokerProofScope.Speaker, source.Intent.Generation)]);
            registrations[worker.Registration].Audio = owner;
            audioSources.Add(source.Intent.DeviceKey, owner);
        }
    }
    // Setup calls this before launching/acquiring a replacement source. Final
    // registration repeats it atomically; neither a phone receipt nor denial
    // alone can release this actual resource-custody fence.
    internal void RequireSpeakerSourceAvailable(Guid deviceKey)
    {
        if (deviceKey == Guid.Empty) throw new InvalidDataException("Missing speaker association.");
        lock (gate)
            if (audioSources.ContainsKey(deviceKey)) throw new InvalidOperationException("Speaker source rundown is unconfirmed.");
    }
    [SupportedOSPlatform("windows")]
    internal void ConfirmAudioRundown(BrokerEnrollmentRundown.Completed completed)
    {
        BrokerAudioOwner? audio;
        lock (gate)
        {
            if (!registrations.TryGetValue(completed.Worker.Registration, out var registration) || !completed.Matches(registration.Worker))
                throw new UnauthorizedAccessException("Native rundown has no exact retained registration.");
            audio = registration.Audio;
        }
        if (audio is null) return;
        audio.ConfirmRundown(completed);
        lock (gate)
        {
            var key = audio.Source.Intent.DeviceKey;
            if (audioSources.TryGetValue(key, out var current) && ReferenceEquals(current, audio))
            { audioSources.Remove(key); completedAudioSources[key] = audio; }
        }
    }
    internal void QuarantineAudioRundown(IBrokerRegisteredWorker worker)
    {
        BrokerAudioOwner? audio;
        lock (gate) audio = registrations.TryGetValue(worker.Registration, out var r) && ReferenceEquals(r.Worker, worker) ? r.Audio : null;
        audio?.QuarantineRundown();
    }
    // Public read-only metadata. Does not restore a dead registration, launch a
    // source or consult positive admission. Retain only the latest completed
    // source per speaker, alongside any currently owned source/actual jobs.
    internal byte[] QueryAudioStopStatus(byte[] body, Func<BrokerAudioOwner.StopStatus, byte[]> signer)
    {
        var (query, target) = AudioSourceStopProtocol.ParseStatusQuery(body);
        BrokerAudioOwner? audio; BrokerAudioOwner.StopStatus frozen;
        lock (gate)
        {
            audioSources.TryGetValue(target.DeviceKey, out var current); completedAudioSources.TryGetValue(target.DeviceKey, out var completed);
            audio = current?.MatchesStopTarget(target) == true ? current : completed?.MatchesStopTarget(target) == true ? completed : null;
            if (audio is null)
            {
                if (unknownAudioStatus is { Signing: true }) throw new InvalidOperationException("Unknown source status signing is occupied.");
                if (unknownAudioStatus is null || !unknownAudioStatus.Snapshot.Query.AsSpan().SequenceEqual(query) ||
                    !AudioSourceStopProtocol.SameTarget(unknownAudioStatus.Snapshot.Target, target))
                    unknownAudioStatus = BrokerAudioOwner.StopStatus.Unknown(query, target);
                frozen = unknownAudioStatus;
                if (frozen.Reply is not null) return frozen.Reply.ToArray();
                if (frozen.Attempted) throw new IOException("Unknown source status signing is unconfirmed.");
                frozen.Signing = frozen.Attempted = true;
            }
            else frozen = null!;
        }
        if (audio is not null) return audio.SignedStopStatus(query, signer);
        try
        {
            var signature = signer(frozen);
            if (signature is not { Length: >= 8 and <= 72 }) throw new InvalidDataException("Invalid source status signature.");
            var reply = EnrollmentFraming.Signed(AudioSourceStopProtocol.Status(frozen.Snapshot), signature);
            lock (gate) { frozen.Reply = reply; return reply.ToArray(); }
        }
        finally { lock (gate) frozen.Signing = false; }
    }
    // Negative admission deliberately excludes positive audio revision/deadline
    // checks. Enrollment, key and real worker/subject custody remain mandatory.
    internal T WithAudioStopFacts<T>(BrokerAudioOwner owner, IBrokerObservedCaller caller,
        Func<byte[], byte[], ulong, T> action)
    {
        lock (gate)
        {
            RequireAudioRegistration(owner, caller);
            var registration = registrations[owner.Source.Identity.Worker];
            var enrolled = ReadEnrolled(registration.Enrollment);
            var identity = owner.Source.Identity;
            if (enrolled.EnrollmentId != identity.Enrollment || enrolled.StorageNamespace != identity.Namespace ||
                !enrolled.HostIdentitySha256.AsSpan().SequenceEqual(identity.Host) ||
                !enrolled.PeerIdentity.AsSpan().SequenceEqual(identity.Peer) ||
                !enrolled.SubjectUserSid!.AsSpan().SequenceEqual(identity.Subject))
                throw new UnauthorizedAccessException("Source Stop is outside its protected enrollment.");
            return action(enrolled.SigningSpki.ToArray(), enrolled.ImmutableRecordSha256!.ToArray(), registration.Revision);
        }
    }
    internal T WithAudioCurrent<T>(BrokerAudioOwner owner, IBrokerObservedCaller caller, Func<T> publish)
    {
        var source = owner.Source;
        return WithAuthorizedFacts(caller, new(source.Identity.Worker, source.Digest, BrokerProofOperation.Acquire,
            BrokerProofScope.Speaker, source.Intent.Generation), (_, _, _) =>
        {
            if (!registrations.TryGetValue(source.Identity.Worker, out var registration) || !ReferenceEquals(registration.Audio, owner))
                throw new UnauthorizedAccessException("Audio owner incarnation changed.");
            return publish();
        });
    }

    public void RevokeEnrollment(Guid enrollmentId)
    {
        if (enrollmentId == Guid.Empty) throw new InvalidDataException("Missing enrollment identity.");
        lock (gate) DenyEnrollmentLocked(enrollmentId);
        enrollments.Revoke(enrollmentId); // Admission stays closed even if durable persistence fails.
    }

    internal IBrokerRegisteredWorker[] CloseEnrollmentAdmission(Guid enrollmentId)
    {
        lock (gate)
        {
            DenyEnrollmentLocked(enrollmentId);
            return registrations.Values.Where(r => r.Enrollment == enrollmentId).Select(r => r.Worker).ToArray();
        }
    }
    internal bool OwnsRegistration(Guid enrollmentId, IBrokerRegisteredWorker worker)
    {
        lock (gate) return registrations.TryGetValue(worker.Registration, out var current) && !current.Closed &&
            current.Enrollment == enrollmentId && ReferenceEquals(current.Worker, worker);
    }
    internal Guid[] EnrollmentsWithWorkers()
    { lock (gate) return registrations.Values.Select(r => r.Enrollment).Distinct().ToArray(); }

    private void DenyEnrollmentLocked(Guid enrollmentId)
    {
        if (enrollmentId == Guid.Empty) throw new InvalidDataException("Missing enrollment identity.");
        deniedEnrollments.Add(enrollmentId);
        foreach (var registration in registrations.Values.Where(r => r.Enrollment == enrollmentId)) registration.Closed = true;
    }
    private void PersistRevocation(Guid enrollmentId) => enrollments.Revoke(enrollmentId);
    private BrokerEnrollmentRecord ReadEnrolled(Guid id)
    {
        if (deniedEnrollments.Contains(id)) throw new UnauthorizedAccessException("Enrollment admission is closed.");
        return enrollments.ReadActive(id);
    }
    private static void RequireSubject(BrokerEnrollmentRecord enrollment, BrokerLogonIdentity actual)
    {
        if (enrollment.Version == 2 && (enrollment.SubjectUserSid is null ||
            !CryptographicOperations.FixedTimeEquals(enrollment.SubjectUserSid, actual.UserSid)))
            throw new UnauthorizedAccessException("The actual Windows worker user is outside the enrolled subject.");
    }

    internal void CloseRegistration(Guid registration)
    {
        lock (gate)
            if (registrations.TryGetValue(registration, out var current)) current.Closed = true;
    }

    internal void AdvanceOwnerGeneration(Guid registration, byte[] resource, BrokerProofScope scope, ulong expected, ulong replacement)
    {
        if (resource is not { Length: 32 } || replacement <= expected) throw new InvalidDataException("Invalid resource generation transition.");
        lock (gate)
        {
            if (!registrations.TryGetValue(registration, out var current) || current.Closed || !current.Worker.IsAlive())
                throw new UnauthorizedAccessException("The retained worker is not active.");
            if (current.Audio is not null) throw new InvalidOperationException("Audio generations belong to the concrete source/delivery owner.");
            var enrollment = ReadEnrolled(current.Enrollment);
            RequireSubject(enrollment, current.Worker.Logon);
            if (!current.Worker.Logon.Matches(current.Logon)) throw new UnauthorizedAccessException("Worker logon changed.");
            var index = Array.FindIndex(current.Resources, r => r.Scope == scope && CryptographicOperations.FixedTimeEquals(r.Identity, resource));
            if (index < 0 || current.Resources[index].OwnerGeneration != expected)
                throw new UnauthorizedAccessException("The resource owner generation changed.");
            var revision = checked(current.Revision + 1);
            current.Resources[index] = current.Resources[index] with { OwnerGeneration = replacement };
            current.Revision = revision;
        }
    }

    internal void ForgetStoppedRegistration(Guid registration)
    {
        lock (gate)
        {
            if (!registrations.TryGetValue(registration, out var current)) return;
            if (!current.Closed || current.Worker.IsAlive()) throw new InvalidOperationException("Close and stop the exact worker before forgetting registration.");
            registrations.Remove(registration);
        }
    }
}

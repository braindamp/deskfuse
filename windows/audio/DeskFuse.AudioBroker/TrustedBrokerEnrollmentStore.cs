using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

/// <summary>One cold writer; active reads use completed immutable memory snapshots.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrustedBrokerEnrollmentStore : IBrokerEnrollments, IDisposable
{
    private sealed record Marker(int Version, Guid Namespace, Guid EnrollmentId, Guid TicketId, byte[] TranscriptSha256, byte[] RecordSha256);
    private sealed record Evidence(EnrollmentCommitEvidence Value, byte[] Bytes, byte[] Hash);
    private sealed record Cached(Evidence Evidence, BrokerEnrollmentRecord View);
    internal sealed record StatusSnapshot(EnrollmentOutcome Outcome, ulong Revision);
    private readonly object stateGate = new(), persistenceGate = new();
    private readonly IEnrollmentRecordFiles storage;
    private readonly byte[] host;
    private readonly Dictionary<Guid, Cached> active = [];
    private readonly Dictionary<Guid, Evidence> durableCommits = [];
    private readonly Dictionary<Guid, Evidence> evidence = [];
    private readonly Dictionary<(Guid, EnrollmentPrivateRecordKind), IEnrollmentRecordFile> files = [];
    private readonly Dictionary<Guid, EnrollmentCeremony.VerifiedEnrollmentCommit> admitted = [];
    private readonly Dictionary<Guid, byte[]> receiptAttempts = [];
    private readonly Dictionary<string, IEnrollmentRecordFile> speakerFiles = [];
    private readonly HashSet<string> speakerQuarantined = [];
    private readonly Dictionary<string, SpeakerRecord> completedSpeakers = [];
    private sealed record SpeakerRecord(int Version, Guid Namespace, byte[] Host, byte[] Peer, byte[] Subject, Guid DeviceKey);
    private readonly HashSet<Guid> denied = [], durableRevoked = [], quarantined = [], receiptQuarantined = [];
    private ulong revision = 1;
    private bool unresolvedStaging;
    private volatile bool closing, disposed;

    internal TrustedBrokerEnrollmentStore(SecurePrivateStorageRoot root, byte[] hostIdentitySha256)
        : this(new EnrollmentRecordFiles(root), hostIdentitySha256) { }

    internal TrustedBrokerEnrollmentStore(IEnrollmentRecordFiles storage, byte[] hostIdentitySha256)
    {
        this.storage = storage;
        if (storage.NamespaceId == Guid.Empty || hostIdentitySha256 is not { Length: 32 })
        { storage.Dispose(); throw new InvalidDataException("Enrollment requires its independently configured host/namespace."); }
        host = hostIdentitySha256.ToArray();
        try
        {
            var names = storage.EnumerateNames();
            var entries = new List<(Guid Id, EnrollmentPrivateRecordKind Kind)>();
            foreach (var name in names)
            {
                if (name.StartsWith("pending-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal))
                { unresolvedStaging = true; continue; } // Never adopt anonymous old publication evidence.
                entries.Add(ParseName(name));
            }
            if (entries.Select(e => e.Id).Distinct().Take(4097).Count() > 4096)
                throw new InvalidDataException("Enrollment registry exceeds its admitted bound.");
            // Negative presence wins before any positive state is published.
            foreach (var entry in entries.Where(e => e.Kind == EnrollmentPrivateRecordKind.Revocation))
            {
                Deny(entry.Id);
                try { CompleteRevocation(entry.Id); }
                catch (Exception error) when (PersistenceFailure(error)) { /* denial remains; durability unconfirmed */ }
            }
            foreach (var entry in entries.Where(e => e.Kind == EnrollmentPrivateRecordKind.Commit))
            {
                if (denied.Contains(entry.Id)) continue;
                try { LoadCommitted(entry.Id); }
                catch (Exception error) when (PersistenceFailure(error)) { quarantined.Add(entry.Id); }
            }
        }
        catch { Dispose(); throw; }
    }

    internal void Stage(EnrollmentCeremony.PreparedCommit preparation)
    {
        var bytes = preparation.EvidenceBytes; var value = ValidateEvidence(bytes);
        lock (persistenceGate)
        {
            RequireLive(); RequireEligible(value.EnrollmentId);
            try
            {
                SavePositive(value.EnrollmentId, EnrollmentPrivateRecordKind.Evidence, bytes);
                evidence[value.EnrollmentId] = new(value, bytes, SHA256.HashData(bytes));
            }
            catch { Quarantine(value.EnrollmentId); throw; }
        }
    }

    // Identifier association only: it grants no source, endpoint or runtime lease.
    // Reuse the established positive-record completion/custody protocol and the
    // same cold writer/owner lease. A torn association is never reallocated.
    internal SpeakerAssociation GetSpeakerAssociation(Guid enrollment)
    {
        var activeRecord = ReadActive(enrollment);
        if (activeRecord.Version != 2 || !activeRecord.Scopes.Contains(BrokerProofScope.Speaker))
            throw new UnauthorizedAccessException("A committed speaker enrollment is required.");
        using var identity = new MemoryStream(); identity.Write("DeskFuse.SpeakerAssociation.v1\0"u8);
        identity.Write(host); identity.Write(storage.NamespaceId.ToByteArray(true)); identity.Write(activeRecord.PeerIdentity);
        identity.WriteByte(checked((byte)activeRecord.SubjectUserSid!.Length)); identity.Write(activeRecord.SubjectUserSid);
        var digest = SHA256.HashData(identity.ToArray()); var key = Convert.ToHexString(digest);
        lock (persistenceGate)
        {
            RequireLive(); _ = ReadActive(enrollment);
            if (speakerQuarantined.Contains(key)) throw new InvalidDataException("Speaker association is quarantined.");
            try
            {
                var created = false;
                if (!speakerFiles.TryGetValue(key, out var file))
                {
                    if (speakerFiles.Count >= 4096) throw new InvalidDataException("Speaker associations exceed the admitted bound.");
                    try { file = storage.OpenSpeakerAssociation(digest); }
                    catch (FileNotFoundException) { file = storage.CreateSpeakerAssociation(digest); created = true; }
                    speakerFiles.Add(key, file);
                }
                SpeakerRecord record;
                byte[] bytes;
                if (created)
                {
                    record = new(1, storage.NamespaceId, host.ToArray(), activeRecord.PeerIdentity.ToArray(), activeRecord.SubjectUserSid.ToArray(), Guid.NewGuid());
                    bytes = JsonSerializer.SerializeToUtf8Bytes(record, ProtectedBrokerEnrollments.Json);
                }
                else
                {
                    bytes = file.Read();
                    record = JsonSerializer.Deserialize<SpeakerRecord>(bytes, ProtectedBrokerEnrollments.Json) ?? throw new InvalidDataException("Missing speaker association.");
                }
                if (record.Version != 1 || record.Namespace != storage.NamespaceId || record.DeviceKey == Guid.Empty ||
                    !record.Host.AsSpan().SequenceEqual(host) || !record.Peer.AsSpan().SequenceEqual(activeRecord.PeerIdentity) ||
                    !record.Subject.AsSpan().SequenceEqual(activeRecord.SubjectUserSid) ||
                    !JsonSerializer.SerializeToUtf8Bytes(record, ProtectedBrokerEnrollments.Json).AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("Speaker association changed its protected peer/subject binding.");
                file.CompletePositive(bytes);
                completedSpeakers[key] = record;
            }
            catch { speakerQuarantined.Add(key); throw; }
            // Completed identifier history survives enrollment denial/replacement.
            // Denial still wins before returning; it is not evidence of bad bytes.
            RequireLive(); _ = ReadActive(enrollment);
            return SpeakerAssociation.FromCompletedStore(this, key);
        }
    }

    internal sealed class SpeakerAssociation
    {
        private readonly Guid ns;
        private readonly byte[] host, peer, subject;
        internal Guid DeviceKey { get; }
        private SpeakerAssociation(Guid ns, byte[] host, byte[] peer, byte[] subject, Guid deviceKey)
        { this.ns = ns; this.host = host.ToArray(); this.peer = peer.ToArray(); this.subject = subject.ToArray(); DeviceKey = deviceKey; }
        internal void RequireMatch(AudioAuthorityIdentity identity)
        {
            if (ns != identity.Namespace || !host.AsSpan().SequenceEqual(identity.Host) || !peer.AsSpan().SequenceEqual(identity.Peer) ||
                !subject.AsSpan().SequenceEqual(identity.Subject)) throw new UnauthorizedAccessException("Foreign protected speaker association.");
        }
        // Closed factory, used only after this store's checked completion.
        internal static SpeakerAssociation FromCompletedStore(TrustedBrokerEnrollmentStore owner, string key)
        {
            lock (owner.persistenceGate)
            {
                owner.RequireLive();
                if (!owner.completedSpeakers.TryGetValue(key, out var record)) throw new UnauthorizedAccessException("No completed speaker association.");
                return new(record.Namespace, record.Host, record.Peer, record.Subject, record.DeviceKey);
            }
        }
    }

    internal Committed Commit(EnrollmentCeremony.VerifiedEnrollmentCommit admission)
    {
        var bytes = admission.EvidenceBytes; var value = ValidateEvidence(bytes); var id = value.EnrollmentId;
        var binding = EnrollmentProtocol.Binding(EnrollmentProtocol.ParseTranscript(value.Transcript));
        lock (persistenceGate)
        {
            RequireLive(); RequireEligible(id);
            if (admitted.TryGetValue(id, out var previous) && !ReferenceEquals(previous, admission))
                throw new UnauthorizedAccessException("Enrollment commit belongs to another operation.");
            admitted[id] = admission; // Retained until terminal shutdown; an exact retry reuses this identity.
            try
            {
                var staged = ReadEvidence(id);
                if (!staged.Bytes.AsSpan().SequenceEqual(bytes))
                    throw new UnauthorizedAccessException("Admitted commit changed prepared evidence.");
                Hold(id, EnrollmentPrivateRecordKind.Evidence, false).CompletePositive(bytes);
                RequireEligible(id); // Cancellation can deny while persistence is running.
                var marker = new Marker(2, storage.NamespaceId, id, binding.TicketId, binding.TranscriptSha256, staged.Hash);
                SavePositive(id, EnrollmentPrivateRecordKind.Commit, JsonSerializer.SerializeToUtf8Bytes(marker, ProtectedBrokerEnrollments.Json));
                return LoadCommitted(id, admittedCompletion: true);
            }
            catch { Quarantine(id); throw; }
        }
    }

    public BrokerEnrollmentRecord ReadActive(Guid id)
    {
        lock (stateGate)
        {
            RequireLive(); RequireEligibleLocked(id);
            return active.TryGetValue(id, out var value) ? Snapshot(value.View)
                : throw new UnauthorizedAccessException("No complete active v2 enrollment.");
        }
    }
    internal EnrollmentChoice[] ListForSubject(byte[] subject)
    {
        EnrollmentProtocol.ValidateSid(subject);
        lock (stateGate)
        {
            RequireLive();
            return active.Where(p => !denied.Contains(p.Key) && !quarantined.Contains(p.Key) &&
                    p.Value.View.SubjectUserSid is not null && CryptographicOperations.FixedTimeEquals(p.Value.View.SubjectUserSid, subject))
                .OrderBy(p => p.Key.ToString("N"), StringComparer.Ordinal)
                .Select(p => new EnrollmentChoice(p.Key, p.Value.View.SigningSpkiSha256!.ToArray(), p.Value.View.Scopes.ToArray())).ToArray();
        }
    }

    // Nonblocking live fence; cancellation must not queue behind the disk owner.
    internal void Deny(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Missing enrollment ID.");
        lock (stateGate)
        {
            RequirePersistenceLive();
            if (denied.Add(id)) AdvanceRevision();
        }
    }

    public void Revoke(Guid id)
    {
        Deny(id);
        lock (persistenceGate) { RequirePersistenceLive(); CompleteRevocation(id); }
    }

    private void CompleteRevocation(Guid id)
    {
        // Presence is enough to deny; zero/torn JSON must never grant access.
        var file = Hold(id, EnrollmentPrivateRecordKind.Revocation, true);
        file.CompleteDenial();
        lock (stateGate) { RequirePersistenceLive(); if (durableRevoked.Add(id)) AdvanceRevision(); }
    }

    internal byte[] GetReceipt(Guid id, Func<Committed, byte[]> signCommitted)
    {
        lock (persistenceGate)
        {
            RequireLive(); var committed = Committed.FromActive(this, id);
            if (receiptQuarantined.Contains(id)) throw new IOException("Receipt recovery is quarantined.");
            try
            {
                IEnrollmentRecordFile? file = null;
                try { file = Hold(id, EnrollmentPrivateRecordKind.Receipt, false); }
                catch (FileNotFoundException) { }
                byte[] result;
                if (file is not null)
                {
                    result = file.Read(); ValidateReceipt(committed, result);
                    file.CompletePositive(result); // Exact stored signature, not another randomized signature.
                }
                else
                {
                    if (unresolvedStaging && !admitted.ContainsKey(id)) throw new IOException("Unresolved old publication evidence blocks recovery receipt signing.");
                    if (!receiptAttempts.TryGetValue(id, out result!))
                    {
                        result = EnrollmentFraming.Signed(committed.ReceiptBytes, signCommitted(committed));
                        ValidateReceipt(committed, result); receiptAttempts.Add(id, result);
                    }
                    RequireEligible(id);
                    SavePositive(id, EnrollmentPrivateRecordKind.Receipt, result);
                }
                lock (stateGate) { RequireLive(); RequireEligibleLocked(id); return result.ToArray(); }
            }
            catch { receiptQuarantined.Add(id); throw; }
        }
    }

    internal StatusSnapshot CaptureStatus(EnrollmentStatusRequest request, byte[] requestBytes, byte[] signature)
    {
        if (!EnrollmentProtocol.StatusRequest(request).AsSpan().SequenceEqual(requestBytes) ||
            !CryptographicOperations.FixedTimeEquals(request.Binding.HostSha256, host) ||
            request.Binding.AuthorityNamespace != storage.NamespaceId ||
            !EnrollmentProtocol.Verify(request.PhoneSpki, requestBytes, signature))
            throw new UnauthorizedAccessException("Invalid status possession proof.");
        lock (persistenceGate)
        {
            RequireLive();
            Evidence? saved = null;
            try { saved = ReadEvidence(request.Binding.EnrollmentId); }
            catch (Exception error) when (PersistenceFailure(error)) { }
            var matches = saved is not null && EnrollmentProtocol.PhoneApproval(
                EnrollmentProtocol.Binding(EnrollmentProtocol.ParseTranscript(saved.Value.Transcript))).AsSpan()
                .SequenceEqual(EnrollmentProtocol.PhoneApproval(request.Binding)) &&
                CryptographicOperations.FixedTimeEquals(EnrollmentProtocol.ParseTranscript(saved.Value.Transcript).Proposal.PhoneSpki, request.PhoneSpki);
            lock (stateGate)
            {
                RequireLive();
                var id = request.Binding.EnrollmentId;
                var outcome = !matches ? EnrollmentOutcome.Unknown :
                    durableRevoked.Contains(id) ? EnrollmentOutcome.Revoked :
                    !denied.Contains(id) && !quarantined.Contains(id) && active.ContainsKey(id) ? EnrollmentOutcome.Committed : EnrollmentOutcome.Unknown;
                return new(outcome, revision);
            }
        }
    }

    internal EnrollmentOutcome StatusFor(EnrollmentStatusRequest request, byte[] bytes, byte[] signature)
        => CaptureStatus(request, bytes, signature).Outcome;

    internal bool AdmitResponse(StatusSnapshot snapshot)
    {
        lock (stateGate) return !closing && !disposed && revision == snapshot.Revision;
    }
    internal StatusSnapshot CaptureCommittedResponse(Guid id)
    {
        lock (stateGate)
        {
            RequireLive(); RequireEligibleLocked(id);
            if (!active.ContainsKey(id)) throw new UnauthorizedAccessException("No completed commit response.");
            return new(EnrollmentOutcome.Committed, revision);
        }
    }

    private Committed LoadCommitted(Guid id, bool admittedCompletion = false)
    {
        if (!admittedCompletion) RequireEligible(id);
        // Validate BOTH existing final artifacts before touching either. Never create a missing marker here.
        var saved = ReadEvidence(id);
        var markerFile = Hold(id, EnrollmentPrivateRecordKind.Commit, false);
        var markerBytes = markerFile.Read();
        var marker = JsonSerializer.Deserialize<Marker>(markerBytes, ProtectedBrokerEnrollments.Json)
            ?? throw new InvalidDataException("Missing commit marker.");
        var t = EnrollmentProtocol.ParseTranscript(saved.Value.Transcript); var b = EnrollmentProtocol.Binding(t);
        if (!JsonSerializer.SerializeToUtf8Bytes(marker, ProtectedBrokerEnrollments.Json).AsSpan().SequenceEqual(markerBytes) ||
            marker.Version != 2 || marker.Namespace != storage.NamespaceId || marker.EnrollmentId != id ||
            marker.TicketId != b.TicketId || !CryptographicOperations.FixedTimeEquals(marker.TranscriptSha256, b.TranscriptSha256) ||
            !CryptographicOperations.FixedTimeEquals(marker.RecordSha256, saved.Hash))
            throw new InvalidDataException("Commit marker is incomplete or foreign.");
        Hold(id, EnrollmentPrivateRecordKind.Evidence, false).CompletePositive(saved.Bytes);
        markerFile.CompletePositive(markerBytes);
        var view = new BrokerEnrollmentRecord(2, storage.NamespaceId, id, host.ToArray(), t.Offer.PeerIdentity.ToArray(),
            t.Proposal.PhoneSpki.ToArray(), t.Proposal.Scopes.ToArray(), b.TranscriptSha256, "S-1-5-18")
        { SubjectUserSid = t.Offer.SubjectUserSid.ToArray(), SigningSpkiSha256 = SHA256.HashData(t.Proposal.PhoneSpki),
            ImmutableRecordSha256 = saved.Hash.ToArray(), ApproverUserSid = saved.Value.UiConfirmation.ApproverUserSid.ToArray() };
        view.Validate(storage.NamespaceId, host);
        lock (stateGate)
        {
            durableCommits[id] = saved;
            // A late successful admitted write is historical durable evidence.
            // It must not reopen admission after cancellation or shutdown.
            if (closing || disposed || denied.Contains(id) || quarantined.Contains(id))
            {
                if (admittedCompletion) return Committed.FromDurable(this, id);
                RequireLive(); RequireEligibleLocked(id);
            }
            if (active.TryGetValue(id, out var previous))
            {
                if (!previous.Evidence.Hash.AsSpan().SequenceEqual(saved.Hash)) throw new InvalidDataException("Immutable enrollment changed.");
                return Committed.FromDurable(this, id);
            }
            active.Add(id, new(saved, view)); AdvanceRevision(); // Never remove a deny/quarantine fence here.
            return Committed.FromDurable(this, id);
        }
    }

    private Evidence ReadEvidence(Guid id)
    {
        if (evidence.TryGetValue(id, out var saved)) return saved;
        var bytes = Hold(id, EnrollmentPrivateRecordKind.Evidence, false).Read();
        var value = ValidateEvidence(bytes);
        if (value.EnrollmentId != id) throw new InvalidDataException("Enrollment filename/context mismatch.");
        saved = new(value, bytes, SHA256.HashData(bytes)); evidence.Add(id, saved); return saved;
    }
    private IEnrollmentRecordFile Hold(Guid id, EnrollmentPrivateRecordKind kind, bool createIfMissing)
    {
        if (files.TryGetValue((id, kind), out var file)) return file;
        try { file = storage.Open(id, kind); }
        catch (FileNotFoundException) when (createIfMissing) { file = storage.Create(id, kind); }
        files.Add((id, kind), file); return file;
    }
    private void SavePositive(Guid id, EnrollmentPrivateRecordKind kind, byte[] bytes)
        => Hold(id, kind, true).CompletePositive(bytes);
    private void Quarantine(Guid id) { lock (stateGate) { if (quarantined.Add(id)) AdvanceRevision(); } }
    private void RequireEligible(Guid id) { lock (stateGate) { RequireLive(); RequireEligibleLocked(id); } }
    private void RequireEligibleLocked(Guid id)
    {
        if (id == Guid.Empty || denied.Contains(id) || quarantined.Contains(id))
            throw new UnauthorizedAccessException("Enrollment admission is denied or quarantined.");
    }
    private void AdvanceRevision() => revision = checked(revision + 1);
    private static bool PersistenceFailure(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or Win32Exception;
    private static (Guid, EnrollmentPrivateRecordKind) ParseName(string name)
    {
        foreach (var (suffix, kind) in new[]
        {
            (".v2.record.json", EnrollmentPrivateRecordKind.Evidence), (".v2.commit.json", EnrollmentPrivateRecordKind.Commit),
            (".v2.receipt.bin", EnrollmentPrivateRecordKind.Receipt), (".revoked.json", EnrollmentPrivateRecordKind.Revocation)
        })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length == 32 + suffix.Length &&
                Guid.TryParseExact(name[..32], "N", out var id) && id != Guid.Empty && name == id.ToString("N") + suffix)
                return (id, kind);
        throw new InvalidDataException("Invalid enrollment artifact name.");
    }
    private EnrollmentCommitEvidence ValidateEvidence(byte[] bytes)
    {
        var e = EnrollmentEvidenceCodec.Decode(bytes);
        if (e.Version != 2 || e.Namespace != storage.NamespaceId || e.EnrollmentId == Guid.Empty || e.RecordedByWindowsSid != "S-1-5-18" ||
            e.Transcript is not { Length: > 0 and <= EnrollmentProtocol.MaxTranscript } || e.UiConfirmation is null ||
            e.OfferSignature is not { Length: >= 8 and <= 72 } || e.ProposalSignature is not { Length: >= 8 and <= 72 } ||
            e.FrozenSignature is not { Length: >= 8 and <= 72 } || e.PhoneSignature is not { Length: >= 8 and <= 72 })
            throw new InvalidDataException("Incomplete v2 enrollment evidence.");
        var t = EnrollmentProtocol.ParseTranscript(e.Transcript); var b = EnrollmentProtocol.Binding(t); var ui = e.UiConfirmation;
        if (t.Offer.EnrollmentId != e.EnrollmentId || b.AuthorityNamespace != storage.NamespaceId || !CryptographicOperations.FixedTimeEquals(b.HostSha256, host) ||
            ui.Registration != t.Offer.UiRegistration || ui.ProcessId != t.Offer.UiProcessId || ui.CreationFileTime != t.Offer.UiCreationFileTime ||
            !ui.ApproverUserSid.AsSpan().SequenceEqual(t.Offer.ApproverUserSid) || !ui.ApprovedBinding.AsSpan().SequenceEqual(EnrollmentProtocol.PhoneApproval(b)) ||
            !EnrollmentProtocol.Verify(t.Offer.HostSpki, EnrollmentProtocol.Offer(t.Offer), e.OfferSignature) ||
            !EnrollmentProtocol.Verify(t.Proposal.PhoneSpki, EnrollmentProtocol.Proposal(t.Proposal), e.ProposalSignature) ||
            !EnrollmentProtocol.Verify(t.Offer.HostSpki, EnrollmentProtocol.Frozen(b.TranscriptSha256), e.FrozenSignature) ||
            !EnrollmentProtocol.Verify(t.Proposal.PhoneSpki, EnrollmentProtocol.PhoneApproval(b), e.PhoneSignature))
            throw new UnauthorizedAccessException("Enrollment evidence does not bind the root, phone, UI and frozen consent.");
        return e;
    }
    private void ValidateReceipt(Committed committed, byte[] bytes)
    {
        var signed = EnrollmentFraming.ParseSigned(bytes); var t = EnrollmentProtocol.ParseTranscript(committed.Evidence.Transcript);
        if (!signed.Message.AsSpan().SequenceEqual(committed.ReceiptBytes) || !EnrollmentProtocol.Verify(t.Offer.HostSpki, signed.Message, signed.Signature))
            throw new InvalidDataException("Stored committed receipt is foreign or incomplete.");
    }
    private static BrokerEnrollmentRecord Snapshot(BrokerEnrollmentRecord value) => value with
    {
        HostIdentitySha256 = value.HostIdentitySha256.ToArray(), PeerIdentity = value.PeerIdentity.ToArray(), SigningSpki = value.SigningSpki.ToArray(),
        Scopes = value.Scopes.ToArray(), ApprovedTranscriptSha256 = value.ApprovedTranscriptSha256.ToArray(), SubjectUserSid = value.SubjectUserSid?.ToArray(),
        SigningSpkiSha256 = value.SigningSpkiSha256?.ToArray(), ImmutableRecordSha256 = value.ImmutableRecordSha256?.ToArray(), ApproverUserSid = value.ApproverUserSid?.ToArray()
    };

    private void RequireLive() => ObjectDisposedException.ThrowIf(closing || disposed, this);
    private void RequirePersistenceLive() => ObjectDisposedException.ThrowIf(disposed, this);
    internal void CloseAdmission()
    {
        lock (stateGate) { if (!closing) { closing = true; AdvanceRevision(); } }
    }
    public void Dispose()
    {
        CloseAdmission(); // Close first; never hold the short state gate while waiting for I/O.
        lock (persistenceGate)
        {
            if (disposed) return;
            foreach (var file in files.Values) file.Dispose();
            foreach (var file in speakerFiles.Values) file.Dispose();
            speakerFiles.Clear();
            files.Clear(); active.Clear();
            storage.Dispose(); // Last: retained owner lease cannot be released during outstanding native I/O.
            disposed = true; // A failed cleanup retains a retryable closing owner, never a false completed dispose.
        }
    }
    internal sealed class Committed
    {
        private readonly byte[] evidenceBytes, hash;
        internal EnrollmentCommitEvidence Evidence => EnrollmentEvidenceCodec.Decode(evidenceBytes);
        internal byte[] ReceiptBytes
        {
            get
            {
                var t = EnrollmentProtocol.ParseTranscript(Evidence.Transcript);
                return EnrollmentProtocol.Receipt(new(EnrollmentProtocol.Binding(t), t.Offer.PeerIdentity, t.Offer.SubjectUserSid,
                    t.Proposal.Scopes, SHA256.HashData(t.Proposal.PhoneSpki), hash));
            }
        }
        private Committed(byte[] bytes, byte[] recordHash) { evidenceBytes = bytes.ToArray(); hash = recordHash.ToArray(); }
        // Only this store calls this after the existing evidence and semantic marker barriers.
        internal static Committed FromDurable(TrustedBrokerEnrollmentStore owner, Guid id)
        {
            lock (owner.stateGate)
                return owner.durableCommits.TryGetValue(id, out var saved) ? new(saved.Bytes, saved.Hash)
                    : throw new UnauthorizedAccessException("No completed semantic commit.");
        }
        internal static Committed FromActive(TrustedBrokerEnrollmentStore owner, Guid id)
        {
            lock (owner.stateGate)
            {
                owner.RequireLive(); owner.RequireEligibleLocked(id);
                return owner.active.TryGetValue(id, out var cached) ? new(cached.Evidence.Bytes, cached.Evidence.Hash)
                    : throw new UnauthorizedAccessException("No durable active v2 commit.");
            }
        }
    }
}

using System.Security.Cryptography;

namespace DeskFuse.AudioBroker;

internal interface IEnrollmentHostSigner
{
    Guid NamespaceId { get; }
    byte[] PublicSpki { get; }
    byte[] SignOffer(EnrollmentOffer offer);
    byte[] SignFrozen(EnrollmentTranscript transcript);
}
internal enum EnrollmentCeremonyPhase { Offered, Freezing, Frozen, ApprovalsComplete, CommitAdmitted, Committed, Aborted, Expired, Quarantined }
internal enum EnrollmentCommitCompletion { Durable, Unconfirmed }

internal sealed record EnrollmentUiConfirmation(Guid Registration, uint ProcessId, ulong CreationFileTime,
    byte[] ApproverUserSid, byte[] ApprovedBinding);
internal sealed record EnrollmentCommitEvidence(int Version, Guid Namespace, Guid EnrollmentId, byte[] Transcript,
    byte[] OfferSignature, byte[] ProposalSignature, byte[] FrozenSignature, byte[] PhoneSignature,
    EnrollmentUiConfirmation UiConfirmation, string RecordedByWindowsSid);

internal static class EnrollmentEvidenceCodec
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal static byte[] Encode(EnrollmentCommitEvidence evidence) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(evidence, Json);
    internal static EnrollmentCommitEvidence Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 16384) throw new InvalidDataException("Invalid enrollment evidence size.");
        var result = System.Text.Json.JsonSerializer.Deserialize<EnrollmentCommitEvidence>(bytes, Json)
            ?? throw new InvalidDataException("Missing enrollment evidence.");
        if (!Encode(result).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Ambiguous enrollment evidence.");
        return result;
    }
}

/// <summary>One frozen candidate, two distinct approval channels. Signatures and disk I/O stay outside its short gate.</summary>
internal sealed class EnrollmentCeremony
{
    private readonly object gate = new();
    private readonly EnrollmentOffer offer;
    private readonly byte[] offerBytes, offerSignature;
    private readonly IEnrollmentHostSigner signer;
    private readonly Func<long> clock;
    private readonly Func<bool> ownerIsCurrent;
    private ulong revision = 1;
    private EnrollmentCeremonyPhase phase = EnrollmentCeremonyPhase.Offered;
    private byte[]? proposalBytes, proposalSignature, frozenBytes, frozenSignature, phoneSignature;
    private EnrollmentUiConfirmation? uiConfirmation;
    internal EnrollmentCeremonyPhase Phase { get { lock (gate) return phase; } }
    internal EnrollmentOffer Offer => EnrollmentProtocol.ParseOffer(offerBytes);
    internal byte[] SignedOffer => EnrollmentFraming.Signed(offerBytes, offerSignature);

    internal EnrollmentCeremony(EnrollmentOffer observedOffer, IEnrollmentHostSigner signer, Func<long> clock, Func<bool> ownerIsCurrent)
    {
        offerBytes = EnrollmentProtocol.Offer(observedOffer); offer = EnrollmentProtocol.ParseOffer(offerBytes);
        this.signer = signer; this.clock = clock; this.ownerIsCurrent = ownerIsCurrent;
        if (signer.NamespaceId != offer.AuthorityNamespace || !signer.PublicSpki.AsSpan().SequenceEqual(offer.HostSpki))
            throw new InvalidDataException("Ceremony does not belong to this installed host.");
        lock (gate) RequirePending();
        offerSignature = signer.SignOffer(offer);
    }

    internal byte[] FreezePhoneProposal(byte[] signedProposal)
    {
        var envelope = EnrollmentFraming.ParseSigned(signedProposal);
        var proposal = EnrollmentProtocol.ParseProposal(envelope.Message);
        if (!CryptographicOperations.FixedTimeEquals(proposal.OfferSha256, SHA256.HashData(offerBytes)) ||
            proposal.Scopes.Any(s => !offer.Scopes.Contains(s)) ||
            !EnrollmentProtocol.Verify(proposal.PhoneSpki, envelope.Message, envelope.Signature))
            throw new UnauthorizedAccessException("Phone proposal does not prove the expected offer/key/scopes.");
        ulong selected;
        lock (gate)
        {
            RequirePending();
            if (proposalBytes is not null)
            {
                if (!proposalBytes.AsSpan().SequenceEqual(envelope.Message))
                { phase = EnrollmentCeremonyPhase.Aborted; revision++; throw new UnauthorizedAccessException("Frozen enrollment candidate changed."); }
                if (frozenBytes is null || frozenSignature is null) throw new InvalidOperationException("Candidate freeze is in progress.");
                return EnrollmentFraming.Signed(frozenBytes, frozenSignature);
            }
            if (phase != EnrollmentCeremonyPhase.Offered) throw new InvalidOperationException("Candidate cannot be selected now.");
            proposalBytes = envelope.Message.ToArray(); proposalSignature = envelope.Signature.ToArray();
            phase = EnrollmentCeremonyPhase.Freezing; selected = ++revision;
        }
        // Selection precedes fresh FreezeNonce generation. Cancel/expiry during signing wins at publication.
        var transcript = new EnrollmentTranscript(offer, proposal, RandomNumberGenerator.GetBytes(32));
        var bytes = EnrollmentProtocol.Transcript(transcript); var signature = signer.SignFrozen(transcript);
        lock (gate)
        {
            RequirePending();
            if (revision != selected || phase != EnrollmentCeremonyPhase.Freezing) throw new OperationCanceledException("Freeze lost its current owner.");
            frozenBytes = bytes; frozenSignature = signature; phase = EnrollmentCeremonyPhase.Frozen; revision++;
            return EnrollmentFraming.Signed(bytes, signature);
        }
    }

    internal void AcceptPhoneApproval(byte[] signedApproval)
    {
        var envelope = EnrollmentFraming.ParseSigned(signedApproval);
        _ = EnrollmentProtocol.ParsePhoneApproval(envelope.Message);
        byte[] transcript;
        lock (gate) { RequirePending(); transcript = RequireFrozen().ToArray(); }
        var candidate = EnrollmentProtocol.ParseTranscript(transcript);
        if (!EnrollmentProtocol.PhoneApproval(EnrollmentProtocol.Binding(candidate)).AsSpan().SequenceEqual(envelope.Message) ||
            !EnrollmentProtocol.Verify(candidate.Proposal.PhoneSpki, envelope.Message, envelope.Signature))
            throw new UnauthorizedAccessException("Phone approval changed the frozen context or key.");
        lock (gate)
        {
            RequirePending();
            if (!RequireFrozen().AsSpan().SequenceEqual(transcript)) throw new OperationCanceledException("Frozen candidate changed during verification.");
            if (phoneSignature is null) { phoneSignature = envelope.Signature.ToArray(); UpdateApprovals(); }
        }
    }

    // Only the service's retained high-UI branch invokes this method. The concrete
    // observer is checked here too; network/medium records cannot supply UI consent.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal void ConfirmFromUi(BrokerCallerBinding observedCaller, BrokerRegisteredWorker actualUi, byte[] approvedBinding)
    {
        if (actualUi.Kind != BrokerWorkerKind.SetupUi || actualUi.Registration != offer.UiRegistration ||
            actualUi.ProcessId != offer.UiProcessId || actualUi.CreationFileTime != offer.UiCreationFileTime ||
            !observedCaller.Matches(actualUi)) throw new UnauthorizedAccessException("PC confirmation is not from the retained protected UI.");
        lock (gate)
        {
            RequirePending();
            var binding = EnrollmentProtocol.PhoneApproval(EnrollmentProtocol.Binding(EnrollmentProtocol.ParseTranscript(RequireFrozen())));
            if (!binding.AsSpan().SequenceEqual(approvedBinding)) throw new UnauthorizedAccessException("PC approved another transcript.");
            if (uiConfirmation is null)
            { uiConfirmation = new(actualUi.Registration, actualUi.ProcessId, actualUi.CreationFileTime, actualUi.Logon.UserSid, binding); UpdateApprovals(); }
        }
    }

    private void UpdateApprovals()
    {
        phase = phoneSignature is not null && uiConfirmation is not null ? EnrollmentCeremonyPhase.ApprovalsComplete : EnrollmentCeremonyPhase.Frozen;
        revision++;
    }

    internal PreparedCommit PrepareCommit()
    {
        lock (gate)
        {
            RequirePending();
            if (phase != EnrollmentCeremonyPhase.ApprovalsComplete || phoneSignature is null || uiConfirmation is null ||
                proposalSignature is null || frozenSignature is null) throw new InvalidOperationException("Both approvals are required before preparing commit.");
            return PreparedCommit.FromApproved(this);
        }
    }
    internal VerifiedEnrollmentCommit AdmitCommit(PreparedCommit preparation)
    {
        lock (gate)
        {
            RequirePending();
            if (!ReferenceEquals(preparation.Owner, this) || phase != EnrollmentCeremonyPhase.ApprovalsComplete || revision != preparation.Revision)
                throw new OperationCanceledException("Prepared commit lost current approval/logon authority.");
            phase = EnrollmentCeremonyPhase.CommitAdmitted; revision++;
            return VerifiedEnrollmentCommit.FromAdmitted(this, preparation);
        }
    }
    internal void RecheckPrepared(PreparedCommit preparation)
    {
        lock (gate)
        {
            RequirePending();
            if (!ReferenceEquals(preparation.Owner, this) || phase != EnrollmentCeremonyPhase.ApprovalsComplete || revision != preparation.Revision)
                throw new OperationCanceledException("Prepared approvals are no longer current.");
        }
    }
    internal void CompleteCommit(VerifiedEnrollmentCommit commit, EnrollmentCommitCompletion completion)
    {
        lock (gate)
        {
            if (!ReferenceEquals(commit.Owner, this) || phase != EnrollmentCeremonyPhase.CommitAdmitted)
                throw new InvalidOperationException("Commit completion does not match the admitted ceremony.");
            phase = completion == EnrollmentCommitCompletion.Durable ? EnrollmentCeremonyPhase.Committed : EnrollmentCeremonyPhase.Quarantined; revision++;
        }
    }
    internal bool CancelBeforeCommit()
    {
        lock (gate)
        {
            if (phase is EnrollmentCeremonyPhase.CommitAdmitted or EnrollmentCeremonyPhase.Committed or EnrollmentCeremonyPhase.Quarantined)
                return false; // Explicit authenticated cancel now requires durable revocation/rundown.
            phase = EnrollmentCeremonyPhase.Aborted; revision++; return true;
        }
    }
    internal bool MatchesFrozenBinding(EnrollmentBinding binding)
    {
        lock (gate) return frozenBytes is not null && EnrollmentProtocol.PhoneApproval(binding).AsSpan().SequenceEqual(
            EnrollmentProtocol.PhoneApproval(EnrollmentProtocol.Binding(EnrollmentProtocol.ParseTranscript(frozenBytes))));
    }
    private byte[] RequireFrozen() => frozenBytes ?? throw new InvalidOperationException("No frozen transcript.");
    private void RequirePending()
    {
        if (phase is EnrollmentCeremonyPhase.CommitAdmitted or EnrollmentCeremonyPhase.Committed or EnrollmentCeremonyPhase.Quarantined or
            EnrollmentCeremonyPhase.Aborted or EnrollmentCeremonyPhase.Expired) throw new InvalidOperationException("Enrollment ceremony is terminal or committing.");
        var now = clock();
        if (now < 0 || (ulong)now >= offer.ExpiresAtMilliseconds)
        { phase = EnrollmentCeremonyPhase.Expired; revision++; throw new UnauthorizedAccessException("Enrollment ceremony expired."); }
        if (!ownerIsCurrent()) { phase = EnrollmentCeremonyPhase.Aborted; revision++; throw new UnauthorizedAccessException("Enrollment UI/logon/worker changed."); }
    }

    internal sealed class PreparedCommit
    {
        internal EnrollmentCeremony Owner { get; }
        private readonly byte[] evidence;
        internal EnrollmentCommitEvidence Evidence => EnrollmentEvidenceCodec.Decode(evidence);
        internal byte[] EvidenceBytes => evidence.ToArray();
        internal ulong Revision { get; }
        private PreparedCommit(EnrollmentCeremony owner, EnrollmentCommitEvidence evidence, ulong revision)
        { Owner = owner; this.evidence = EnrollmentEvidenceCodec.Encode(evidence); Revision = revision; }
        internal static PreparedCommit FromApproved(EnrollmentCeremony owner)
        {
            lock (owner.gate)
            {
                if (owner.phase != EnrollmentCeremonyPhase.ApprovalsComplete || owner.proposalSignature is null ||
                    owner.frozenSignature is null || owner.phoneSignature is null || owner.uiConfirmation is null)
                    throw new InvalidOperationException("Enrollment is not approved.");
                var evidence = new EnrollmentCommitEvidence(2, owner.offer.AuthorityNamespace, owner.offer.EnrollmentId,
                    owner.RequireFrozen().ToArray(), owner.offerSignature.ToArray(), owner.proposalSignature.ToArray(),
                    owner.frozenSignature.ToArray(), owner.phoneSignature.ToArray(), owner.uiConfirmation with
                    { ApproverUserSid = owner.uiConfirmation.ApproverUserSid.ToArray(), ApprovedBinding = owner.uiConfirmation.ApprovedBinding.ToArray() }, "S-1-5-18");
                return new(owner, evidence, owner.revision);
            }
        }
    }
    internal sealed class VerifiedEnrollmentCommit
    {
        internal EnrollmentCeremony Owner { get; }
        private readonly byte[] evidence;
        internal byte[] EvidenceBytes => evidence.ToArray();
        internal EnrollmentCommitEvidence Evidence => EnrollmentEvidenceCodec.Decode(evidence);
        private VerifiedEnrollmentCommit(EnrollmentCeremony owner, PreparedCommit preparation) { Owner = owner; evidence = preparation.EvidenceBytes; }
        internal static VerifiedEnrollmentCommit FromAdmitted(EnrollmentCeremony owner, PreparedCommit preparation)
        {
            if (owner.phase != EnrollmentCeremonyPhase.CommitAdmitted || !ReferenceEquals(preparation.Owner, owner))
                throw new InvalidOperationException("Commit has no current admitted ceremony.");
            return new(owner, preparation);
        }
    }
}

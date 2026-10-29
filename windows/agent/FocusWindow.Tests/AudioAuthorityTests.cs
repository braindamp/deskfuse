using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class AudioAuthorityTests
{
    [Fact]
    public void Frozen_request_deadline_and_exact_receipt_retry_do_not_refresh_authority()
    {
        using var f = new Fixture();
        var request = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(1), 1000);
        f.Now += 2000000;
        Assert.Equal(request.Nonce, f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(1), 1000).Nonce);
        var operation = f.Admit(request, false);
        Assert.Equal(request.ProposedUntil, f.Owner.Snapshot().AuthorityUntil);
        Assert.Same(operation, f.Owner.Admit(f.Origin, request.Nonce, f.LastProof!));
        var signer = new Signer(f);
        var first = operation.SignedReceipt(signer); f.Now += 100000;
        Assert.Equal(first, operation.SignedReceipt(signer)); Assert.Equal(1, signer.Calls);
        var receipt = AudioAuthorityProtocol.ParseReceipt(EnrollmentFraming.ParseSigned(first).Message);
        Assert.Equal(request.ProposedUntil, receipt.AuthorityUntil); Assert.Equal(8000000UL, receipt.Remaining100ns);
        Assert.Equal(Nonce(1), receipt.RequestNonce); Assert.Equal(SHA256.HashData(request.Transcript), receipt.ProofSha256);
        Assert.Equal(0UL, receipt.DeliveryGeneration);
        f.Now = request.ProposedUntil;
        Assert.Throws<UnauthorizedAccessException>(() => operation.SignedReceipt(signer));
        Assert.Equal(AudioAuthorityPhase.Closed, f.Owner.Snapshot().Phase);
    }

    [Fact]
    public void Source_parent_binding_generations_readiness_and_disconnect_grace_are_enforced()
    {
        using var f = new Fixture(); var acquisition = f.Acquire();
        f.Owner.ClaimPreparation(acquisition);
        Assert.Throws<InvalidOperationException>(() => f.Owner.ClaimPreparation(acquisition));
        var wrong = f.Observed with { DeviceKey = Guid.NewGuid() };
        Assert.Throws<InvalidDataException>(() => f.Owner.CompletePreparation(acquisition, wrong));
        Assert.True(f.Owner.CompletePreparation(acquisition, f.Observed));
        var candidate = f.Owner.PrepareDelivery(new(1, Nonce(4), Nonce(5), Nonce(2), Nonce(6)), f.Origin);
        var attach = f.Owner.Issue(f.Origin, AudioOperationKind.AttachDelivery, Nonce(2), 1000, candidate);
        f.Admit(attach); Assert.False(f.Owner.Snapshot().OutputReady);
        f.Owner.OutputChanged(1, 1, true); var before = f.Owner.Snapshot();
        f.Owner.OutputChanged(1, 1, false);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.OutputChanged(1, 1, true));
        f.Owner.OutputChanged(1, 2, true); Assert.True(f.Owner.Snapshot().OutputReady);
        f.Owner.Detach(1); var detached = f.Owner.Snapshot();
        Assert.Equal(Math.Min(before.AuthorityUntil, f.Now + 2000000), detached.AuthorityUntil);
        Assert.False(detached.OutputReady); Assert.Equal(0UL, detached.DeliveryGeneration);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Nonce(3), 1000));
        var next = f.Owner.PrepareDelivery(new(2, Nonce(10), Nonce(11), Nonce(3), Nonce(13)), f.NewConnection(10, 11, 13));
        f.Origin = next.Connection!; f.Admit(f.Owner.Issue(f.Origin, AudioOperationKind.SupersedeDelivery, Nonce(3), 1000, next));
        Assert.Equal(2UL, f.Owner.Snapshot().DeliveryGeneration);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.OutputChanged(1, 3, true));
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Detach(1));
        f.Now = f.Owner.Snapshot().AuthorityUntil;
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Nonce(14), 1000));
    }

    [Fact]
    public void Renew_is_frozen_at_issue_and_stale_revision_or_parent_cannot_dispatch()
    {
        using var f = new Fixture(); f.Attach(); f.Now += 100000;
        var first = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Nonce(3), 1000);
        Assert.Throws<InvalidOperationException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Nonce(4), 1000));
        f.Now += 2000000; f.Admit(first);
        Assert.Equal(first.ProposedUntil, f.Owner.Snapshot().AuthorityUntil);
        Assert.Same(f.Owner.Admit(f.Origin, first.Nonce, f.LastProof!), f.Owner.Admit(f.Origin, first.Nonce, f.LastProof!));
        var pending = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Nonce(4), 1000);
        f.Registry.CloseEnrollmentAdmission(f.Persistence.Id);
        Assert.Throws<UnauthorizedAccessException>(() => f.Admit(pending));
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.RequirePreparation(f.Acquisition!));
    }

    [Fact]
    public async Task Revocation_does_not_wait_for_signing_and_late_receipt_cannot_acknowledge_active_authority()
    {
        using var f = new Fixture(); var acquisition = f.Acquire(false);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var signer = new Signer(f, () => { entered.Set(); Assert.True(release.Wait(10000)); });
        var response = Task.Run(() => Record.Exception(() => acquisition.SignedReceipt(signer)));
        try
        {
            Assert.True(entered.Wait(10000));
            f.Registry.CloseEnrollmentAdmission(f.Persistence.Id); f.Owner.CloseAdmission();
            Assert.Equal(AudioAuthorityPhase.Closed, f.Owner.Snapshot().Phase);
            release.Set(); Assert.IsType<UnauthorizedAccessException>(await response.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Throws<UnauthorizedAccessException>(() => f.Owner.CompletePreparation(acquisition, f.Observed));
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Association_recovery_recompletes_exact_bytes_and_torn_or_failed_records_are_not_reallocated()
    {
        using var persistence = new EnrollmentDurabilityTests.Fixture(); Guid key;
        using (var first = persistence.Open()) { key = first.GetSpeakerAssociation(persistence.Id).DeviceKey; }
        using (var second = persistence.Open()) { Assert.Equal(key, second.GetSpeakerAssociation(persistence.Id).DeviceKey); }
        Assert.Equal(2, persistence.AssociationCompletions);
        var association = Assert.Single(persistence.Associations);
        persistence.Associations[association.Key] = association.Value[..^1];
        using var torn = persistence.Open(); Assert.ThrowsAny<Exception>(() => torn.GetSpeakerAssociation(persistence.Id));
        Assert.Single(persistence.Associations);
        using var failedPersistence = new EnrollmentDurabilityTests.Fixture { FailAssociation = true };
        using var failed = failedPersistence.Open(); Assert.Throws<IOException>(() => failed.GetSpeakerAssociation(failedPersistence.Id));
        failedPersistence.FailAssociation = false;
        Assert.Throws<InvalidDataException>(() => failed.GetSpeakerAssociation(failedPersistence.Id));
        Assert.Single(failedPersistence.Associations);
    }

    private static byte[] Nonce(byte value) => Request(value, value);
    private static byte[] Request(ulong sequence, byte salt = 1)
    { var bytes = Enumerable.Repeat(salt, 32).ToArray(); BinaryPrimitives.WriteUInt64BigEndian(bytes, sequence); return bytes; }

    [Fact]
    public void Caller_nonce_mutation_cannot_verify_one_proof_and_apply_another_frozen_operation()
    {
        using var f = new Fixture();
        var first = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(1), 1000);
        f.Now += 1000000;
        f.Owner.ConnectionClosed(f.Origin); f.Origin = f.NewConnection(4, 5, 6);
        var second = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(2), 1000);
        var mutableNonce = first.Nonce.ToArray(); var secondProof = f.Persistence.SignPhone(second.Transcript);
        f.BeforeClockRead = () => second.Nonce.CopyTo(mutableNonce, 0);
        Assert.Null(f.Owner.Admit(f.Origin, mutableNonce, secondProof));
        Assert.Equal(AudioAuthorityPhase.Idle, f.Owner.Snapshot().Phase);
        Assert.NotNull(f.Owner.Admit(f.Origin, second.Nonce, secondProof));
        Assert.Equal(second.ProposedUntil, f.Owner.Snapshot().AuthorityUntil);
    }

    [Fact]
    public void Denial_during_association_completion_preserves_identifier_history_without_returning_authority()
    {
        using var f = new EnrollmentDurabilityTests.Fixture(); using var store = f.Open();
        f.BeforeAssociationComplete = () => store.Deny(f.Id);
        Assert.Throws<UnauthorizedAccessException>(() => store.GetSpeakerAssociation(f.Id));
        var key = Assert.Single(f.Associations).Key;
        var historical = TrustedBrokerEnrollmentStore.SpeakerAssociation.FromCompletedStore(store, key);
        Assert.NotEqual(Guid.Empty, historical.DeviceKey);
        Assert.Throws<UnauthorizedAccessException>(() => store.ReadActive(f.Id));
    }

    [Fact]
    public void Control_projection_agrees_with_native_vector_and_clones_descriptor_inputs()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio-authority-v1.json")));
        var bytes = Convert.FromHexString(fixture.RootElement.GetProperty("controlHex").GetString()!);
        var vector = AudioAuthorityProtocol.ParseControl(bytes);
        Assert.Equal(7UL, vector.SourceGeneration); Assert.Equal(10UL, vector.DeliveryGeneration);
        Assert.Equal(12UL, vector.Revision); Assert.Equal(1000UL, vector.AuthorityUntil); Assert.True(vector.OutputReady);
        Assert.Equal(bytes, AudioAuthorityProtocol.Control(vector));
        Assert.Throws<InvalidDataException>(() => AudioAuthorityProtocol.ParseControl(bytes.Concat(new byte[] { 0 }).ToArray()));
        using var f = new Fixture(); f.Attach();
        var digest = f.Owner.Source.Digest; f.Owner.Source.Identity.Peer[0] ^= 1; f.Owner.Source.Intent.ProfileSha256[0] ^= 1;
        Assert.Equal(digest, f.Owner.Source.Digest);
        var snapshot = f.Owner.ControlSnapshot(); var encoded = AudioAuthorityProtocol.Control(snapshot);
        Assert.Equal(snapshot.SourceGrant, AudioAuthorityProtocol.ParseControl(encoded).SourceGrant);
        Assert.Throws<InvalidOperationException>(() => f.Registry.AdvanceOwnerGeneration(f.Owner.Source.Identity.Worker,
            digest, BrokerProofScope.Speaker, 1, 2));
        f.Owner.OutputChanged(1, 1, true);
        Assert.False(f.Owner.IsControlCurrent(snapshot));
        Assert.Equal(AudioAuthorityPhase.Prepared, f.Owner.Snapshot().Phase);
        snapshot = f.Owner.ControlSnapshot();
        f.Owner.CloseAdmission(); Assert.True(f.Owner.ControlSnapshot().Closed);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.RequireControlCurrent(snapshot));
    }

    [Fact]
    public void Malformed_proof_is_consumed_runtime_cutoff_beats_proof_expiry_and_clock_fault_is_latched()
    {
        using var f = new Fixture(); var challenge = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(1), 10000);
        Assert.Null(f.Owner.Admit(f.Origin, challenge.Nonce, []));
        Assert.Null(f.Owner.Admit(f.Origin, challenge.Nonce, f.Persistence.SignPhone(challenge.Transcript)));
        var next = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(2), 10000);
        f.Now = next.ProposedUntil;
        Assert.Equal((long)(next.ProposedUntil / 10000), AudioRequestProtocol.ParseProof(next.Transcript).Facts.Expiry);
        Assert.Null(f.Owner.Admit(f.Origin, next.Nonce, f.Persistence.SignPhone(next.Transcript)));
        Assert.Equal(AudioAuthorityPhase.Idle, f.Owner.Snapshot().Phase);
        f.Now--;
        Assert.Throws<InvalidOperationException>(() => f.Owner.Snapshot());
        f.Now += 1000;
        Assert.Throws<InvalidOperationException>(() => f.Owner.Snapshot());
    }
    [Fact]
    public void Five_thousand_renewals_keep_bounded_replies_and_the_original_acquisition_custody()
    {
        using var f = new Fixture(); f.Attach(); f.Owner.OutputChanged(1, 1, true);
        var source = f.Owner.Source.Intent.Grant; var first = f.Acquisition!;
        for (ulong sequence = 3; sequence < 5003; sequence++)
        {
            f.Now += 10000;
            var challenge = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(sequence), 1000);
            var receipt = f.Admit(challenge).SignedReceipt(new Signer(f));
            Assert.Equal(challenge.ProposedUntil, AudioAuthorityProtocol.ParseReceipt(EnrollmentFraming.ParseSigned(receipt).Message).AuthorityUntil);
        }
        Assert.Equal((5002UL, 8, 0, 0), f.Owner.RequestCounts);
        Assert.Equal(source, f.Owner.Source.Intent.Grant); Assert.Equal(1UL, f.Owner.Snapshot().DeliveryGeneration);
        Assert.True(f.Owner.Snapshot().OutputReady);
        first.RequireAcquisition(f.Owner); // Not backed by an eternal reply-cache entry.
        Assert.False(f.Owner.CompletePreparation(first, f.Observed));
        Assert.Throws<UnauthorizedAccessException>(() => first.SignedReceipt(new Signer(f)));
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Nonce(1), 1000));
    }
    [Fact]
    public void Candidate_churn_counter_jumps_and_generic_proofs_cannot_poison_the_healthy_lane()
    {
        using var f = new Fixture(); f.Attach(); f.Now += 10000;
        var rogue = f.NewConnection(20, 21, 22);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(rogue, AudioOperationKind.Renew, Request(3), 1000));
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.SignedRequestStatus(rogue, Nonce(9), _ => throw new Exception("Must not sign")));
        Assert.Throws<InvalidDataException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(ulong.MaxValue), 1000));
        Assert.Equal(2UL, f.Owner.RequestCounts.IssuedThrough);
        var challenge = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(3), 1000);
        var parsed = AudioRequestProtocol.ParseProof(challenge.Transcript);
        Assert.Null(f.Owner.Admit(f.Origin, challenge.Nonce, f.Persistence.SignPhone(parsed.Core)));
        Assert.Equal((3UL, 2, 0, 0), f.Owner.RequestCounts);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(3), 1000));
        f.Admit(f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(4), 1000));
        Assert.Equal(ulong.MaxValue, new AudioRequestId(Request(ulong.MaxValue)).Sequence);
        Assert.Equal(1UL << 63, new AudioRequestId(Request(1UL << 63)).Sequence);
    }
    [Fact]
    public async Task Retired_challenge_signing_keeps_its_slot_until_terminal_and_failed_issues_retire_underlying_nonces()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.BeforeChallengeSign = () => { entered.Set(); Assert.True(release.Wait(10000)); };
        var issuance = Task.Run(() => Record.Exception(() => f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(1), 1000)));
        try
        {
            Assert.True(entered.Wait(10000)); f.Owner.ConnectionClosed(f.Origin); f.Origin = f.NewConnection(4, 5, 6);
            Assert.Equal((1UL, 0, 1, 0), f.Owner.RequestCounts);
            Assert.Throws<InvalidOperationException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(2), 1000));
            release.Set(); Assert.IsType<UnauthorizedAccessException>(await issuance.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); }
        f.BeforeChallengeSign = () => throw new IOException("Injected CNG failure");
        for (ulong sequence = 2; sequence <= 34; sequence++)
        {
            Assert.Throws<IOException>(() => f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(sequence), 1000));
            Assert.Equal((sequence, 0, 0, 0), f.Owner.RequestCounts);
        }
        f.BeforeChallengeSign = null;
        f.Admit(f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(35), 1000));
    }
    [Fact]
    public void Query_bound_status_reports_issuance_without_mutating_it_or_granting_authority()
    {
        using var f = new Fixture(); var query = Nonce(9);
        byte[] Status() => f.Owner.SignedRequestStatus(f.Origin, query, value => f.Persistence.SignHost(AudioRequestProtocol.Status(value.Snapshot)));
        var envelope = EnrollmentFraming.ParseSigned(Status());
        Assert.True(EnrollmentProtocol.Verify(f.Persistence.HostSpki, envelope.Message, envelope.Signature));
        var initial = AudioRequestProtocol.ParseStatus(envelope.Message);
        Assert.Equal(query, initial.Query); Assert.Equal(0UL, initial.IssuedThrough); Assert.Equal(AudioAuthorityPhase.Idle, initial.Phase);
        var challenge = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(1), 1000);
        var before = f.Owner.RequestCounts;
        var waiting = AudioRequestProtocol.ParseStatus(EnrollmentFraming.ParseSigned(Status()).Message);
        Assert.Equal(AudioRequestStage.AwaitingProof, waiting.Stage); Assert.Equal(Request(1), waiting.ActiveRequest);
        Assert.Equal((ulong)AudioRequestProtocol.ParseProof(challenge.Transcript).Facts.Expiry, waiting.ProofExpiryMilliseconds);
        Assert.Equal(before, f.Owner.RequestCounts); Assert.Equal(0UL, f.Owner.Snapshot().AuthorityUntil);
        f.Now = waiting.ProofExpiryMilliseconds * 10000;
        var expired = AudioRequestProtocol.ParseStatus(EnrollmentFraming.ParseSigned(Status()).Message);
        Assert.Equal(AudioRequestStage.Retiring, expired.Stage); Assert.Equal(1UL, expired.IssuedThrough);
        var successor = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(2), 1000);
        Assert.Equal((2UL, 0, 1, 1), f.Owner.RequestCounts);
        Assert.NotEqual(challenge.Nonce, successor.Nonce);
    }
    [Fact]
    public void Live_handoff_uses_the_approved_lane_and_a_closed_candidate_cannot_complete_late()
    {
        using var f = new Fixture(); f.Attach(); var original = f.Origin; var source = f.Owner.Source.Intent.Grant;
        var candidate = f.NewConnection(20, 21, 22);
        var descriptor = f.Owner.PrepareDelivery(new(2, Nonce(20), Nonce(21), Request(3), Nonce(22)), candidate);
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(candidate, AudioOperationKind.SupersedeDelivery, Request(3), 1000, descriptor));
        var pending = f.Owner.Issue(original, AudioOperationKind.SupersedeDelivery, Request(3), 1000, descriptor);
        f.Owner.ConnectionClosed(candidate);
        Assert.Equal((3UL, 2, 0, 0), f.Owner.RequestCounts);
        Assert.Null(f.Owner.Admit(original, pending.Nonce, f.Persistence.SignPhone(pending.Transcript)));
        f.Now += 10000; f.Admit(f.Owner.Issue(original, AudioOperationKind.Renew, Request(4), 1000));
        candidate = f.NewConnection(30, 31, 32);
        descriptor = f.Owner.PrepareDelivery(new(2, Nonce(30), Nonce(31), Request(5), Nonce(32)), candidate);
        f.Admit(f.Owner.Issue(original, AudioOperationKind.SupersedeDelivery, Request(5), 1000, descriptor));
        f.Origin = candidate;
        Assert.Throws<UnauthorizedAccessException>(() => f.Owner.Issue(original, AudioOperationKind.Renew, Request(6), 1000));
        f.Owner.ConnectionClosed(original); // Old native closure cannot detach the new child.
        f.Now += 10000; f.Admit(f.Owner.Issue(candidate, AudioOperationKind.Renew, Request(6), 1000));
        Assert.Equal(source, f.Owner.Source.Intent.Grant); Assert.Equal(2UL, f.Owner.Snapshot().DeliveryGeneration);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void A_valid_phone_signature_cannot_change_the_frozen_audio_binding(int substitution)
    {
        using var f = new Fixture(); var challenge = f.Owner.Issue(f.Origin, AudioOperationKind.AcquireSource, Request(1), 1000);
        var parsed = AudioRequestProtocol.ParseProof(challenge.Transcript); var b = parsed.Binding;
        var changed = new AudioProofBinding(substitution == 0 ? Nonce(9) : b.Boot, substitution == 1 ? Guid.NewGuid() : b.Grant,
            b.Generation, b.SourceDigest, b.Kind, substitution == 2 ? Request(1, 9) : b.RequestId,
            substitution == 3 ? b.Revision + 1 : b.Revision, substitution == 4 ? b.Until + 1 : b.Until);
        var altered = AudioRequestProtocol.Proof(parsed.Core, changed);
        Assert.Null(f.Owner.Admit(f.Origin, challenge.Nonce, f.Persistence.SignPhone(altered)));
        Assert.Equal(AudioAuthorityPhase.Idle, f.Owner.Snapshot().Phase); Assert.Equal((1UL, 0, 0, 0), f.Owner.RequestCounts);
    }
    [Fact]
    public void Unconfirmed_receipt_signing_is_latched_and_does_not_hold_the_protocol_slot_forever()
    {
        using var f = new Fixture(); var operation = f.Acquire(false);
        var failed = new Signer(f, () => throw new IOException("Injected signing completion failure"));
        Assert.Throws<IOException>(() => operation.SignedReceipt(failed));
        var retry = new Signer(f);
        Assert.Throws<IOException>(() => operation.SignedReceipt(retry));
        Assert.Equal(1, failed.Calls); Assert.Equal(0, retry.Calls);
        Assert.Equal((1UL, 1, 0, 0), f.Owner.RequestCounts);
        f.Owner.ClaimPreparation(operation); Assert.True(f.Owner.CompletePreparation(operation, f.Observed));
    }
    [Fact]
    public void Output_revision_invalidates_unadmitted_proofs_but_not_an_already_applied_exact_receipt()
    {
        using var f = new Fixture(); f.Attach(); f.Now += 10000;
        var old = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(3), 1000);
        f.Owner.OutputChanged(1, 1, true);
        Assert.Equal((3UL, 2, 0, 0), f.Owner.RequestCounts);
        Assert.Null(f.Owner.Admit(f.Origin, old.Nonce, f.Persistence.SignPhone(old.Transcript)));
        var current = f.Owner.Issue(f.Origin, AudioOperationKind.Renew, Request(4), 1000);
        var applied = f.Admit(current, false);
        f.Owner.OutputChanged(1, 1, false);
        var signer = new Signer(f); var receipt = applied.SignedReceipt(signer);
        f.Now += 10000; f.Owner.OutputChanged(1, 2, true);
        Assert.Equal(receipt, applied.SignedReceipt(signer)); Assert.Equal(1, signer.Calls);
        Assert.Throws<UnauthorizedAccessException>(() => applied.TakeReceiptBytesForSigning());
    }
    private sealed class Worker : IBrokerAudioWorker
    {
        public Guid Registration { get; } = Guid.NewGuid();
        public BrokerLogonIdentity Logon { get; } = new(TrustedEnrollmentProtocolTests.User, TrustedEnrollmentProtocolTests.Logon, 42, 1);
        public uint ProcessId => 101; public ulong CreationFileTime => 200;
        public byte[] InstalledProfileSha256 => Nonce(7);
        public bool IsAlive() => true;
    }
    private sealed class Caller(IBrokerRegisteredWorker worker) : IBrokerObservedCaller
    { public bool Matches(IBrokerRegisteredWorker actual) => ReferenceEquals(worker, actual); }
    private sealed class Signer(Fixture fixture, Action? before = null) : IAudioGrantSigner
    {
        internal int Calls;
        public byte[] SignAudioGrant(BrokerAudioOwner.Committed operation)
        { var bytes = operation.TakeReceiptBytesForSigning(); Calls++; before?.Invoke(); return fixture.Persistence.SignHost(bytes); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly EnrollmentDurabilityTests.Fixture Persistence = new();
        private readonly TrustedBrokerEnrollmentStore store;
        internal readonly BrokerAuthorityState Registry;
        internal readonly BrokerAudioOwner Owner;
        private readonly Worker worker;
        internal BrokerAudioOwner.Connection Origin;
        internal ulong Now = 10000000;
        internal byte[]? LastProof;
        internal Action? BeforeClockRead;
        internal Action? BeforeChallengeSign;
        internal BrokerAudioOwner.Committed? Acquisition;
        internal AudioObservedSource Observed => new(Owner.Source.Intent.DeviceKey, "owned-endpoint", Guid.NewGuid(), 2, 0x2026102502, 1, 7);
        internal Fixture()
        {
            store = Persistence.Open(); Registry = new(store); worker = new Worker();
            Owner = new(Registry, worker, new Caller(worker), Persistence.Id, store.GetSpeakerAssociation(Persistence.Id), AudioSourceBackend.Committed,
                new(1000, 200), false, new(2, 3, 4), Nonce(3), () => { var hook = BeforeClockRead; BeforeClockRead = null; hook?.Invoke(); return Now; },
                challenge => { BeforeChallengeSign?.Invoke(); return Persistence.SignHost(AudioRequestProtocol.ChallengeMessage(challenge.Transcript)); });
            Origin = NewConnection(4, 5, 6);
        }
        internal BrokerAudioOwner.Connection NewConnection(byte native, byte phone, byte nonce)
            => Owner.ObserveConnection(worker, Guid.NewGuid(), Nonce(native), Nonce(phone), Nonce(nonce));
        internal BrokerAudioOwner.Committed Admit(AudioOperationChallenge challenge, bool receipt = true)
        { LastProof = Persistence.SignPhone(challenge.Transcript); var result = Owner.Admit(Origin, challenge.Nonce, LastProof)!;
            if (receipt) _ = result.SignedReceipt(new Signer(this)); return result; }
        internal BrokerAudioOwner.Committed Acquire(bool receipt = true) => Acquisition = Admit(Owner.Issue(Origin, AudioOperationKind.AcquireSource, Nonce(1), 1000), receipt);
        internal void Attach()
        {
            var acquisition = Acquire(); Owner.ClaimPreparation(acquisition); Owner.CompletePreparation(acquisition, Observed);
            var delivery = Owner.PrepareDelivery(new(1, Nonce(4), Nonce(5), Nonce(2), Nonce(6)), Origin);
            Admit(Owner.Issue(Origin, AudioOperationKind.AttachDelivery, Nonce(2), 1000, delivery));
        }
        public void Dispose() { store.Dispose(); Persistence.Dispose(); }
    }
}

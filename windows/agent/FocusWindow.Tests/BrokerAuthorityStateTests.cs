using System.Security.Cryptography;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

public sealed class BrokerAuthorityStateTests : IDisposable
{
    private sealed class Enrollments(BrokerEnrollmentRecord record) : IBrokerEnrollments
    {
        public bool Revoked, FailPersistence;
        public BrokerEnrollmentRecord ReadActive(Guid id)
        {
            if (Revoked || id != record.EnrollmentId) throw new UnauthorizedAccessException();
            return record;
        }
        public void Revoke(Guid id)
        {
            if (FailPersistence) throw new IOException("persistence failure");
            Revoked = true;
        }
    }
    private sealed class Worker : IBrokerRegisteredWorker
    {
        public Guid Registration { get; } = Guid.NewGuid();
        public BrokerLogonIdentity Logon { get; set; } = new(
            [1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0],
            [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0], 42, 1);
        public uint ProcessId { get; set; } = 101;
        public ulong CreationFileTime { get; set; } = 200;
        public bool Alive = true;
        public bool IsAlive() => Alive;
    }
    private sealed class Caller(IBrokerRegisteredWorker worker) : IBrokerObservedCaller
    {
        public bool Matches(IBrokerRegisteredWorker other) => ReferenceEquals(worker, other);
    }
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Worker worker = new();
    private readonly Guid enrollmentId = Guid.NewGuid();
    private readonly byte[] resource = Enumerable.Repeat((byte)0x22, 32).ToArray();
    private readonly Enrollments enrollments;
    private readonly BrokerAuthorityState state;
    private readonly BrokerAuthority authority;
    private BrokerAuthorityRequest Request => new(worker.Registration, resource, BrokerProofOperation.Acquire, BrokerProofScope.Speaker, 1);

    public BrokerAuthorityStateTests()
    {
        var record = new BrokerEnrollmentRecord(1, Guid.NewGuid(), enrollmentId, new byte[32], new byte[32],
            key.ExportSubjectPublicKeyInfo(), [BrokerProofScope.Speaker], new byte[32], "S-1-5-18");
        record.Validate(record.StorageNamespace, record.HostIdentitySha256);
        enrollments = new(record);
        state = new(enrollments);
        state.RegisterLaunchedWorker(worker, enrollmentId, [new(resource, BrokerProofScope.Speaker, 1)]);
        authority = new(state.BindCaller(new Caller(worker)), () => 10000);
    }
    private BrokerOperationAdmission Admission()
    {
        var challenge = authority.Issue(Request, 1000);
        return authority.Admit(Request, challenge.Nonce, key.SignData(challenge.Transcript,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))!;
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("generation")]
    [InlineData("process")]
    [InlineData("logon")]
    [InlineData("closed")]
    public void Held_admission_is_rechecked_at_dispatch_against_real_registry(string changed)
    {
        var admitted = Admission();
        if (changed == "revoke") authority.RevokeEnrollment(enrollmentId);
        if (changed == "generation") state.AdvanceOwnerGeneration(worker.Registration, resource, BrokerProofScope.Speaker, 1, 2);
        if (changed == "process") worker.CreationFileTime++;
        if (changed == "logon") worker.Logon = new(worker.Logon.UserSid, worker.Logon.LogonSid, 43, 1);
        if (changed == "closed") state.CloseRegistration(worker.Registration);
        var called = false;
        Assert.Throws<UnauthorizedAccessException>(() => admitted.ExecuteOnce(_ => called = true));
        Assert.False(called);
    }

    [Fact]
    public void Revocation_failure_denies_current_process_without_claiming_durable_success()
    {
        var admitted = Admission();
        enrollments.FailPersistence = true;
        Assert.Throws<IOException>(() => authority.RevokeEnrollment(enrollmentId));
        Assert.False(enrollments.Revoked);
        Assert.Throws<UnauthorizedAccessException>(() => admitted.ExecuteOnce(_ => true));
        Assert.Throws<UnauthorizedAccessException>(() => authority.Issue(Request, 1000));
    }

    [Fact]
    public void Restart_does_not_restore_active_worker_authority()
    {
        var restarted = new BrokerAuthority(new BrokerAuthorityState(enrollments).BindCaller(new Caller(worker)), () => 10000);
        Assert.Throws<UnauthorizedAccessException>(() => restarted.Issue(Request, 1000));
        authority.RevokeEnrollment(enrollmentId);
        Assert.Throws<UnauthorizedAccessException>(() => new BrokerAuthorityState(enrollments)
            .RegisterLaunchedWorker(worker, enrollmentId, [new(resource, BrokerProofScope.Speaker, 1)]));
    }

    [Fact]
    public void Resource_scope_generation_and_process_incarnation_are_not_nominated_by_requests()
    {
        Assert.Throws<UnauthorizedAccessException>(() => authority.Issue(Request with { Scope = BrokerProofScope.Display }, 1000));
        Assert.Throws<UnauthorizedAccessException>(() => authority.Issue(Request with { Resource = new byte[32] }, 1000));
        Assert.Throws<UnauthorizedAccessException>(() => authority.Issue(Request with { ExpectedOwnerGeneration = 2 }, 1000));
        Assert.Throws<UnauthorizedAccessException>(() => state.RegisterLaunchedWorker(new Worker(), enrollmentId,
            [new(resource, BrokerProofScope.Camera, 1)]));
    }

    [Fact]
    public void Revoke_does_not_wait_for_a_native_callback_and_later_dispatch_is_denied()
    {
        var first = Admission();
        var waiting = Admission();
        first.ExecuteOnce(_ => { authority.RevokeEnrollment(enrollmentId); return true; });
        Assert.Throws<UnauthorizedAccessException>(() => waiting.ExecuteOnce(_ => true));
    }

    [Fact]
    public void Registration_id_and_phone_proof_do_not_authorize_a_different_pipe_process()
    {
        var rogue = new BrokerAuthority(state.BindCaller(new Caller(new Worker())), () => 10000);
        Assert.Throws<UnauthorizedAccessException>(() => rogue.Issue(Request, 1000));
        Assert.Throws<UnauthorizedAccessException>(() => rogue.RevokeEnrollment(enrollmentId));
        Assert.False(enrollments.Revoked);
    }

    public void Dispose() => key.Dispose();
}

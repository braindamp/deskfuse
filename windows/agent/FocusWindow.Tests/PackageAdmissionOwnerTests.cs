using DeskFuse.DriverTestOwner;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

// Real registry/coordinator, package streams and verification composition; only
// SYSTEM/Windows trust and protected-root establishment are injected here.
public sealed class PackageAdmissionOwnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deskfuse-owner-test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] policy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-test-policy.json"));
    private sealed class Clock : IDriverTestClock { public long Milliseconds => 1000; }
    private static WorkerIdentity Worker(WorkerRole role) => new(1000 + (int)role, 123456789, Guid.NewGuid(), role);

    private sealed class Backend : IWindowsCatalogTrustBackend
    {
        internal static readonly byte[] Der = [1, 2, 3, 4];
        internal bool Reject;
        internal int CatalogCalls;
        internal Action? DuringVerification;
        public void RequireSystemContext() { }
        public void RequireExpectedCatalog(FileStream inf, string catalogName) { }
        public VerifiedSigner VerifyCatalog(FileStream catalog, PackageProfile profile)
        {
            CatalogCalls++;
            DuringVerification?.Invoke();
            if (Reject) throw new WindowsTrustException(unchecked((int)0x800B010C), "revoked");
            return new(Der);
        }
        public (VerifiedSigner Signer, string CatalogHash) VerifyMember(FileStream catalog, FileStream member,
            PackageProfile profile) => (new(Der), new string('a', 64));
        public bool IsLocalMachineTrustedPublisher(byte[] certificateDer) => true;
    }

    private sealed class Store(string root, string packageDirectory, byte[] verificationBytes) : IProtectedAdmissionStore
    {
        public RunRegistry Registry { get; } = new(root);
        internal byte[] VerificationBytes = verificationBytes;
        internal bool IdentityInvalid;
        internal int ActiveScopes;
        public IProtectedAdmissionScope Acquire(RunManifest manifest)
        {
            ActiveScopes++;
            return new Scope(this, packageDirectory);
        }
        private sealed class Scope(Store store, string packageDirectory) : IProtectedAdmissionScope
        {
            private bool disposed;
            public LockedPackage OpenPackage(PackageApproval approval) => LockedPackage.Open(packageDirectory, approval);
            public byte[] ReadVerificationRecord() => store.VerificationBytes.ToArray();
            public void ValidateHeldIdentity()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (store.IdentityInvalid) throw new InvalidDataException("changed protected identity");
            }
            public void Dispose() { if (disposed) return; disposed = true; store.ActiveScopes--; }
        }
    }

    private (RunJournal Journal, Store Store, Backend Backend, PackageApproval Approval) Setup(
        Func<PackageVerificationRecord, PackageVerificationRecord>? modify = null,
        Func<byte[], byte[]>? encode = null)
    {
        var packageDirectory = Path.Combine(root, "candidate");
        Directory.CreateDirectory(packageDirectory);
        var files = new[] { "FocusIdd.cat", "FocusIdd.dll", "FocusIdd.inf" }.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name + " test-only payload");
            File.WriteAllBytes(Path.Combine(packageDirectory, name), bytes);
            return new PackageFile(name, ContractJson.Hash(bytes));
        }).ToArray();
        var approval = new PackageApproval(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(files).Digest(), files, "FocusIdd.cat", files[0].Sha256,
            [ContractJson.Hash(Backend.Der)], 1, "S-1-5-21-1", PackageApprovalState.Available, null);
        var approvalBytes = ContractJson.Write(approval);
        Directory.CreateDirectory(Path.Combine(root, "approvals"));
        File.WriteAllBytes(Path.Combine(root, "approvals", approval.ApprovalId.ToString("N") + ".json"), approvalBytes);
        var id = Guid.NewGuid();
        var evidence = new CatalogVerificationEvidence(approval.Profile, approval.PackageContentDigest,
            approval.CatalogRawSha256, ContractJson.Hash(Backend.Der), 1,
            files.Skip(1).Select(f => new CatalogMemberProof(f.Path, f.Sha256, new string('a', 64))).ToArray());
        var record = PackageVerificationRecord.FromEvidence(id, approval.ApprovalId, ContractJson.Hash(approvalBytes), evidence);
        var recordBytes = ContractJson.Write(modify?.Invoke(record) ?? record);
        recordBytes = encode?.Invoke(recordBytes) ?? recordBytes;
        var binding = new PackageAdmissionBinding(approval.Profile, approval.ApprovalId, ContractJson.Hash(approvalBytes),
            approval.PackageContentDigest, ContractJson.Hash(recordBytes));
        var manifest = new RunManifest(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            ContractJson.Hash(policy), new() { ["helper.exe"] = new string('a', 64) },
            RunManifest.WatchName(id), RunManifest.BootName(id), binding);
        var store = new Store(root, packageDirectory, recordBytes);
        var journal = store.Registry.Reserve(manifest);
        return (journal, store, new Backend(), approval);
    }

    private RunCoordinator Coordinator(RunJournal journal, Store store, Backend backend)
    {
        var owner = new PackageAdmissionOwner(store, new WindowsCatalogVerifier(backend));
        var coordinator = new RunCoordinator(journal, policy, new Clock(), owner);
        coordinator.Arm(new(journal.Manifest.RunId, Worker(WorkerRole.Watch), journal.Manifest.BootTask, true, true));
        return coordinator;
    }

    [Fact]
    public void Trusted_preparation_binds_exact_approval_run_and_final_verification_bytes()
    {
        var fixture = Setup();
        using var package = LockedPackage.Open(Path.Combine(root, "candidate"), fixture.Approval);
        var approvalBytes = ContractJson.Write(fixture.Approval);
        var prepared = PackageAdmissionPreparation.Prepare(fixture.Journal.Manifest.RunId, approvalBytes, package,
            new WindowsCatalogVerifier(fixture.Backend));
        Assert.Equal(fixture.Journal.Manifest.PackageAdmission, prepared.Binding);
        Assert.Equal(fixture.Store.VerificationBytes, prepared.RecordBytes);
        Assert.Throws<InvalidDataException>(() => PackageAdmissionPreparation.Prepare(Guid.Empty, approvalBytes, package,
            new WindowsCatalogVerifier(fixture.Backend)));
        var ambiguous = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1," +
            System.Text.Encoding.UTF8.GetString(approvalBytes)[1..]);
        Assert.Throws<InvalidDataException>(() => PackageAdmissionPreparation.Prepare(Guid.NewGuid(), ambiguous, package,
            new WindowsCatalogVerifier(fixture.Backend)));
    }

    [Fact]
    public void Bound_record_fresh_verification_and_custody_are_used_by_the_real_coordinator()
    {
        var fixture = Setup();
        var coordinator = Coordinator(fixture.Journal, fixture.Store, fixture.Backend);
        var creator = Worker(WorkerRole.Creator);
        coordinator.RegisterCreator(creator);
        Assert.Equal(0, fixture.Store.ActiveScopes);
        coordinator.Execute(creator, NativeOperation.StagePackage, _ =>
        {
            Assert.Equal(1, fixture.Store.ActiveScopes);
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(root, "candidate", "FocusIdd.dll"), "changed"));
            return new(true);
        });
        Assert.Equal(2, fixture.Backend.CatalogCalls); // fresh at registration and staging
        Assert.Equal(0, fixture.Store.ActiveScopes);
        Assert.Empty(coordinator.Snapshot().Pending);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("approval")]
    [InlineData("approvalHash")]
    [InlineData("context")]
    [InlineData("outcome")]
    [InlineData("signer")]
    [InlineData("member")]
    public void A_canonical_but_wrong_record_cannot_assert_authority(string field)
    {
        var fixture = Setup(record => field switch
        {
            "run" => record with { RunId = Guid.NewGuid() },
            "approval" => record with { ApprovalId = Guid.NewGuid() },
            "approvalHash" => record with { ApprovalSha256 = new string('b', 64) },
            "context" => record with { VerificationContext = "CurrentUser" },
            "outcome" => record with { Outcome = "Unverified" },
            "signer" => record with { SignerCertificateDerSha256 = new string('b', 64) },
            "member" => record with { Members = [record.Members[0], record.Members[0]] },
            _ => throw new ArgumentException(nameof(field))
        });
        var coordinator = Coordinator(fixture.Journal, fixture.Store, fixture.Backend);
        Assert.Throws<InvalidDataException>(() => coordinator.RegisterCreator(Worker(WorkerRole.Creator)));
        Assert.True(coordinator.Snapshot().Closing);
        Assert.Equal(0, fixture.Store.ActiveScopes);
    }

    [Fact]
    public void Duplicate_json_fields_and_changed_record_bytes_are_denied()
    {
        var fixture = Setup(encode: bytes => System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":1," + System.Text.Encoding.UTF8.GetString(bytes)[1..]));
        var coordinator = Coordinator(fixture.Journal, fixture.Store, fixture.Backend);
        Assert.Throws<InvalidDataException>(() => coordinator.RegisterCreator(Worker(WorkerRole.Creator)));
        Assert.Equal(0, fixture.Backend.CatalogCalls);
        Assert.Equal(0, fixture.Store.ActiveScopes);
    }

    [Fact]
    public void Revocation_during_windows_verification_fences_final_dispatch()
    {
        var fixture = Setup();
        var coordinator = Coordinator(fixture.Journal, fixture.Store, fixture.Backend);
        var creator = Worker(WorkerRole.Creator);
        coordinator.RegisterCreator(creator);
        fixture.Backend.DuringVerification = () => fixture.Store.Registry.RevokeApproval(fixture.Approval.ApprovalId);
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => coordinator.Execute(creator, NativeOperation.StagePackage,
            _ => { calls++; return new(true); }));
        Assert.Equal(0, calls);
        Assert.True(coordinator.Snapshot().Closing);
        Assert.Empty(coordinator.Snapshot().Pending);
        Assert.Equal(0, fixture.Store.ActiveScopes);
    }

    [Fact]
    public void Trust_loss_and_missing_record_do_not_block_existing_exact_run_cleanup()
    {
        var fixture = Setup();
        var coordinator = Coordinator(fixture.Journal, fixture.Store, fixture.Backend);
        var creator = Worker(WorkerRole.Creator);
        coordinator.RegisterCreator(creator);
        fixture.Backend.Reject = true;
        Assert.Throws<WindowsTrustException>(() => coordinator.Execute(creator, NativeOperation.StagePackage, _ => new(true)));
        fixture.Store.VerificationBytes = [];
        coordinator.ObserveExitedWorker(creator);
        coordinator.Execute(Worker(WorkerRole.Recovery), NativeOperation.RemoveDevice,
            _ => new(true, ParentAndSubtreeAbsent: true, EnumerationSucceeded: true));
        Assert.True(coordinator.TryRetire(m => new(m.RunId, m.InstanceId, true, true)));
        Assert.Equal(0, fixture.Store.ActiveScopes);
    }

    [Fact]
    public void Changed_held_identity_before_commit_cannot_dispatch()
    {
        var fixture = Setup();
        var owner = new PackageAdmissionOwner(fixture.Store, new WindowsCatalogVerifier(fixture.Backend));
        using var lease = owner.AcquireForCreation(fixture.Journal.Manifest, NativeOperation.StagePackage);
        fixture.Store.IdentityInvalid = true;
        var calls = 0;
        Assert.Throws<InvalidDataException>(() => lease.CommitAdmission(() => ++calls));
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected cleanup root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

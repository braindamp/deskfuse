using System.Runtime.Versioning;
using System.Text;
using DeskFuse.DriverTestOwner;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

// Executes production preparation/copy/journal/approval logic. Files are ordinary
// test fixtures; SYSTEM storage, runner retention, Scheduler and trust are mocked.
[SupportedOSPlatform("windows")]
public sealed class TrustedMaintenanceSetupTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "deskfuse-setup-" + Guid.NewGuid().ToString("N"));
    private readonly Store store;
    private readonly TrustedMaintenanceSetup setup;
    private readonly string code, package;
    private readonly MaintenanceSetupApproval approval;
    private readonly PackageApproval packageApproval;

    public TrustedMaintenanceSetupTests()
    {
        store = new(Path.Combine(temporary, "private"));
        setup = new(store);
        code = Path.Combine(temporary, "source-code"); package = Path.Combine(temporary, "source-package");
        Directory.CreateDirectory(code); Directory.CreateDirectory(package);
        var members = new[] { "FocusIdd.cat", "FocusIdd.dll", "FocusIdd.inf" }.Select(name =>
        {
            var bytes = Encoding.UTF8.GetBytes("NOT A DRIVER: " + name);
            File.WriteAllBytes(Path.Combine(package, name), bytes);
            return new PackageFile(name, ContractJson.Hash(bytes));
        }).ToArray();
        packageApproval = new(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(members).Digest(), members, "FocusIdd.cat", members[0].Sha256,
            [ContractJson.Hash(Backend.Der)], 1, "S-1-5-21-1", PackageApprovalState.Available, null);
        var hashes = new Dictionary<string, string>();
        foreach (var member in new[] { "DeskFuse.DriverTestOwner.exe", "DeskFuse.DriverTestOwner.dll", "DeskFuse.DriverTestOwner.Core.dll",
            "DeskFuse.WindowsSecurity.dll", "DeskFuse.DriverTestOwner.deps.json", "DeskFuse.DriverTestOwner.runtimeconfig.json",
            "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "runner/maintenance-runner.psm1", "runner/safe-driver-proof.ps1",
            "runner/safety-watchdog.ps1", "runner/safety-boot-recovery.ps1", "runner/rollback-focusidd.ps1", "runner/run-proof.ps1",
            "runner/driver-install-testpc.ps1", OwnerRuntimeContract.ProbeImage })
        {
            var path = Path.Combine(code, member.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes("NOT EXECUTABLE: " + member);
            File.WriteAllBytes(path, bytes); hashes.Add(member, ContractJson.Hash(bytes));
        }
        var id = Guid.NewGuid();
        var runtime = new OwnerRuntimeContract(1, id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(1234, 123456, Guid.NewGuid(), WorkerRole.Runner), 1440, 3120, 3.5, 170, 60);
        approval = new(1, id, packageApproval.ApprovalId, "S-1-5-21-1", hashes, runtime);
    }

    [Fact]
    public void Complete_preparation_registers_only_after_exact_private_bundle_and_journal_exist()
    {
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        store.DuringRegister = manifest =>
        {
            var journal = new RunRegistry(store.DirectoryPath).Open(manifest.RunId);
            Assert.False(journal.Snapshot().Armed);
            Assert.Equal(CreatorPermit.Available, journal.Snapshot().Permit);
            approval.RequireManifest(journal.Manifest);
            foreach (var member in manifest.BundleSha256)
                Assert.Equal(member.Value, ContractJson.Hash(File.ReadAllBytes(store.PathFor("runs/" + manifest.RunId.ToString("N") + "/bundle/" + member.Key))));
            Assert.True(File.Exists(store.PathFor("runs/" + manifest.RunId.ToString("N") + "/verification.json")));
        };
        var result = setup.Prepare(approval.RunId, code, package, new WindowsCatalogVerifier(new Backend()));
        Assert.Equal(store.NamespaceId, result.NamespaceId);
        Assert.Equal(RunManifest.BootName(approval.RunId), result.BootTask);
        Assert.Equal(1, store.RegisterCalls);
        Assert.True(File.Exists(store.PathFor("active.json")));
        Assert.Equal(1, RunnerControlMailbox.Read(store.PathFor("runs/" + approval.RunId.ToString("N"))).Revision);
        Assert.Empty(File.ReadAllBytes(store.PathFor("runs/" + approval.RunId.ToString("N") + "/monitor-held.flag")));
        Assert.Throws<IOException>(() => setup.PublishFromTrustedLocalSetup(approval, packageApproval));
    }

    [Fact]
    public void Source_code_replacement_fails_without_registering_tasks_and_keeps_burned_reservation()
    {
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        File.WriteAllText(Path.Combine(code, "DeskFuse.DriverTestOwner.exe"), "REPLACED");
        Assert.Throws<InvalidDataException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
        Assert.Equal(0, store.RegisterCalls);
        Assert.True(File.Exists(store.PathFor("active.json")));
        Assert.False(new RunRegistry(store.DirectoryPath).Open(approval.RunId).Snapshot().Retired);
    }

    [Fact]
    public void Untrusted_package_or_old_maintenance_tasks_cannot_publish_an_active_run()
    {
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        store.UnresolvedTask = true;
        Assert.Throws<InvalidOperationException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
        Assert.False(File.Exists(store.PathFor("active.json")));
        store.UnresolvedTask = false;
        File.WriteAllText(Path.Combine(package, "FocusIdd.dll"), "REPLACED");
        Assert.Throws<InvalidDataException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
        Assert.False(File.Exists(store.PathFor("active.json")));
        Assert.Equal(0, store.RegisterCalls);
    }

    [Fact]
    public void Runner_exit_or_scheduler_failure_never_erases_reserved_history()
    {
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        store.DuringRegister = _ => throw new IOException("scheduler registration failed after first task");
        Assert.Throws<IOException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
        Assert.True(File.Exists(store.PathFor("active.json")));
        Assert.False(new RunRegistry(store.DirectoryPath).Open(approval.RunId).Snapshot().Armed);
        Assert.Throws<IOException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
    }

    [Fact]
    public void Dead_prepared_runner_causes_no_staging_or_task_registration()
    {
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        store.RunnerExited = true;
        Assert.Throws<InvalidOperationException>(() => setup.Prepare(approval.RunId, code, package, new(new Backend())));
        Assert.False(Directory.Exists(store.PathFor("staging-" + approval.RunId.ToString("N"))));
        Assert.Equal(0, store.RegisterCalls);
    }

    [Fact]
    public void Receipts_generated_files_and_extra_code_do_not_become_helper_approval()
    {
        Assert.Throws<InvalidDataException>(() => (approval with
        {
            CodeSha256 = new(approval.CodeSha256) { ["runtime.json"] = new string('a', 64) }
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (approval with
        {
            CodeSha256 = new(approval.CodeSha256) { ["package/FocusIdd.inf"] = new string('a', 64) }
        }).Validate());
        setup.PublishFromTrustedLocalSetup(approval, packageApproval);
        File.WriteAllText(Path.Combine(code, "unapproved.dll"), "unapproved source neighbor");
        setup.Prepare(approval.RunId, code, package, new(new Backend()));
        Assert.False(File.Exists(store.PathFor("runs/" + approval.RunId.ToString("N") + "/bundle/unapproved.dll")));
    }

    [Theory]
    [InlineData("Desklink-FocusIdd-Safety-legacy")]
    [InlineData("DeskFuse-FocusIdd-Recover-old")]
    public void Legacy_and_existing_run_tasks_require_reconciliation(string name) =>
        Assert.Throws<InvalidOperationException>(() => WindowsRecoveryTasks.RequireNotMaintenanceTask(name));

    [Fact]
    public void Preflight_never_treats_unrelated_physical_or_root_displays_as_owned_devices()
    {
        WindowsMaintenancePreflight.RequireNoLegacyIdentity(@"PCI\PHYSICAL\1", RunManifest.DisplayClass, [@"Root\OtherDisplay"]);
        WindowsMaintenancePreflight.RequireNoLegacyIdentity(@"ROOT\DISPLAY\001", RunManifest.DisplayClass, [@"Root\OtherDisplay"]);
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenancePreflight.RequireNoLegacyIdentity(
            @"ROOT\DISPLAY\002", RunManifest.DisplayClass, [@"Root\FocusIdd"]));
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenancePreflight.RequireNoLegacyIdentity(
            @"ROOT\FOCUSIDD\003", RunManifest.DisplayClass, null));
        Assert.Throws<InvalidDataException>(() => WindowsMaintenancePreflight.RequireNoLegacyIdentity(
            @"ROOT\DISPLAY\004", RunManifest.DisplayClass, null));
    }

    private sealed class Backend : IWindowsCatalogTrustBackend
    {
        internal static readonly byte[] Der = [1, 2, 3];
        public void RequireSystemContext() { }
        public void RequireExpectedCatalog(FileStream inf, string name) { }
        public VerifiedSigner VerifyCatalog(FileStream file, PackageProfile profile) => new(Der);
        public (VerifiedSigner Signer, string CatalogHash) VerifyMember(FileStream catalog, FileStream member, PackageProfile profile) => (new(Der), new string('a', 64));
        public bool IsLocalMachineTrustedPublisher(byte[] der) => true;
    }

    private sealed class Store : ITrustedMaintenanceSetupStorage
    {
        public string DirectoryPath { get; }
        public Guid NamespaceId { get; } = Guid.NewGuid();
        internal bool RunnerExited, UnresolvedTask;
        internal int RegisterCalls;
        internal Action<RunManifest>? DuringRegister;
        internal Store(string root)
        {
            DirectoryPath = root;
            Directory.CreateDirectory(Path.Combine(root, "approvals"));
            Directory.CreateDirectory(Path.Combine(root, "runs"));
        }
        internal string PathFor(string relative) => SecureMaintenanceRoot.ResolveRelative(DirectoryPath, relative);
        public void ValidateHeldIdentity() { }
        public IDisposable OpenDirectory(string relative) => Directory.Exists(PathFor(relative)) ? new Lease() : throw new DirectoryNotFoundException();
        public IDisposable CreateProtectedDirectory(string relative)
        {
            if (Directory.Exists(PathFor(relative))) throw new IOException("No adoption");
            Directory.CreateDirectory(PathFor(relative)); return new Lease();
        }
        public FileStream OpenProtectedRead(string relative) => new(PathFor(relative), FileMode.Open, FileAccess.Read, FileShare.Read);
        public FileStream CopyProtectedFile(string relative, Stream source, string hash, int maximum)
        {
            using (var target = new FileStream(PathFor(relative), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            { PrivateFileCopy.CopyAndVerify(source, target, hash, maximum); target.Flush(true); }
            return OpenProtectedRead(relative);
        }
        public ISetupRunner RetainRunner(WorkerIdentity identity) => new Runner(this);
        public void RequireNoUnresolvedTasks() { if (UnresolvedTask) throw new InvalidOperationException("unknown task"); }
        public void RegisterPrepared(RunManifest manifest, OwnerRuntimeContract runtime) { RegisterCalls++; DuringRegister?.Invoke(manifest); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
        private sealed class Runner(Store store) : ISetupRunner { public bool HasExited() => store.RunnerExited; public void Dispose() { } }
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(temporary)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}

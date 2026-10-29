using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>Cold trusted-installer composition. No network/legacy pairing entrypoint may call this owner.
/// Constructing it performs no bootstrap, scheduler or PnP operation.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrustedMaintenanceSetup(ITrustedMaintenanceSetupStorage root)
{
    internal TrustedMaintenanceSetup(SecureMaintenanceRoot secureRoot) : this(new WindowsMaintenanceSetupStorage(secureRoot)) { }
    // The trusted local installer must independently obtain the operator's
    // approval of these exact code/package bytes and the actual runner identity.
    // These methods deliberately have no command-line/network activation path.
    internal void PublishFromTrustedLocalSetup(MaintenanceSetupApproval setup, PackageApproval package)
    {
        setup = ContractJson.Read<MaintenanceSetupApproval>(ContractJson.Write(setup));
        package = ContractJson.Read<PackageApproval>(ContractJson.Write(package));
        setup.Validate(); package.Validate();
        if (setup.PackageApprovalId != package.ApprovalId || package.State != PackageApprovalState.Available || package.BoundRunId is not null)
            throw new InvalidDataException("Setup requires its own available one-run package authorization.");
        root.ValidateHeldIdentity();
        CopyBytes("approvals/" + package.ApprovalId.ToString("N") + ".json", ContractJson.Write(package));
        CopyBytes(MaintenanceSetupApproval.RecordPath(setup.RunId), ContractJson.Write(setup));
    }

    internal PreparedMaintenanceRun Prepare(Guid runId, string codeSourceDirectory, string packageSourceDirectory,
        WindowsCatalogVerifier verifier)
    {
        root.ValidateHeldIdentity();
        var setupBytes = Read(MaintenanceSetupApproval.RecordPath(runId), 512 * 1024);
        var setup = ContractJson.Read<MaintenanceSetupApproval>(setupBytes);
        setup.Validate();
        if (setup.RunId != runId || !ContractJson.Write(setup).AsSpan().SequenceEqual(setupBytes))
            throw new InvalidDataException("Ambiguous or mismatched local setup authorization.");
        var approvalBytes = Read("approvals/" + setup.PackageApprovalId.ToString("N") + ".json", 32768);
        var packageApproval = ContractJson.Read<PackageApproval>(approvalBytes);
        packageApproval.Validate();
        if (!ContractJson.Write(packageApproval).AsSpan().SequenceEqual(approvalBytes))
            throw new InvalidDataException("Ambiguous package authorization.");
        using var runner = root.RetainRunner(setup.Runtime.Runner);
        if (runner.HasExited()) throw new InvalidOperationException("Approved runner has exited.");
        root.RequireNoUnresolvedTasks();
        var held = new List<IDisposable>();
        try
        {
            // Candidate verification precedes reservation. Do not pre-create a
            // runs/<id> directory without a journal: that is unresolved history.
            var stage = "staging-" + runId.ToString("N");
            held.Add(root.CreateProtectedDirectory(stage));
            foreach (var member in packageApproval.Files)
                held.Add(Import(stage + "/" + member.Path, packageSourceDirectory, member.Path, member.Sha256));
            using var package = LockedPackage.Open(SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, stage), packageApproval,
                path => root.OpenProtectedRead(stage + "/" + Path.GetFileName(path)));
            var verified = PackageAdmissionPreparation.Prepare(runId, approvalBytes, package, verifier);
            var policy = ContractJson.Write(new SafetyPolicy(1, [60000, 180000, 300000], [85000, 205000, 325000],
                60000, 325000, 350000, 10000, 1200000));
            var runtimeBytes = ContractJson.Write(setup.Runtime);
            var bundle = new Dictionary<string, string>(setup.CodeSha256, StringComparer.OrdinalIgnoreCase)
            {
                [OwnerRuntimeContract.BundleName] = ContractJson.Hash(runtimeBytes),
                [MaintenanceSetupApproval.BundleName] = ContractJson.Hash(setupBytes)
            };
            foreach (var member in packageApproval.Files) bundle.Add("package/" + member.Path, member.Sha256);
            var manifest = new RunManifest(2, runId, RunManifest.DeviceId(runId), @"Root\FocusIdd", RunManifest.DisplayClass,
                ContractJson.Hash(policy), bundle, RunManifest.WatchName(runId), RunManifest.BootName(runId), verified.Binding);
            setup.RequireManifest(manifest);
            var registry = new RunRegistry(root.DirectoryPath);
            var journal = registry.Reserve(manifest); // burns approval before publishing any executable task
            var relative = "runs/" + runId.ToString("N");
            held.Add(root.OpenDirectory(relative));
            held.Add(root.CreateProtectedDirectory(relative + "/bundle"));
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            foreach (var member in bundle.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var parent = Path.GetDirectoryName(member.Key.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
                CreateParents(relative + "/bundle", parent, directories, held);
                if (member.Key == OwnerRuntimeContract.BundleName) CopyBytes(relative + "/bundle/" + member.Key, runtimeBytes);
                else if (member.Key == MaintenanceSetupApproval.BundleName) CopyBytes(relative + "/bundle/" + member.Key, setupBytes);
                else if (member.Key.StartsWith("package/", StringComparison.Ordinal))
                {
                    using var input = root.OpenProtectedRead(stage + "/" + member.Key[8..]);
                    held.Add(root.CopyProtectedFile(relative + "/bundle/" + member.Key, input, member.Value, 256 * 1024 * 1024));
                }
                else held.Add(Import(relative + "/bundle/" + member.Key, codeSourceDirectory, member.Key, member.Value));
            }
            CopyBytes(relative + "/policy.json", policy);
            CopyBytes(relative + "/verification.json", verified.RecordBytes);
            CopyBytes(relative + "/" + RunnerControlMailbox.FileName,
                ContractJson.Write(new RunnerControl(1, runId, setup.Runtime.Runner.Nonce, 1)));
            CopyBytes(relative + "/" + WatchStatus.FileName, "{}"u8.ToArray());
            CopyBytes(relative + "/" + RecoveryReport.FileName, "{}"u8.ToArray());
            // System-born ownership is retained when the elevated probe later
            // writes readiness. An empty placeholder is never proof-ready.
            CopyBytes(relative + "/monitor-held.flag", []);
            if (runner.HasExited()) throw new InvalidOperationException("Approved runner exited during preparation.");
            root.ValidateHeldIdentity();
            root.RegisterPrepared(manifest, setup.Runtime);
            return new(root.NamespaceId, runId, journal.Manifest.WatchTask, journal.Manifest.BootTask);
        }
        finally { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
        // Any failure leaves its exact staging/reservation/tasks for explicit
        // reconciliation. Never erase uncertainty or recycle the burned approval.
    }

    private void CreateParents(string bundle, string relative, HashSet<string> created, List<IDisposable> held)
    {
        if (created.Contains(relative)) return;
        var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        CreateParents(bundle, parent, created, held);
        held.Add(root.CreateProtectedDirectory(bundle + "/" + relative));
        created.Add(relative);
    }

    private FileStream Import(string target, string sourceDirectory, string member, string hash)
    {
        var source = SecureMaintenanceRoot.ResolveRelative(Path.GetFullPath(sourceDirectory), member);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        return root.CopyProtectedFile(target, input, hash, 256 * 1024 * 1024);
    }

    private byte[] Read(string relative, int maximum)
    {
        using var file = root.OpenProtectedRead(relative);
        return SecureMaintenanceRoot.ReadBounded(file, maximum);
    }

    private void CopyBytes(string relative, byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var file = root.CopyProtectedFile(relative, input, ContractJson.Hash(bytes), Math.Max(1, bytes.Length));
    }
}

internal sealed record PreparedMaintenanceRun(Guid NamespaceId, Guid RunId, string WatchTask, string BootTask);

// Only the platform boundary is injected in offline fault tests. The real
// preparation executor, copy/hash checks, registry and journal are exercised.
internal interface ISetupRunner : IDisposable { bool HasExited(); }
internal interface ITrustedMaintenanceSetupStorage
{
    string DirectoryPath { get; }
    Guid NamespaceId { get; }
    void ValidateHeldIdentity();
    IDisposable OpenDirectory(string relative);
    IDisposable CreateProtectedDirectory(string relative);
    FileStream OpenProtectedRead(string relative);
    FileStream CopyProtectedFile(string relative, Stream source, string hash, int maximum);
    ISetupRunner RetainRunner(WorkerIdentity identity);
    void RequireNoUnresolvedTasks();
    void RegisterPrepared(RunManifest manifest, OwnerRuntimeContract runtime);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMaintenanceSetupStorage(SecureMaintenanceRoot root) : ITrustedMaintenanceSetupStorage
{
    public string DirectoryPath => root.DirectoryPath;
    public Guid NamespaceId => root.NamespaceId;
    public void ValidateHeldIdentity() => root.ValidateHeldIdentity();
    public IDisposable OpenDirectory(string relative) => root.OpenDirectory(relative);
    public IDisposable CreateProtectedDirectory(string relative) => root.CreateProtectedDirectory(relative);
    public FileStream OpenProtectedRead(string relative) => root.OpenProtectedRead(relative);
    public FileStream CopyProtectedFile(string relative, Stream source, string hash, int maximum) => root.CopyProtectedFile(relative, source, hash, maximum);
    public ISetupRunner RetainRunner(WorkerIdentity identity) => new Runner(WorkerProcessMonitor.OpenExact(identity));
    public void RequireNoUnresolvedTasks()
    {
        WindowsRecoveryTasks.RequireNoUnresolvedTasks(root);
        WindowsMaintenancePreflight.RequireNoFocusIddInstances();
    }
    public void RegisterPrepared(RunManifest manifest, OwnerRuntimeContract runtime) => WindowsRecoveryTasks.RegisterPrepared(root, manifest, runtime);
    private sealed class Runner(WorkerProcessMonitor process) : ISetupRunner
    {
        public bool HasExited() => process.HasExited();
        public void Dispose() => process.Dispose();
    }
}

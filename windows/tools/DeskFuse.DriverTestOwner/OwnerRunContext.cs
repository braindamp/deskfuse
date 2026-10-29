using System.Runtime.Versioning;
using System.Security.Cryptography;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>Retains the fixed namespace, verified journal and executing helper's exact code closure.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OwnerRunContext : IDisposable
{
    private readonly List<IDisposable> custody = [];
    internal SecureMaintenanceRoot Root { get; }
    internal RunRegistry Registry { get; }
    internal RunJournal Journal { get; }
    internal RunManifest Manifest => Journal.Manifest;
    internal string RunRelative { get; }
    internal string DirectoryPath { get; }
    internal string HelperImage { get; }
    internal byte[] PolicyBytes { get; }

    private OwnerRunContext(SecureMaintenanceRoot root, Guid runId)
    {
        Root = root;
        try
        {
        Registry = new(root.DirectoryPath);
        RunRelative = "runs/" + runId.ToString("N");
        DirectoryPath = SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, RunRelative);
        custody.Add(root.OpenDirectory(RunRelative));
        Journal = Registry.Open(runId);
        _ = Journal.Snapshot();
        custody.Add(root.OpenDirectory(RunRelative + "/bundle"));
        BundleIdentity.RequireHelperProfile(Manifest.BundleSha256);
        HelperImage = Path.Combine(DirectoryPath, "bundle", "DeskFuse.DriverTestOwner.exe");
        if (!string.Equals(Environment.ProcessPath, HelperImage, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The owner must execute the exact prepared run helper image.");
        // Package disappearance or trust loss must not strand cleanup. The
        // executing helper closure is verified independently of package/probe.
        var expected = Manifest.BundleSha256.Where(p => IsHelperFile(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var actual = new List<string>();
        EnumerateCode("", actual, 0);
        BundleIdentity.RequireExactFiles(expected, actual);
        foreach (var member in expected)
        {
            var file = root.OpenProtectedRead(RunRelative + "/bundle/" + member.Key.Replace('\\', '/'));
            custody.Add(file);
            var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            file.Position = 0;
            if (hash != member.Value) throw new InvalidDataException("Prepared helper code differs from the run manifest.");
        }
        using var policy = root.OpenProtectedRead(RunRelative + "/policy.json");
        PolicyBytes = SecureMaintenanceRoot.ReadBounded(policy, 4096);
        if (ContractJson.Hash(PolicyBytes) != Manifest.PolicySha256)
            throw new InvalidDataException("Prepared safety policy differs from the run manifest.");
        ContractJson.Read<SafetyPolicy>(PolicyBytes).Validate();
        }
        catch
        {
            for (var i = custody.Count - 1; i >= 0; i--) custody[i].Dispose();
            custody.Clear();
            throw;
        }
    }

    internal static OwnerRunContext Open(Guid namespaceId, Guid runId)
    {
        var root = SecureMaintenanceRoot.OpenExisting(namespaceId);
        OwnerRunContext? context = null;
        try { context = new(root, runId); return context; }
        catch { context?.Dispose(); root.Dispose(); throw; }
    }

    private static bool IsHelperFile(string path)
    {
        var canonical = path.Replace('\\', '/');
        return !canonical.StartsWith("package/", StringComparison.OrdinalIgnoreCase) &&
            !canonical.StartsWith("probe/", StringComparison.OrdinalIgnoreCase);
    }

    private void EnumerateCode(string relative, List<string> files, int depth)
    {
        if (depth > 32 || files.Count > 4096 || custody.Count > 4096)
            throw new InvalidDataException("Prepared helper code exceeds its structural bound.");
        var folder = RunRelative + "/bundle" + (relative.Length == 0 ? "" : "/" + relative);
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(SecureMaintenanceRoot.ResolveRelative(Root.DirectoryPath, folder)))
        {
            var leaf = Path.GetFileName(entry);
            var member = relative.Length == 0 ? leaf : relative + "/" + leaf;
            if (!IsHelperFile(member + (((File.GetAttributes(entry) & FileAttributes.Directory) != 0) ? "/" : ""))) continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Helper code contains a reparse entry.");
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                custody.Add(Root.OpenDirectory(folder + "/" + leaf));
                EnumerateCode(member, files, depth + 1);
            }
            else files.Add(member);
        }
    }

    internal OwnerRuntimeContract Runtime()
    {
        using var file = Root.OpenProtectedRead(RunRelative + "/bundle/" + OwnerRuntimeContract.BundleName);
        var bytes = SecureMaintenanceRoot.ReadBounded(file, 8192);
        if (!Manifest.BundleSha256.TryGetValue(OwnerRuntimeContract.BundleName, out var hash) || ContractJson.Hash(bytes) != hash)
            throw new InvalidDataException("Runtime contract is not bound to this run.");
        var runtime = ContractJson.Read<OwnerRuntimeContract>(bytes);
        if (!ContractJson.Write(runtime).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Runtime contract is not canonical.");
        runtime.Validate(Manifest);
        return runtime;
    }

    internal void RequireIndependentSetupApproval()
    {
        using var file = Root.OpenProtectedRead(MaintenanceSetupApproval.RecordPath(Manifest.RunId));
        var bytes = SecureMaintenanceRoot.ReadBounded(file, 512 * 1024);
        var approval = ContractJson.Read<MaintenanceSetupApproval>(bytes);
        if (!ContractJson.Write(approval).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Ambiguous setup approval.");
        approval.RequireManifest(Manifest);
    }

    internal void HoldProbeCode()
    {
        var expected = Manifest.BundleSha256.Where(p => p.Key.Replace('\\', '/').StartsWith("probe/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var actual = new List<string>();
        void Visit(string relative, int depth)
        {
            if (depth > 32 || custody.Count > 4096) throw new InvalidDataException("Probe bundle exceeds structural bounds.");
            var folder = RunRelative + "/bundle/" + relative;
            custody.Add(Root.OpenDirectory(folder));
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(SecureMaintenanceRoot.ResolveRelative(Root.DirectoryPath, folder)))
            {
                var member = relative + "/" + Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Probe bundle contains a reparse entry.");
                if ((attributes & FileAttributes.Directory) != 0) Visit(member, depth + 1);
                else
                {
                    actual.Add(member);
                    var file = Root.OpenProtectedRead(RunRelative + "/bundle/" + member);
                    custody.Add(file);
                    if (!expected.TryGetValue(member, out var hash) || Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != hash)
                        throw new InvalidDataException("Probe code differs from its approved bundle.");
                    file.Position = 0;
                }
            }
        }
        Visit("probe", 0);
        BundleIdentity.RequireExactFiles(expected, actual);
    }


    internal RunCoordinator Coordinator(bool creation) => new(Journal, PolicyBytes, new WindowsDriverTestClock(),
        creation ? new PackageAdmissionOwner(new SecurePackageAdmissionStore(Root), new WindowsCatalogVerifier()) : null);

    public void Dispose()
    {
        for (var i = custody.Count - 1; i >= 0; i--) custody[i].Dispose();
        custody.Clear();
        Root.Dispose();
    }
}

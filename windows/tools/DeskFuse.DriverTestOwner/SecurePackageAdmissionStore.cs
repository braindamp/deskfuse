using System.Runtime.Versioning;
using System.Security.Cryptography;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>
/// The production custody store is derived from a retained secure namespace,
/// never from a path nominated by a pipe caller. The trusted installation/setup
/// path must independently approve the helper bundle; its manifest hashes are
/// consistency evidence and do not make arbitrary executable code trustworthy.
/// The caller retains the SecureMaintenanceRoot for this store's lifetime.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SecurePackageAdmissionStore : IProtectedAdmissionStore
{
    private readonly SecureMaintenanceRoot root;
    public RunRegistry Registry { get; }

    internal SecurePackageAdmissionStore(SecureMaintenanceRoot root)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        root.ValidateHeldIdentity();
        Registry = new RunRegistry(root.DirectoryPath);
    }

    public IProtectedAdmissionScope Acquire(RunManifest manifest)
    {
        manifest = ContractJson.Read<RunManifest>(ContractJson.Write(manifest));
        var runRelative = "runs/" + manifest.RunId.ToString("N");
        manifest.Validate(SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, runRelative));
        BundleIdentity.RequireHelperProfile(manifest.BundleSha256);
        return Scope.Open(root, manifest, runRelative);
    }

    private sealed class Scope : IProtectedAdmissionScope
    {
        private readonly SecureMaintenanceRoot root;
        private readonly RunManifest manifest;
        private readonly string runRelative;
        private readonly List<IDisposable> directories = [];
        private readonly List<FileStream> bundleFiles = [];
        private FileStream? verification;
        private bool disposed;

        private Scope(SecureMaintenanceRoot root, RunManifest manifest, string runRelative)
            => (this.root, this.manifest, this.runRelative) = (root, manifest, runRelative);

        internal static Scope Open(SecureMaintenanceRoot root, RunManifest manifest, string runRelative)
        {
            var scope = new Scope(root, manifest, runRelative);
            try
            {
                root.ValidateHeldIdentity();
                scope.directories.Add(root.OpenDirectory(runRelative));
                var bundle = runRelative + "/bundle";
                scope.directories.Add(root.OpenDirectory(bundle));
                var actual = new List<string>();
                var entries = 0;
                scope.EnumerateBundle(bundle, "", actual, 0, retainDirectories: true, ref entries);
                BundleIdentity.RequireExactFiles(manifest.BundleSha256, actual);
                foreach (var entry in manifest.BundleSha256.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var file = root.OpenProtectedRead(bundle + "/" + entry.Key.Replace('\\', '/'));
                    scope.bundleFiles.Add(file);
                    var actualHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
                    file.Position = 0;
                    if (actualHash != entry.Value)
                        throw new InvalidDataException("The protected helper/package bundle differs from the approved run manifest.");
                }
                scope.directories.Add(root.OpenDirectory(bundle + "/package"));
                scope.verification = root.OpenProtectedRead(runRelative + "/verification.json");
                scope.ValidateHeldIdentity();
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }

        private void EnumerateBundle(string bundleRelative, string relative, List<string> files, int depth,
            bool retainDirectories, ref int entries)
        {
            if (depth > 32 || directories.Count > 4096 || files.Count > 4096)
                throw new InvalidDataException("The helper bundle exceeds its structural bound.");
            var current = relative.Length == 0 ? bundleRelative : bundleRelative + "/" + relative;
            foreach (var entry in Directory.EnumerateFileSystemEntries(SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, current)))
            {
                if (++entries > 4096) throw new InvalidDataException("The helper bundle has too many entries.");
                var name = Path.GetFileName(entry);
                var child = relative.Length == 0 ? name : relative + "/" + name;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A protected helper bundle cannot contain a reparse entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var held = root.OpenDirectory(bundleRelative + "/" + child);
                    if (retainDirectories) directories.Add(held);
                    try { EnumerateBundle(bundleRelative, child, files, depth + 1, retainDirectories, ref entries); }
                    finally { if (!retainDirectories) held.Dispose(); }
                }
                else
                {
                    files.Add(child);
                    if (files.Count > 4096) throw new InvalidDataException("The helper bundle has too many files.");
                }
            }
        }

        public LockedPackage OpenPackage(PackageApproval approval)
        {
            ValidateHeldIdentity();
            foreach (var file in approval.Files)
            {
                var key = "package/" + file.Path;
                var match = manifest.BundleSha256.SingleOrDefault(p =>
                    string.Equals(p.Key.Replace('\\', '/'), key, StringComparison.OrdinalIgnoreCase));
                if (match.Key is null || match.Value != file.Sha256)
                    throw new InvalidDataException("The run bundle does not bind every approved package member.");
            }
            var relative = runRelative + "/bundle/package";
            var path = SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, relative);
            return LockedPackage.Open(path, approval, fullPath =>
                root.OpenProtectedRead(relative + "/" + Path.GetFileName(fullPath)));
        }

        public byte[] ReadVerificationRecord()
        {
            ValidateHeldIdentity();
            return SecureMaintenanceRoot.ReadBounded(verification!, 32768);
        }

        public void ValidateHeldIdentity()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            root.ValidateHeldIdentity();
            // Recheck the exact executable search surface at each boundary,
            // including payloads added by a delayed trusted population worker.
            var current = new List<string>();
            var entries = 0;
            EnumerateBundle(runRelative + "/bundle", "", current, 0, retainDirectories: false, ref entries);
            BundleIdentity.RequireExactFiles(manifest.BundleSha256, current);
            foreach (var file in bundleFiles)
            {
                if (!file.CanRead || file.SafeFileHandle.IsClosed || file.SafeFileHandle.IsInvalid)
                    throw new InvalidDataException("Protected bundle custody was lost.");
                WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
            }
            if (verification is not null)
                WindowsFileStreams.RequireOnlyUnnamedDataStream(verification.SafeFileHandle);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            verification?.Dispose();
            foreach (var file in bundleFiles) file.Dispose();
            for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
        }
    }
}

internal static class BundleIdentity
{
    private static readonly string[] RequiredHelperFiles =
    ["DeskFuse.DriverTestOwner.exe", "DeskFuse.DriverTestOwner.dll", "DeskFuse.DriverTestOwner.Core.dll", "DeskFuse.WindowsSecurity.dll",
        "DeskFuse.DriverTestOwner.deps.json", "DeskFuse.DriverTestOwner.runtimeconfig.json"];

    internal static void RequireHelperProfile(IReadOnlyDictionary<string, string> expected)
    {
        var names = expected.Keys.Select(key => key.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (RequiredHelperFiles.Any(name => !names.Contains(name)))
            throw new InvalidDataException("The fixed helper entrypoint/dependency profile is incomplete.");
    }

    internal static void RequireExactFiles(IReadOnlyDictionary<string, string> expected, IEnumerable<string> actual)
    {
        static string Canonical(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
                value.Split('/', '\\').Any(p => p is "" or "." or ".." || p.Contains(':') ||
                    p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32)))
                throw new InvalidDataException("Unsafe bundle path.");
            return value.Replace('\\', '/');
        }
        if (expected.Count is 0 or > 4096 || expected.Values.Any(value => !ContractJson.IsHash(value)))
            throw new InvalidDataException("Invalid bundle hash set.");
        var approved = expected.Keys.Select(Canonical).ToArray();
        var found = actual.Select(Canonical).ToArray();
        if (found.Length > 4096 || approved.Distinct(StringComparer.OrdinalIgnoreCase).Count() != approved.Length ||
            found.Distinct(StringComparer.OrdinalIgnoreCase).Count() != found.Length ||
            !new HashSet<string>(approved, StringComparer.OrdinalIgnoreCase).SetEquals(found))
            throw new InvalidDataException("The helper bundle contains missing, duplicate or unexpected payloads.");
    }
}

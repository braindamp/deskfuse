using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DeskFuse.WindowsSecurity;

/// <summary>
/// A fixed fresh RX deployment, independently named by protected installer configuration.
/// Hashes establish byte agreement with that approval, not approval of caller-supplied code.
/// The trusted installer is the only creator; no path adoption or ACL repair is supported.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureInstalledCodeRoot : IDisposable
{
    private readonly object gate = new();
    private readonly ProtectedDirectoryInspection anchor, directory;
    private readonly Dictionary<string, FileStream> files;
    private readonly Dictionary<string, string> hashes;
    private int references;
    private bool disposed;
    public Guid DeploymentId { get; }
    public string DirectoryPath { get; }

    private SecureInstalledCodeRoot(Guid id, string path, ProtectedDirectoryInspection anchor,
        ProtectedDirectoryInspection directory, Dictionary<string, FileStream> files, Dictionary<string, string> hashes)
        => (DeploymentId, DirectoryPath, this.anchor, this.directory, this.files, this.hashes) = (id, path, anchor, directory, files, hashes);

    public static SecureInstalledCodeRoot CreateFresh(Guid deploymentId, IReadOnlyDictionary<string, string> independentlyApprovedFiles,
        Func<string, Stream> openApprovedSource)
    {
        PrivateStorageNative.RequireSystem();
        var hashes = SnapshotApproval(independentlyApprovedFiles); ArgumentNullException.ThrowIfNull(openApprovedSource);
        var path = PathFor(deploymentId);
        using var anchor = ProtectedDirectoryInspection.OpenInstallerAnchorForBootstrap(PrivateStorageNative.InstallationAnchor());
        PrivateStorageNative.RequirePersistentAcls(path);
        PrivateStorageNative.CreateInstalledCodeDirectory(path);
        using var directory = ProtectedDirectoryInspection.OpenExistingForInspection(path);
        long total = 0;
        foreach (var item in hashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            using var source = openApprovedSource(item.Key);
            using var destination = PrivateStorageNative.CreateInstalledCodeFile(Path.Combine(path, item.Key));
            PrivateFileCopy.CopyAndVerify(source, destination, item.Value, 256 * 1024 * 1024, CancellationToken.None);
            total = checked(total + destination.Length);
            if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Installed closure exceeds its bound.");
            destination.Flush(true);
        }
        // Partial deployments are preserved after any failure and are never adopted by CreateFresh.
        return OpenExisting(deploymentId, hashes);
    }

    public static SecureInstalledCodeRoot OpenExisting(Guid deploymentId, IReadOnlyDictionary<string, string> protectedApproval)
    {
        PrivateStorageNative.RequireSystem();
        var hashes = SnapshotApproval(protectedApproval); var path = PathFor(deploymentId);
        var anchor = ProtectedDirectoryInspection.OpenInstallerAnchorForBootstrap(PrivateStorageNative.InstallationAnchor());
        ProtectedDirectoryInspection? directory = null;
        var files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            directory = ProtectedDirectoryInspection.OpenExistingForInspection(path);
            RequireExactNames(path, hashes);
            foreach (var item in hashes)
            {
                var file = PrivateStorageNative.OpenRead(Path.Combine(path, item.Key));
                try
                {
                    directory.InspectHeldFile(file.SafeFileHandle, item.Key);
                    WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
                    if (file.Length is < 1 or > 256 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() != item.Value)
                        throw new InvalidDataException("Installed code differs from the protected approval.");
                    file.Position = 0; files.Add(item.Key, file);
                }
                catch { file.Dispose(); throw; }
            }
            return new(deploymentId, path, anchor, directory, files, hashes);
        }
        catch { foreach (var file in files.Values) file.Dispose(); directory?.Dispose(); anchor.Dispose(); throw; }
    }

    public CodeLease Acquire(string fixedEntrypoint)
    {
        lock (gate)
        {
            Revalidate();
            if (!files.ContainsKey(fixedEntrypoint) || !fixedEntrypoint.EndsWith(".exe", StringComparison.Ordinal))
                throw new InvalidDataException("Entrypoint is outside the approved installed closure.");
            references = checked(references + 1); return new(this, fixedEntrypoint);
        }
    }
    public void RequireSignedEntrypoint(string fixedEntrypoint)
    {
        lock (gate)
        {
            Revalidate();
            if (!fixedEntrypoint.EndsWith(".exe", StringComparison.Ordinal) || !files.TryGetValue(fixedEntrypoint, out var file))
                throw new InvalidDataException("Signature target is outside the approved closure.");
            InstalledCodeSignature.RequireValid(file);
        }
    }
    private void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this); PrivateStorageNative.RequireSystem();
        RequireExactNames(DirectoryPath, hashes);
        foreach (var item in files)
        { directory.InspectHeldFile(item.Value.SafeFileHandle, item.Key); WindowsFileStreams.RequireOnlyUnnamedDataStream(item.Value.SafeFileHandle); }
        // Read-only/no-write-sharing custody spans the complete closure; no data copy or repeated hashing per control call.
    }
    private static void RequireExactNames(string path, Dictionary<string, string> expected)
    {
        var actual = Directory.GetFileSystemEntries(path).Select(Path.GetFileName).ToArray();
        if (actual.Length != expected.Count || actual.Any(name => name is null || !expected.ContainsKey(name)))
            throw new InvalidDataException("Unexpected or missing installed dependency.");
    }
    private static Dictionary<string, string> SnapshotApproval(IReadOnlyDictionary<string, string> values)
    {
        if (values is null || values.Count is < 1 or > 4096) throw new InvalidDataException("Invalid installed closure.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            _ = SecurePrivateStorageRoot.ResolveRelative("C:\\", pair.Key);
            if (pair.Key.Contains('/') || pair.Key.Contains('\\') || !PrivateStorageJson.IsHash(pair.Value) || !result.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException("Invalid flat x64 installed closure.");
        }
        return result;
    }
    private static string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Missing independently configured deployment.");
        return Path.Combine(PrivateStorageNative.InstallationAnchor(), $"DeskFuse-Deployment.{id:N}");
    }
    public sealed class CodeLease : IDisposable
    {
        private SecureInstalledCodeRoot? owner;
        private readonly string entrypoint;
        internal CodeLease(SecureInstalledCodeRoot owner, string entrypoint) { this.owner = owner; this.entrypoint = entrypoint; }
        public string ExecutablePath => Path.Combine(RequireOwner().DirectoryPath, entrypoint);
        public string WorkingDirectory => RequireOwner().DirectoryPath;
        public void RevalidateHeldProfile() { var current = RequireOwner(); lock (current.gate) current.Revalidate(); }
        private SecureInstalledCodeRoot RequireOwner() => owner ?? throw new ObjectDisposedException(nameof(CodeLease));
        public void Dispose()
        { var current = Interlocked.Exchange(ref owner, null); if (current is not null) lock (current.gate) current.references--; }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (references != 0) throw new InvalidOperationException("Retire launched owners before releasing installed-code custody.");
            disposed = true;
            foreach (var file in files.Values) file.Dispose(); directory.Dispose(); anchor.Dispose();
        }
    }
}

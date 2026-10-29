using System.Security.Cryptography;
using System.Collections.ObjectModel;

namespace DeskFuse.DriverTestOwner.Core;

/// <summary>
/// Read-locks and hashes the exact final FocusIdd payload. The platform owner
/// must separately establish a protected, non-reparse parent hierarchy and
/// validate its access controls before using paths for trust or native work.
/// This class does not create or certify such a root.
/// </summary>
public sealed class LockedPackage : IDisposable
{
    private static readonly string[] Names = ["FocusIdd.cat", "FocusIdd.dll", "FocusIdd.inf"];
    private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public IReadOnlyDictionary<string, FileStream> Files { get; }

    private LockedPackage() => Files = new ReadOnlyDictionary<string, FileStream>(files);

    public static LockedPackage Open(string directory, PackageApproval approval) => Open(directory, approval,
        path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    /// <summary>
    /// The trusted platform scope supplies checked, owned read handles. It must
    /// retain protected directory identity through use; this callback is not an
    /// authorization boundary for external callers.
    /// </summary>
    public static LockedPackage Open(string directory, PackageApproval approval, Func<string, FileStream> openProtectedRead)
    {
        approval.Validate();
        ArgumentNullException.ThrowIfNull(openProtectedRead);
        var result = new LockedPackage();
        try
        {
            var root = Path.GetFullPath(directory);
            if ((File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("Package directory must not be a reparse point.");
            ValidateNames(root);
            foreach (var name in Names)
            {
                var path = Path.Combine(root, name);
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("Package payload must be a regular non-reparse file.");
                // Windows sharing rules reject concurrent writers or delete/rename
                // handles. Read sharing permits Windows trust and SetupAPI readers.
                var file = openProtectedRead(path) ?? throw new InvalidDataException("Missing protected package handle.");
                result.files.Add(name, file);
                if (!file.CanRead || !file.CanSeek || file.SafeFileHandle.IsClosed || file.SafeFileHandle.IsInvalid ||
                    !string.Equals(Path.GetFullPath(file.Name), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Protected opener returned a different or unreadable package file.");
                WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
                var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
                file.Position = 0;
                var expected = approval.Files.Single(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase));
                if (hash != expected.Sha256) throw new InvalidDataException("Package bytes differ from approval.");
            }
            ValidateNames(root);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void ValidateNames(string root)
    {
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray();
        if (names.Length != Names.Length || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Names.Length ||
            Names.Any(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unexpected or duplicate package payload.");
    }

    /// <summary>
    /// Recheck the currently held file identities. NTFS sharing applies per
    /// stream: this observation cannot replace protected file/directory ACLs.
    /// </summary>
    public void ValidateStreams()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var file in files.Values) WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var file in files.Values) file.Dispose();
        files.Clear();
    }
}

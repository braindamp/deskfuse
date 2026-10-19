namespace FocusWindow.Core.Session;

public sealed record FileEntry(string Path, string Name, bool IsDirectory, long Size);
public sealed record FileListing(string Path, string? Parent, IReadOnlyList<FileEntry> Entries, bool Truncated, int Offset, int NextOffset);

/// <summary>
/// Lists files in the signed-in user's security context, after an explicitly approved session.
/// No installer/SYSTEM impersonation and no artificial Documents/Downloads sandbox.
/// Device namespaces are not filesystem navigation targets; ordinary UNC shares are allowed.
/// </summary>
public static class UserFileBrowser
{
    public const int PageLimit = 200;
    public static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("Expected an absolute drive or UNC path");
        return System.IO.Path.GetFullPath(path);
    }

    public static FileListing List(string? requested, int offset = 0)
    {
        if (offset < 0 || offset > 1_000_000) throw new ArgumentException("Invalid folder offset");
        if (string.IsNullOrEmpty(requested))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FileEntry(d.RootDirectory.FullName, d.Name, true, 0))
                .ToArray();
            // Accessible mapped drives are included; a typed UNC path is also supported.
            return new FileListing("", null, drives, false, 0, 0);
        }
        var path = FullPath(requested);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Folder is unavailable");
        var items = new List<FileEntry>();
        var truncated = false;
        var scanned = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(path).Skip(offset))
        {
            if (scanned == PageLimit) { truncated = true; break; }
            scanned++;
            try
            {
                var attr = File.GetAttributes(child);
                var dir = (attr & FileAttributes.Directory) != 0;
                items.Add(new FileEntry(child, System.IO.Path.GetFileName(child.TrimEnd('\\', '/')),
                    dir, dir ? 0 : new FileInfo(child).Length));
            }
            catch (IOException) { } // disappearing entry is not a session failure
            catch (UnauthorizedAccessException) { } // permissions still belong to Windows
        }
        var root = System.IO.Path.GetPathRoot(path);
        var parent = string.Equals(path.TrimEnd('\\'), root?.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase) ? "" : Directory.GetParent(path)?.FullName ?? "";
        return new FileListing(path, parent,
            items.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            truncated, offset, offset + scanned);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.WindowsSecurity;

/// <summary>
/// Non-authorizing inspection of an existing private storage directory. It checks
/// current ACLs and holds each directory without delete sharing. A snapshot of
/// current ACLs cannot establish that untrusted principals never retained old
/// metadata/security handles: this object MUST NOT authorize a live operation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedDirectoryInspection : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    public string DirectoryPath { get; }

    private ProtectedDirectoryInspection(string path) => DirectoryPath = path;

    internal static ProtectedDirectoryInspection OpenExistingForInspection(string path)
        => Open(path, finalProtectedRoot: true);

    // Used only by the OS-known installer bootstrap, never to bless a peer path.
    internal static ProtectedDirectoryInspection OpenInstallerAnchorForBootstrap(string path)
        => Open(path, finalProtectedRoot: false);

    private static ProtectedDirectoryInspection Open(string path, bool finalProtectedRoot)
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("Private storage inspection requires SYSTEM.");
        var full = ValidateLocalPath(path);
        var inspection = new ProtectedDirectoryInspection(full);
        try
        {
            var root = Path.GetPathRoot(full)!;
            var current = root;
            var components = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0) throw new InvalidDataException("A drive root is not a private storage directory.");
            inspection.OpenDirectory(current, isProtectedRoot: false);
            for (var i = 0; i < components.Length; i++)
            {
                current = Path.Combine(current, components[i]);
                inspection.OpenDirectory(current, isProtectedRoot: finalProtectedRoot && i == components.Length - 1);
            }
            return inspection;
        }
        catch { inspection.Dispose(); throw; }
    }

    // A successful ACL snapshot is explicitly not evidence of safe creation.
    internal void DemandLiveAuthorization() => throw new InvalidOperationException(
        "Live use requires independently established secure creation and handle provenance.");

    // Caller keeps its separately read-locked handle and this inspection alive.
    // This check neither proves the requested share mode nor safe first creation.
    internal void InspectHeldFile(SafeFileHandle file, string expectedLeaf)
    {
        if (directories.Count == 0 || file.IsInvalid || file.IsClosed || string.IsNullOrWhiteSpace(expectedLeaf) ||
            expectedLeaf != Path.GetFileName(expectedLeaf) || expectedLeaf.Contains(':') ||
            expectedLeaf.EndsWith('.') || expectedLeaf.EndsWith(' '))
            throw new InvalidDataException("Invalid held private storage file.");
        if (!string.Equals(FinalPath(file), Path.Combine(DirectoryPath, expectedLeaf), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Held file is not the expected private storage file.");
        var info = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(file, 9, ref info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect held file attributes.");
        if ((info.Attributes & (0x400u | 0x10u)) != 0)
            throw new InvalidDataException("Held private storage file is not a regular non-reparse file.");
        PrivateStorageDescriptorPolicy.Validate(ReadDescriptor(file), protectedRoot: true);
    }

    private void OpenDirectory(string path, bool isProtectedRoot)
    {
        const uint readControl = 0x00020000, readAttributes = 0x80;
        const uint shareReadWrite = 0x3, openExisting = 3;
        const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
        var handle = CreateFileW(path, readControl | readAttributes, shareReadWrite, nint.Zero,
            openExisting, backupSemantics | openReparsePoint, nint.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot hold private storage path."); }
        directories.Add(handle);
        var info = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(handle, 9, ref info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect private storage path attributes.");
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
            throw new InvalidDataException("Private storage path contains a reparse point or non-directory.");
        if (!string.Equals(FinalPath(handle), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Private storage path does not match its opened directory.");
        PrivateStorageDescriptorPolicy.Validate(ReadDescriptor(handle), isProtectedRoot);
    }

    private static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Private storage path must be an absolute local drive path.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not { Length: 3 } || root[1] != ':' || root[2] != '\\' || GetDriveTypeW(root) != 3)
            throw new InvalidDataException("Private storage path must be on a fixed local drive.");
        foreach (var component in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (component.IndexOf(':') >= 0 || component.EndsWith('.') || component.EndsWith(' '))
                throw new InvalidDataException("Ambiguous private storage path component.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var length = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (length is 0 or > 32767) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify private storage path.");
        var chars = new char[length + 1];
        var written = GetFinalPathNameByHandleW(handle, chars, (uint)chars.Length, 0);
        if (written == 0 || written >= chars.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify private storage path.");
        var result = new string(chars, 0, checked((int)written));
        if (!result.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected private storage path namespace.");
        return Path.TrimEndingDirectorySeparator(result[4..]);
    }

    private static RawSecurityDescriptor ReadDescriptor(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 0x5, nint.Zero, nint.Zero, nint.Zero, nint.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "Cannot inspect private storage security descriptor.");
        try
        {
            if (descriptor == nint.Zero) throw new InvalidDataException("Missing security descriptor.");
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) throw new InvalidDataException("Invalid security descriptor size.");
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { if (descriptor != nint.Zero) LocalFree(descriptor); }
    }

    public void Dispose()
    {
        for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
        directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, ref FileAttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[]? path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

/// <summary>Conservative DACL allow-list, not an effective-token access check.</summary>
[SupportedOSPlatform("windows")]
internal static class PrivateStorageDescriptorPolicy
{
    private const string SystemSid = "S-1-5-18", AdminSid = "S-1-5-32-544";
    private const string InstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const uint SafeReadTraverse = 0x001200a9, AddFileOrDirectory = 0x00000006;
    private const uint GenericRead = 0x80000000, GenericExecute = 0x20000000;
    private const uint FileGenericRead = 0x00120089, FileGenericExecute = 0x001200a0;

    internal static void Validate(RawSecurityDescriptor descriptor, bool protectedRoot)
    {
        var owner = descriptor.Owner?.Value;
        if (owner != SystemSid && owner != AdminSid && (protectedRoot || owner != InstallerSid))
            throw new InvalidDataException("Untrusted owner of private storage path.");
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
            throw new InvalidDataException("Missing or null private storage DACL.");
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not CommonAce common || common.IsCallback ||
                common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new InvalidDataException("Unsupported private storage DACL ACE.");
            if (common.AceQualifier == AceQualifier.AccessDenied) continue;
            if (!protectedRoot && (common.AceFlags & AceFlags.InheritOnly) != 0) continue;
            var sid = common.SecurityIdentifier.Value;
            if (sid == SystemSid || sid == AdminSid || (!protectedRoot && sid == InstallerSid)) continue;
            var mask = unchecked((uint)common.AccessMask);
            if ((mask & GenericRead) != 0) mask = (mask & ~GenericRead) | FileGenericRead;
            if ((mask & GenericExecute) != 0) mask = (mask & ~GenericExecute) | FileGenericExecute;
            var allowed = SafeReadTraverse | (protectedRoot ? 0u : AddFileOrDirectory);
            if ((mask & ~allowed) != 0) throw new InvalidDataException("Untrusted maintenance write/delete/owner permission.");
        }
    }
}

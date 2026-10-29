using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SecurityAttributes = DeskFuse.WindowsSecurity.PrivateStorageNativeLayouts.SecurityAttributes;
using FileIdInfo = DeskFuse.WindowsSecurity.PrivateStorageNativeLayouts.FileIdInfo;

namespace DeskFuse.WindowsSecurity;

/// <summary>
/// SYSTEM-owned fixed-profile private namespace, born private under the OS-known x64
/// installation anchor. Its security premise is the supported trusted OS and
/// installer boundary: administrator/kernel compromise or an administrator's
/// prior weakening of that anchor is outside this capability's threat model.
/// Arbitrary paths and directories made private only after exposure are never adopted.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecurePrivateStorageRoot : IDisposable
{
    private const string Marker = "bootstrap.json";
    private readonly ProtectedDirectoryInspection anchor;
    private readonly ProtectedDirectoryInspection inspection;
    private readonly SafeFileHandle root;
    private readonly FileStream marker;
    private readonly PrivateStorageBootstrapRecord bootstrap;
    private bool disposed;
    public PrivateStorageProfile Profile { get; }
    public string DirectoryPath { get; }
    public Guid NamespaceId => bootstrap.NamespaceId;

    private SecurePrivateStorageRoot(PrivateStorageProfile profile, string path, ProtectedDirectoryInspection anchor,
        ProtectedDirectoryInspection inspection, SafeFileHandle root, FileStream marker,
        PrivateStorageBootstrapRecord bootstrap)
        => (Profile, DirectoryPath, this.anchor, this.inspection, this.root, this.marker, this.bootstrap) =
            (profile, path, anchor, inspection, root, marker, bootstrap);

    public static SecurePrivateStorageRoot CreateFresh(PrivateStorageProfile profile)
    {
        PrivateStorageProfiles.Require(profile);
        PrivateStorageNative.RequireSystem();
        var basePath = PrivateStorageNative.InstallationAnchor();
        var anchor = ProtectedDirectoryInspection.OpenInstallerAnchorForBootstrap(basePath);
        var path = PrivateStorageBootstrapReservation.DirectoryForAnchor(basePath, profile);
        ProtectedDirectoryInspection? inspection = null;
        SafeFileHandle? root = null;
        FileStream? marker = null;
        try
        {
            PrivateStorageNative.RequirePersistentAcls(basePath);
            // This fixed name is the machine-wide, durable bootstrap reservation.
            // There is no fresh suffix that could bypass an unresolved old registry.
            // Security is supplied to CREATE, not applied after exposure.
            var id = PrivateStorageBootstrapReservation.Reserve(basePath, profile, PrivateStorageNative.CreatePrivateDirectory).NamespaceId;
            inspection = ProtectedDirectoryInspection.OpenExistingForInspection(path);
            root = PrivateStorageNative.OpenDirectory(path);
            var identity = PrivateStorageNative.Identity(root);
            var bootstrap = new PrivateStorageBootstrapRecord(1, id, identity.Volume, identity.Low, identity.High);
            bootstrap.Require(id, identity);
            foreach (var directory in PrivateStorageProfiles.InitialDirectories(profile))
                PrivateStorageNative.CreatePrivateDirectory(Path.Combine(path, directory));
            foreach (var file in PrivateStorageProfiles.InitialFiles(profile))
            {
                using var output = PrivateStorageNative.CreatePrivateFile(Path.Combine(path, file));
                output.Write("DeskFuse broker owner lease v1\n"u8);
                output.Flush(flushToDisk: true);
            }
            using (var output = PrivateStorageNative.CreatePrivateFile(Path.Combine(path, Marker)))
            {
                output.Write(PrivateStorageJson.Write(bootstrap));
                output.Flush(flushToDisk: true);
            }
            marker = PrivateStorageNative.OpenRead(Path.Combine(path, Marker));
            inspection.InspectHeldFile(marker.SafeFileHandle, Marker);
            WindowsFileStreams.RequireOnlyUnnamedDataStream(marker.SafeFileHandle);
            return new(profile, path, anchor, inspection, root, marker, bootstrap);
        }
        catch
        {
            marker?.Dispose(); root?.Dispose(); inspection?.Dispose(); anchor.Dispose();
            // A partially created namespace is retained for explicit inspection.
            // Neither a collision nor a crash makes it reusable for another run.
            throw;
        }
    }

    public static SecurePrivateStorageRoot OpenExisting(PrivateStorageProfile profile, Guid namespaceId)
    {
        PrivateStorageProfiles.Require(profile);
        PrivateStorageNative.RequireSystem();
        if (namespaceId == Guid.Empty) throw new InvalidDataException("Missing private namespace identity.");
        var basePath = PrivateStorageNative.InstallationAnchor();
        var anchor = ProtectedDirectoryInspection.OpenInstallerAnchorForBootstrap(basePath);
        var path = PrivateStorageBootstrapReservation.DirectoryForAnchor(basePath, profile);
        ProtectedDirectoryInspection? inspection = null;
        SafeFileHandle? root = null;
        FileStream? marker = null;
        try
        {
            inspection = ProtectedDirectoryInspection.OpenExistingForInspection(path);
            root = PrivateStorageNative.OpenDirectory(path);
            marker = PrivateStorageNative.OpenRead(Path.Combine(path, Marker));
            inspection.InspectHeldFile(marker.SafeFileHandle, Marker);
            WindowsFileStreams.RequireOnlyUnnamedDataStream(marker.SafeFileHandle);
            var bytes = ReadBounded(marker, 4096);
            var bootstrap = PrivateStorageJson.Read<PrivateStorageBootstrapRecord>(bytes);
            if (!PrivateStorageJson.Write(bootstrap).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Ambiguous private storage bootstrap record.");
            bootstrap.Require(namespaceId, PrivateStorageNative.Identity(root));
            return new(profile, path, anchor, inspection, root, marker, bootstrap);
        }
        catch { marker?.Dispose(); root?.Dispose(); inspection?.Dispose(); anchor.Dispose(); throw; }
    }

    public void ValidateHeldIdentity()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PrivateStorageNative.RequireSystem();
        bootstrap.Require(bootstrap.NamespaceId, PrivateStorageNative.Identity(root));
        using var current = ProtectedDirectoryInspection.OpenExistingForInspection(DirectoryPath);
        current.InspectHeldFile(marker.SafeFileHandle, Marker);
        WindowsFileStreams.RequireOnlyUnnamedDataStream(marker.SafeFileHandle);
        if (!ReadBounded(marker, 4096).AsSpan().SequenceEqual(PrivateStorageJson.Write(bootstrap)))
            throw new InvalidDataException("Private storage bootstrap identity changed.");
    }

    /// <summary>
    /// Holds the fixed BrokerAuthority ownership file exclusively across processes.
    /// The lease exposes no mutable stream; disposal or process death releases it.
    /// It does not restore any previous active registrations or grants.
    /// </summary>
    public IDisposable AcquireBrokerOwnerLease()
    {
        BrokerOwnerLeasePolicy.RequireProfile(Profile);
        ValidateHeldIdentity();
        var parent = ProtectedDirectoryInspection.OpenExistingForInspection(DirectoryPath);
        FileStream file;
        try
        {
            file = PrivateStorageNative.OpenBrokerOwnerLease(
                Path.Combine(DirectoryPath, BrokerOwnerLeasePolicy.FileName), parent);
        }
        catch { parent.Dispose(); throw; }
        try
        {
            parent.InspectHeldFile(file.SafeFileHandle, BrokerOwnerLeasePolicy.FileName);
            WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
            return new BrokerOwnerLease(file);
        }
        catch { file.Dispose(); throw; }
    }

    private sealed class BrokerOwnerLease(FileStream file) : IDisposable
    {
        private FileStream? custody = file;
        public void Dispose() => Interlocked.Exchange(ref custody, null)?.Dispose();
    }

    public ProtectedDirectoryInspection OpenDirectory(string relativePath)
    {
        ValidateHeldIdentity();
        return ProtectedDirectoryInspection.OpenExistingForInspection(ResolveRelative(DirectoryPath, relativePath));
    }

    public FileStream OpenProtectedRead(string relativePath)
    {
        ValidateHeldIdentity();
        var path = ResolveRelative(DirectoryPath, relativePath);
        var parent = ProtectedDirectoryInspection.OpenExistingForInspection(Path.GetDirectoryName(path)!);
        FileStream file;
        try { file = PrivateStorageNative.OpenRead(path, parent); }
        catch { parent.Dispose(); throw; }
        try
        {
            parent.InspectHeldFile(file.SafeFileHandle, Path.GetFileName(path));
            WindowsFileStreams.RequireOnlyUnnamedDataStream(file.SafeFileHandle);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    public EnrollmentPrivateRecord OpenEnrollmentRecord(Guid enrollmentId, EnrollmentPrivateRecordKind kind)
        => HoldEnrollmentRecord(enrollmentId, kind, create: false);

    public EnrollmentPrivateRecord CreateEnrollmentRecord(Guid enrollmentId, EnrollmentPrivateRecordKind kind)
        => HoldEnrollmentRecord(enrollmentId, kind, create: true);

    public void RequireEnrollmentPersistenceProfile()
    {
        if (Profile != PrivateStorageProfile.BrokerAuthority) throw new InvalidDataException("Wrong enrollment persistence profile.");
        ValidateHeldIdentity();
        PrivateStorageNative.RequireLocalNtfs(DirectoryPath);
    }

    private EnrollmentPrivateRecord HoldEnrollmentRecord(Guid id, EnrollmentPrivateRecordKind kind, bool create)
    {
        if (Profile != PrivateStorageProfile.BrokerAuthority || id == Guid.Empty || !Enum.IsDefined(kind))
            throw new InvalidDataException("Enrollment custody requires the fixed authority profile and artifact kind.");
        RequireEnrollmentPersistenceProfile();
        var suffix = kind switch
        {
            EnrollmentPrivateRecordKind.Evidence => ".v2.record.json",
            EnrollmentPrivateRecordKind.Commit => ".v2.commit.json",
            EnrollmentPrivateRecordKind.Receipt => ".v2.receipt.bin",
            EnrollmentPrivateRecordKind.Revocation => ".revoked.json",
            _ => throw new InvalidDataException("Unknown enrollment artifact.")
        };
        var name = id.ToString("N") + suffix;
        var path = ResolveRelative(DirectoryPath, "enrollments/" + name);
        var parent = ProtectedDirectoryInspection.OpenExistingForInspection(Path.GetDirectoryName(path)!);
        FileStream? file = null;
        try
        {
            file = create ? PrivateStorageNative.CreatePrivateFile(path) : PrivateStorageNative.OpenEnrollmentFile(path);
            var held = file;
            void Validate()
            {
                ValidateHeldIdentity();
                parent.InspectHeldFile(held.SafeFileHandle, name);
                WindowsFileStreams.RequireOnlyUnnamedDataStream(held.SafeFileHandle);
            }
            Validate();
            return new(file, parent, Validate, kind, create);
        }
        catch { file?.Dispose(); parent.Dispose(); throw; }
    }

    public ProtectedDirectoryInspection CreateProtectedDirectory(string relativePath)
    {
        ValidateHeldIdentity();
        var path = ResolveRelative(DirectoryPath, relativePath);
        using var parent = ProtectedDirectoryInspection.OpenExistingForInspection(Path.GetDirectoryName(path)!);
        PrivateStorageNative.CreatePrivateDirectory(path); // Existing paths are never adopted.
        return ProtectedDirectoryInspection.OpenExistingForInspection(path);
    }

    /// <summary>
    /// Imports bytes into a newly born private object. Never moves or hardlinks
    /// an exposed source object, preserving the no-prior-untrusted-handle invariant.
    /// Failure leaves a private incomplete file; it is never treated as verified.
    /// </summary>
    public FileStream CopyProtectedFile(string relativePath, Stream source, string expectedSha256,
        int maximumBytes, CancellationToken cancellationToken = default)
    {
        ValidateHeldIdentity();
        PrivateFileCopy.ValidateArguments(source, expectedSha256, maximumBytes);
        var path = ResolveRelative(DirectoryPath, relativePath);
        using var parent = ProtectedDirectoryInspection.OpenExistingForInspection(Path.GetDirectoryName(path)!);
        using (var destination = PrivateStorageNative.CreatePrivateFile(path))
        {
            PrivateFileCopy.CopyAndVerify(source, destination, expectedSha256, maximumBytes, cancellationToken);
            destination.Flush(flushToDisk: true);
        }
        var held = OpenProtectedRead(relativePath);
        try
        {
            // Verify actual persisted file bytes again under the read custody
            // returned to the caller, rather than trusting only copy-loop input.
            var actual = Convert.ToHexString(SHA256.HashData(held)).ToLowerInvariant();
            held.Position = 0;
            if (actual != expectedSha256) throw new InvalidDataException("Protected copy bytes changed before custody.");
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    public static string ResolveRelative(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Any(c => c is "" or "." or ".." || c.Contains(':') ||
                c.EndsWith('.') || c.EndsWith(' ') || IsDeviceName(c) ||
                c.Any(ch => !(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))))
            throw new InvalidDataException("Invalid protected storage relative path.");
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Publish a complete born-private record by same-directory, no-replace rename.
    /// The staging object is created here, never imported/adopted from an exposed path.
    /// Failure preserves private staging/final evidence for explicit recovery.
    /// </summary>
    public FileStream PublishProtectedFile(string relativePath, Stream source, string expectedSha256, int maximumBytes)
    {
        ValidateHeldIdentity();
        var target = ResolveRelative(DirectoryPath, relativePath);
        var parentRelative = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var temporary = $"pending-{Guid.NewGuid():N}.bin";
        var staging = string.IsNullOrEmpty(parentRelative) ? temporary : parentRelative.Replace('\\', '/') + "/" + temporary;
        using var parent = ProtectedDirectoryInspection.OpenExistingForInspection(Path.GetDirectoryName(target)!);
        using (var held = CopyProtectedFile(staging, source, expectedSha256, maximumBytes)) { }
        // Only trusted SYSTEM/admin can write this born-private directory; the
        // exclusively held product-owner lease serializes the product writer.
        PrivateStorageNative.PublishPrivateRecord(ResolveRelative(DirectoryPath, staging), target);
        var result = OpenProtectedRead(relativePath);
        try
        {
            if (Convert.ToHexString(SHA256.HashData(result)).ToLowerInvariant() != expectedSha256)
                throw new InvalidDataException("Published private record changed.");
            result.Position = 0; return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool IsDeviceName(string component)
    {
        var name = component.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "NUL" or "AUX" or "PRN" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
            (name.Length == 4 && name[3] is >= '1' and <= '9' && (name.StartsWith("COM") || name.StartsWith("LPT")));
    }

    public static byte[] ReadBounded(FileStream file, int maximum)
    {
        lock (file)
        {
            if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Invalid protected record size.");
            var bytes = new byte[checked((int)file.Length)];
            file.Position = 0;
            try { file.ReadExactly(bytes); return bytes; }
            finally { file.Position = 0; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { marker.Dispose(); }
        finally { root.Dispose(); inspection.Dispose(); anchor.Dispose(); }
    }
}

internal readonly record struct PrivateStorageFileIdentity(ulong Volume, ulong Low, ulong High);

internal static class PrivateStorageBootstrapReservation
{
    internal static string DirectoryForAnchor(string anchor, PrivateStorageProfile profile)
        => Path.Combine(anchor, PrivateStorageProfiles.DirectoryName(profile));

    // Production passes only the held OS-known installation anchor and the native
    // atomic private CreateDirectory operation. The seam permits crash/collision
    // tests without creating machine directories or modifying permissions.
    internal static (string Directory, Guid NamespaceId) Reserve(string heldAnchor, PrivateStorageProfile profile, Action<string> createNewPrivateDirectory)
    {
        var path = DirectoryForAnchor(heldAnchor, profile);
        createNewPrivateDirectory(path);
        return (path, Guid.NewGuid());
    }
}

internal static class PrivateFileCopy
{
    internal static void ValidateArguments(Stream source, string expectedSha256, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !PrivateStorageJson.IsHash(expectedSha256) || maximumBytes is < 1 or > 268435456)
            throw new InvalidDataException("Invalid bounded protected-copy request.");
    }

    internal static void CopyAndVerify(Stream source, Stream destination, string expectedSha256, int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(source, expectedSha256, maximumBytes);
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek || destination.Length != 0 || destination.Position != 0)
            throw new InvalidDataException("Protected copy destination must be a fresh empty object.");
        var buffer = new byte[65536];
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, checked(maximumBytes - total + 1)));
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("Protected file exceeds the approved copy bound.");
            destination.Write(buffer, 0, read);
        }
        destination.Flush();
        destination.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(destination)).ToLowerInvariant();
        destination.Position = 0;
        if (actual != expectedSha256) throw new InvalidDataException("Protected copy differs from the approved bytes.");
    }
}

internal sealed record PrivateStorageBootstrapRecord(int Version, Guid NamespaceId, ulong Volume, ulong FileLow, ulong FileHigh)
{
    internal void Require(Guid expected, PrivateStorageFileIdentity identity)
    {
        if (Version != 1 || expected == Guid.Empty || NamespaceId != expected ||
            Volume != identity.Volume || FileLow != identity.Low || FileHigh != identity.High ||
            (FileLow == 0 && FileHigh == 0))
            throw new InvalidDataException("Private namespace identity does not match its protected bootstrap.");
    }
}

internal static class PrivateStorageNativeLayouts
{
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public uint Size; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileIdInfo { public ulong Volume, Low, High; }
}

[SupportedOSPlatform("windows")]
internal static class PrivateStorageNative
{
    private static readonly Guid ProgramFilesX64 = new("6d809377-6af0-444b-8957-a3773f02200e");
    private const string PrivateSddl = "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    private const uint ReadControlAndAttributes = 0x00020080;

    internal static void RequireSystem()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Private namespace ownership requires SYSTEM.");
    }

    internal static string InstallationAnchor()
    {
        var initialized = CoInitializeEx(nint.Zero, 0); // COINIT_MULTITHREADED
        var uninitialize = initialized is 0 or 1;
        if (!uninitialize && initialized != unchecked((int)0x80010106)) // already initialized in another apartment
            Marshal.ThrowExceptionForHR(initialized);
        nint result = nint.Zero;
        try
        {
            var id = ProgramFilesX64;
            // KF_FLAG_DEFAULT_PATH: no caller environment variable or current-user
            // folder redirection may nominate the privileged storage anchor.
            var status = SHGetKnownFolderPath(ref id, 0x00000400, nint.Zero, out result);
            if (status != 0)
            {
                Marshal.ThrowExceptionForHR(status);
                throw new InvalidDataException("Windows did not resolve the installation anchor.");
            }
            return Marshal.PtrToStringUni(result) ?? throw new InvalidDataException("Missing OS installation anchor.");
        }
        finally
        {
            if (result != nint.Zero) Marshal.FreeCoTaskMem(result);
            if (uninitialize) CoUninitialize();
        }
    }

    internal static void RequirePersistentAcls(string path)
    {
        if (!GetVolumeInformationW(Path.GetPathRoot(path)!, nint.Zero, 0, out _, out _, out var flags, nint.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect installation volume security support.");
        if ((flags & 0x8) == 0) throw new InvalidDataException("Private storage volume does not support persistent ACLs.");
    }

    internal static void RequireLocalNtfs(string path)
    {
        var drive = Path.GetPathRoot(path)!;
        var name = new System.Text.StringBuilder(32);
        if (GetDriveTypeW(drive) != 3 ||
            !GetVolumeInformationNameW(drive, nint.Zero, 0, out _, out _, out _, name, (uint)name.Capacity) ||
            !string.Equals(name.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Enrollment persistence requires its qualified local NTFS profile.");
    }

    internal static FileStream OpenEnrollmentFile(string path)
    {
        RequireSystem();
        var handle = CreateFileW(path, 0xc0000000 | ReadControlAndAttributes, 0, nint.Zero, 3, 0x00200000, nint.Zero);
        if (!handle.IsInvalid)
        {
            try { return new FileStream(handle, FileAccess.ReadWrite); }
            catch { handle.Dispose(); throw; }
        }
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 2) throw new FileNotFoundException("Enrollment artifact is absent.", path);
        throw new Win32Exception(error, "Cannot hold enrollment completion custody.");
    }

    internal static void FlushRecord(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Enrollment persistence completion is unconfirmed.");
    }

    internal static SafeFileHandle OpenDirectory(string path)
    {
        var file = CreateFileW(path, ReadControlAndAttributes, 3, nint.Zero, 3, 0x02200000, nint.Zero);
        if (!file.IsInvalid) return file;
        var error = Marshal.GetLastWin32Error(); file.Dispose();
        throw new Win32Exception(error, "Cannot retain private directory identity.");
    }

    internal static FileStream OpenRead(string path, IDisposable? retainedParent = null)
    {
        var handle = CreateFileW(path, 0x80000000 | ReadControlAndAttributes, 1, nint.Zero, 3, 0x00200000, nint.Zero);
        if (!handle.IsInvalid)
        {
            try { return new RetainedFileStream(handle, path, retainedParent); }
            catch { handle.Dispose(); throw; }
        }
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new Win32Exception(error, "Cannot hold protected storage file.");
    }

    internal static FileStream OpenBrokerOwnerLease(string path, IDisposable retainedParent)
    {
        RequireSystem();
        // OPEN_EXISTING and zero sharing must remain fixed: acquiring ownership
        // cannot create/adopt a missing lock or coexist with another owner.
        var handle = CreateFileW(path, BrokerOwnerLeasePolicy.DesiredAccess, BrokerOwnerLeasePolicy.ShareMode,
            nint.Zero, BrokerOwnerLeasePolicy.OpenExisting, BrokerOwnerLeasePolicy.OpenReparsePoint, nint.Zero);
        if (!handle.IsInvalid)
        {
            try { return new RetainedFileStream(handle, path, retainedParent, FileAccess.ReadWrite); }
            catch { handle.Dispose(); throw; }
        }
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new Win32Exception(error, "Cannot exclusively acquire broker authority ownership.");
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RequireSystem();
        WithSecurity(attributes =>
        {
            if (!CreateDirectoryW(path, ref attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create a fresh protected private directory.");
            return 0;
        });
    }

    internal static void PublishPrivateRecord(string source, string target)
    {
        RequireSystem();
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Private publication must remain in the same retained directory.");
        if (!MoveFileExW(source, target, 8)) // MOVEFILE_WRITE_THROUGH; no replace-existing/copy-across-volume.
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Private record publication is unconfirmed; retain evidence.");
    }

    // Public installed code has a distinct, fixed policy. Private namespaces keep their original policy.
    private const string InstalledCodeSddl = "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x001200a9;;;BU)";
    internal static void CreateInstalledCodeDirectory(string path)
    {
        RequireSystem();
        WithSecurity(attributes =>
        {
            if (!CreateDirectoryW(path, ref attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot reserve a fresh installed-code deployment.");
            return 0;
        }, InstalledCodeSddl);
    }

    internal static FileStream CreateInstalledCodeFile(string path)
    {
        RequireSystem();
        return WithSecurity(attributes =>
        {
            // No sharing while writing: ordinary users cannot execute a partially copied file.
            var handle = CreateFileWithSecurity(path, 0xc0000000 | ReadControlAndAttributes, 0, ref attributes, 1, 0x00200000, nint.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot create installed code."); }
            try { return new FileStream(handle, FileAccess.ReadWrite); } catch { handle.Dispose(); throw; }
        }, InstalledCodeSddl);
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        RequireSystem();
        return WithSecurity(attributes =>
        {
            var file = CreateFileWithSecurity(path, 0xc0000000 | ReadControlAndAttributes, 0, ref attributes, 1, 0x00200000, nint.Zero);
            if (!file.IsInvalid)
            {
                try { return new FileStream(file, FileAccess.ReadWrite); }
                catch { file.Dispose(); throw; }
            }
            var error = Marshal.GetLastWin32Error(); file.Dispose();
            throw new Win32Exception(error, "Cannot create a fresh protected storage record.");
        });
    }

    private static T WithSecurity<T>(Func<SecurityAttributes, T> create, string sddl = PrivateSddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot construct private storage security.");
        try { return create(new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor }); }
        finally { LocalFree(descriptor); }
    }

    internal static PrivateStorageFileIdentity Identity(SafeFileHandle handle)
    {
        var info = new FileIdInfo();
        if (!GetFileInformationByHandleEx(handle, 18, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect private storage file identity.");
        return new(info.Volume, info.Low, info.High);
    }

    private sealed class RetainedFileStream(SafeFileHandle file, string path, IDisposable? parent,
        FileAccess access = FileAccess.Read) : FileStream(file, access)
    {
        private IDisposable? custody = parent;
        public override string Name => path;
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) Interlocked.Exchange(ref custody, null)?.Dispose(); }
        }
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref custody, null)?.Dispose(); }
        }
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPath, nint volumeName, uint volumeNameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, nint fileSystemName, uint fileSystemNameSize);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationNameW(string rootPath, nint volumeName, uint volumeNameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, System.Text.StringBuilder fileSystemName, uint fileSystemNameSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string rootPath);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWithSecurity(string path, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string target, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, ref FileIdInfo info, uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}

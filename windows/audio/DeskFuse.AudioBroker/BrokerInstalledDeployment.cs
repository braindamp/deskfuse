using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

internal enum BrokerInstalledRole { Authority = 1, Setup = 2, EnrollmentWorker = 3, AudioWorker = 4 }

internal sealed record BrokerDeploymentConfiguration(int Version, Guid MaintenanceNamespace, Guid AuthorityNamespace,
    Guid DeploymentId, Guid HostKeyCreationDeploymentId, SortedDictionary<string, string> Files, string RecordedByWindowsSid)
{
    internal void Validate(Guid maintenance, Guid authority, Guid deployment)
    {
        if (Version != 1 || maintenance == Guid.Empty || authority == Guid.Empty || deployment == Guid.Empty ||
            MaintenanceNamespace != maintenance || AuthorityNamespace != authority || DeploymentId != deployment ||
            HostKeyCreationDeploymentId == Guid.Empty || RecordedByWindowsSid != "S-1-5-18")
            throw new InvalidDataException("Installed authority configuration mismatch.");
        BrokerInstalledDeployment.RequireClosure(Files);
    }
}

/// <summary>Concrete role selection from the complete, protected installer approval; never from a pipe path.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerInstalledDeployment : IDisposable
{
    private readonly SecureInstalledCodeRoot code;
    private readonly BrokerDeploymentConfiguration configuration;
    private BrokerInstalledDeployment(SecureInstalledCodeRoot code, BrokerDeploymentConfiguration configuration)
    { this.code = code; this.configuration = configuration; }
    internal static string Entrypoint(BrokerInstalledRole role) => role switch
    {
        BrokerInstalledRole.Authority => "DeskFuse.AudioBroker.exe",
        BrokerInstalledRole.Setup => "DeskFuse.Setup.exe",
        BrokerInstalledRole.EnrollmentWorker => "DeskFuse.EnrollmentWorker.exe",
        BrokerInstalledRole.AudioWorker => "DeskFuse.AudioWorker.exe",
        _ => throw new InvalidDataException("Unknown installed authority role.")
    };

    internal static void RequireClosure(IReadOnlyDictionary<string, string>? files)
    {
        if (files is null || files.Count is < 16 or > 4096) throw new InvalidDataException("Missing installed authority closure.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in files)
        {
            _ = SecurePrivateStorageRoot.ResolveRelative("C:\\", pair.Key);
            if (pair.Key.Contains('/') || pair.Key.Contains('\\') || !names.Add(pair.Key) || pair.Value is not { Length: 64 } ||
                pair.Value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                pair.Key.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid installed authority file set.");
        }
        foreach (var role in Enum.GetValues<BrokerInstalledRole>())
        {
            var executable = Entrypoint(role);
            if (!names.Contains(executable)) throw new InvalidDataException($"Missing fixed installed role: {executable}.");
            if (role == BrokerInstalledRole.AudioWorker) continue; // Native Rust binary, no managed dependency manifest.
            var stem = Path.GetFileNameWithoutExtension(executable);
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                if (!names.Contains(stem + suffix)) throw new InvalidDataException($"Incomplete managed role: {stem}.");
        }
        foreach (var name in new[] { "DeskFuse.WindowsSecurity.dll", "DeskFuse.Audio.dll", "deskfuse-source-control.dll" })
            if (!names.Contains(name)) throw new InvalidDataException($"Missing installed dependency: {name}.");
    }

    internal static BrokerInstalledDeployment Open(BrokerDeploymentConfiguration configuration)
    {
        var snapshot = configuration with { Files = new(configuration.Files, StringComparer.Ordinal) };
        snapshot.Validate(snapshot.MaintenanceNamespace, snapshot.AuthorityNamespace, snapshot.DeploymentId);
        return new(SecureInstalledCodeRoot.OpenExisting(snapshot.DeploymentId, snapshot.Files), snapshot);
    }
    internal IInstalledBrokerWorkerImage Acquire(BrokerInstalledRole role)
    {
        var profile = ProfileDigest(role);
        return new InstalledImage(code.Acquire(Entrypoint(role)), role, profile);
    }
    private byte[] ProfileDigest(BrokerInstalledRole role)
    {
        using var stream = new MemoryStream(); stream.Write("DeskFuse.WorkerProfile.v1\0"u8);
        Span<byte> scratch = stackalloc byte[16]; configuration.DeploymentId.TryWriteBytes(scratch, true, out _); stream.Write(scratch);
        stream.WriteByte((byte)role);
        foreach (var pair in configuration.Files)
        {
            var name = Encoding.UTF8.GetBytes(pair.Key);
            BinaryPrimitives.WriteUInt16BigEndian(scratch, checked((ushort)name.Length)); stream.Write(scratch[..2]); stream.Write(name);
            stream.Write(Convert.FromHexString(pair.Value));
        }
        return SHA256.HashData(stream.ToArray());
    }
    internal void RequireSignedSetup() => code.RequireSignedEntrypoint(Entrypoint(BrokerInstalledRole.Setup));
    internal string AuthorityExecutablePath
    {
        get { using var lease = code.Acquire(Entrypoint(BrokerInstalledRole.Authority)); return lease.ExecutablePath; }
    }
    private sealed class InstalledImage(SecureInstalledCodeRoot.CodeLease lease, BrokerInstalledRole role, byte[] profile) : IInstalledBrokerWorkerImage
    {
        public byte[] InstalledProfileSha256 => profile.ToArray();
        public string ExecutablePath => lease.ExecutablePath;
        public string WorkingDirectory => lease.WorkingDirectory;
        public void RevalidateHeldProfile()
        {
            if (!string.Equals(Path.GetFileName(lease.ExecutablePath), Entrypoint(role), StringComparison.Ordinal))
                throw new InvalidDataException("Installed role identity changed.");
            lease.RevalidateHeldProfile();
        }
        public void Dispose() => lease.Dispose();
    }
    public void Dispose() => code.Dispose();
}

/// <summary>
/// Cold trusted-installer APIs. Calling these is installation, not ordinary service startup.
/// The supplied closure is the installer's independent approval, not discovered payload metadata.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrustedAuthorityInstallation
{
    internal static BrokerDeploymentConfiguration PrepareFresh(Guid deploymentId, IReadOnlyDictionary<string, string> approvedFiles,
        Func<string, Stream> openApprovedSource)
    {
        RequireSystem(); BrokerInstalledDeployment.RequireClosure(approvedFiles);
        var files = Snapshot(approvedFiles);
        using var code = SecureInstalledCodeRoot.CreateFresh(deploymentId, files, openApprovedSource);
        code.RequireSignedEntrypoint(BrokerInstalledDeployment.Entrypoint(BrokerInstalledRole.Setup));
        using var maintenance = SecurePrivateStorageRoot.CreateFresh(PrivateStorageProfile.Maintenance);
        using var authority = SecurePrivateStorageRoot.CreateFresh(PrivateStorageProfile.BrokerAuthority);
        using var owner = authority.AcquireBrokerOwnerLease();
        using var identity = BrokerHostIdentity.CreateDuringTrustedInstallation(authority, deploymentId);
        var configuration = new BrokerDeploymentConfiguration(1, maintenance.NamespaceId, authority.NamespaceId,
            deploymentId, deploymentId, files, "S-1-5-18");
        SaveConfiguration(authority, configuration);
        return configuration;
    }

    internal static BrokerDeploymentConfiguration PrepareUpgrade(Guid maintenanceNamespace, Guid authorityNamespace,
        Guid previousDeploymentId, Guid nextDeploymentId, IReadOnlyDictionary<string, string> approvedFiles,
        Func<string, Stream> openApprovedSource)
    {
        RequireSystem(); BrokerInstalledDeployment.RequireClosure(approvedFiles);
        if (nextDeploymentId == Guid.Empty || nextDeploymentId == previousDeploymentId)
            throw new InvalidDataException("A code update requires a fresh independently approved deployment.");
        using var maintenance = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.Maintenance, maintenanceNamespace);
        using var authority = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.BrokerAuthority, authorityNamespace);
        using var owner = authority.AcquireBrokerOwnerLease();
        var previous = ReadConfiguration(authority, maintenanceNamespace, previousDeploymentId);
        using var identity = BrokerHostIdentity.OpenExisting(authority, previous.HostKeyCreationDeploymentId);
        using var previousCode = BrokerInstalledDeployment.Open(previous);
        var files = Snapshot(approvedFiles);
        using var code = SecureInstalledCodeRoot.CreateFresh(nextDeploymentId, files, openApprovedSource);
        code.RequireSignedEntrypoint(BrokerInstalledDeployment.Entrypoint(BrokerInstalledRole.Setup));
        var next = new BrokerDeploymentConfiguration(1, maintenanceNamespace, authorityNamespace, nextDeploymentId,
            previous.HostKeyCreationDeploymentId, files, "S-1-5-18");
        SaveConfiguration(authority, next); // Host key/enrollments retain their original identity and namespace.
        return next;
    }

    internal static BrokerDeploymentConfiguration ReadConfiguration(SecurePrivateStorageRoot root, Guid maintenance, Guid deployment)
    {
        RequireSystem(); root.ValidateHeldIdentity();
        if (root.Profile != PrivateStorageProfile.BrokerAuthority) throw new InvalidDataException("Wrong authority storage profile.");
        using var file = root.OpenProtectedRead(ConfigurationPath(deployment));
        var bytes = SecurePrivateStorageRoot.ReadBounded(file, 1024 * 1024);
        var value = JsonSerializer.Deserialize<BrokerDeploymentConfiguration>(bytes, ProtectedBrokerEnrollments.Json)
            ?? throw new InvalidDataException("Missing deployment configuration.");
        value.Validate(maintenance, root.NamespaceId, deployment);
        if (!JsonSerializer.SerializeToUtf8Bytes(value, ProtectedBrokerEnrollments.Json).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Ambiguous deployment approval.");
        return value;
    }
    private static void SaveConfiguration(SecurePrivateStorageRoot root, BrokerDeploymentConfiguration value)
    {
        value.Validate(value.MaintenanceNamespace, root.NamespaceId, value.DeploymentId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProtectedBrokerEnrollments.Json);
        using var held = root.CopyProtectedFile(ConfigurationPath(value.DeploymentId), new MemoryStream(bytes, false),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1024 * 1024);
    }
    private static SortedDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string> files)
        => new(files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    private static string ConfigurationPath(Guid id)
    { if (id == Guid.Empty) throw new InvalidDataException("Missing deployment ID."); return $"bootstrap/deployment.{id:N}.json"; }
    internal static void RequireSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Authority installation and publication require SYSTEM.");
    }
}

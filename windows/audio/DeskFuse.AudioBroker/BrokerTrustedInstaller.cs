using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

/// <summary>
/// Cold, SYSTEM-only installer entry. Approval is an independently reviewed SHA-256
/// of a bounded manifest, never a boolean inside the candidate package.
/// The local operator/installer must obtain that digest outside the source bundle.
/// An installed service is not enabled by preparation or publication alone.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BrokerTrustedInstaller
{
    internal static void InstallFresh(Guid deploymentId, string source, string review, string approvedSha256)
    {
        TrustedAuthorityInstallation.RequireSystem();
        var files = ApprovedFiles(review, approvedSha256, source);
        var prepared = TrustedAuthorityInstallation.PrepareFresh(deploymentId, files, name => OpenApproved(source, name));
        BrokerServicePublication.PublishFreshDisabled(prepared.MaintenanceNamespace,
            prepared.AuthorityNamespace, prepared.DeploymentId);
        ActivateInstalled(prepared);
    }

    internal static void InstallUpgrade(Guid maintenance, Guid authority, Guid previous, Guid next,
        string source, string review, string approvedSha256)
    {
        TrustedAuthorityInstallation.RequireSystem();
        if (previous == next || previous == Guid.Empty) throw new InvalidDataException("Missing distinct prior deployment.");
        var files = ApprovedFiles(review, approvedSha256, source);
        // Existing exact service must already be disabled/stopped by the approved update procedure.
        // PrepareUpgrade preserves the old protected namespace, host key and enrollments.
        var prepared = TrustedAuthorityInstallation.PrepareUpgrade(maintenance, authority, previous, next,
            files, name => OpenApproved(source, name));
        BrokerServicePublication.PublishUpdateDisabled(maintenance, authority, previous, next);
        ActivateInstalled(prepared);
    }

    private static SortedDictionary<string, string> ApprovedFiles(string review, string approvedSha256, string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) ||
            string.IsNullOrWhiteSpace(review) || !Path.IsPathFullyQualified(review) ||
            !IsLowerHash(approvedSha256)) throw new InvalidDataException("Exact independent installer approval is required.");
        // The approved entrypoint is also the executable performing installation:
        // no independently launched helper may borrow its staged signature.
        var expectedInstaller = Path.GetFullPath(Path.Combine(source,
            BrokerInstalledDeployment.Entrypoint(BrokerInstalledRole.Authority)));
        if (!string.Equals(Environment.ProcessPath, expectedInstaller, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Install must execute the exact signed authority binary in the approved source.");
        byte[] manifest;
        using (var file = new FileStream(review, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length is < 100 or > 1024 * 1024)
                throw new InvalidDataException("Review document exceeds the admission bound.");
            manifest = new byte[checked((int)file.Length)];
            file.ReadExactly(manifest);
            if (file.Length != manifest.Length) throw new InvalidDataException("Review document changed during inspection.");
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifest), Convert.FromHexString(approvedSha256)))
            throw new UnauthorizedAccessException("Review manifest differs from the independently approved SHA-256.");
        using var parsed = JsonDocument.Parse(manifest);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid approval document.");
        var root = parsed.RootElement;
        if (root.EnumerateObject().Count() != 3 ||
            !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            schema.GetInt32() != 1 ||
            !root.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String ||
            !IsCommit(commit.GetString()) ||
            !root.TryGetProperty("files", out var fileList) || fileList.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Review document must contain only schema, commit and files.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in fileList.EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.String ||
                !IsLowerHash(file.Value.GetString()) ||
                !files.TryAdd(file.Name, file.Value.GetString()!))
                throw new InvalidDataException("Invalid or duplicated approved file.");
        }
        BrokerInstalledDeployment.RequireClosure(files);
        // Verify the two protected managed entrypoints before any root/key creation.
        foreach (var entry in new[] { BrokerInstalledRole.Authority, BrokerInstalledRole.Setup })
        {
            using var held = OpenApproved(source, BrokerInstalledDeployment.Entrypoint(entry));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(held),
                Convert.FromHexString(files[BrokerInstalledDeployment.Entrypoint(entry)])))
                throw new UnauthorizedAccessException("Signed source is not the approved source.");
            held.Position = 0;
            InstalledCodeSignature.RequireValid(held);
        }
        return files;
    }

    private static FileStream OpenApproved(string source, string name)
    {
        // RequireClosure already enforces flat names. Never load paths nominated by a pipe client.
        if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("Approved installed names must be flat.");
        return new FileStream(Path.Combine(source, name), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static void ActivateInstalled(BrokerDeploymentConfiguration configuration)
    {
        // The activation role demands Environment.ProcessPath == the exact installed
        // authority executable, so the *staging* installer must not invoke it in-process.
        using var installed = BrokerInstalledDeployment.Open(configuration);
        installed.RequireSignedAuthority();
        var executable = installed.AuthorityExecutablePath;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--activate-authority");
        process.StartInfo.ArgumentList.Add("--maintenance=" + configuration.MaintenanceNamespace.ToString("N"));
        process.StartInfo.ArgumentList.Add("--authority=" + configuration.AuthorityNamespace.ToString("N"));
        process.StartInfo.ArgumentList.Add("--deployment=" + configuration.DeploymentId.ToString("N"));
        if (!process.Start()) throw new InvalidOperationException("Could not start exact installed activation.");
        // If activation becomes ambiguous, retain the installed roots/service for explicit
        // read-only reconciliation; never silently delete or recycle authority state.
        if (!process.WaitForExit(95_000) || process.ExitCode != 0)
            throw new InvalidOperationException("Exact installed service activation was not confirmed; inspect SCM before retrying.");
        Console.WriteLine($"DeskFuse authority activated. Maintenance={configuration.MaintenanceNamespace:N} " +
            $"Authority={configuration.AuthorityNamespace:N} Deployment={configuration.DeploymentId:N}");
    }

    private static bool IsLowerHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsCommit(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

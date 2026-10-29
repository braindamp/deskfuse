using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using DeskFuse.WindowsSecurity;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

/// <summary>Explicit trusted-installer publication. Creates/updates a DISABLED service; never starts or enables it.</summary>
[SupportedOSPlatform("windows")]
internal static class BrokerServicePublication
{
    internal const string ServiceName = "DeskFuse.Authority";
    private const uint ServiceAccess = 0x00020007; // READ_CONTROL, QUERY_CONFIG, CHANGE_CONFIG, QUERY_STATUS.
    internal static string CommandLine(string executable, BrokerDeploymentConfiguration configuration)
    {
        configuration.Validate(configuration.MaintenanceNamespace, configuration.AuthorityNamespace, configuration.DeploymentId);
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\0') ||
            Path.GetFileName(executable) != BrokerInstalledDeployment.Entrypoint(BrokerInstalledRole.Authority))
            throw new InvalidDataException("Unexpected installed authority executable.");
        return $"\"{executable}\" --authority-service --maintenance={configuration.MaintenanceNamespace:N} --authority={configuration.AuthorityNamespace:N} --deployment={configuration.DeploymentId:N}";
    }

    internal static void PublishFreshDisabled(Guid maintenanceNamespace, Guid authorityNamespace, Guid deploymentId)
    {
        TrustedAuthorityInstallation.RequireSystem();
        using var maintenance = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.Maintenance, maintenanceNamespace);
        using var authority = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.BrokerAuthority, authorityNamespace);
        using var owner = authority.AcquireBrokerOwnerLease();
        var configuration = TrustedAuthorityInstallation.ReadConfiguration(authority, maintenanceNamespace, deploymentId);
        using var identity = BrokerHostIdentity.OpenExisting(authority, configuration.HostKeyCreationDeploymentId);
        using var code = BrokerInstalledDeployment.Open(configuration); code.RequireSignedSetup();
        var command = CommandLine(code.AuthorityExecutablePath, configuration);
        using var scm = OpenSCManagerW(null, null, 0x3); // Connect + create service.
        if (scm.IsInvalid) throw Error("Cannot open SCM for explicit installation.");
        // Collision is not an invitation to adopt or rewrite another service.
        using var service = CreateServiceW(scm, ServiceName, "DeskFuse Authority", ServiceAccess, 0x10, 4, 1,
            command, null, nint.Zero, null, null, null);
        if (service.IsInvalid) throw Error("Cannot reserve a fresh disabled authority service; existing services are not adopted.");
        RequireServiceDescriptor(service);
        RequireConfiguration(service, command);
        WritePublication(authority, configuration, command);
    }

    internal static void PublishUpdateDisabled(Guid maintenanceNamespace, Guid authorityNamespace, Guid previousDeploymentId, Guid nextDeploymentId)
    {
        TrustedAuthorityInstallation.RequireSystem();
        if (previousDeploymentId == nextDeploymentId) throw new InvalidDataException("No deployment change.");
        using var maintenance = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.Maintenance, maintenanceNamespace);
        using var authority = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.BrokerAuthority, authorityNamespace);
        using var owner = authority.AcquireBrokerOwnerLease();
        var previous = TrustedAuthorityInstallation.ReadConfiguration(authority, maintenanceNamespace, previousDeploymentId);
        var next = TrustedAuthorityInstallation.ReadConfiguration(authority, maintenanceNamespace, nextDeploymentId);
        if (previous.HostKeyCreationDeploymentId != next.HostKeyCreationDeploymentId)
            throw new InvalidDataException("Code update cannot replace the host signing identity.");
        using var identity = BrokerHostIdentity.OpenExisting(authority, next.HostKeyCreationDeploymentId);
        using var oldCode = BrokerInstalledDeployment.Open(previous);
        using var newCode = BrokerInstalledDeployment.Open(next); newCode.RequireSignedSetup();
        using var scm = OpenSCManagerW(null, null, 1);
        if (scm.IsInvalid) throw Error("Cannot open SCM.");
        using var service = OpenServiceW(scm, ServiceName, ServiceAccess);
        if (service.IsInvalid) throw Error("The explicitly configured authority service is missing.");
        RequireServiceDescriptor(service);
        RequireConfiguration(service, CommandLine(oldCode.AuthorityExecutablePath, previous));
        var command = CommandLine(newCode.AuthorityExecutablePath, next);
        if (!ChangeServiceConfigW(service, 0xffffffff, 4, 0xffffffff, command, null, nint.Zero, null, null, null, null))
            throw Error("Service update completion is unconfirmed; retain both deployments for explicit recovery.");
        RequireConfiguration(service, command);
        WritePublication(authority, next, command);
    }

    private static void RequireConfiguration(ServiceHandle service, string expectedCommand)
    {
        _ = QueryServiceConfigW(service, nint.Zero, 0, out var needed);
        if (needed is < 40 or > 65536) throw Error("Invalid service configuration size.");
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw Error("Cannot inspect configured service.");
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            if (config.ServiceType != 0x10 || config.StartType != 4 || config.ErrorControl != 1 || config.TagId != 0 ||
                Marshal.PtrToStringUni(config.BinaryPath) != expectedCommand || Marshal.PtrToStringUni(config.Account) != "LocalSystem" ||
                !string.IsNullOrEmpty(Marshal.PtrToStringUni(config.LoadOrderGroup)) ||
                (config.Dependencies != nint.Zero && Marshal.ReadInt16(config.Dependencies) != 0))
                throw new InvalidDataException("Service configuration differs from the approved disabled deployment.");
            if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) || status.CurrentState != 1 || status.ProcessId != 0)
                throw new InvalidOperationException("Stop and qualify the exact service before changing its deployment.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RequireServiceDescriptor(ServiceHandle service)
    {
        var error = GetSecurityInfo(service, 2, 0x5, nint.Zero, nint.Zero, nint.Zero, nint.Zero, out var pointer);
        if (error != 0) throw new Win32Exception((int)error, "Cannot inspect authority service security.");
        try
        {
            var size = GetSecurityDescriptorLength(pointer);
            if (size is < 20 or > 65536) throw new InvalidDataException("Invalid service security descriptor.");
            var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (descriptor.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544") ||
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
                throw new UnauthorizedAccessException("Untrusted service owner or null DACL.");
            foreach (GenericAce item in descriptor.DiscretionaryAcl)
            {
                if (item is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                    throw new UnauthorizedAccessException("Unsupported service security ACE.");
                if (ace.AceQualifier == AceQualifier.AccessDenied || ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544") continue;
                const uint readOnly = 0x0002008d; // read-control, query-config/status, enumerate dependents, interrogate.
                if ((unchecked((uint)ace.AccessMask) & ~readOnly) != 0)
                    throw new UnauthorizedAccessException("Untrusted principal can control or modify the service.");
            }
        }
        finally { if (pointer != nint.Zero) LocalFree(pointer); }
    }
    private static void WritePublication(SecurePrivateStorageRoot root, BrokerDeploymentConfiguration configuration, string command)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, authorityNamespace = root.NamespaceId,
            deployment = configuration.DeploymentId, service = ServiceName, command, disabled = true });
        using var file = root.CopyProtectedFile($"bootstrap/service-published.{configuration.DeploymentId:N}.json", new MemoryStream(bytes, false),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 4096);
    }
    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    { private ServiceHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { internal uint ServiceType, StartType, ErrorControl; internal nint BinaryPath, LoadOrderGroup; internal uint TagId; internal nint Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { internal uint ServiceType, CurrentState, AcceptedControls, Win32Exit, SpecificExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateServiceW(ServiceHandle scm, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(ServiceHandle service, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, uint level, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint service);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(ServiceHandle handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}

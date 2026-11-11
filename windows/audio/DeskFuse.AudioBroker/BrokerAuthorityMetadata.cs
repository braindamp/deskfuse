using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

/// <summary>Cold-only read access needed to authenticate the actual SYSTEM authority.</summary>
[SupportedOSPlatform("windows")]
internal static class BrokerAuthorityMetadata
{
    internal const string ProcessBaseline = "O:BAD:(A;;0x001fffff;;;SY)(A;;0x00121411;;;BA)";
    internal const string TokenBaseline = "O:BAD:(A;;0x000f01ff;;;SY)(A;;0x0002001f;;;BA)";
    internal const string ProcessProfile = "O:BAD:P(A;;0x001fffff;;;SY)(A;;0x00121411;;;BA)(A;;0x00101000;;;IU)";
    internal const string TokenProfile = "O:BAD:P(A;;0x000f01ff;;;SY)(A;;0x0002001f;;;BA)(A;;0x00000008;;;IU)";

    internal static void RequireBaselineOrFinal(RawSecurityDescriptor actual, string baseline, string final)
    {
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        var value = actual.GetSddlForm(sections);
        if (value != new RawSecurityDescriptor(baseline).GetSddlForm(sections) &&
            value != new RawSecurityDescriptor(final).GetSddlForm(sections))
            throw new UnauthorizedAccessException("Unexpected authority metadata security: " + value);
    }

    internal static void Publish()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Authority metadata publication requires SYSTEM.");
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.SafeHandle, 0x00060008, out var token))
            throw BrokerWorkerLauncher.Native.Error("Cannot retain authority metadata token.");
        using (token)
        {
            using var observed = new WindowsIdentity(token.DangerousGetHandle());
            if (observed.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("Authority primary token is not SYSTEM.");
            // Inspect both before any write. Neither token contents nor owner change.
            RequireBaselineOrFinal(BrokerWorkerLauncher.ReadObjectDescriptor(process.SafeHandle), ProcessBaseline, ProcessProfile);
            RequireBaselineOrFinal(BrokerWorkerLauncher.ReadObjectDescriptor(token), TokenBaseline, TokenProfile);
            Apply(process.SafeHandle, ProcessProfile);
            Apply(token, TokenProfile);
            BrokerWorkerLauncher.RequireObjectDescriptor(process.SafeHandle, ProcessProfile);
            BrokerWorkerLauncher.RequireObjectDescriptor(token, TokenProfile);
        }
    }

    private static void Apply(SafeHandle handle, string profile)
    {
        var acl = new RawSecurityDescriptor(profile).DiscretionaryAcl!;
        var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0);
        using var memory = new BrokerWorkerLauncher.NativeBuffer(bytes.Length);
        Marshal.Copy(bytes, 0, memory.Pointer, bytes.Length);
        var error = SetSecurityInfo(handle, 6, 0x80000004, 0, 0, memory.Pointer, 0);
        if (error != 0) throw new Win32Exception((int)error, "Cannot publish read-only authority metadata.");
    }

    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeHandle handle, int kind, uint information, nint owner, nint group, nint dacl, nint sacl);
}

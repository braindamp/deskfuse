using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DeskFuse.AudioBroker;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerWorkerLaunchTests
{
    [DriverTestSdkFact]
    public void Worker_launch_structures_and_security_constants_match_real_MSVC_SDK()
    {
        var fixture = Environment.GetEnvironmentVariable("DESKFUSE_DRIVER_TEST_ABI_FILE");
        Assert.False(string.IsNullOrWhiteSpace(fixture));
        BrokerLaunchContract.Validate(File.ReadAllBytes(fixture!));
    }

    [Fact]
    public void Native_worker_environment_excludes_user_profile_and_runtime_injection_variables()
    {
        var block = BrokerWorkerLauncher.BuildEnvironment(@"C:\Windows");
        Assert.Equal("DOTNET_EnableDiagnostics=0\0PATH=C:\\Windows\\System32\0SystemRoot=C:\\Windows\0WINDIR=C:\\Windows\0\0", block);
        Assert.Single(block.Split('\0'), value => value.StartsWith("DOTNET_", StringComparison.Ordinal));
        Assert.DoesNotContain("COR_", block);
        Assert.DoesNotContain("USERPROFILE", block);
        Assert.Throws<InvalidDataException>(() => BrokerWorkerLauncher.BuildEnvironment("relative"));
        Assert.Throws<InvalidDataException>(() => BrokerWorkerLauncher.BuildEnvironment("C:\\Windows\0PATH=evil"));
    }

    [Fact]
    public void Non_system_caller_cannot_start_or_nominate_a_worker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true) return;
        Assert.Throws<UnauthorizedAccessException>(() => new BrokerWorkerLauncher().Launch(null!, null!));
    }

    [Fact]
    public void Windows_access_check_denies_owner_thread_control_and_private_token_mutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Synthetic SID/context only: no account lookup, process launch, or
        // changes to any token, privilege or machine security descriptor.
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var sidBytes = SidBytes(user);
        var sid = Marshal.AllocHGlobal(sidBytes.Length);
        nint manager = 0, ordinary = 0;
        try
        {
            Marshal.Copy(sidBytes, 0, sid, sidBytes.Length);
            Assert.True(Native.AuthzInitializeResourceManager(1, 0, 0, 0, null, out manager));
            Assert.True(Native.AuthzInitializeContextFromSid(2, sid, manager, 0, 0, 0, out ordinary));
            var defaults = new RawSecurityDescriptor(BrokerWorkerToken.DefaultObjectsSddl);
            foreach (CommonAce ace in defaults.DiscretionaryAcl!)
                if (ace.AccessMask == unchecked((int)0x10000000)) ace.AccessMask = 0x001fffff;
            var thread = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
                user, user, null, defaults.DiscretionaryAcl);
            Assert.True(Check(thread, ordinary, 0x20000)); // READ_CONTROL
            foreach (var denied in new uint[] { 0x40000, 0x80000, 1, 2, 8, 0x10, 0x20, 0x80, 0x100, 0x200, 0x400 })
                Assert.False(Check(thread, ordinary, denied), $"Unexpected thread right: 0x{denied:x}.");
            // Without OWNER RIGHTS the same user regains WRITE_DAC.
            defaults.DiscretionaryAcl.RemoveAce(2);
            var unsafeThread = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
                user, user, null, defaults.DiscretionaryAcl);
            Assert.True(Check(unsafeThread, ordinary, 0x40000));

            var logon = new BrokerLogonIdentity(sidBytes, sidBytes, 1, 1);
            var token = new RawSecurityDescriptor(BrokerWorkerToken.ObjectSddl(logon));
            Assert.True(Check(token, ordinary, BrokerWorkerToken.QueryAccess));
            foreach (var denied in new uint[] { 1, 2, 4, 0x20, 0x40, 0x80, 0x100, 0x40000, 0x80000 })
                Assert.False(Check(token, ordinary, denied), $"Unexpected token right: 0x{denied:x}.");
        }
        finally
        {
            if (ordinary != 0) Native.AuthzFreeContext(ordinary);
            if (manager != 0) Native.AuthzFreeResourceManager(manager);
            Marshal.FreeHGlobal(sid);
        }
    }

    [Fact]
    public void Worker_default_policy_rejects_extra_user_grants_and_missing_owner_rights()
    {
        if (!OperatingSystem.IsWindows()) return;
        BrokerWorkerToken.RequireDefaultAcl(BrokerWorkerToken.DefaultAclBytes());
        foreach (var sddl in new[] { "D:(A;;GA;;;SY)(A;;GA;;;BA)",
            BrokerWorkerToken.DefaultObjectsSddl + "(A;;GA;;;AU)" })
        {
            var acl = new RawSecurityDescriptor(sddl).DiscretionaryAcl!;
            var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0);
            Assert.Throws<InvalidDataException>(() => BrokerWorkerToken.RequireDefaultAcl(bytes));
        }
    }

    private static byte[] SidBytes(SecurityIdentifier sid)
    { var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); return bytes; }

    private static bool Check(RawSecurityDescriptor descriptor, nint context, uint desired)
    {
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var results = Marshal.AllocHGlobal(12);
        try
        {
            var request = new AccessRequest { DesiredAccess = desired };
            var reply = new AccessReply { Count = 1, Granted = results, Sacl = results + 4, Error = results + 8 };
            Assert.True(Native.AuthzAccessCheck(0, context, ref request, 0, bytes, 0, 0, ref reply, 0),
                $"AuthzAccessCheck failed: {Marshal.GetLastWin32Error()}.");
            return Marshal.ReadInt32(reply.Error) == 0 && (unchecked((uint)Marshal.ReadInt32(reply.Granted)) & desired) == desired;
        }
        finally { Marshal.FreeHGlobal(results); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AccessRequest
    { internal uint DesiredAccess; internal nint PrincipalSelf, ObjectTypes; internal uint ObjectTypeCount; internal nint Arguments; }
    [StructLayout(LayoutKind.Sequential)] private struct AccessReply
    { internal uint Count; internal nint Granted, Sacl, Error; }
    private static class Native
    {
        [DllImport("authz.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AuthzInitializeResourceManager(uint flags, nint check, nint compute, nint free, string? name, out nint manager);
        [DllImport("authz.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AuthzInitializeContextFromSid(uint flags, nint sid, nint manager, nint expiration, ulong id, nint arguments, out nint context);
        [DllImport("authz.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AuthzAccessCheck(uint flags, nint context, ref AccessRequest request, nint audit, byte[] descriptor,
            nint optional, uint optionalCount, ref AccessReply reply, nint cached);
        [DllImport("authz.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AuthzFreeContext(nint context);
        [DllImport("authz.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AuthzFreeResourceManager(nint manager);
    }
}

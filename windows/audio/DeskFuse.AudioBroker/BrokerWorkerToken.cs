using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

/// <summary>Cold launch policy only. Never modifies the WTS/logon token.</summary>
[SupportedOSPlatform("windows")]
internal static class BrokerWorkerToken
{
    // TokenDefaultDacl contains an ACL, not the descriptor's protection flags.
    // Generic access is mapped by each new object's type. OWNER RIGHTS is
    // essential: omitting a user allow ACE alone leaves implicit WRITE_DAC.
    internal const string DefaultObjectsSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;RC;;;OW)";
    internal const uint QueryAccess = 0x00020008; // READ_CONTROL | TOKEN_QUERY
    internal const uint AllAccess = 0x000f01ff;

    internal static string ObjectSddl(BrokerLogonIdentity logon)
        => $"O:SYD:P(A;;0x000f01ff;;;SY)(A;;0x000f01ff;;;BA)(A;;0x{QueryAccess:x8};;;{new SecurityIdentifier(logon.UserSid, 0).Value})(A;;RC;;;OW)";

    internal static SafeAccessTokenHandle Duplicate(SafeAccessTokenHandle source, BrokerLogonIdentity approved)
    {
        if (!BrokerWorkerLauncher.ReadLogon(source).Matches(approved))
            throw new UnauthorizedAccessException("Worker token does not match the approved medium logon.");
        using var memory = new BrokerWorkerLauncher.Allocations();
        // An explicit owner in DuplicateTokenEx requires SeRestorePrivilege.
        // Leave ownership to the SYSTEM creator initially; before publication,
        // set SYSTEM using our retained WRITE_OWNER handle. No privilege change.
        // OWNER RIGHTS also bounds any initial implicit-owner access.
        var expected = ObjectSddl(approved);
        var attributes = memory.Security(new RawSecurityDescriptor(expected).GetSddlForm(AccessControlSections.Access));
        if (!Native.DuplicateTokenEx(source, AllAccess, ref attributes, 2, 1, out var copy))
            throw BrokerWorkerLauncher.Native.Error("Cannot duplicate the private worker primary token.");
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var sid = new byte[system.BinaryLength]; system.GetBinaryForm(sid, 0);
            using var owner = new BrokerWorkerLauncher.NativeBuffer(sid.Length);
            Marshal.Copy(sid, 0, owner.Pointer, sid.Length);
            var error = Native.SetSecurityInfo(copy, 6, 1, owner.Pointer, nint.Zero, nint.Zero, nint.Zero);
            if (error != 0) throw new Win32Exception((int)error, "Cannot assign private token custody to SYSTEM.");

            var acl = DefaultAclBytes();
            using var aclMemory = new BrokerWorkerLauncher.NativeBuffer(acl.Length);
            Marshal.Copy(acl, 0, aclMemory.Pointer, acl.Length);
            using var information = new BrokerWorkerLauncher.NativeBuffer(nint.Size);
            Marshal.WriteIntPtr(information.Pointer, aclMemory.Pointer);
            if (!Native.SetTokenInformation(copy, 6, information.Pointer, (uint)nint.Size))
                throw BrokerWorkerLauncher.Native.Error("Cannot set private worker object defaults.");
            Require(copy, approved);
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    internal static void RequireProcess(SafeProcessHandle process, BrokerLogonIdentity approved)
    {
        if (!Native.OpenProcessToken(process, QueryAccess, out var token))
            throw BrokerWorkerLauncher.Native.Error("Cannot inspect the suspended worker's effective token.");
        using (token) Require(token, approved);
    }

    private static void Require(SafeAccessTokenHandle token, BrokerLogonIdentity approved)
    {
        if (!BrokerWorkerLauncher.ReadLogon(token).Matches(approved))
            throw new UnauthorizedAccessException("Private worker token changed the approved logon.");
        BrokerWorkerLauncher.RequireObjectDescriptor(token, ObjectSddl(approved));
        using var information = BrokerWorkerLauncher.TokenInformation(token, 6);
        var pointer = Marshal.ReadIntPtr(information.Pointer);
        var offset = pointer.ToInt64() - information.Pointer.ToInt64();
        if (pointer == nint.Zero || offset < nint.Size || offset > information.Length - 8)
            throw new InvalidDataException("Missing or invalid worker token default ACL.");
        var length = (ushort)Marshal.ReadInt16(pointer, 2);
        if (length < 8 || length > information.Length - offset)
            throw new InvalidDataException("Unbounded worker token default ACL.");
        var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
        RequireDefaultAcl(bytes);
    }

    internal static byte[] DefaultAclBytes()
    {
        var acl = new RawSecurityDescriptor(DefaultObjectsSddl).DiscretionaryAcl!;
        var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); return bytes;
    }

    internal static void RequireDefaultAcl(byte[] bytes)
    {
        // Exact policy: reject extra user/logon/Everyone or inherited grants.
        // Parse first as well as compare, so malformed input fails closed.
        var acl = new RawAcl(bytes, 0);
        if (acl.BinaryLength != bytes.Length || !bytes.AsSpan().SequenceEqual(DefaultAclBytes()))
            throw new InvalidDataException("Worker object defaults differ from the private launch policy.");
    }

    private static class Native
    {
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access,
            ref BrokerWorkerLauncher.Native.SecurityAttributes attributes, uint level, uint type, out SafeAccessTokenHandle duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, nint information, uint length);
        [DllImport("advapi32.dll")]
        internal static extern uint SetSecurityInfo(SafeHandle handle, int kind, uint information, nint owner, nint group, nint dacl, nint sacl);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}

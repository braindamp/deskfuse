using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DeskFuse.AudioBroker;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerLocalPipeTests
{
    [Fact]
    public void Policy_uses_exact_logon_data_rights_local_only_mode_and_first_instance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var logon = new BrokerLogonIdentity([1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0],
            [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0], 42, 1);
        var descriptor = new RawSecurityDescriptor(BrokerLocalPipe.SecurityDescriptorForLogon(logon));
        Assert.Equal("S-1-5-18", descriptor.Owner!.Value);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Equal(2, descriptor.DiscretionaryAcl!.Count);
        var clientAce = Assert.IsType<CommonAce>(descriptor.DiscretionaryAcl[1]);
        Assert.Equal("S-1-5-5-22-33", clientAce.SecurityIdentifier.Value);
        Assert.Equal(AceQualifier.AccessAllowed, clientAce.AceQualifier);
        Assert.Equal(0x00120083, clientAce.AccessMask);
        Assert.Equal(0, clientAce.AccessMask & (0x4 | unchecked((int)0xf0000000)));
        Assert.Equal(0x40080003u, BrokerLocalPipe.ServerOpenMode);
        Assert.Equal(8u, BrokerLocalPipe.LocalBytePipeMode);
        Assert.Equal("DeskFuse.Broker.Control.00112233445566778899aabbccddeeff",
            BrokerLocalPipe.NameForRegistration(new Guid("00112233-4455-6677-8899-aabbccddeeff")));
        Assert.Throws<InvalidDataException>(() => BrokerLocalPipe.NameForRegistration(Guid.Empty));
        Assert.Throws<InvalidDataException>(() => BrokerLocalPipe.SecurityDescriptorForLogon(
            new(logon.UserSid, logon.UserSid, 42, 1)));
    }

    [Fact]
    public void Production_creation_does_not_allow_an_unprivileged_test_bypass()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User!.IsWellKnown(WellKnownSidType.LocalSystemSid)) return;
        Assert.Throws<UnauthorizedAccessException>(() => BrokerLocalPipe.CreateForWorker(null!));
    }

    [Fact]
    public async Task Ephemeral_native_pipe_denies_server_creation_and_generic_write_but_accepts_minimal_client_io()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var logon = new BrokerLogonIdentity(Bytes(identity.User!),
            [1, 3, 0, 0, 0, 0, 0, 5, 5, 0, 0, 0, 22, 0, 0, 0, 33, 0, 0, 0], 42, 1);
        // This fixture creates only an ephemeral raw pipe owned by the test
        // account. It cannot construct a typed production listener/observer.
        // Some isolated test tokens have no logon-SID group. Only this raw
        // kernel-rights fixture substitutes the test user for the policy SID;
        // production accepts only its launched worker's exact logon SID.
        var sddl = BrokerLocalPipe.SecurityDescriptorForLogon(logon)
            .Replace("O:SYG:SY", $"O:{identity.User!.Value}", StringComparison.Ordinal)
            .Replace("S-1-5-5-22-33", identity.User.Value, StringComparison.Ordinal);
        Assert.True(BrokerLocalPipe.Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _));
        var path = @"\\.\pipe\DeskFuse.Test.CallerBinding." + Guid.NewGuid().ToString("N");
        try
        {
            var attributes = new BrokerLocalPipe.Native.SecurityAttributes
            { Length = (uint)Marshal.SizeOf<BrokerLocalPipe.Native.SecurityAttributes>(), Descriptor = descriptor };
            using var rawServer = BrokerLocalPipe.Native.CreateNamedPipeW(path, BrokerLocalPipe.ServerOpenMode,
                BrokerLocalPipe.LocalBytePipeMode, 2, 4096, 4096, 0, ref attributes);
            Assert.False(rawServer.IsInvalid, $"CreateNamedPipe failed: {Marshal.GetLastWin32Error()}");
            using var squatter = BrokerLocalPipe.Native.CreateNamedPipeW(path, BrokerLocalPipe.ServerOpenMode,
                BrokerLocalPipe.LocalBytePipeMode, 2, 4096, 4096, 0, ref attributes);
            Assert.True(squatter.IsInvalid);
            Assert.Equal(5, Marshal.GetLastWin32Error());
            using var extraInstance = BrokerLocalPipe.Native.CreateNamedPipeW(path, BrokerLocalPipe.ServerOpenMode & ~0x80000u,
                BrokerLocalPipe.LocalBytePipeMode, 2, 4096, 4096, 0, ref attributes);
            Assert.True(extraInstance.IsInvalid);
            Assert.Equal(5, Marshal.GetLastWin32Error());
            using var genericClient = Native.CreateFileW(path, 0xc0000000, 0, nint.Zero, 3, 0x40110000, nint.Zero);
            Assert.True(genericClient.IsInvalid);
            Assert.Equal(5, Marshal.GetLastWin32Error());

            using var server = new NamedPipeServerStream(PipeDirection.InOut, true, false, rawServer);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accepted = server.WaitForConnectionAsync(deadline.Token);
            using var rawClient = Native.CreateFileW(path, BrokerLocalPipe.ClientAccessMask, 0, nint.Zero, 3, 0x40110000, nint.Zero);
            Assert.False(rawClient.IsInvalid, $"Minimal client open failed: {Marshal.GetLastWin32Error()}");
            using var client = new NamedPipeClientStream(PipeDirection.InOut, true, true, rawClient);
            await accepted;
            Assert.Equal(0u, BrokerWorkerLauncher.Native.GetSecurityInfo(rawClient, 1, 0x5,
                nint.Zero, nint.Zero, nint.Zero, nint.Zero, out var actualDescriptor));
            try
            {
                var bytes = new byte[BrokerWorkerLauncher.Native.GetSecurityDescriptorLength(actualDescriptor)];
                Marshal.Copy(actualDescriptor, bytes, 0, bytes.Length);
                var actual = new RawSecurityDescriptor(bytes, 0);
                Assert.Equal(identity.User, actual.Owner); // Raw fixture owner; production requires SYSTEM.
                var actualClientAce = Assert.IsType<CommonAce>(actual.DiscretionaryAcl![1]);
                Assert.Equal(0, actualClientAce.AccessMask & 0x4);
            }
            finally { BrokerWorkerLauncher.Native.LocalFree(actualDescriptor); }
            Assert.True(Native.GetNamedPipeClientProcessId(rawServer, out var observed));
            Assert.Equal((uint)Environment.ProcessId, observed);
            using var retained = Native.OpenProcess(0x00101000, false, observed);
            using var sameProcess = Native.OpenProcess(0x00101000, false, (uint)Environment.ProcessId);
            Assert.False(retained.IsInvalid);
            Assert.True(Native.CompareObjectHandles(retained, sameProcess));
            Assert.False(Native.CompareObjectHandles(retained, rawServer));
            await client.WriteAsync(new byte[] { 0x42 }, deadline.Token);
            var buffer = new byte[1];
            Assert.Equal(1, await server.ReadAsync(buffer, deadline.Token));
            Assert.Equal(0x42, buffer[0]);
            await server.WriteAsync(new byte[] { 0x43 }, deadline.Token);
            Assert.Equal(1, await client.ReadAsync(buffer, deadline.Token));
            Assert.Equal(0x43, buffer[0]);
        }
        finally { BrokerLocalPipe.Native.LocalFree(descriptor); }
    }

    private static byte[] Bytes(SecurityIdentifier sid)
    { var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0); return bytes; }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernelbase.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CompareObjectHandles(SafeHandle first, SafeHandle second);
    }
}

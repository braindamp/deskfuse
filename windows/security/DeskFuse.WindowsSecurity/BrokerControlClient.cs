using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.WindowsSecurity;

/// <summary>One connection to the actual installed SYSTEM service; never reconnects an established channel.</summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerControlClient : IDisposable
{
    private const uint ClientMask = 0x00120083;
    private readonly SafeProcessHandle server;
    public NamedPipeClientStream Stream { get; }
    private BrokerControlClient(NamedPipeClientStream stream, SafeProcessHandle server) { Stream = stream; this.server = server; }
    public static Task<BrokerControlClient> ConnectSetupActivationAsync(CancellationToken ct)
        => Connect("DeskFuse.Authority.Setup.v1", ct);
    public static Task<BrokerControlClient> ConnectWorkerAsync(Guid registration, bool setupUi, CancellationToken ct)
    {
        if (registration == Guid.Empty) throw new InvalidDataException("Missing trusted registration.");
        return Connect($"{(setupUi ? "DeskFuse.Setup.Control" : "DeskFuse.Broker.Control")}.{registration:N}", ct);
    }
    private static async Task<BrokerControlClient> Connect(string name, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var handle = Native.CreateFileW(@"\\.\pipe\" + name, ClientMask, 0, nint.Zero, 3, 0x40110000, nint.Zero);
            if (!handle.IsInvalid)
            {
                SafeProcessHandle? server = null; NamedPipeClientStream? stream = null;
                try
                {
                    RequireDescriptor(handle);
                    if (!Native.GetNamedPipeServerProcessId(handle, out var pid) || pid == 0 ||
                        !Native.GetNamedPipeServerSessionId(handle, out var session) || session != 0)
                        throw new UnauthorizedAccessException("Control server is not the Session 0 service.");
                    server = Native.OpenProcess(0x00101000, false, pid);
                    if (server.IsInvalid || !Native.OpenProcessToken(server, 8, out var token)) throw Error("Cannot observe authority server.");
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                            throw new UnauthorizedAccessException("Control server is not SYSTEM.");
                    var path = new StringBuilder(32768); var count = (uint)path.Capacity;
                    if (!Native.QueryFullProcessImageNameW(server, 0, path, ref count) ||
                        !string.Equals(path.ToString(), Path.Combine(AppContext.BaseDirectory, "DeskFuse.AudioBroker.exe"), StringComparison.OrdinalIgnoreCase) ||
                        Native.WaitForSingleObject(server, 0) != 258 || !Native.GetNamedPipeServerProcessId(handle, out var again) || again != pid)
                        throw new UnauthorizedAccessException("Control server deployment/incarnation changed.");
                    stream = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
                    return new(stream, server);
                }
                catch { stream?.Dispose(); server?.Dispose(); handle.Dispose(); throw; }
            }
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error != 231 || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                throw new Win32Exception(error, "The exact protected control pipe is unavailable.");
            await Task.Delay(25, ct).ConfigureAwait(false); // Initial PIPE_BUSY only; never an established reconnect.
        }
    }
    private static void RequireDescriptor(SafePipeHandle handle)
    {
        var error = Native.GetSecurityInfo(handle, 6, 0x5, nint.Zero, nint.Zero, nint.Zero, nint.Zero, out var pointer);
        if (error != 0) throw new Win32Exception((int)error, "Cannot inspect actual control pipe descriptor.");
        try
        {
            var count = Native.GetSecurityDescriptorLength(pointer);
            if (count is < 20 or > 65536) throw new InvalidDataException("Invalid pipe descriptor size.");
            var bytes = new byte[count]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (descriptor.Owner?.Value != "S-1-5-18" || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
                throw new UnauthorizedAccessException("Control pipe does not have its SYSTEM-owned protected DACL.");
            foreach (GenericAce item in descriptor.DiscretionaryAcl)
            {
                if (item is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                    throw new UnauthorizedAccessException("Unsupported pipe descriptor ACE.");
                if (ace.AceQualifier == AceQualifier.AccessDenied || ace.SecurityIdentifier.Value == "S-1-5-18") continue;
                if ((unchecked((uint)ace.AccessMask) & ~ClientMask) != 0)
                    throw new UnauthorizedAccessException("Untrusted principal has pipe server-instance/security rights.");
            }
        }
        finally { if (pointer != nint.Zero) Native.LocalFree(pointer); }
    }
    public void Dispose() { Stream.Dispose(); server.Dispose(); }
    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafePipeHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(SafePipeHandle handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(nint descriptor);
        [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint pointer);
    }
}

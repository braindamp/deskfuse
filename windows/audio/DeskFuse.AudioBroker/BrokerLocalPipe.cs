using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

internal enum BrokerPipeProfile { MediumWorker = 1, SetupUi = 2, SetupActivation = 3 }

/// <summary>
/// One persistent, local-only control pipe for an exact launched worker. Its name
/// is an identifier, never a credential. FIRST_PIPE_INSTANCE fails closed on name
/// squatting. An unsolicited connection may be rejected before protocol I/O;
/// after protocol I/O begins, disconnect permanently retires this pipe instance.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerLocalPipe : IDisposable, IEnrollmentChannel
{
    // In particular, neither mask contains FILE_CREATE_PIPE_INSTANCE (0x4) for
    // the logon. GENERIC_WRITE would silently include that server-creation right.
    // READ_CONTROL permits the native client to inspect the actual pipe owner
    // and ACL. It grants no server-instance or descriptor mutation right.
    internal const uint ClientAccessMask = 0x00120083; // synchronize, read-control, data read/write, read attributes
    internal const uint ServerOpenMode = 0x40080003; // duplex, overlapped, first instance
    internal const uint LocalBytePipeMode = 0x8; // PIPE_REJECT_REMOTE_CLIENTS
    private readonly object gate = new();
    private readonly NamedPipeServerStream stream;
    private ulong generation;
    private bool waiting, connected, disposed, ioStarted;
    internal string Name { get; }
    internal BrokerPipeProfile Profile { get; }

    private BrokerLocalPipe(string name, NamedPipeServerStream stream, BrokerPipeProfile profile)
    { Name = name; this.stream = stream; Profile = profile; }

    internal static BrokerLocalPipe CreateForWorker(BrokerRegisteredWorker worker)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Broker control pipes require SYSTEM.");
        ArgumentNullException.ThrowIfNull(worker);
        if (worker.Registration == Guid.Empty || worker.ProcessId == 0 || worker.CreationFileTime == 0 || !worker.IsAlive())
            throw new UnauthorizedAccessException("An exact live broker-launched worker is required.");
        var profile = worker.Kind == BrokerWorkerKind.SetupUi ? BrokerPipeProfile.SetupUi : BrokerPipeProfile.MediumWorker;
        var name = profile == BrokerPipeProfile.SetupUi ? NameForSetupRegistration(worker.Registration) : NameForRegistration(worker.Registration);
        return CreateOwned(name, SecurityDescriptorForLogon(worker.Logon), profile);
    }

    internal static BrokerLocalPipe CreateSetupActivation()
        => CreateOwned("DeskFuse.Authority.Setup.v1", $"O:SYG:SYD:P(A;;0x001f01ff;;;SY)(A;;0x{ClientAccessMask:x8};;;BA)", BrokerPipeProfile.SetupActivation);

    private static BrokerLocalPipe CreateOwned(string name, string sddl, BrokerPipeProfile profile)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Authority control pipes require SYSTEM.");
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw Native.Error("Cannot prepare the broker pipe descriptor.");
        try
        {
            var security = new Native.SecurityAttributes
            { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor };
            var handle = Native.CreateNamedPipeW(@"\\.\pipe\" + name, ServerOpenMode, LocalBytePipeMode,
                1, 4096, 4096, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = Native.Error("Cannot claim the first broker pipe instance; no alternate pipe is accepted.");
                handle.Dispose(); throw error;
            }
            try { return new(name, new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle), profile); }
            catch { handle.Dispose(); throw; }
        }
        finally { Native.LocalFree(descriptor); }
    }

    internal static string NameForRegistration(Guid registration)
    {
        if (registration == Guid.Empty) throw new InvalidDataException("Missing worker registration.");
        return $"DeskFuse.Broker.Control.{registration:N}";
    }
    internal static string NameForSetupRegistration(Guid registration)
    { if (registration == Guid.Empty) throw new InvalidDataException("Missing setup registration."); return $"DeskFuse.Setup.Control.{registration:N}"; }

    internal static string SecurityDescriptorForLogon(BrokerLogonIdentity logon)
    {
        ArgumentNullException.ThrowIfNull(logon);
        var sid = new SecurityIdentifier(logon.LogonSid, 0);
        if (logon.SessionId == 0 || !sid.IsWellKnown(WellKnownSidType.LogonIdsSid))
            throw new InvalidDataException("An interactive worker logon SID is required.");
        return $"O:SYG:SYD:P(A;;0x001f01ff;;;SY)(A;;0x{ClientAccessMask:x8};;;{sid.Value})";
    }

    internal async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (waiting || connected) throw new InvalidOperationException("The worker pipe already has a connection or waiter.");
            waiting = true;
        }
        try
        {
            await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                generation = checked(generation + 1);
                connected = true;
            }
        }
        finally { lock (gate) waiting = false; }
    }

    internal BrokerCallerBinding ObserveConnectedCaller()
    {
        lock (gate)
        {
            if (Profile == BrokerPipeProfile.SetupActivation) throw new InvalidOperationException("Use the elevated activation observer for BeginSetup.");
            RequireConnection(generation);
            return BrokerCallerBinding.Capture(this, generation);
        }
    }

    internal BrokerSetupActivation ObserveSetupActivation(IInstalledBrokerWorkerImage launcherImage)
    {
        lock (gate)
        {
            if (Profile != BrokerPipeProfile.SetupActivation) throw new InvalidOperationException("Wrong pipe role.");
            RequireConnection(generation);
            return BrokerSetupActivation.Capture(this, generation, launcherImage);
        }
    }

    // I/O remains owned by the listener. Do not expose the reusable server or its
    // handle, which would allow a stale connection to bypass generation fencing.
    internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (gate) { RequireConnection(generation); ioStarted = true; }
        return stream.ReadAsync(buffer, cancellationToken);
    }
    internal ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (gate) { RequireConnection(generation); ioStarted = true; }
        return stream.WriteAsync(buffer, cancellationToken);
    }
    ValueTask<int> IEnrollmentChannel.ReadAsync(Memory<byte> buffer, CancellationToken ct) => ReadAsync(buffer, ct);
    ValueTask IEnrollmentChannel.WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => WriteAsync(buffer, ct);

    internal bool WithConnectedClient(ulong expectedGeneration, Func<SafePipeHandle, bool> check)
    {
        lock (gate)
        {
            if (disposed || !connected || expectedGeneration != generation) return false;
            return check(stream.SafePipeHandle);
        }
    }

    internal void Disconnect()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (waiting) throw new InvalidOperationException("Cannot disconnect while accepting a worker connection.");
            // Invalidate observations even if native disconnect subsequently fails.
            connected = false;
            generation = checked(generation + 1);
            if (ioStarted)
            {
                // Never let an old pending async read/write attach to a later
                // connection. The native worker must stop on broken control;
                // recovery uses a new trusted launch and registration.
                disposed = true;
                stream.Dispose();
                return;
            }
            stream.Disconnect();
        }
    }

    private void RequireConnection(ulong expectedGeneration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!connected || expectedGeneration != generation) throw new UnauthorizedAccessException("The worker pipe is not connected.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; connected = false;
            stream.Dispose();
        }
    }

    internal static class Native
    {
        internal static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes
        { internal uint Length; internal nint Descriptor; internal int Inherit; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint instances, uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes attributes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, out uint length);
        [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint pointer);
    }
}

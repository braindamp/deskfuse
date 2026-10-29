using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

internal interface IBrokerObservedCaller
{
    bool Matches(IBrokerRegisteredWorker worker);
}

/// <summary>
/// Minted only from the typed SYSTEM-created local pipe, never from a request
/// PID or registration UUID. Retains the observed process object across PID
/// reuse and is invalidated before the listener can accept another connection.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerCallerBinding : IBrokerObservedCaller, IDisposable
{
    private readonly object gate = new();
    private readonly BrokerLocalPipe pipe;
    private readonly ulong generation;
    private readonly SafeProcessHandle process;
    private readonly uint processId, sessionId;
    private readonly ulong creationFileTime;
    private readonly BrokerLogonIdentity logon;
    private bool disposed;

    private BrokerCallerBinding(BrokerLocalPipe pipe, ulong generation, SafeProcessHandle process,
        uint processId, uint sessionId, ulong creationFileTime, BrokerLogonIdentity logon)
    {
        this.pipe = pipe; this.generation = generation; this.process = process;
        this.processId = processId; this.sessionId = sessionId;
        this.creationFileTime = creationFileTime; this.logon = logon;
    }

    internal static BrokerCallerBinding Capture(BrokerLocalPipe pipe, ulong generation)
    {
        BrokerCallerBinding? observation = null;
        if (!pipe.WithConnectedClient(generation, handle =>
            {
                observation = CaptureConnected(pipe, generation, handle);
                return true;
            })) throw new UnauthorizedAccessException("The typed broker pipe is not connected.");
        return observation!;
    }

    private static BrokerCallerBinding CaptureConnected(BrokerLocalPipe pipe, ulong generation, SafePipeHandle connectedPipe)
    {
        if (!Native.PeekNamedPipe(connectedPipe, nint.Zero, 0, nint.Zero, nint.Zero, nint.Zero) ||
            !Native.GetNamedPipeClientProcessId(connectedPipe, out var pid) || pid == 0 ||
            !Native.GetNamedPipeClientSessionId(connectedPipe, out var session))
            throw Native.Error("Cannot observe the local pipe caller.");
        var process = Native.OpenProcess(0x00101000, false, pid); // query-limited + synchronize
        if (process.IsInvalid) { var error = Native.Error("Cannot retain the local caller process."); process.Dispose(); throw error; }
        try
        {
            var logon = ReadProcessLogon(process, pipe.Profile);
            var creation = ReadCreationTime(process);
            if (logon.SessionId != session || Native.WaitForSingleObject(process, 0) != 258 ||
                !Native.GetNamedPipeClientProcessId(connectedPipe, out var recheckedPid) || recheckedPid != pid ||
                !Native.GetNamedPipeClientSessionId(connectedPipe, out var recheckedSession) || recheckedSession != session)
                throw new UnauthorizedAccessException("The observed local caller changed or exited.");
            return new(pipe, generation, process, pid, session, creation, logon);
        }
        catch { process.Dispose(); throw; }
    }

    public bool Matches(IBrokerRegisteredWorker worker)
    {
        // A test-double or arbitrary implementation cannot nominate the native
        // comparison handle. Production matching accepts only the sealed launcher object.
        if (worker is not BrokerRegisteredWorker launched) return false;
        lock (gate)
        {
            if (disposed) return false;
            return pipe.WithConnectedClient(generation, handle =>
            {
                if (!Native.PeekNamedPipe(handle, nint.Zero, 0, nint.Zero, nint.Zero, nint.Zero) ||
                    !Native.GetNamedPipeClientProcessId(handle, out var pid) || pid != processId ||
                    !Native.GetNamedPipeClientSessionId(handle, out var session) || session != sessionId ||
                    Native.WaitForSingleObject(process, 0) != 258 || !launched.IsAlive() ||
                    launched.ProcessId != processId || launched.CreationFileTime != creationFileTime ||
                    !Native.CompareObjectHandles(process, launched.ProcessHandle)) return false;
                // Recheck the actual primary-token facts, including the binary
                // logon SID/authentication LUID, rather than trusting a same-user SID.
                if ((pipe.Profile == BrokerPipeProfile.SetupUi) != (launched.Kind == BrokerWorkerKind.SetupUi)) return false;
                var currentLogon = ReadProcessLogon(process, pipe.Profile);
                return currentLogon.SessionId == sessionId && currentLogon.Matches(logon) &&
                    currentLogon.Matches(launched.Logon) && ReadCreationTime(process) == creationFileTime;
            });
        }
    }

    private static BrokerLogonIdentity ReadProcessLogon(SafeProcessHandle process, BrokerPipeProfile profile)
    {
        if (!Native.OpenProcessToken(process, 0x8, out var token)) throw Native.Error("Cannot inspect the local caller token.");
        using (token) return profile switch
        {
            BrokerPipeProfile.MediumWorker => BrokerWorkerLauncher.ReadLogon(token),
            BrokerPipeProfile.SetupUi => BrokerSetupActivation.ReadElevatedLogon(token),
            _ => throw new UnauthorizedAccessException("Invalid caller profile.")
        };
    }

    private static ulong ReadCreationTime(SafeProcessHandle process)
    {
        if (!Native.GetProcessTimes(process, out var creation, out _, out _, out _)) throw Native.Error("Cannot inspect the local caller incarnation.");
        var result = unchecked((ulong)(uint)creation.dwLowDateTime | ((ulong)(uint)creation.dwHighDateTime << 32));
        if (result == 0) throw new InvalidDataException("Missing caller process incarnation.");
        return result;
    }

    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; process.Dispose(); } }

    private static class Native
    {
        internal static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint read, nint available, nint remaining);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernelbase.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CompareObjectHandles(SafeProcessHandle first, SafeProcessHandle second);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}

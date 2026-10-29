using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

/// <summary>Actual elevated BeginSetup caller and its intended interactive subject; request bytes nominate neither.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerSetupActivation : IDisposable
{
    private readonly BrokerLocalPipe pipe;
    private readonly ulong connectionGeneration;
    private readonly SafeProcessHandle process;
    private readonly SafeAccessTokenHandle token;
    private readonly uint processId;
    private readonly ulong creationTime;
    private readonly IInstalledBrokerWorkerImage launcherImage; // Caller retains the lease through UI launch.
    private bool disposed;
    internal BrokerLogonIdentity Approver { get; }
    internal BrokerLogonIdentity Subject { get; }

    private BrokerSetupActivation(BrokerLocalPipe pipe, ulong generation, SafeProcessHandle process,
        SafeAccessTokenHandle token, uint processId, ulong creationTime, BrokerLogonIdentity approver,
        BrokerLogonIdentity subject, IInstalledBrokerWorkerImage launcherImage)
    {
        this.pipe = pipe; connectionGeneration = generation; this.process = process; this.token = token;
        this.processId = processId; this.creationTime = creationTime; Approver = approver; Subject = subject;
        this.launcherImage = launcherImage;
    }

    internal static BrokerSetupActivation Capture(BrokerLocalPipe pipe, ulong generation, IInstalledBrokerWorkerImage launcherImage)
    {
        if (pipe.Profile != BrokerPipeProfile.SetupActivation || Path.GetFileName(launcherImage.ExecutablePath) != "DeskFuse.Setup.exe")
            throw new UnauthorizedAccessException("Wrong installed setup activation profile.");
        launcherImage.RevalidateHeldProfile(); BrokerWorkerLauncher.ValidateImagePaths(launcherImage);
        BrokerSetupActivation? result = null;
        if (!pipe.WithConnectedClient(generation, connected =>
        {
            if (!Native.GetNamedPipeClientProcessId(connected, out var pid) || pid == 0 ||
                !Native.GetNamedPipeClientSessionId(connected, out var session)) throw Error("Cannot observe setup caller.");
            var process = Native.OpenProcess(0x00101000, false, pid);
            SafeAccessTokenHandle? token = null;
            try
            {
                if (process.IsInvalid || !Native.OpenProcessToken(process, 0xb, out token)) throw Error("Cannot retain actual setup process/token.");
                var approver = ReadElevatedLogon(token);
                if (session != approver.SessionId) throw new UnauthorizedAccessException("Setup caller session changed.");
                var path = new StringBuilder(32768); var size = (uint)path.Capacity;
                if (!Native.QueryFullProcessImageNameW(process, 0, path, ref size) ||
                    !string.Equals(path.ToString(), launcherImage.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("BeginSetup did not come from the fixed installed signed launcher.");
                var created = CreationTime(process);
                var subject = ObserveSubject(approver.SessionId);
                if (BrokerWorkerLauncher.Native.WaitForSingleObject(process, 0) != 258 ||
                    !Native.GetNamedPipeClientProcessId(connected, out var again) || again != pid ||
                    !Native.GetNamedPipeClientSessionId(connected, out var againSession) || againSession != session)
                    throw new UnauthorizedAccessException("Setup caller exited or changed during observation.");
                result = new(pipe, generation, process, token, pid, created, approver, subject, launcherImage);
                token = null; return true;
            }
            catch { process.Dispose(); throw; }
            finally { token?.Dispose(); }
        })) throw new UnauthorizedAccessException("Setup activation channel is no longer current.");
        return result!;
    }

    internal SafeAccessTokenHandle DuplicateForSetupLaunch()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        launcherImage.RevalidateHeldProfile();
        if (!pipe.WithConnectedClient(connectionGeneration, connected =>
            Native.GetNamedPipeClientProcessId(connected, out var pid) && pid == processId &&
            Native.GetNamedPipeClientSessionId(connected, out var session) && session == Approver.SessionId &&
            BrokerWorkerLauncher.Native.WaitForSingleObject(process, 0) == 258 && CreationTime(process) == creationTime &&
            ReadElevatedLogon(token).Matches(Approver) && ObserveSubject(session).Matches(Subject)))
            throw new UnauthorizedAccessException("Setup activation/logon is stale.");
        if (!Native.DuplicateTokenEx(token, 0x02000000, nint.Zero, 2, 1, out var duplicate))
            throw Error("Cannot duplicate the observed elevated primary token.");
        return duplicate;
    }

    internal static BrokerLogonIdentity ObserveSubject(uint session)
    {
        RequireUnlocked(session);
        if (!BrokerWorkerLauncher.Native.WTSQueryUserToken(session, out var token)) throw Error("Cannot observe intended interactive user.");
        using (token) return BrokerWorkerLauncher.ReadLogon(token); // Medium-only contract remains unchanged.
    }

    private static void RequireUnlocked(uint session)
    {
        if (session == 0 || !Native.WTSQuerySessionInformationW(nint.Zero, session, 25, out var pointer, out var length))
            throw Error("Cannot observe the interactive session state.");
        try
        {
            // WTSINFOEX: DWORD Level, aligned LEVEL1 union. Its initial fields are session, state, flags.
            // Supported Windows 10+ profile: WTSActive=0 and WTS_SESSIONSTATE_UNLOCK=1.
            if (length is < 20 or > 65536 || Marshal.ReadInt32(pointer) != 1 ||
                unchecked((uint)Marshal.ReadInt32(pointer, 8)) != session || Marshal.ReadInt32(pointer, 12) != 0 ||
                Marshal.ReadInt32(pointer, 16) != 1)
                throw new UnauthorizedAccessException("Trusted setup requires the current unlocked interactive session.");
        }
        finally { Native.WTSFreeMemory(pointer); }
    }

    internal static BrokerLogonIdentity ReadElevatedLogon(SafeAccessTokenHandle token)
    {
        using var user = BrokerWorkerLauncher.TokenInformation(token, 1);
        using var groups = BrokerWorkerLauncher.TokenInformation(token, 28);
        using var statistics = BrokerWorkerLauncher.TokenInformation(token, 10);
        using var session = BrokerWorkerLauncher.TokenInformation(token, 12);
        using var elevation = BrokerWorkerLauncher.TokenInformation(token, 20);
        using var integrity = BrokerWorkerLauncher.TokenInformation(token, 25);
        if (Marshal.ReadInt32(groups.Pointer) != 1 || Marshal.ReadInt32(elevation.Pointer) != 1)
            throw new UnauthorizedAccessException("Setup requires an actual elevated administrator token.");
        var userSid = BrokerWorkerLauncher.ReadSid(Marshal.ReadIntPtr(user.Pointer));
        var logonSid = BrokerWorkerLauncher.ReadSid(Marshal.ReadIntPtr(groups.Pointer, 8));
        var logonAttributes = unchecked((uint)Marshal.ReadInt32(groups.Pointer, 16));
        var integritySid = BrokerWorkerLauncher.ReadSid(Marshal.ReadIntPtr(integrity.Pointer));
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (new SecurityIdentifier(userSid, 0).IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            !new SecurityIdentifier(logonSid, 0).IsWellKnown(WellKnownSidType.LogonIdsSid) ||
            (logonAttributes & 0xc0000000) != 0xc0000000 ||
            !integritySid.AsSpan().SequenceEqual(new byte[] { 1, 1, 0, 0, 0, 0, 0, 16, 0, 48, 0, 0 }))
            throw new UnauthorizedAccessException("Setup requires its distinct high-integrity administrator profile.");
        var stats = Marshal.PtrToStructure<BrokerWorkerLauncher.Native.TokenStatistics>(statistics.Pointer);
        var sessionId = unchecked((uint)Marshal.ReadInt32(session.Pointer));
        if (stats.TokenType != 1 || sessionId == 0) throw new UnauthorizedAccessException("Interactive primary setup token required.");
        var luid = unchecked((ulong)(uint)stats.AuthenticationIdLow | ((ulong)(uint)stats.AuthenticationIdHigh << 32));
        return new(userSid, logonSid, luid, sessionId);
    }

    private static ulong CreationTime(SafeProcessHandle process)
    {
        if (!BrokerWorkerLauncher.Native.GetProcessTimes(process, out var created, out _, out _, out _)) throw Error("Cannot retain setup incarnation.");
        var value = unchecked((ulong)(uint)created.dwLowDateTime | ((ulong)(uint)created.dwHighDateTime << 32));
        if (value == 0) throw new InvalidDataException("Invalid setup process incarnation."); return value;
    }
    public void Dispose() { if (disposed) return; disposed = true; token.Dispose(); process.Dispose(); }
    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, uint level, uint type, out SafeAccessTokenHandle duplicate);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSQuerySessionInformationW(nint server, uint session, int information, out nint buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(nint memory);
    }
}

/// <summary>Separate cold high-UI profile; never relaxes the native worker's medium token parser.</summary>
[SupportedOSPlatform("windows")]
internal static class BrokerSetupUiLauncher
{
    internal static BrokerRegisteredWorker Launch(IInstalledBrokerWorkerImage image, BrokerSetupActivation activation)
    {
        using (var current = WindowsIdentity.GetCurrent())
            if (current.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true) throw new UnauthorizedAccessException("Setup UI launch requires SYSTEM.");
        if (Path.GetFileName(image.ExecutablePath) != "DeskFuse.Setup.exe") throw new InvalidDataException("Wrong fixed Setup UI entrypoint.");
        image.RevalidateHeldProfile(); BrokerWorkerLauncher.ValidateImagePaths(image);
        using var token = activation.DuplicateForSetupLaunch();
        if (!BrokerSetupActivation.ReadElevatedLogon(token).Matches(activation.Approver)) throw new UnauthorizedAccessException("Approver token changed.");
        using var memory = new BrokerWorkerLauncher.Allocations();
        var userSid = new SecurityIdentifier(activation.Approver.UserSid, 0).Value;
        var processSddl = $"O:SYG:SYD:P(A;;0x001fffff;;;SY)(A;;0x001fffff;;;BA)(A;;0x00101000;;;{userSid})";
        const string threadSddl = "O:SYG:SYD:P(A;;0x001fffff;;;SY)(A;;0x001fffff;;;BA)";
        var processSecurity = memory.Security(processSddl); var threadSecurity = memory.Security(threadSddl);
        var job = new BrokerWorkerLauncher.WorkerJob(); BrokerRegisteredWorker? worker = null;
        try
        {
            using var attributes = new BrokerWorkerLauncher.CreationAttributes(job.Handle);
            var startup = new BrokerWorkerLauncher.Native.StartupInfoEx
            {
                Startup = new() { Size = (uint)Marshal.SizeOf<BrokerWorkerLauncher.Native.StartupInfoEx>(), Desktop = @"winsta0\default" },
                Attributes = attributes.Pointer
            };
            var environment = memory.String(BrokerWorkerLauncher.BuildEnvironment(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            var registration = Guid.NewGuid();
            var command = new StringBuilder('"' + image.ExecutablePath + '"' + " --setup-registration=" + registration.ToString("N"));
            if (!BrokerWorkerLauncher.Native.CreateProcessAsUserW(token, image.ExecutablePath, command, ref processSecurity, ref threadSecurity, false,
                BrokerWorkerLauncher.Native.CreateSuspended | BrokerWorkerLauncher.Native.CreateUnicodeEnvironment | BrokerWorkerLauncher.Native.ExtendedStartupInfoPresent,
                environment, image.WorkingDirectory, ref startup, out var created)) throw BrokerWorkerLauncher.Native.Error("Protected Setup UI creation failed.");
            worker = new(created, registration, activation.Approver, image, job, BrokerWorkerKind.SetupUi);
            try
            {
                job.RequireMembership(worker.ProcessHandle);
                BrokerWorkerLauncher.RequireObjectDescriptor(worker.ProcessHandle, processSddl);
                BrokerWorkerLauncher.RequireObjectDescriptor(worker.ThreadHandle, threadSddl);
                if (!BrokerWorkerLauncher.Native.GetProcessMitigationPolicy(worker.ProcessHandle, 6, out var extension, 4) || (extension & 1) == 0 ||
                    !BrokerWorkerLauncher.Native.GetProcessMitigationPolicy(worker.ProcessHandle, 13, out var child, 4) || (child & 1) == 0)
                    throw new InvalidDataException("Setup UI containment was not applied.");
                worker.ReadCreationIdentity(); image.RevalidateHeldProfile();
                worker.AttachControlPipe(BrokerLocalPipe.CreateForWorker(worker));
                if (!BrokerSetupActivation.ObserveSubject(activation.Subject.SessionId).Matches(activation.Subject))
                    throw new UnauthorizedAccessException("Intended subject changed during Setup UI launch.");
                if (BrokerWorkerLauncher.Native.ResumeThread(worker.ThreadHandle) != 1) throw BrokerWorkerLauncher.Native.Error("Setup UI did not resume exactly once.");
                return worker;
            }
            catch (Exception failure)
            {
                try { worker.StopAndJoin(); worker.Dispose(); }
                catch (Exception cleanup) { throw new BrokerWorkerLaunchIncompleteException(worker, failure, cleanup); }
                throw;
            }
        }
        finally { if (worker is null) job.Dispose(); }
    }
}

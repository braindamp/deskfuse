using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskFuse.AudioBroker;

/// <summary>
/// Supplied by the trusted installed-worker profile owner, retaining the exact RX
/// image and dependency closure. A path/hash nominated by a pipe client must
/// never implement this interface. T02's SYSTEM-only maintenance root is not an
/// installed worker directory and cannot supply this lease.
/// </summary>
internal interface IInstalledBrokerWorkerImage : IDisposable
{
    string ExecutablePath { get; }
    string WorkingDirectory { get; }
    byte[] InstalledProfileSha256 { get; }
    void RevalidateHeldProfile();
}

internal enum BrokerWorkerKind { NativeResource = 1, SetupUi = 2, EnrollmentTransport = 3 }

internal sealed class BrokerLogonIdentity(byte[] userSid, byte[] logonSid, ulong authenticationId, uint sessionId)
{
    private readonly byte[] user = userSid.ToArray(), logon = logonSid.ToArray();
    internal ulong AuthenticationId { get; } = authenticationId;
    internal uint SessionId { get; } = sessionId;
    internal byte[] UserSid => user.ToArray();
    internal byte[] LogonSid => logon.ToArray();
    internal bool Matches(BrokerLogonIdentity other) => SessionId == other.SessionId && AuthenticationId == other.AuthenticationId &&
        CryptographicOperations.FixedTimeEquals(user, other.user) && CryptographicOperations.FixedTimeEquals(logon, other.logon);
}

/// <summary>
/// Launches a fixed, protected native worker in an independently approved logon.
/// It never registers an arbitrary caller PID and never changes token privileges,
/// desktop ACLs or machine policy. No production caller is wired until the
/// installed-image and enrollment/launch ceremony are provisioned and qualified.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerWorkerLauncher
{
    internal BrokerRegisteredWorker LaunchAudio(IInstalledBrokerWorkerImage image, BrokerLogonIdentity approvedLogon,
        AudioDeliveryMode mode)
    {
        AudioDeliveryModes.RequireSupported(mode);
        if (Path.GetFileName(image.ExecutablePath) != "DeskFuse.AudioWorker.exe")
            throw new InvalidDataException("Audio requires its fixed installed worker role.");
        return Launch(image, approvedLogon);
    }
    internal BrokerRegisteredWorker Launch(IInstalledBrokerWorkerImage image, BrokerLogonIdentity approvedLogon)
        => LaunchMedium(image, approvedLogon, BrokerWorkerKind.NativeResource);

    internal BrokerRegisteredWorker LaunchEnrollment(IInstalledBrokerWorkerImage image, BrokerLogonIdentity approvedLogon)
    {
        if (Path.GetFileName(image.ExecutablePath) != "DeskFuse.EnrollmentWorker.exe")
            throw new InvalidDataException("The enrollment transport requires its fixed installed role.");
        return LaunchMedium(image, approvedLogon, BrokerWorkerKind.EnrollmentTransport);
    }

    private static BrokerRegisteredWorker LaunchMedium(IInstalledBrokerWorkerImage image, BrokerLogonIdentity approvedLogon, BrokerWorkerKind kind)
    {
        using (var current = WindowsIdentity.GetCurrent())
            if (current.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("Trusted worker launch requires SYSTEM.");
        if (kind == BrokerWorkerKind.NativeResource && Path.GetFileName(image.ExecutablePath) != "DeskFuse.AudioWorker.exe")
            throw new InvalidDataException("The native worker requires its fixed installed role.");
        if (approvedLogon.SessionId == 0) throw new InvalidDataException("An interactive user logon is required.");
        image.RevalidateHeldProfile();
        ValidateImagePaths(image);
        if (!Native.WTSQueryUserToken(approvedLogon.SessionId, out var token)) throw Native.Error("Cannot acquire intended logon token.");
        using (token)
        {
            var actualLogon = ReadLogon(token);
            if (!actualLogon.Matches(approvedLogon)) throw new UnauthorizedAccessException("The approved Windows logon changed.");
            using var privateToken = BrokerWorkerToken.Duplicate(token, approvedLogon);
            using var memory = new Allocations();
            var userSid = new SecurityIdentifier(actualLogon.UserSid, 0).Value;
            // Use concrete access masks: the object manager maps GA during
            // creation, so comparing its mapped ACL to an unmapped GA ACE
            // would reject correctly protected objects.
            var processSddl = $"O:SYG:SYD:P(A;;0x001fffff;;;SY)(A;;0x001fffff;;;BA)(A;;0x00101000;;;{userSid})";
            const string threadSddl = "O:SYG:SYD:P(A;;0x001fffff;;;SY)(A;;0x001fffff;;;BA)";
            var processSecurity = memory.Security(processSddl);
            var threadSecurity = memory.Security(threadSddl);
            var job = new WorkerJob();
            BrokerRegisteredWorker? worker = null;
            try
            {
            using var attributes = new CreationAttributes(job.Handle);
            var startup = new Native.StartupInfoEx
            {
                Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Desktop = @"winsta0\default" },
                Attributes = attributes.Pointer
            };
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // No caller environment, profile hooks, PATH additions, inherited
            // handles, redirected standard handles or arbitrary command args.
            var environment = memory.String(BuildEnvironment(windows));
            var registration = Guid.NewGuid();
            // A generated identifier, never a secret or caller command line.
            // The native worker profile accepts only this typed bootstrap arg.
            var command = new StringBuilder('"' + image.ExecutablePath + '"' +
                " --deskfuse-broker-registration=" + registration.ToString("N"));
            if (!Native.CreateProcessAsUserW(privateToken, image.ExecutablePath, command, ref processSecurity, ref threadSecurity,
                    false, Native.CreateSuspended | Native.CreateNoWindow | Native.CreateUnicodeEnvironment | Native.ExtendedStartupInfoPresent,
                    environment, image.WorkingDirectory, ref startup, out var created))
                throw Native.Error("Trusted worker creation failed.");
            worker = new BrokerRegisteredWorker(created, registration, actualLogon, image, job, kind);
            try
            {
                job.RequireMembership(worker.ProcessHandle);
                RequireObjectDescriptor(worker.ProcessHandle, processSddl);
                RequireObjectDescriptor(worker.ThreadHandle, threadSddl);
                BrokerWorkerToken.RequireProcess(worker.ProcessHandle, approvedLogon);
                if (!Native.GetProcessMitigationPolicy(worker.ProcessHandle, 6, out var extensionFlags, 4) || (extensionFlags & 1) == 0 ||
                    !Native.GetProcessMitigationPolicy(worker.ProcessHandle, 13, out var childFlags, 4) || (childFlags & 1) == 0)
                    throw new InvalidDataException("Worker extension-point mitigation was not applied.");
                image.RevalidateHeldProfile();
                worker.ReadCreationIdentity();
                // Claim the namespace before any worker code can connect. The
                // worker makes one connection; broken control requires a fresh
                // trusted launch rather than reconnecting to an unowned name.
                worker.AttachControlPipe(BrokerLocalPipe.CreateForWorker(worker));
                // A Windows session ID can be reused while creation is in
                // progress. Re-observe the current primary token before any
                // worker code runs; service WTS lock/logoff admission remains
                // an additional independently maintained lifecycle fence.
                if (!Native.WTSQueryUserToken(approvedLogon.SessionId, out var currentToken))
                    throw Native.Error("The approved user logon is no longer available.");
                using (currentToken)
                    if (!ReadLogon(currentToken).Matches(approvedLogon))
                        throw new UnauthorizedAccessException("The intended user logon changed during worker creation.");
                if (Native.ResumeThread(worker.ThreadHandle) != 1) throw Native.Error("Worker did not resume from its initial suspended state.");
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

    internal static string BuildEnvironment(string windows)
    {
        if (!Path.IsPathFullyQualified(windows) || windows.Contains('\0') || windows.Contains('='))
            throw new InvalidDataException("Invalid trusted Windows directory.");
        // Must precede runtime initialization. Removing inherited profiling
        // variables alone leaves the default same-user diagnostic port enabled.
        return $"DOTNET_EnableDiagnostics=0\0PATH={Path.Combine(windows, "System32")}\0SystemRoot={windows}\0WINDIR={windows}\0\0";
    }

    internal static void ValidateImagePaths(IInstalledBrokerWorkerImage image)
    {
        var executable = image.ExecutablePath;
        var directory = image.WorkingDirectory;
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(directory) ||
            !string.Equals(Path.GetDirectoryName(executable), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains('"') || executable.Contains('\0') || directory.Contains('\0'))
            throw new InvalidDataException("Invalid held worker image profile.");
    }

    internal static BrokerLogonIdentity ReadLogon(SafeAccessTokenHandle token)
    {
        using var user = TokenInformation(token, 1);
        using var groups = TokenInformation(token, 28);
        using var statistics = TokenInformation(token, 10);
        using var session = TokenInformation(token, 12);
        using var elevation = TokenInformation(token, 20);
        using var integrity = TokenInformation(token, 25);
        if (Marshal.ReadInt32(groups.Pointer) != 1 || Marshal.ReadInt32(elevation.Pointer) != 0)
            throw new UnauthorizedAccessException("An exact non-elevated user logon token is required.");
        var userSid = ReadSid(Marshal.ReadIntPtr(user.Pointer));
        var logonSid = ReadSid(Marshal.ReadIntPtr(groups.Pointer, 8));
        var logonAttributes = unchecked((uint)Marshal.ReadInt32(groups.Pointer, 16));
        var integritySid = ReadSid(Marshal.ReadIntPtr(integrity.Pointer));
        var userIdentity = new SecurityIdentifier(userSid, 0);
        if (userIdentity.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            !new SecurityIdentifier(logonSid, 0).IsWellKnown(WellKnownSidType.LogonIdsSid) ||
            (logonAttributes & 0xc0000000) != 0xc0000000 ||
            !integritySid.AsSpan().SequenceEqual(new byte[] { 1, 1, 0, 0, 0, 0, 0, 16, 0, 32, 0, 0 }))
            throw new UnauthorizedAccessException("Worker token must be a medium-integrity intended user logon.");
        var stats = Marshal.PtrToStructure<Native.TokenStatistics>(statistics.Pointer);
        if (stats.TokenType != 1) throw new InvalidDataException("A primary user token is required.");
        var authentication = unchecked((ulong)(uint)stats.AuthenticationIdLow | ((ulong)(uint)stats.AuthenticationIdHigh << 32));
        return new(userSid, logonSid, authentication, unchecked((uint)Marshal.ReadInt32(session.Pointer)));
    }

    internal static byte[] ReadSid(nint sid)
    {
        if (sid == nint.Zero || !Native.IsValidSid(sid)) throw new InvalidDataException("Invalid token SID.");
        var length = Native.GetLengthSid(sid);
        if (length is < 8 or > 68) throw new InvalidDataException("Unbounded token SID.");
        var bytes = new byte[length]; Marshal.Copy(sid, bytes, 0, bytes.Length); return bytes;
    }

    internal static NativeBuffer TokenInformation(SafeAccessTokenHandle token, int kind)
    {
        _ = Native.GetTokenInformation(token, kind, nint.Zero, 0, out var required);
        if (required is < 4 or > 65536) throw Native.Error("Invalid token information size.");
        var buffer = new NativeBuffer((int)required);
        if (!Native.GetTokenInformation(token, kind, buffer.Pointer, required, out _))
        { buffer.Dispose(); throw Native.Error("Cannot inspect worker logon token."); }
        return buffer;
    }

    internal static void RequireObjectDescriptor(SafeHandle handle, string expectedSddl)
    {
        var error = Native.GetSecurityInfo(handle, 6, 0x5, nint.Zero, nint.Zero, nint.Zero, nint.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "Cannot inspect created worker object security.");
        try
        {
            var length = Native.GetSecurityDescriptorLength(descriptor);
            if (descriptor == nint.Zero || length is 0 or > 65536) throw new InvalidDataException("Invalid worker security descriptor.");
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var actual = new RawSecurityDescriptor(bytes, 0);
            var expected = new RawSecurityDescriptor(expectedSddl);
            if (actual.Owner != expected.Owner || (actual.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                actual.DiscretionaryAcl is null || expected.DiscretionaryAcl is null ||
                actual.DiscretionaryAcl.Count != expected.DiscretionaryAcl.Count)
                throw new InvalidDataException("Worker object owner or DACL differs from the protected launch profile.");
            for (var i = 0; i < expected.DiscretionaryAcl.Count; i++)
            {
                var a = actual.DiscretionaryAcl[i]; var e = expected.DiscretionaryAcl[i];
                var ab = new byte[a.BinaryLength]; var eb = new byte[e.BinaryLength];
                a.GetBinaryForm(ab, 0); e.GetBinaryForm(eb, 0);
                if (!ab.AsSpan().SequenceEqual(eb)) throw new InvalidDataException("Worker object ACL differs from protected launch profile.");
            }
        }
        finally { if (descriptor != nint.Zero) Native.LocalFree(descriptor); }
    }

    internal sealed class CreationAttributes : IDisposable
    {
        internal nint Pointer { get; }
        private readonly nint mitigation, jobList, childPolicy;
        private bool initialized, disposed;
        internal CreationAttributes(SafeFileHandle job)
        {
            nuint size = 0;
            _ = Native.InitializeProcThreadAttributeList(nint.Zero, 3, 0, ref size);
            if (size is 0 or > 65536) throw Native.Error("Invalid worker attribute-list size.");
            Pointer = Marshal.AllocHGlobal((int)size);
            mitigation = Marshal.AllocHGlobal(8);
            jobList = Marshal.AllocHGlobal(nint.Size);
            childPolicy = Marshal.AllocHGlobal(4);
            try
            {
                if (!Native.InitializeProcThreadAttributeList(Pointer, 3, 0, ref size)) throw Native.Error("Cannot initialize worker mitigation attributes.");
                initialized = true;
                Marshal.WriteInt64(mitigation, unchecked((long)Native.CreationMitigations));
                if (!Native.UpdateProcThreadAttribute(Pointer, 0, (nuint)0x00020007, mitigation, 8, nint.Zero, nint.Zero))
                    throw Native.Error("Cannot set worker creation mitigations.");
                Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
                Marshal.WriteInt32(childPolicy, 1); // PROCESS_CREATION_CHILD_PROCESS_RESTRICTED
                if (!Native.UpdateProcThreadAttribute(Pointer, 0, (nuint)0x0002000d, jobList, (nuint)nint.Size, nint.Zero, nint.Zero) ||
                    !Native.UpdateProcThreadAttribute(Pointer, 0, (nuint)0x0002000e, childPolicy, 4, nint.Zero, nint.Zero))
                    throw Native.Error("Cannot atomically contain the native worker at creation.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (initialized) Native.DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(mitigation); Marshal.FreeHGlobal(jobList); Marshal.FreeHGlobal(childPolicy); Marshal.FreeHGlobal(Pointer);
        }
    }

    // The job is supplied through PROC_THREAD_ATTRIBUTE_JOB_LIST, so broker
    // death even between CreateProcessAsUser and registration cannot orphan an
    // uncontained suspended process. This native source-worker profile forbids
    // children; ordinary application/document processes use separate owners.
    internal sealed class WorkerJob : IDisposable
    {
        internal SafeFileHandle Handle { get; }
        internal WorkerJob()
        {
            using var memory = new Allocations();
            var security = memory.Security("O:SYG:SYD:P(A;;0x001f003f;;;SY)(A;;0x001f003f;;;BA)");
            Handle = Native.CreateJobObjectW(ref security, null);
            if (Handle.IsInvalid) { Handle.Dispose(); throw Native.Error("Cannot create worker lifetime job."); }
            try
            {
                var limits = new Native.ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!Native.SetInformationJobObject(Handle, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
                    throw Native.Error("Cannot arm worker lifetime job.");
            }
            catch { Handle.Dispose(); throw; }
        }
        internal void RequireMembership(SafeProcessHandle process)
        {
            if (!Native.IsProcessInJob(process, Handle, out var member) || !member)
                throw Native.Error("Created worker is not in its private lifetime job.");
        }
        internal void Terminate() { if (!Native.TerminateJobObject(Handle, 1)) throw Native.Error("Worker job termination was not accepted."); }
        internal void RequireEmpty()
        {
            if (!Native.QueryInformationJobObject(Handle, 1, out var accounting, (uint)Marshal.SizeOf<Native.BasicAccounting>(), out _) ||
                accounting.ActiveProcesses != 0)
                throw new InvalidOperationException("Worker job rundown is unconfirmed; retain its owner and custody.");
        }
        public void Dispose() => Handle.Dispose();
    }

    internal sealed class NativeBuffer(int size) : IDisposable
    {
        internal int Length { get; } = size;
        internal nint Pointer { get; } = Marshal.AllocHGlobal(size);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
    internal sealed class Allocations : IDisposable
    {
        private readonly List<nint> local = [], global = [];
        internal nint String(string value) { var ptr = Marshal.StringToHGlobalUni(value); global.Add(ptr); return ptr; }
        internal Native.SecurityAttributes Security(string sddl)
        {
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var ptr, out _)) throw Native.Error("Cannot prepare worker object descriptor.");
            local.Add(ptr);
            return new() { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = ptr, Inherit = 0 };
        }
        public void Dispose() { foreach (var ptr in local) Native.LocalFree(ptr); foreach (var ptr in global) Marshal.FreeHGlobal(ptr); }
    }

    internal static class Native
    {
        internal const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x400, ExtendedStartupInfoPresent = 0x80000;
        internal const ulong CreationMitigations = (1UL << 32) | (1UL << 52) | (1UL << 56) | (1UL << 60);
        internal static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal uint Length; internal nint Descriptor; internal int Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            internal uint Size; internal nint Reserved;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? Desktop;
            internal nint Title;
            internal uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedBytes;
            internal nint Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal nint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct TokenStatistics
        {
            internal uint TokenIdLow; internal int TokenIdHigh;
            internal uint AuthenticationIdLow; internal int AuthenticationIdHigh;
            internal long Expiration; internal uint TokenType, Impersonation, DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
            internal uint ModifiedIdLow; internal int ModifiedIdHigh;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        {
            internal long ProcessTime, JobTime; internal uint LimitFlags;
            internal nuint MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcessLimit;
            internal nuint Affinity; internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
        {
            internal BasicLimits Basic; internal IoCounters Io;
            internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicAccounting
        {
            internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            internal uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(ref SecurityAttributes attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out BasicAccounting accounting, uint length, out uint returned);
        [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, uint length, out uint required);
        [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsValidSid(nint sid);
        [DllImport("advapi32.dll")] internal static extern uint GetLengthSid(nint sid);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command,
            ref SecurityAttributes processAttributes, ref SecurityAttributes threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, out uint size);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(SafeHandle handle, int type, uint information,
            nint owner, nint group, nint dacl, nint sacl, out nint descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(nint descriptor);
        [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint pointer);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int kind, out uint policy, nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}

/// <summary>Retained process object created by the broker. No public PID-based registration path exists.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerRegisteredWorker : IBrokerAudioWorker, IDisposable
{
    internal SafeProcessHandle ProcessHandle { get; }
    internal SafeWaitHandle ThreadHandle { get; }
    public Guid Registration { get; }
    public BrokerLogonIdentity Logon { get; }
    internal BrokerWorkerKind Kind { get; }
    public uint ProcessId { get; }
    public ulong CreationFileTime { get; private set; }
    private readonly IInstalledBrokerWorkerImage image;
    public byte[] InstalledProfileSha256 => image.InstalledProfileSha256;
    private readonly BrokerWorkerLauncher.WorkerJob job;
    internal BrokerLocalPipe? ControlPipe { get; private set; }
    private bool stopped, disposed;
    internal BrokerRegisteredWorker(BrokerWorkerLauncher.Native.ProcessInformation created, Guid registration, BrokerLogonIdentity logon,
        IInstalledBrokerWorkerImage image, BrokerWorkerLauncher.WorkerJob job, BrokerWorkerKind kind = BrokerWorkerKind.NativeResource)
    {
        ProcessHandle = new(created.Process, ownsHandle: true); ThreadHandle = new(created.Thread, ownsHandle: true);
        Registration = registration; ProcessId = created.ProcessId; Logon = logon; Kind = kind; this.image = image; this.job = job;
    }
    internal void ReadCreationIdentity()
    {
        if (!BrokerWorkerLauncher.Native.GetProcessTimes(ProcessHandle, out var creation, out _, out _, out _))
            throw BrokerWorkerLauncher.Native.Error("Cannot retain worker process incarnation.");
        CreationFileTime = unchecked((ulong)(uint)creation.dwLowDateTime | ((ulong)(uint)creation.dwHighDateTime << 32));
        if (ProcessId == 0 || CreationFileTime == 0) throw new InvalidDataException("Invalid created worker identity.");
    }
    public bool IsAlive() => BrokerWorkerLauncher.Native.WaitForSingleObject(ProcessHandle, 0) switch
    {
        0 => false, 258 => true, _ => throw BrokerWorkerLauncher.Native.Error("Cannot observe retained worker process.")
    };
    internal void AttachControlPipe(BrokerLocalPipe pipe)
    {
        if (ControlPipe is not null) throw new InvalidOperationException("The worker already owns its control pipe.");
        ControlPipe = pipe;
    }
    internal void StopAndJoin()
    {
        if (stopped) return;
        ControlPipe?.Dispose(); // Fence observations before terminating work.
        job.Terminate();
        if (BrokerWorkerLauncher.Native.WaitForSingleObject(ProcessHandle, 5000) != 0)
            throw new InvalidOperationException("Worker exit is unconfirmed; retain registration and custody.");
        job.RequireEmpty();
        stopped = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        if (!stopped && IsAlive()) throw new InvalidOperationException("Close admission and confirm worker exit before disposing its registration.");
        job.RequireEmpty();
        disposed = true;
        try { ControlPipe?.Dispose(); }
        finally
        {
            ThreadHandle.Dispose(); ProcessHandle.Dispose(); job.Dispose(); image.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class BrokerWorkerLaunchIncompleteException(BrokerRegisteredWorker worker, Exception cause, Exception cleanup)
    : Exception("Worker launch failed and its exact process requires retained recovery.", new AggregateException(cause, cleanup))
{
    internal BrokerRegisteredWorker Worker { get; } = worker;
}

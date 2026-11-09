using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskFuse.WindowsSecurity;

namespace DeskFuse.AudioBroker;

/// <summary>SCM-only production authority. Dispatch/control callbacks never initialize storage or launch workers.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerAuthorityService
{
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private readonly Guid maintenance, authority, deployment;
    private readonly CancellationTokenSource stop = new();
    private readonly object statusGate = new();
    private readonly ServiceMainCallback mainCallback;
    private readonly ServiceControlCallback controlCallback;
    private nint statusHandle;
    private int stopping, result;
    private uint checkpoint;
    private BrokerAuthorityService(Guid maintenance, Guid authority, Guid deployment)
    {
        this.maintenance = maintenance; this.authority = authority; this.deployment = deployment;
        mainCallback = ServiceMain; controlCallback = Control;
    }
    internal static int Dispatch(Guid maintenance, Guid authority, Guid deployment)
    {
        TrustedAuthorityInstallation.RequireSystem();
        if (maintenance == Guid.Empty || authority == Guid.Empty || deployment == Guid.Empty)
            throw new InvalidDataException("Missing installed authority identity.");
        return new BrokerAuthorityService(maintenance, authority, deployment).DispatchCore();
    }
    private int DispatchCore()
    {
        // Two SERVICE_TABLE_ENTRYW slots: our handler followed by the null sentinel.
        var entries = Marshal.AllocHGlobal(4 * IntPtr.Size);
        var name = Marshal.StringToHGlobalUni(BrokerServicePublication.ServiceName);
        try
        {
            Marshal.WriteIntPtr(entries, 0, name);
            Marshal.WriteIntPtr(entries, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(mainCallback));
            Marshal.WriteIntPtr(entries, 2 * IntPtr.Size, nint.Zero);
            Marshal.WriteIntPtr(entries, 3 * IntPtr.Size, nint.Zero);
            if (!StartServiceCtrlDispatcherW(entries))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Only SCM may launch the authority service.");
            GC.KeepAlive(mainCallback); GC.KeepAlive(controlCallback);
            return result;
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(entries); stop.Dispose(); }
    }
    private uint Control(uint code, uint eventType, nint data, nint context)
    {
        if (code is 1 or 5) // STOP or SHUTDOWN
        {
            if (Interlocked.Exchange(ref stopping, 1) == 0)
            {
                try { Report(StopPending, 30000); }
                catch (Exception error) { Console.Error.WriteLine("SCM Stop status failed: " + error.Message); }
                stop.Cancel(); // Short admission fence executes synchronously; rundown stays on worker.
            }
            return 0;
        }
        return code == 4 ? 0u : 120u; // INTERROGATE / ERROR_CALL_NOT_IMPLEMENTED
    }
    private void ServiceMain(uint argc, nint argv)
    {
        statusHandle = RegisterServiceCtrlHandlerExW(BrokerServicePublication.ServiceName, controlCallback, nint.Zero);
        if (statusHandle == nint.Zero) { result = 1; return; }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? worker = null;
        try
        {
            Report(StartPending, 30000);
            worker = Task.Run(() => RunOwnersAsync(ready, stop.Token));
            var start = Environment.TickCount64;
            while (!ready.Task.IsCompleted && !worker.IsCompleted && !stop.IsCancellationRequested)
            {
                if (Environment.TickCount64 - start > 60000)
                {
                    stop.Cancel();
                    throw new TimeoutException("Protected setup listener was not established.");
                }
                Task.WaitAny(new[] { ready.Task, worker }, 2000);
                if (!ready.Task.IsCompleted && !worker.IsCompleted) Report(StartPending, 30000);
            }
            if (!stop.IsCancellationRequested)
            {
                if (worker.IsCompleted) worker.GetAwaiter().GetResult();
                ready.Task.GetAwaiter().GetResult();
                Report(Running, 0);
            }
            while (!worker.IsCompleted)
            {
                Task.WhenAny(worker, Task.Delay(2000)).GetAwaiter().GetResult();
                if (stop.IsCancellationRequested) Report(StopPending, 30000);
            }
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            result = 1;
            Console.Error.WriteLine("DeskFuse authority service: " + error);
            stop.Cancel();
            if (worker is not null)
            {
                while (!worker.IsCompleted)
                {
                    Task.WhenAny(worker, Task.Delay(2000)).GetAwaiter().GetResult();
                    Report(StopPending, 30000);
                }
                try { worker.GetAwaiter().GetResult(); } catch (Exception cleanup)
                { Console.Error.WriteLine("DeskFuse authority rundown: " + cleanup); }
            }
        }
        finally { Report(Stopped, 0, result == 0 ? 0u : 1u); }
    }
    private async Task RunOwnersAsync(TaskCompletionSource ready, CancellationToken cancellation)
    {
        // Startup opens existing roots/identity only. Fresh creation belongs to the installer.
        using var maintenanceRoot = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.Maintenance, maintenance);
        using var authorityRoot = SecurePrivateStorageRoot.OpenExisting(PrivateStorageProfile.BrokerAuthority, authority);
        var configuration = TrustedAuthorityInstallation.ReadConfiguration(authorityRoot, maintenance, deployment);
        using var installed = BrokerInstalledDeployment.Open(configuration);
        installed.RequireSignedAuthority(); installed.RequireSignedSetup();
        BrokerServicePublication.RequireStartingExact(configuration, installed.AuthorityExecutablePath);
        using var host = BrokerHostIdentity.OpenExisting(authorityRoot, configuration.HostKeyCreationDeploymentId);
        // EnrollmentRecordFiles acquires the one exclusive BrokerAuthority owner lease.
        // Acquiring a second lease here would conflict even inside this process.
        using var store = new TrustedBrokerEnrollmentStore(authorityRoot, host.HostSha256);
        var registry = new BrokerAuthorityState(store);
        var rundown = new BrokerEnrollmentRundown(registry);
        var persistence = new EnrollmentPersistenceOwner(store, host, rundown);
        var setup = new BrokerSetupCoordinator(installed, host, store, persistence, Enum.GetValues<BrokerProofScope>());
        using var denyOnStop = cancellation.Register(store.CloseAdmission);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // FIRST_PIPE_INSTANCE is reacquired after the prior connection and workers retire.
                using var pipe = BrokerLocalPipe.CreateSetupActivation();
                ready.TrySetResult();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                    await setup.RunActivationAsync(pipe, cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (AggregateException) { throw; } // Unconfirmed actor retirement: no new listener.
                catch (Exception error)
                {
                    if (cancellation.IsCancellationRequested) break;
                    Console.Error.WriteLine("Rejected setup activation: " + error.Message);
                    await Task.Delay(250, cancellation).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            store.CloseAdmission();
            rundown.CloseAllAdmission();
            await persistence.StopAsync().ConfigureAwait(false); // Exact joined owners, or a visible failure.
        }
    }
    private void Report(uint state, uint waitHint, uint exitCode = 0)
    {
        lock (statusGate)
        {
            if (statusHandle == nint.Zero) return;
            if (Volatile.Read(ref stopping) != 0 && state is StartPending or Running) state = StopPending;
            var value = new ServiceStatus
            {
                ServiceType = 0x10, CurrentState = state, ControlsAccepted = state == Running ? 0x5u : 0u,
                Win32ExitCode = exitCode,
                Checkpoint = state is StartPending or StopPending ? ++checkpoint : 0,
                WaitHint = waitHint
            };
            if (!SetServiceStatus(statusHandle, ref value))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SCM status update failed.");
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, Checkpoint, WaitHint;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint argc, nint argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlCallback(uint control, uint eventType, nint data, nint context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW(nint table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerExW(string name, ServiceControlCallback handler, nint context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint service, ref ServiceStatus status);
}

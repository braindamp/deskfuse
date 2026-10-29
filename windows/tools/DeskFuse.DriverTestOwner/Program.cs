using System.Diagnostics;
using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

internal static class Program
{
    internal const string BuildIdentity = "DeskFuse.DriverTestOwner/1";

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(BuildIdentity);
            Console.WriteLine("status|control|provision|watch|recover --namespace <32hex> --run <32hex>");
            Console.WriteLine("Workers require --nonce <32hex>. Control requires --action close|ack [--step 1|2|3].");
            Console.WriteLine("recover --boot is remove-only. All run roles require the prepared SYSTEM context.");
            return 0;
        }
        if (args is ["--version"]) { Console.WriteLine(BuildIdentity); return 0; }
        try
        {
            var command = OwnerCommand.Parse(args);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows x64 is required.");
            if (nint.Size != 8) throw new PlatformNotSupportedException("The driver owner requires x64.");
            return await RunWindows(command).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindows(OwnerCommand command)
    {
        using var context = OwnerRunContext.Open(command.NamespaceId, command.RunId);
        var owner = context.Coordinator(command.Role == OwnerRole.Provision);
        var accepted = true;
        switch (command.Role)
        {
            case OwnerRole.Status:
                break;
            case OwnerRole.Control:
                if (command.Action == OwnerControlAction.Close) owner.Close("OperatorRequested");
                else accepted = owner.Acknowledge(command.RunId, command.Step!.Value);
                break;
            case OwnerRole.Provision:
            {
                context.RequireIndependentSetupApproval();
                var runtime = context.Runtime();
                if (command.Nonce != runtime.CreatorNonce) throw new InvalidDataException("Wrong creator launch identity.");
                var state = owner.Snapshot();
                if (!state.Armed || state.Closing) throw new InvalidOperationException("Independent recovery is not armed.");
                var watch = WindowsRecoveryTasks.ReadWatchIdentity(context);
                using var monitor = WorkerProcessMonitor.OpenExact(watch);
                if (monitor.HasExited()) throw new InvalidOperationException("Independent watchdog has exited.");
                WindowsRecoveryTasks.Verify(context, runtime);
                using var adapter = new WindowsPnpAdapter(context.Manifest, context.DirectoryPath);
                new RunProvisioner(owner, adapter).Run(CurrentWorker(command.Nonce!.Value, WorkerRole.Creator), CancellationToken.None);
                break;
            }
            case OwnerRole.Recover:
            {
                if (command.Boot && command.Nonce != context.Runtime().BootNonce)
                    throw new InvalidDataException("Wrong boot recovery identity.");
                owner.Close(command.Boot ? "BootRecovery" : "ScopedRecovery");
                await WindowsProbeRundown.TryObserveStopped(context, owner).ConfigureAwait(false);
                using var adapter = new WindowsPnpAdapter(context.Manifest, context.DirectoryPath);
                var recoveryIdentity = CurrentWorker(command.Nonce!.Value, WorkerRole.Recovery);
                new RunRecovery(owner, adapter).RunOnce(recoveryIdentity,
                    command.Boot ? "BootRecovery" : "ScopedRecovery");
                new RecoveryReport(1, command.RunId, recoveryIdentity, command.Boot, owner.Snapshot()).Write(context.DirectoryPath);
                if (owner.Snapshot().Retired) WindowsRecoveryTasks.UnregisterRetired(context, context.Runtime());
                break;
            }
            case OwnerRole.Watch:
                return await WindowsWatchRole.Run(context, command).ConfigureAwait(false);
            default: throw new InvalidOperationException("Unknown owner role.");
        }
        var snapshot = owner.Snapshot();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new OwnerCommandResult(1, command.Role, accepted, snapshot), ContractJson.Options));
        return !accepted || (command.Role == OwnerRole.Recover && !snapshot.Retired) ? 3 : 0;
    }

    [SupportedOSPlatform("windows")]
    internal static WorkerIdentity CurrentWorker(Guid nonce, WorkerRole role)
    {
        using var current = Process.GetCurrentProcess();
        return new(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc(), nonce, role);
    }
}

internal sealed record OwnerCommandResult(int Version, OwnerRole Role, bool Accepted, RunSnapshot State);

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

[SupportedOSPlatform("windows")]
internal static class WindowsRecoveryTasks
{
    private const string TaskSecurity = "O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)";
    internal static RecoveryTaskContract Watch(OwnerRunContext context, OwnerRuntimeContract runtime) =>
        new(context.Manifest.WatchTask, context.HelperImage, Path.GetDirectoryName(context.HelperImage)!, runtime.Watch(context.Root.NamespaceId), false);
    internal static RecoveryTaskContract Boot(OwnerRunContext context, OwnerRuntimeContract runtime) =>
        new(context.Manifest.BootTask, context.HelperImage, Path.GetDirectoryName(context.HelperImage)!, runtime.BootRecovery(context.Root.NamespaceId), true);

    internal static void Verify(OwnerRunContext context, OwnerRuntimeContract runtime)
    {
        WithFolder(folder =>
        {
            VerifyTask(folder, Boot(context, runtime), requireRunning: false);
            VerifyTask(folder, Watch(context, runtime), requireRunning: true);
            return 0;
        });
    }

    // Invoked only by trusted setup, not by a worker trying to manufacture its
    // own recovery confirmation. TASK_CREATE refuses overwrite/adoption.
    internal static void Register(OwnerRunContext context, OwnerRuntimeContract runtime)
        => RegisterPrepared(context.Root, context.Manifest, runtime);

    internal static void RegisterPrepared(SecureMaintenanceRoot root, RunManifest manifest, OwnerRuntimeContract runtime)
    {
        root.ValidateHeldIdentity();
        manifest.Validate(SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, "runs/" + manifest.RunId.ToString("N")));
        runtime.Validate(manifest);
        var image = SecureMaintenanceRoot.ResolveRelative(root.DirectoryPath, "runs/" + manifest.RunId.ToString("N") + "/bundle/DeskFuse.DriverTestOwner.exe");
        RecoveryTaskContract[] specs =
        [new(manifest.BootTask, image, Path.GetDirectoryName(image)!, runtime.BootRecovery(root.NamespaceId), true),
         new(manifest.WatchTask, image, Path.GetDirectoryName(image)!, runtime.Watch(root.NamespaceId), false)];
        WithFolder(folder =>
        {
            foreach (var spec in specs)
            {
                object task = folder.RegisterTask(spec.Name, spec.ToXml(), 2, "SYSTEM", null, 5, TaskSecurity);
                try { spec.RequireMatches(((dynamic)task).Xml); RequireTaskSecurity(((dynamic)task).GetSecurityDescriptor(5)); }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            return 0;
        });
    }

    internal static void RequireNoUnresolvedTasks(SecureMaintenanceRoot root)
    {
        root.ValidateHeldIdentity();
        WithFolder(folder =>
        {
            object collection = folder.GetTasks(1); // include hidden tasks
            try
            {
                var count = (int)((dynamic)collection).Count;
                if (count > 16384) throw new InvalidDataException("Scheduler inventory exceeds its bound.");
                for (var i = 1; i <= count; i++)
                {
                    object task = ((dynamic)collection)[i];
                    try { RequireNotMaintenanceTask((string)((dynamic)task).Name); }
                    finally { Marshal.FinalReleaseComObject(task); }
                }
            }
            finally { Marshal.FinalReleaseComObject(collection); }
            return 0;
        });
    }

    internal static void RequireNotMaintenanceTask(string name)
    {
        if (name.StartsWith("Desklink-FocusIdd-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DeskFuse-FocusIdd-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An existing maintenance task requires exact-run reconciliation before new preparation.");
    }

    internal static WorkerIdentity ReadWatchIdentity(OwnerRunContext context)
    {
        using var file = context.Root.OpenProtectedRead(context.RunRelative + "/watch.json");
        var identity = ContractJson.Read<WorkerIdentity>(SecureMaintenanceRoot.ReadBounded(file, 4096));
        identity.Validate();
        if (identity.Role != WorkerRole.Watch || identity.Nonce != context.Runtime().WatchNonce || context.Journal.Snapshot().Watch != identity)
            throw new InvalidDataException("Watch identity does not match the independently armed process.");
        return identity;
    }

    internal static void UnregisterRetired(OwnerRunContext context, OwnerRuntimeContract runtime)
    {
        if (!context.Journal.Snapshot().Retired) throw new InvalidOperationException("Recovery has not retired.");
        WithFolder(folder =>
        {
            foreach (var spec in new[] { Watch(context, runtime), Boot(context, runtime) })
            {
                object? task = null;
                try
                {
                    task = folder.GetTask(spec.Name);
                    spec.RequireMatches(((dynamic)task).Xml);
                    RequireTaskSecurity(((dynamic)task).GetSecurityDescriptor(5));
                    folder.DeleteTask(spec.Name, 0);
                }
                catch (COMException e) when (e.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130f)) { }
                finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
            }
            return 0;
        });
        context.Registry.ReleaseRetired(context.Manifest.RunId);
    }

    private static void VerifyTask(dynamic folder, RecoveryTaskContract spec, bool requireRunning)
    {
        object task = folder.GetTask(spec.Name);
        try
        {
            dynamic value = task;
            spec.RequireMatches((string)value.Xml);
            RequireTaskSecurity((string)value.GetSecurityDescriptor(5));
            if (!(bool)value.Enabled || (int)value.Definition.Principal.LogonType != 5 || (requireRunning && (int)value.State != 4))
                throw new InvalidOperationException("Independent recovery task is not enabled/running.");
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    private static void RequireTaskSecurity(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544") || descriptor.DiscretionaryAcl is null ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new InvalidDataException("Recovery task has an untrusted owner or DACL.");
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            if (ace is not CommonAce common || common.IsCallback || common.AceQualifier != AceQualifier.AccessAllowed ||
                common.SecurityIdentifier.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new InvalidDataException("Recovery task grants authority outside SYSTEM/Administrators.");
    }

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        object service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            return action((dynamic)folder);
        }
        finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace DeskFuse.AudioBroker;

/// <summary>Pure ABI metadata; validation never launches a process or creates a job.</summary>
[SupportedOSPlatform("windows")]
internal static class BrokerLaunchContract
{
    internal static IReadOnlyDictionary<string, long> Values() => new Dictionary<string, long>
    {
        ["broker_security_attributes_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.SecurityAttributes>(),
        ["broker_startup_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.StartupInfo>(),
        ["broker_startup_desktop"] = Marshal.OffsetOf<BrokerWorkerLauncher.Native.StartupInfo>(nameof(BrokerWorkerLauncher.Native.StartupInfo.Desktop)).ToInt64(),
        ["broker_startup_ex_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.StartupInfoEx>(),
        ["broker_startup_ex_attributes"] = Marshal.OffsetOf<BrokerWorkerLauncher.Native.StartupInfoEx>(nameof(BrokerWorkerLauncher.Native.StartupInfoEx.Attributes)).ToInt64(),
        ["broker_process_information_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.ProcessInformation>(),
        ["broker_token_statistics_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.TokenStatistics>(),
        ["broker_token_statistics_authentication"] = Marshal.OffsetOf<BrokerWorkerLauncher.Native.TokenStatistics>(nameof(BrokerWorkerLauncher.Native.TokenStatistics.AuthenticationIdLow)).ToInt64(),
        ["broker_token_default_dacl_size"] = nint.Size,
        ["broker_token_default_dacl_information"] = 6,
        ["broker_token_all_access"] = BrokerWorkerToken.AllAccess,
        ["broker_token_query_access"] = BrokerWorkerToken.QueryAccess,
        ["broker_create_no_window"] = BrokerWorkerLauncher.Native.CreateNoWindow,
        ["broker_extended_limits_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.ExtendedLimits>(),
        ["broker_basic_accounting_size"] = Marshal.SizeOf<BrokerWorkerLauncher.Native.BasicAccounting>(),
        ["broker_active_processes_offset"] = Marshal.OffsetOf<BrokerWorkerLauncher.Native.BasicAccounting>(nameof(BrokerWorkerLauncher.Native.BasicAccounting.ActiveProcesses)).ToInt64(),
        ["broker_creation_mitigations"] = (long)BrokerWorkerLauncher.Native.CreationMitigations,
        ["broker_attribute_mitigation"] = 0x20007,
        ["broker_attribute_job_list"] = 0x2000d,
        ["broker_attribute_child_policy"] = 0x2000e,
        ["broker_child_restricted"] = 1,
        ["broker_process_all_access"] = 0x1fffff,
        ["broker_thread_all_access"] = 0x1fffff,
        ["broker_job_all_access"] = 0x1f003f,
        ["broker_extension_policy"] = 6,
        ["broker_child_policy"] = 13,
        ["broker_pipe_security_size"] = Marshal.SizeOf<BrokerLocalPipe.Native.SecurityAttributes>(),
        ["broker_pipe_client_access"] = BrokerLocalPipe.ClientAccessMask,
        ["broker_pipe_open_mode"] = BrokerLocalPipe.ServerOpenMode,
        ["broker_pipe_local_mode"] = BrokerLocalPipe.LocalBytePipeMode
    };

    internal static void Validate(byte[] bytes)
    {
        var native = JsonSerializer.Deserialize<Dictionary<string, long>>(bytes) ?? throw new InvalidDataException("Missing native ABI fixture.");
        var expected = Values();
        if (native.Count(p => p.Key.StartsWith("broker_", StringComparison.Ordinal)) != expected.Count ||
            expected.Any(p => !native.TryGetValue(p.Key, out var actual) || actual != p.Value))
            throw new InvalidDataException("The native broker launch ABI differs from the actual x64 Windows SDK.");
    }
}

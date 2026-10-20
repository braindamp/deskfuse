using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeskFuse.Audio;
using NAudio.CoreAudioApi;

namespace DeskFuse.AudioBroker;

/// <summary>
/// The DeskFuse audio endpoint broker.
///
/// Creating a Windows device for a phone is a privileged operation, so this
/// small process owns it and the unelevated LAN host talks to it over a
/// named pipe. The broker is deliberately narrow:
///
///   * it only ever creates or removes DeskFuse Audio device instances
///   * it only serves the Windows user that started it
///   * the phone identity it accepts is the enrollment hash the host already
///     authenticated, and the label is only ever a display name
///   * the lifetime of every instance belongs to the pipe connection that
///     created it, so a dead host takes its phone's endpoint with it
///
/// It never installs drivers, never changes system settings and never
/// writes outside the DeskFuse endpoint registry.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("DeskFuse.AudioBroker");
            Console.WriteLine("  Creates one Windows playback endpoint per enrolled phone using the");
            Console.WriteLine("  installed DeskFuse Audio driver. Must run elevated.");
            Console.WriteLine("  The unelevated host connects to the pipe '" + PhoneEndpointContract.PipeName + "'.");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "swtest")
        {
            var cases = new (string Label, uint Flags, bool Property)[]
            {
                ("driver required + property", SwDevice.CapabilityRemovable | SwDevice.CapabilitySilentInstall | SwDevice.CapabilityDriverRequired, true),
                ("driver required, no property", SwDevice.CapabilityDriverRequired, false),
                ("no flags, no property", 0, false),
                ("removable + silent, no property", SwDevice.CapabilityRemovable | SwDevice.CapabilitySilentInstall, false),
            };
            foreach (var (label, flags, property) in cases)
            {
                try
                {
                    using var device = SwDevice.Create(
                        "deskfuse-speakers-" + Guid.NewGuid().ToString("N")[..8],
                        "Root\\DeskFuseAudio", "DeskFuse SW Test", "ROOT", "HTREE\\ROOT\\0",
                        flags, property);
                    Console.WriteLine($"{label} -> {device.DeviceInstanceId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{label} -> {ex.Message}");
                }
            }
            return 0;
        }
        if (args.Length >= 1 && args[0] == "devtest")
        {
            try
            {
                var device = DeviceInstance.Create(
                    PhoneEndpointContract.InstanceId("00112233445566778899aabbccddeeff"),
                    PhoneEndpointContract.HardwareId, "DeskFuse Test Phone", "DeskFuse testpeer");
                Console.WriteLine($"created: {device.InstancePath} (friendly '{device.FriendlyName}')");
                Console.WriteLine("leaving it in place");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed: {ex.Message}");
            }
            return 0;
        }
        if (args.Length >= 1 && args[0] == "matrix")
        {
            DeviceInstance.Matrix();
            return 0;
        }
        if (args.Length >= 1 && args[0] == "devprobe")
        {
            DeviceInstance.Probe();
            return 0;
        }
        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "The DeskFuse audio endpoint broker must run elevated: creating a Windows " +
                "device is a privileged operation.");
            return 1;
        }
        if (DeviceInstance.FindDriverStoreInf() is null)
        {
            Console.Error.WriteLine(
                "The DeskFuse Audio driver package is not in the driver store on this PC, so no " +
                "phone speaker endpoint can be created. Install windows/driver/DeskFuseAudio first.");
            return 1;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var broker = new EndpointBroker();
        Console.WriteLine($"DeskFuse audio endpoint broker listening on '{PhoneEndpointContract.PipeName}'.");
        try
        {
            await broker.RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static readonly Guid Media = new("4d36e96c-e325-11ce-bfc1-08002be10318");
}

/// <summary>Owns the pipe and the device instances behind it.</summary>
internal sealed class EndpointBroker
{
    private sealed class Instance
    {
        public required string PeerId { get; init; }
        public required long Generation { get; init; }
        public required PhoneDevice Device { get; init; }
        public required string EndpointId { get; init; }
        public required string FriendlyName { get; init; }
    }

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _generation;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // The pipe instance belongs to the connection worker, not to this
            // loop: disposing it here would close the pipe under the client
            // that just connected.
            var server = CreatePipe();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                server.Dispose();
                continue;
            }
            _ = Task.Run(() => ServeAsync(server, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// The pipe is restricted to the Windows user that runs the broker,
    /// plus SYSTEM and the local administrators.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PhoneEndpointContract.PipeName, PipeDirection.InOut,
            maxNumberOfServerInstances: 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        // Every instance created over this connection dies with it.
        var owned = new List<(string PeerId, long Generation)>();
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        break;
                    }
                    if (line is null) break;
                    var response = await HandleAsync(line, owned, cancellationToken).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");
                    try
                    {
                        await server.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            ReleaseOwned(owned);
        }
    }

    private async Task<BrokerResponse> HandleAsync(string line, List<(string, long)> owned,
        CancellationToken cancellationToken)
    {
        BrokerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BrokerRequest>(line);
        }
        catch (JsonException)
        {
            return new BrokerResponse(false, "Malformed broker request.");
        }
        if (request is null) return new BrokerResponse(false, "Malformed broker request.");

        switch (request.Op)
        {
            case BrokerOps.Ping:
                return new BrokerResponse(true);
            case BrokerOps.List:
                return new BrokerResponse(true, Endpoints: Snapshot());
            case BrokerOps.Release:
                Release(request.PeerId);
                return new BrokerResponse(true, PeerId: request.PeerId);
            case BrokerOps.Create:
                return await CreateAsync(request, owned, cancellationToken).ConfigureAwait(false);
            default:
                return new BrokerResponse(false, "Unknown broker operation.");
        }
    }

    private async Task<BrokerResponse> CreateAsync(BrokerRequest request, List<(string, long)> owned,
        CancellationToken cancellationToken)
    {
        if (!PhoneEndpointContract.IsPeerId(request.PeerId))
            return new BrokerResponse(false, "The phone speaker identity is not a valid enrollment hash.");
        var peerId = request.PeerId!.ToLowerInvariant();
        var label = PhoneEndpointContract.NormalizeLabel(request.Label);

        Instance? existing;
        lock (_gate) existing = _instances.GetValueOrDefault(peerId);
        if (existing is not null)
        {
            // The phone reconnected while its endpoint was still alive.
            // Reuse it; the newer connection takes ownership of the lease.
            long existingLease;
            lock (_gate)
            {
                existingLease = ++_generation;
                _instances[peerId] = new Instance
                {
                    PeerId = peerId,
                    Generation = existingLease,
                    Device = existing.Device,
                    EndpointId = existing.EndpointId,
                    FriendlyName = existing.FriendlyName,
                };
            }
            owned.Add((peerId, existingLease));
            return new BrokerResponse(true, peerId, existing.EndpointId, existing.FriendlyName);
        }

        var instanceId = PhoneEndpointContract.InstanceId(peerId);
        var friendlyName = PhoneEndpointContract.DeviceFriendlyName(label);
        PhoneDevice device;
        try
        {
            device = DeviceInstance.Create(
                PhoneEndpointContract.InstancePath(peerId), PhoneEndpointContract.HardwareId,
                friendlyName, "DeskFuse " + peerId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // A device instance for this phone could not be created. The
            // endpoint the driver already publishes is then used for this
            // phone, and renamed after it. This is a shared endpoint: it can
            // carry one phone at a time until device instance creation works
            // on this Windows build.
            var shared = await ClaimSharedAsync(friendlyName, cancellationToken).ConfigureAwait(false);
            if (shared is null)
                return new BrokerResponse(false, ex.Message);
            Console.WriteLine("The phone speaker device instance could not be created; " +
                "this phone is using the shared DeskFuse endpoint for now.");
            return new BrokerResponse(true, peerId, shared, friendlyName, "shared");
        }

        var endpointId = await EndpointResolver.ResolveAsync(friendlyName, instanceId, TimeSpan.FromSeconds(20),

            cancellationToken, device.InstancePath).ConfigureAwait(false);
        if (endpointId is null)
        {
            DeviceInstance.Remove(device.InstancePath);
            return new BrokerResponse(false,
                "Windows created the phone speaker device but no audio endpoint appeared for it. " +
                "Check the DeskFuse Audio driver in Device Manager.");
        }

        long lease;
        lock (_gate)
        {
            lease = ++_generation;
            _instances[peerId] = new Instance
            {
                PeerId = peerId,
                Generation = lease,
                Device = device,
                EndpointId = endpointId,
                FriendlyName = friendlyName,
            };
        }
        owned.Add((peerId, lease));
        return new BrokerResponse(true, peerId, endpointId, friendlyName, device.InstancePath);
    }

    /// <summary>
    /// Removes the instances this connection created, unless a newer
    /// connection has already taken the lease over. A stale teardown must
    /// never remove the endpoint of a phone that has just reconnected.
    /// </summary>

    /// <summary>
    /// Finds the endpoint the driver already publishes and names it after
    /// the phone that is using it.
    /// </summary>
    private async Task<string?> ClaimSharedAsync(string friendlyName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var (endpointId, instancePath) = await EndpointResolver
                .FindDeskFuseAsync(cancellationToken).ConfigureAwait(false);
            if (endpointId is null) { await Task.Delay(500, cancellationToken).ConfigureAwait(false); continue; }
            if (instancePath is not null) DeviceInstance.RenameDevice(instancePath, friendlyName);
            return endpointId;
        }
        return null;
    }

    private void ReleaseOwned(List<(string PeerId, long Generation)> owned)
    {
        foreach (var (peerId, generation) in owned)
        {
            Instance? removed = null;
            lock (_gate)
            {
                if (_instances.TryGetValue(peerId, out var current) && current.Generation == generation)
                {
                    _instances.Remove(peerId);
                    removed = current;
                }
            }
            if (removed is not null) DeviceInstance.Remove(removed.Device.InstancePath);
        }
    }

    private void Release(string? peerId)
    {
        if (!PhoneEndpointContract.IsPeerId(peerId)) return;
        Instance? removed = null;
        lock (_gate)
        {
            _instances.Remove(peerId!.ToLowerInvariant(), out removed);
        }
        if (removed is not null) DeviceInstance.Remove(removed.Device.InstancePath);
    }

    private IReadOnlyList<BrokerResponse> Snapshot()
    {
        lock (_gate)
        {
            return _instances.Values
                .Select(i => new BrokerResponse(true, i.PeerId, i.EndpointId, i.FriendlyName))
                .ToArray();
        }
    }
}

/// <summary>
/// Finds the audio endpoint that Windows created for a device.
///
/// The endpoint's own property store carries the device instance path, so
/// the endpoint is matched by identity rather than by guessing from its
/// display name.
/// </summary>
internal static class EndpointResolver
{
    internal static async Task<string?> ResolveAsync(string friendlyName, string instanceId,
        TimeSpan timeout, CancellationToken cancellationToken, string? instancePath = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var found = TryResolve(friendlyName, instancePath);
            if (found is not null) return found;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string? TryResolve(string friendlyName, string? instancePath)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var deskfuseCandidates = new List<string>();
            foreach (var state in new[] { DeviceState.Active, DeviceState.Unplugged, DeviceState.NotPresent })
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, state))
                {
                    using (device)
                    {
                        try
                        {
                            // Identity first: the endpoint belongs to the
                            // device instance the broker created.
                            if (instancePath is not null && Mentions(device, instancePath))
                                return device.ID;
                            if (device.FriendlyName.Contains(friendlyName, StringComparison.OrdinalIgnoreCase))
                                return device.ID;
                            if (device.FriendlyName.Contains("DeskFuse", StringComparison.OrdinalIgnoreCase))
                                deskfuseCandidates.Add(device.ID);
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                            // A device can depart while it is being inspected.
                        }
                    }
                }
            }
            if (deskfuseCandidates.Count == 1) return deskfuseCandidates[0];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The audio service can be busy; try again on the next tick.
        }
        return null;
    }

    /// <summary>
    /// Finds the endpoint the DeskFuse driver publishes, and the device
    /// instance it belongs to.
    /// </summary>
    internal static Task<(string? EndpointId, string? InstancePath)> FindDeskFuseAsync(
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (!device.FriendlyName.Contains("DeskFuse", StringComparison.OrdinalIgnoreCase))
                    continue;
                return Task.FromResult<(string?, string?)>((device.ID, InstancePathOf(device)));
            }
        }
        return Task.FromResult<(string?, string?)>((null, null));
    }

    /// <summary>The device instance path an endpoint belongs to, if it has one.</summary>
    private static string? InstancePathOf(MMDevice device)
    {
        try
        {
            var store = device.Properties;
            for (var i = 0; i < store.Count; i++)
            {
                string? text;
                try { text = store.GetValue(i).Value as string; }
                catch (NotImplementedException) { continue; }
                if (text is null) continue;
                var marker = text.IndexOf("}.", StringComparison.Ordinal);
                if (marker < 0) continue;
                var path = text[(marker + 2)..];
                if (path.Contains('\\'))
                    return path;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>True when one of the endpoint's properties names the device.</summary>
    private static bool Mentions(MMDevice device, string instancePath)
    {
        try
        {
            var store = device.Properties;
            for (var i = 0; i < store.Count; i++)
            {
                string? text;
                try { text = store.GetValue(i).Value as string; }
                catch (NotImplementedException) { continue; }
                if (text is not null && text.Contains(instancePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException)
        {
        }
        return false;
    }
}

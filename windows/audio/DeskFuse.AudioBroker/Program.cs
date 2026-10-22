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
///   * a phone's endpoint is removed when its active session disconnects;
///     reconnecting provisions it again without requiring user approval.
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
                    PhoneEndpointContract.InstancePath("00112233445566778899aabbccddeeff"),
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
        finally { await broker.ShutdownAsync().ConfigureAwait(false); }
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
        public string? InstancePath { get; init; }
    }

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
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
            await ReleaseOwnedAsync(owned).ConfigureAwait(false);
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
                return await ReleaseAsync(request.PeerId, owned, cancellationToken).ConfigureAwait(false);
            case BrokerOps.Create:
                // Device creation, lease takeover and removal must not race
                // with another connection for the same stable PnP path.
                await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await CreateAsync(request, owned, cancellationToken).ConfigureAwait(false); }
                finally { _deviceGate.Release(); }
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
        var friendlyName = PhoneEndpointContract.DeviceFriendlyName(label);

        Instance? existing;
        lock (_gate) existing = _instances.GetValueOrDefault(peerId);
        if (existing is not null)
        {
            // The phone reconnected while its endpoint was still alive.
            // Reuse it; the newer connection takes ownership of the lease.
            // The endpoint id is resolved again: Windows gives an endpoint a
            // new id when the device is re-enumerated, and a stale id would
            // point at a device that no longer exists.
            long existingLease;
            var refreshed = await ResolveAsync(existing, cancellationToken)
                .ConfigureAwait(false);
            if (refreshed is null)
                return new BrokerResponse(false, "The phone's playback endpoint is not active.");
            lock (_gate)
            {
                existingLease = ++_generation;
                _instances[peerId] = new Instance
                {
                    PeerId = peerId,
                    Generation = existingLease,
                    Device = existing.Device,
                    EndpointId = refreshed,
                    FriendlyName = existing.FriendlyName,
                    InstancePath = existing.InstancePath,
                };
            }
            owned.Add((peerId, existingLease));
            return new BrokerResponse(true, PeerId: peerId, EndpointId: refreshed,
                DeviceFriendlyName: existing.FriendlyName, DeviceInstanceId: existing.Device.InstancePath);
        }

        PhoneDevice device;
        try
        {
            device = DeviceInstance.Create(
                PhoneEndpointContract.InstancePath(peerId), PhoneEndpointContract.HardwareId,
                friendlyName, "DeskFuse " + peerId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return new BrokerResponse(false, ex.Message);
        }

        string? endpointId;
        try
        {
            endpointId = await EndpointResolver.ResolveAsync(device.InstancePath, TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeviceInstance.RemoveAsync(device.InstancePath).ConfigureAwait(false);
            throw;
        }
        if (endpointId is null)
        {
            await DeviceInstance.RemoveAsync(device.InstancePath).ConfigureAwait(false);
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
                InstancePath = device.InstancePath,
            };
        }
        owned.Add((peerId, lease));
        return new BrokerResponse(true, PeerId: peerId, EndpointId: endpointId,
            DeviceFriendlyName: friendlyName, DeviceInstanceId: device.InstancePath);
    }

    /// <summary>
    /// Finds the endpoint of an existing instance again, so a reconnected
    /// phone never receives the id of an endpoint that has been replaced.
    /// </summary>
    private async Task<string?> ResolveAsync(Instance instance, CancellationToken cancellationToken)
    {
        return await EndpointResolver.ResolveAsync(instance.Device.InstancePath,
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Close of the authenticated phone's broker lease removes the endpoint
    /// promptly, including when airplane mode retires the old TCP session.
    /// Windows no longer retains a phantom phone for a three-minute grace.
    /// A new authenticated connection may provision the endpoint again.
    /// </summary>
    private async Task ReleaseOwnedAsync(List<(string PeerId, long Generation)> owned)
    {
        await _deviceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (peerId, generation) in owned.Distinct())
            {
                Instance? current;
                lock (_gate)
                    current = _instances.TryGetValue(peerId, out var instance) &&
                        instance.Generation == generation ? instance : null;
                if (current is null) continue; // Replaced by a newer pipe lease.

                try
                {
                    await RemoveReleasedDeviceAsync(current).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_instances.TryGetValue(peerId, out var existing) &&
                            existing.Generation == generation)
                            _instances.Remove(peerId);
                    }
                }
                catch (Exception ex)
                {
                    // Keep the identity in the registry if PnP removal fails.
                    // A new Create must never generate a duplicate orphan.
                    Console.Error.WriteLine(
                        $"speaker endpoint removal failed on disconnect: {ex.Message}");
                }
            }
        }
        finally { _deviceGate.Release(); }
    }

    /// <summary>
    /// Broker shutdown also removes all owned nodes so a dead host cannot
    /// leave behind phantom playback devices.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await _deviceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Instance[] instances;
            lock (_gate) instances = _instances.Values.ToArray();
            foreach (var instance in instances)
            {
                try
                {
                    await RemoveReleasedDeviceAsync(instance).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_instances.TryGetValue(instance.PeerId, out var current)
                            && current.Generation == instance.Generation)
                            _instances.Remove(instance.PeerId);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"speaker endpoint shutdown removal failed: {ex.Message}");
                }
            }
        }
        finally { _deviceGate.Release(); }
    }

    private async Task<BrokerResponse> ReleaseAsync(string? peerId,
        List<(string PeerId, long Generation)> owned, CancellationToken cancellationToken)
    {
        if (!PhoneEndpointContract.IsPeerId(peerId))
            return new BrokerResponse(false, "The phone speaker identity is not a valid enrollment hash.");
        peerId = peerId!.ToLowerInvariant();
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Instance? removed = null;
            lock (_gate)
            {
                if (_instances.TryGetValue(peerId, out var current))
                {
                    if (!owned.Contains((peerId, current.Generation)))
                        return new BrokerResponse(false, "This connection does not own the phone endpoint lease.",
                            PeerId: peerId);
                    removed = current;
                }
            }
            // Wait until Windows confirms departure before forgetting the PnP
            // instance. If Windows refuses removal, keep its stable identity
            // so a later reconnect cannot create a duplicate device.
            if (removed is not null)
            {
                try
                {
                    await RemoveReleasedDeviceAsync(removed).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_instances.TryGetValue(peerId, out var current) &&
                            current.Generation == removed.Generation)
                            _instances.Remove(peerId);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"speaker endpoint removal failed on release: {ex.Message}");
                    return new BrokerResponse(false,
                        "Windows could not remove the released phone speaker: " + ex.Message,
                        PeerId: peerId);
                }
            }
            return new BrokerResponse(true, PeerId: peerId);
        }
        finally { _deviceGate.Release(); }
    }

    /// <summary>
    /// Remove only the enrollment-derived DeskFuse device that the lease owned.
    /// Caller MUST hold _deviceGate. Never delete a shared/root driver device.
    /// DeviceInstance.RemoveAsync validates the path and confirms departure.
    /// </summary>
    private static async Task RemoveReleasedDeviceAsync(Instance removed)
    {
        await DeviceInstance.RemoveAsync(removed.Device.InstancePath).ConfigureAwait(false);
        Console.WriteLine($"speaker-endpoint-removed=True instance={removed.Device.InstancePath}");
    }

    private IReadOnlyList<BrokerResponse> Snapshot()
    {
        lock (_gate)
        {
            return _instances.Values
                .Select(i => new BrokerResponse(true, PeerId: i.PeerId, EndpointId: i.EndpointId,
                    DeviceFriendlyName: i.FriendlyName, DeviceInstanceId: i.Device.InstancePath))
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
    internal static async Task<string?> ResolveAsync(string instancePath,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var found = TryResolve(instancePath);
            if (found is not null) return found;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string? TryResolve(string instancePath)
    {
        try { return FindActiveEndpoint(instancePath); }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The audio service can be busy; try again on the next tick.
        }
        return null;
    }

    // Removal requires a successful scan. A COM failure must never be
    // mistaken for proof that the old endpoint has departed.
    internal static string? FindActiveEndpoint(string instancePath)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (Mentions(device, instancePath)) return device.ID;
            }
        }
        return null;
    }

    /// <summary>True when one of the endpoint's properties names the device.</summary>
    private static bool Mentions(MMDevice device, string instancePath)
    {
        var store = device.Properties;
        for (var i = 0; i < store.Count; i++)
        {
            string? text;
            try { text = store.GetValue(i).Value as string; }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException) { continue; }
            if (text is null) continue;
            if (text.Equals(instancePath, StringComparison.OrdinalIgnoreCase)) return true;
            // Windows audio properties carry e.g. {1}.ROOT\\MEDIA\\id.
            // Compare the complete suffix, never a device-id prefix.
            var marker = text.IndexOf("}.", StringComparison.Ordinal);
            if (marker >= 0 && text[(marker + 2)..].Equals(instancePath,
                    StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace FocusWindow.Core.Session;

/// <summary>
/// Identity and session ownership for authenticated accessory-capability peers.
/// This is NOT a Windows PnP device provider; a later signed endpoint service
/// will consume these lifetimes to publish real camera/audio device interfaces.
/// </summary>
public sealed class DevicePeerRegistry
{
    public sealed record Endpoints(string Camera, string Microphone, string Speakers);
    public sealed record ConnectedPeer(string DeviceId, string Label, Endpoints Endpoints,
        IReadOnlyList<string> RequestedCapabilities);
    private sealed record OnlineEntry(Guid Lease, ConnectedPeer Peer, CancellationTokenSource Stop,
        TaskCompletionSource Terminated);
    private readonly Dictionary<string, OnlineEntry> _online = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _takeover = new();
    private readonly string _hostScope;

    /// <param name="hostScope">Stable host-specific identity, not an IP/port.</param>
    public DevicePeerRegistry(string hostScope)
    {
        if (string.IsNullOrWhiteSpace(hostScope)) throw new ArgumentException("Host identity required", nameof(hostScope));
        _hostScope = hostScope;
    }

    public Endpoints Identities(string enrolledDeviceId)
    {
        if (string.IsNullOrWhiteSpace(enrolledDeviceId)) throw new ArgumentException("Enrolled device ID required", nameof(enrolledDeviceId));
        // Stable endpoint identity across network/session reconnect. These are
        // logical provider keys, NOT Windows device-interface IDs yet.
        string Key(string kind)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
                "deskfuse:endpoint:v1:" + _hostScope + ":" + enrolledDeviceId + ":" + kind));
            return Convert.ToHexString(bytes.AsSpan(0, 16));
        }
        return new Endpoints(Key("camera"), Key("microphone"), Key("speakers"));
    }

    public async Task<PeerLease> AttachAsync(string deviceId, string label, CancellationToken shutdown,
        IEnumerable<string>? requestedCapabilities = null)
    {
        var serial = _takeover.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await serial.WaitAsync(shutdown).ConfigureAwait(false);
        try
        {
            OnlineEntry? prior;
            lock (_gate) prior = _online.GetValueOrDefault(deviceId);
            if (prior is not null)
            {
                try { prior.Stop.Cancel(); } catch (ObjectDisposedException) { }
                // Completion is signalled by lease Dispose, AFTER async route
                // disposal has joined acquisition and released its native owner.
                await prior.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(15), shutdown).ConfigureAwait(false);
            }
            return Attach(deviceId, label, shutdown, requestedCapabilities);
        }
        finally { serial.Release(); }
    }

    public PeerLease Attach(string deviceId, string label, CancellationToken shutdown,
        IEnumerable<string>? requestedCapabilities = null)
    {
        var requested = DeviceCapabilities.Validate(requestedCapabilities, accessoryOnly: true);
        var peer = new ConnectedPeer(deviceId, label, Identities(deviceId), requested);
        var token = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var lease = Guid.NewGuid();
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnlineEntry? prior;
        lock (_gate)
        {
            _online.TryGetValue(deviceId, out prior);
            _online[deviceId] = new OnlineEntry(lease, peer, token, terminated);
        }
        // A reconnect supersedes an older authenticated socket of the same
        // enrollment. Never allow its eventual teardown to evict the new peer.
        if (prior is not null)
        {
            // Prior lease can be disposing concurrently with this Attach.
            try { prior.Stop.Cancel(); }
            catch (ObjectDisposedException) { /* old session already finished */ }
        }
        return new PeerLease(this, deviceId, lease, token, peer, terminated);
    }

    public IReadOnlyList<ConnectedPeer> Online
    {
        get { lock (_gate) return _online.Values.Select(entry => entry.Peer).ToArray(); }
    }

    private void Release(string deviceId, Guid lease)
    {
        lock (_gate)
        {
            if (_online.TryGetValue(deviceId, out var current) && current.Lease == lease)
                _online.Remove(deviceId);
        }
    }

    public sealed class PeerLease : IDisposable
    {
        private readonly DevicePeerRegistry _registry;
        private readonly string _deviceId;
        private readonly Guid _lease;
        private readonly CancellationTokenSource _source;
        private readonly TaskCompletionSource _terminated;
        private int _disposed;
        public ConnectedPeer Peer { get; }
        public CancellationToken Cancellation => _source.Token;

        internal PeerLease(DevicePeerRegistry registry, string deviceId, Guid lease,
            CancellationTokenSource source, ConnectedPeer peer, TaskCompletionSource terminated)
        {
            _registry = registry;
            _deviceId = deviceId;
            _lease = lease;
            _source = source;
            _terminated = terminated;
            Peer = peer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _source.Cancel(); }
            finally
            {
                _registry.Release(_deviceId, _lease);
                _source.Dispose();
                _terminated.TrySetResult();
            }
        }
    }
}

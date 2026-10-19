using System.Security.Cryptography;
using System.Text;

namespace FocusWindow.Core.Session;

/// <summary>
/// Identity and session ownership for authenticated media-only Android peers.
/// This is NOT a Windows PnP device provider; a later signed endpoint service
/// will consume these lifetimes to publish real camera/audio device interfaces.
/// </summary>
public sealed class DevicePeerRegistry
{
    public sealed record Endpoints(string Camera, string Microphone, string Speakers);
    public sealed record ConnectedPeer(string DeviceId, string Label, Endpoints Endpoints);
    private sealed record Online(Guid Lease, ConnectedPeer Peer, CancellationTokenSource Stop);
    private readonly Dictionary<string, Online> _online = new(StringComparer.Ordinal);
    private readonly object _gate = new();
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

    public PeerLease Attach(string deviceId, string label, CancellationToken shutdown)
    {
        var peer = new ConnectedPeer(deviceId, label, Identities(deviceId));
        var token = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var lease = Guid.NewGuid();
        Online? prior;
        lock (_gate)
        {
            _online.TryGetValue(deviceId, out prior);
            _online[deviceId] = new Online(lease, peer, token);
        }
        // A reconnect supersedes an older authenticated socket of the same
        // enrollment. Never allow its eventual teardown to evict the new peer.
        if (prior is not null) prior.Stop.Cancel();
        return new PeerLease(this, deviceId, lease, token, peer);
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
        private int _disposed;
        public ConnectedPeer Peer { get; }
        public CancellationToken Cancellation => _source.Token;

        internal PeerLease(DevicePeerRegistry registry, string deviceId, Guid lease,
            CancellationTokenSource source, ConnectedPeer peer)
        {
            _registry = registry;
            _deviceId = deviceId;
            _lease = lease;
            _source = source;
            Peer = peer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registry.Release(_deviceId, _lease);
            _source.Cancel();
            _source.Dispose();
        }
    }
}

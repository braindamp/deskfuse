using FocusWindow.Core.Session;
using NAudio.CoreAudioApi;

namespace FocusWindow.Agent;

/// <summary>
/// The phone speaker provider, backed by the local endpoint broker.
///
/// The broker owns the privileged part (creating a Windows device is
/// privileged). This class is the host side: it asks for the endpoint of
/// one enrolled phone, keeps its pipe lease for the length of this session,
/// and mirrors Windows volume. Disposing an ended session removes the
/// endpoint promptly, rather than leaving a phantom Windows audio output.
/// </summary>
internal sealed class BrokerPhoneSpeakerProvider : IPhoneSpeakerProvider
{
    private readonly DeskFuse.Audio.PhoneEndpointBrokerClient _client;
    private readonly PhoneSpeakerDefaultRestorer _defaults = new();

    public BrokerPhoneSpeakerProvider(DeskFuse.Audio.PhoneEndpointBrokerClient client)
    {
        _client = client;
    }

    public async Task<(IPhoneSpeakerEndpoint? Endpoint, string? Error)> ProvisionAsync(
        string peerId, string label, CancellationToken ct)
    {
        // A separate retained pipe is the lease of this session, including
        // when another session reconnects with the same enrollment identity.
        var client = _client.CreateLeaseClient();
        try
        {
            var (endpointId, error) = await client.ProvisionAsync(peerId, label, ct).ConfigureAwait(false);
            if (endpointId is null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return (null, error);
            }
            var endpoint = new BrokerPhoneSpeakerEndpoint(client, endpointId, peerId);
            var lease = _defaults.Attached(peerId);
            endpoint.TrackDefaults(_defaults, lease.Generation);
            // Give Windows' own audio-selection history the first chance.
            // A private-policy fallback is attempted only if this phone was
            // the prior default and no later user selection was observed.
            try
            {
                await _defaults.RestoreOnReturnAsync(peerId, endpointId, lease, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await endpoint.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return (endpoint, null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or ArgumentException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return (null, "The phone speaker endpoint appeared but Windows would not open it.");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ReleaseAsync(string peerId, CancellationToken ct) => _client.ReleaseAsync(peerId, ct);
}

/// <summary>
/// One phone's Windows playback endpoint, with its volume control.
/// </summary>
internal sealed class BrokerPhoneSpeakerEndpoint : IPhoneSpeakerEndpoint
{
    private readonly DeskFuse.Audio.PhoneEndpointBrokerClient _client;
    private readonly string _peerId;
    private readonly MMDevice _device;
    private PhoneSpeakerDefaultRestorer? _defaults;
    private long _leaseGeneration;
    private readonly AudioEndpointVolume? _volume;
    private readonly AudioEndpointVolumeNotificationDelegate? _volumeHandler;
    private int _released;

    public BrokerPhoneSpeakerEndpoint(DeskFuse.Audio.PhoneEndpointBrokerClient client,
        string endpointId, string peerId)
    {
        _client = client;
        _peerId = peerId;
        EndpointId = endpointId;
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(endpointId);
        try
        {
            FriendlyName = _device.FriendlyName;
            _volume = _device.AudioEndpointVolume;
            _volumeHandler = notification => VolumeChanged?.Invoke(Volume);
            _volume.OnVolumeNotification += _volumeHandler;
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    internal void TrackDefaults(PhoneSpeakerDefaultRestorer defaults, long generation)
    {
        _defaults = defaults;
        _leaseGeneration = generation;
    }

    public string EndpointId { get; }

    public string FriendlyName { get; }

    public float Volume
    {
        get
        {
            try { return _volume?.Mute == true ? 0f : _volume?.MasterVolumeLevelScalar ?? 1f; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return 1f;
            }
        }
    }

    public event Action<float>? VolumeChanged;

    public void SetVolume(float volume)
    {
        if (_volume is null) return;
        try { _volume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The endpoint stopped accepting volume changes.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try
        {
            if (_volume is not null)
            {
                if (_volumeHandler is not null) _volume.OnVolumeNotification -= _volumeHandler;
            }
        }
        finally
        {
            // Record the user's selected output BEFORE releasing the device.
            // After PnP departure Windows picks a fallback automatically.
            var hint = _defaults?.BeforeDeparture(_peerId, _leaseGeneration, EndpointId);
            // MMDevice owns its cached AudioEndpointVolume COM wrapper.
            // Keep both alive together and let the device dispose it once.
            try { _device.Dispose(); }
            finally
            {
                // An ended authenticated phone session is not an active
                // Windows speaker. Remove the endpoint immediately; the
                // durable pairing credential is retained for the next join.
                try { await _client.ReleaseAsync(_peerId, CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                    _defaults?.Departed(_peerId, _leaseGeneration, hint);
                }
            }
        }
    }
}

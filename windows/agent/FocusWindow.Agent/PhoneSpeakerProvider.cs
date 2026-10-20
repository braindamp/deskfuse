using FocusWindow.Core.Session;
using NAudio.CoreAudioApi;

namespace FocusWindow.Agent;

/// <summary>
/// The phone speaker provider, backed by the local endpoint broker.
///
/// The broker owns the privileged part (creating a Windows device is
/// privileged). This class is the host side: it asks for the endpoint of
/// one enrolled phone, keeps it for the length of the connection, and
/// mirrors the Windows volume control onto the phone.
/// </summary>
internal sealed class BrokerPhoneSpeakerProvider : IPhoneSpeakerProvider
{
    private readonly DeskFuse.Audio.PhoneEndpointBrokerClient _client;

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
            return (new BrokerPhoneSpeakerEndpoint(client, endpointId, peerId), null);
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
            // MMDevice owns its cached AudioEndpointVolume COM wrapper.
            // Keep both alive together and let the device dispose it once.
            try { _device.Dispose(); }
            finally
            {
                try { await _client.ReleaseAsync(_peerId, CancellationToken.None).ConfigureAwait(false); }
                finally { await _client.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}

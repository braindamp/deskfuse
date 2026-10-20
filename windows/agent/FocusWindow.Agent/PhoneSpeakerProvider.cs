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
        var (endpointId, error) = await _client.ProvisionAsync(peerId, label, ct).ConfigureAwait(false);
        if (endpointId is null) return (null, error);
        try
        {
            return (new BrokerPhoneSpeakerEndpoint(_client, endpointId, peerId), null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or ArgumentException)
        {
            await _client.ReleaseAsync(peerId, ct).ConfigureAwait(false);
            return (null, "The phone speaker endpoint appeared but Windows would not open it.");
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
        var device = enumerator.GetDevice(endpointId);
        try
        {
            FriendlyName = device.FriendlyName;
            _volume = device.AudioEndpointVolume;
            _volumeHandler = notification => VolumeChanged?.Invoke(Volume);
            _volume.OnVolumeNotification += _volumeHandler;
        }
        finally
        {
            device.Dispose();
        }
    }

    public string EndpointId { get; }

    public string FriendlyName { get; }

    public float Volume
    {
        get
        {
            try { return _volume?.MasterVolumeLevelScalar ?? 1f; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return 1f;
            }
        }
    }

    public event Action<float>? VolumeChanged;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        if (_volume is not null)
        {
            if (_volumeHandler is not null) _volume.OnVolumeNotification -= _volumeHandler;
            _volume.Dispose();
        }
        await _client.ReleaseAsync(_peerId, CancellationToken.None).ConfigureAwait(false);
    }
}

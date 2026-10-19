using System.Net;
using System.Security.Cryptography;
using FocusWindow.Core.Media;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
/// <summary>
/// One virtual-display owner at a time, while media-only authenticated phones
/// can connect simultaneously without creating or changing any monitor.
///
/// The pairing is deliberately LONG-LIVED: the phone stores the host key + salt so it can
/// reattach after sleep or a Wi-Fi handover without a new scan (FR-REF-05). Authorisation
/// is therefore split: possession of the QR secret gets a phone to the approval prompt, and
/// the human at the PC (or a previous approval) decides whether it gets the desktop.
/// </summary>
public sealed class SessionHost : IDisposable
{
    private readonly HostOptions _options;
    private readonly HostServices _services;
    private readonly ILog _log;
    private HostListener? _listener;
    private PairingOffer? _offer;
    private CameraFreePairingHost? _cameraFree;
    private DevicePeerRegistry? _mediaPeers;
    private string _encoderName = "pending native-resolution qualification";
    public SessionHost(HostOptions options, HostServices services, ILog log)
    {
        _options = options;
        _services = services;
        _log = log;
    }
    public PairingOffer? Offer => _offer;
    public int Port => _listener?.Port ?? 0;
    public string EncoderName => _encoderName;
    /// <summary>Binds the port, picks a working encoder, and builds the QR offer.</summary>
    public Task<PairingOffer> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The phone's native dimensions are unknown here. Selecting a GPU based
        // on a tiny startup probe can produce a black screen at 1440x3120.
        // PhoneSession qualifies NVENC/QSV/AMF/software after density negotiation.
        _listener = new HostListener(IPAddress.Parse(_options.Bind), _options.Port, _log);
        var state = Pairing.LoadOrCreate(_services.PairingStore, _options.NewPairing);
        var address = Pairing.PickLanAddress() ?? "127.0.0.1";
        var offer = Pairing.BuildOffer(state, address, _listener.Port, _options.HostName);
        _offer = offer;
        // Host key is stable across ordinary restarts, unlike the host address.
        // DevicePeerRegistry provides *logical* identities only, not PnP devices.
        var scope = Convert.ToHexString(SHA256.HashData(
            offer.KeyPair.ExportSubjectPublicKeyInfo()));
        _mediaPeers = new DevicePeerRegistry(scope);
        if (_options.CameraFreePairing)
        {
            if (_services.DeviceCredentials is null)
                throw new InvalidOperationException("camera-free pairing requires a persistent credential store");
            _cameraFree = new CameraFreePairingHost(offer, _options.HostName,
                _services.DeviceCredentials, _services.Approval, _log);
        }
        _log.Info($"listening on {_options.Bind}:{_listener.Port}; pairing address {address}");
        return Task.FromResult(offer);
    }
    public async Task RunSessionsAsync(CancellationToken ct)
    {
        if (_listener is null || _offer is null || _mediaPeers is null)
            throw new InvalidOperationException("call StartAsync first");
        var peers = _mediaPeers;
        // Display session is exclusive to the monitor. Camera/mic/speaker peers
        // can connect in parallel; these sessions never instantiate PhoneSession.
        Task? displayTask = null;
        var mediaTasks = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                (PhoneConnection connection, Protocol.ClientHello hello) accepted;
                try
                {
                    accepted = await _listener.AcceptAuthenticatedAsync(_offer,
                        _options.CameraFreePairing ? _services.DeviceCredentials : null, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                if (accepted.hello.Role == "media")
                {
                    // Media-only identities MUST be backed by per-device
                    // credentials. Legacy QR credentials don't bind DeviceId.
                    if (!_options.CameraFreePairing || _services.DeviceCredentials is null ||
                        _options.SingleSession)
                    {
                        await accepted.connection.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                    mediaTasks.RemoveAll(task => task.IsCompleted);
                    if (mediaTasks.Count >= 8)
                    {
                        await accepted.connection.DisposeAsync().ConfigureAwait(false);
                        _log.Warn("media-only connection capacity reached");
                        continue;
                    }
                    var connection = accepted.connection;
                    var hello = accepted.hello;
                    mediaTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await new DevicePeerSession(_options, _services, peers, _log)
                                .RunAsync(connection, hello, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            _log.Error("media-only session failed: " + ex);
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                    }));
                    continue;
                }

                if (accepted.hello.Role != "display")
                {
                    await accepted.connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                if (displayTask is { IsCompleted: false })
                {
                    // A second display must not reinitialize the physical
                    // display guard or remove the first phone's virtual monitor.
                    _log.Warn("display is occupied by another authenticated phone");
                    await accepted.connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var displayConnection = accepted.connection;
                var displayHello = accepted.hello;
                displayTask = Task.Run(async () =>
                {
                    try
                    {
                        await new PhoneSession(_options, _services, _encoderName, _log)
                            .RunAsync(displayConnection, displayHello, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        _log.Error("display session failed: " + ex);
                        await displayConnection.DisposeAsync().ConfigureAwait(false);
                    }
                });
                if (_options.SingleSession)
                {
                    await displayTask.ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            // Cancelled host shutdown closes media and display independently.
            // No media-only task ever invokes FocusIdd removal/scale restore.
            var tasks = mediaTasks.ToList();
            if (displayTask is not null) tasks.Add(displayTask);
            if (tasks.Count != 0) await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct).ConfigureAwait(false);
        await RunSessionsAsync(ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        _cameraFree?.Dispose();
        _listener?.Dispose();
        _offer?.Dispose();
    }
}

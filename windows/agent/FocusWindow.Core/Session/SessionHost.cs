using System.Net;
using System.Security.Cryptography;
using FocusWindow.Core.Media;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
/// <summary>
/// Concurrent authenticated accessory and display sessions. Backend admission
/// currently protects the single-monitor FocusIdd runtime, but the protocol and
/// peer model do not impose a one-device or one-display product limit.
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
    private DevicePeerRegistry? _devicePeers;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _preferredFrameRates = new(StringComparer.Ordinal);
    private readonly string _encoderName = "WGC / NVENC (qualified per source)";
    public SessionHost(HostOptions options, HostServices services, ILog log)
    {
        _options = options;
        _services = services;
        _log = log;
    }
    public PairingOffer? Offer => _offer;
    public int Port => _listener?.Port ?? 0;
    public string EncoderName => _encoderName;
    /// <summary>Binds the port and builds the enrollment offer; video is qualified per source.</summary>
    public Task<PairingOffer> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The phone's native dimensions are unknown here. Selecting a GPU based
        // on a tiny startup probe can produce a black screen at 1440x3120.
        // PhoneSession qualifies the native WGC/NVENC pipeline after density negotiation.
        _listener = new HostListener(IPAddress.Parse(_options.Bind), _options.Port, _log);
        var state = Pairing.LoadOrCreate(_services.PairingStore, _options.NewPairing);
        var address = Pairing.PickLanAddress() ?? "127.0.0.1";
        var offer = Pairing.BuildOffer(state, address, _listener.Port, _options.HostName);
        _offer = offer;
        // Host key is stable across ordinary restarts, unlike the host address.
        // DevicePeerRegistry provides *logical* identities only, not PnP devices.
        var scope = Convert.ToHexString(SHA256.HashData(
            offer.KeyPair.ExportSubjectPublicKeyInfo()));
        _devicePeers = new DevicePeerRegistry(scope);
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
        if (_listener is null || _offer is null || _devicePeers is null)
            throw new InvalidOperationException("call StartAsync first");
        var peers = _devicePeers;
        // Every display request receives an independent lease; only the current
        // host-global FocusIdd backend requires exclusive admission. This can
        // expand after the independent GPU/virtual-monitor POC has a per-display
        // resource interface, without changing how a device declares its roles.
        var displayAdmission = _services.DisplayAdmission ?? new ExclusiveVirtualDisplayAdmission();
        var displayTasks = new List<Task>();
        var accessoryTasks = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PhoneConnection? acceptedConnection = null;
                Protocol.ClientHello? acceptedHello = null;
                try
                {
                    (acceptedConnection, acceptedHello) = await _listener.AcceptAuthenticatedAsync(_offer,
                        (_options.CameraFreePairing || _services.CreateNativeAudio is not null) ? _services.DeviceCredentials : null, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One peer must never be able to stop the host from
                    // serving the next one. Say so plainly: a silent wedge
                    // here looks like "the PC is not answering".
                    _log.Error("accept loop: " + ex.GetType().Name + ": " + ex.Message);
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                if (acceptedConnection is null || acceptedHello is null) continue;
                var connection = acceptedConnection;
                var hello = acceptedHello;

                if (acceptedHello.Role is "media" or "accessory")
                {
                    // Accessory identities MUST be backed by per-device
                    // credentials. Legacy QR credentials don't bind DeviceId.
                    if (!_options.CameraFreePairing || _services.DeviceCredentials is null ||
                        _options.SingleSession)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                    accessoryTasks.RemoveAll(task => task.IsCompleted);
                    if (accessoryTasks.Count >= 8)
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        _log.Warn("accessory connection capacity reached");
                        continue;
                    }
                    accessoryTasks.Add(Task.Run(async () =>
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

                if (acceptedHello.Role != "display")
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                displayTasks.RemoveAll(task => task.IsCompleted);
                // Resource admission is a backend safety contract, not a user
                // or device role limit. It prevents current FocusIdd sessions
                // from racing over global display creation/removal.
                var displayLease = displayAdmission.TryAcquire(hello.Profile.DeviceId);
                if (displayLease is null)
                {
                    _log.Warn("virtual display backend is currently occupied");
                    await connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var displayConnection = connection;
                var displayHello = hello;
                var displayTask = Task.Run(async () =>
                {
                    using (displayLease)
                    {
                        try
                        {
                            await new PhoneSession(_options, _services, _encoderName, _log, _preferredFrameRates)
                                .RunAsync(displayConnection, displayHello, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            _log.Error("display session failed: " + ex);
                            await displayConnection.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                });
                displayTasks.Add(displayTask);
                if (_options.SingleSession)
                {
                    await displayTask.ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            // Host shutdown joins accessory and display workers independently.
            // Only a display worker may own and remove its virtual display.
            var tasks = accessoryTasks.Concat(displayTasks).ToArray();
            if (tasks.Length != 0) await Task.WhenAll(tasks).ConfigureAwait(false);
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

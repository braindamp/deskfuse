using System.Net;
using FocusWindow.Core.Media;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
/// <summary>
/// Owns the listener and the pairing, and runs one phone session at a time (the occupancy
/// model is "the PC in front of you", not a multi-tenant server).
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
        _log.Info($"listening on {_options.Bind}:{_listener.Port}; pairing address {address}");
        return Task.FromResult(offer);
    }
    public async Task RunSessionsAsync(CancellationToken ct)
    {
        if (_listener is null || _offer is null) throw new InvalidOperationException("call StartAsync first");
        while (!ct.IsCancellationRequested)
        {
            (PhoneConnection connection, Protocol.ClientHello hello) accepted;
            try
            {
                accepted = await _listener.AcceptAuthenticatedAsync(_offer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                var session = new PhoneSession(_options, _services, _encoderName, _log);
                await session.RunAsync(accepted.connection, accepted.hello, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad session must never take the host down: log it and go back to listening.
                _log.Error($"session failed: {ex}");
            }
            if (_options.SingleSession) break;
        }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct).ConfigureAwait(false);
        await RunSessionsAsync(ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        _listener?.Dispose();
        _offer?.Dispose();
    }
}

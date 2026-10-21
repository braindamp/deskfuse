using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FocusWindow.Core.Protocol;
using FocusWindow.Core.Transport;
namespace FocusWindow.Core.Session;
/// <summary>
/// Accepts TCP connections and returns the first one that AUTHENTICATES against the current
/// pairing offer.
///
/// The ECDH handshake itself is anonymous, so anyone who can reach the port can complete it.
/// What authenticates the peer is the pairing secret (the HKDF salt from the QR): a peer
/// without it derives different keys, its first packet fails AES-GCM, and we drop the socket
/// and keep listening. A port scan therefore costs one failed attempt, not the pairing.
/// </summary>
public sealed class HostListener : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpListener _listener;
    private readonly ILog _log;
    public int Port { get; }
    public HostListener(IPAddress bind, int port, ILog log)
    {
        _log = log;
        _listener = new TcpListener(bind, port);
        // A generous accept backlog. A phone that reconnects in a burst
        // must never be refused because the queue is one deep, and a slow
        // peer must not be able to starve the next one.
        _listener.Start(64);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }
    public async Task<(PhoneConnection Connection, ClientHello Hello)> AcceptAuthenticatedAsync(
        PairingOffer offer, CancellationToken ct)
    {
        return await AcceptAuthenticatedAsync(offer, null, ct).ConfigureAwait(false);
    }

    /// <summary>When credentials are supplied, QR possession alone never grants a session.</summary>
    public async Task<(PhoneConnection Connection, ClientHello Hello)> AcceptAuthenticatedAsync(
        PairingOffer offer, IDeviceCredentialStore? credentials, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException(ct);
            }
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(HandshakeTimeout);
                var channel = credentials is null
                    ? await SecureChannel.AcceptAsync(stream, offer.KeyPair, offer.Secret, budget.Token).ConfigureAwait(false)
                    : await SecureChannel.AcceptAnyAsync(stream, offer.KeyPair, credentials.All(), budget.Token).ConfigureAwait(false);
                var connection = new PhoneConnection(client, stream, channel, _log);
                ClientMessage first;
                try
                {
                    first = await connection.ReadFirstMessageAsync(budget.Token).ConfigureAwait(false);
                }
                catch
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                if (first is not ClientHello hello)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    _log.Warn($"{remote}: first message was not a hello; dropped");
                    continue;
                }
                if (credentials is not null &&
                    !string.Equals(channel.CredentialDeviceId, hello.Profile.DeviceId, StringComparison.Ordinal))
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    _log.Warn($"{remote}: encrypted credential does not match phone identity; dropped");
                    continue;
                }
                _log.Info($"{remote}: authenticated ({hello.Profile.DeviceLabel}, client {hello.ClientVersion})");
                return (connection, hello);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException
                                           or EndOfStreamException or FormatException or SocketException
                                           or OperationCanceledException && !ct.IsCancellationRequested)
            {
                _log.Warn($"{remote}: rejected ({ex.GetType().Name}: {ex.Message})");
                try { client.Close(); } catch { /* already gone */ }
            }
        }
    }
    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}

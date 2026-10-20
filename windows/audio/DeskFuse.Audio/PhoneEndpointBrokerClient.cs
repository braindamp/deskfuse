using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DeskFuse.Audio;

/// <summary>
/// Client for the local endpoint broker.
///
/// Creating a Windows software device is a privileged operation, so the
/// broker that does it runs separately from the unelevated LAN host. The
/// host only ever asks for "the endpoint of this enrolled phone" and gets
/// back the WASAPI endpoint id of a real render device.
///
/// The broker is not part of the product's trust boundary for the phone: it
/// can only create and remove DeskFuse Audio instances, only for identities
/// the host already authenticated, and only for the Windows user that owns
/// the session. If the broker is absent, the phone speaker capability stays
/// inactive and the host says so; it never falls back to another device.
/// </summary>
public sealed class PhoneEndpointBrokerClient : IAsyncDisposable
{
    private const int ConnectTimeoutMs = 3000;

    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PhoneEndpointBrokerClient(string pipeName = PhoneEndpointContract.PipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>True when a broker is listening. Never throws.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates (or reuses) the Windows render endpoint of one enrolled
    /// phone and returns its WASAPI endpoint id, or null with a reason when
    /// the endpoint could not be provided.
    /// </summary>
    public async Task<(string? EndpointId, string? Error)> ProvisionAsync(
        string peerId, string label, CancellationToken cancellationToken = default)
    {
        if (!PhoneEndpointContract.IsPeerId(peerId))
            return (null, "The phone speaker identity is not a valid enrollment hash.");
        var response = await SendAsync(new BrokerRequest(BrokerOps.Create, peerId, label), cancellationToken)
            .ConfigureAwait(false);
        if (response is null) return (null, "The DeskFuse audio endpoint broker is not running.");
        if (!response.Ok || string.IsNullOrEmpty(response.EndpointId))
            return (null, response.Error ?? "The phone speaker endpoint could not be created.");
        return (response.EndpointId, null);
    }

    /// <summary>Removes the Windows render endpoint of one enrolled phone.</summary>
    public async Task ReleaseAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (!PhoneEndpointContract.IsPeerId(peerId)) return;
        await SendAsync(new BrokerRequest(BrokerOps.Release, peerId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the endpoints the broker currently owns.</summary>
    public async Task<IReadOnlyList<BrokerResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new BrokerRequest(BrokerOps.List), cancellationToken).ConfigureAwait(false);
        if (response is null || !response.Ok) return Array.Empty<BrokerResponse>();
        return response.Endpoints ?? (IReadOnlyList<BrokerResponse>)Array.Empty<BrokerResponse>();
    }

    private async Task<BrokerResponse?> SendAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            var json = JsonSerializer.Serialize(request);
            var payload = Encoding.UTF8.GetBytes(json + "\n");
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (line is null) return null;
            return JsonSerializer.Deserialize<BrokerResponse>(line);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var line = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            if (buffer[0] == (byte)'\n')
                return Encoding.UTF8.GetString(line.ToArray());
            line.WriteByte(buffer[0]);
            if (line.Length > 64 * 1024) return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskFuse.WindowsSecurity;

namespace FocusWindow.Agent;

/// <summary>
/// Public bounded LAN prepare/locate bridge. The unprivileged agent relays only
/// the enrollment UUID to SYSTEM. It never receives source, keys or PCM.
/// Replies are hints; the native phone TLS/receipt chain still authenticates.
/// </summary>
internal sealed class TrustedSpeakerDiscovery : IDisposable
{
    private const string Probe = "DESKFUSE_SPEAKER_PREPARE_V1|";
    private const string Reply = "DESKFUSE_SPEAKER_READY_V1|";
    private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Any, 47814));
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim outstanding = new(2, 2);
    private readonly Dictionary<IPAddress, long> last = [];
    private readonly object rateGate = new();
    private readonly Task receiver;
    private readonly List<Task> requests = [];
    private readonly object requestGate = new();

    internal TrustedSpeakerDiscovery()
    {
        receiver = Task.Run(ReceiveAsync);
    }

    private async Task ReceiveAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(stop.Token).ConfigureAwait(false);
                if (packet.Buffer.Length != Probe.Length + 36 ||
                    !Encoding.ASCII.GetString(packet.Buffer).StartsWith(Probe, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(Encoding.ASCII.GetString(packet.Buffer.AsSpan(Probe.Length)), "D", out var enrollment) ||
                    enrollment == Guid.Empty) continue;
                var now = Environment.TickCount64;
                lock (rateGate)
                {
                    if (last.TryGetValue(packet.RemoteEndPoint.Address, out var previous) && now - previous < 200)
                        continue;
                    if (last.Count >= 256) last.Clear();
                    last[packet.RemoteEndPoint.Address] = now;
                }
                if (!outstanding.Wait(0)) continue;
                var job = AnswerAsync(enrollment, packet.RemoteEndPoint);
                lock (requestGate)
                {
                    requests.RemoveAll(t => t.IsCompleted);
                    requests.Add(job);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error)
            { Console.Error.WriteLine("Trusted speaker public discovery: " + error.Message); }
        }
    }

    private async Task AnswerAsync(Guid enrollment, IPEndPoint sender)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = await BrokerControlClient.ConnectSpeakerPreparationAsync(timeout.Token).ConfigureAwait(false);
            var request = new byte[16];
            enrollment.TryWriteBytes(request, bigEndian: true, out _);
            await client.Stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await client.Stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            var answer = new byte[2];
            await client.Stream.ReadExactlyAsync(answer, timeout.Token).ConfigureAwait(false);
            var port = BinaryPrimitives.ReadUInt16BigEndian(answer);
            if (port == 0) return;
            var reply = Encoding.ASCII.GetBytes(Reply + enrollment.ToString("D") + "|" + port);
            await udp.SendAsync(reply, sender, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            OperationCanceledException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            // No negative enumeration oracle and no fallback to legacy speaker.
            if (!stop.IsCancellationRequested)
                Console.Error.WriteLine("Trusted speaker preparation unavailable: " + error.Message);
        }
        finally { outstanding.Release(); }
    }

    public void Dispose()
    {
        stop.Cancel(); udp.Dispose();
        try { receiver.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Task[] active;
        lock (requestGate) active = requests.ToArray();
        try { Task.WhenAll(active).GetAwaiter().GetResult(); }
        catch (Exception) { /* requests are terminated by the cancellation and the protected service fences */ }
        outstanding.Dispose(); stop.Dispose();
    }
}

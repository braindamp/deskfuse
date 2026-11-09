using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DeskFuse.AudioBroker;

/// <summary>
/// One bounded service-owned PrepareSpeaker intent. A caller nominates only the
/// committed enrollment UUID. SYSTEM derives actual logon, source, bind, budget
/// and owner; the response is an unauthenticated public port hint, not a grant.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerSpeakerPreparation
{
    private sealed record Prepared(BrokerProtectedAudioAcquisition.PreparedSession Session, DateTimeOffset UnprovedUntil);
    private readonly BrokerInstalledDeployment deployment;
    private readonly BrokerHostIdentity host;
    private readonly TrustedBrokerEnrollmentStore store;
    private readonly BrokerAuthorityState registry;
    private readonly BrokerEnrollmentRundown rundown;
    private readonly byte[] serviceBoot;
    private readonly Dictionary<Guid, Prepared> prepared = [];
    private readonly List<Task> reapers = [];
    private readonly object gate = new();

    internal BrokerSpeakerPreparation(BrokerInstalledDeployment deployment, BrokerHostIdentity host,
        TrustedBrokerEnrollmentStore store, BrokerAuthorityState registry, BrokerEnrollmentRundown rundown,
        byte[] serviceBoot)
    {
        this.deployment = deployment; this.host = host; this.store = store;
        this.registry = registry; this.rundown = rundown; this.serviceBoot = serviceBoot.ToArray();
    }

    internal async Task RunAsync(CancellationToken stop, TaskCompletionSource ready)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = BrokerLocalPipe.CreateSpeakerPreparation();
            ready.TrySetResult();
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var caller = pipe.ObserveConnectedCaller();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var request = new byte[16];
                var offset = 0;
                while (offset != request.Length)
                {
                    var read = await pipe.ReadAsync(request.AsMemory(offset), requestTimeout.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Incomplete speaker enrollment selector.");
                    offset += read;
                }
                var enrollment = new Guid(request, bigEndian: true);
                if (enrollment == Guid.Empty) throw new InvalidDataException("Empty speaker enrollment.");
                var logon = caller.ObserveSpeakerLogon();
                var listener = await PrepareAsync(enrollment, logon, requestTimeout.Token).ConfigureAwait(false);
                // Recheck retained client after potentially expensive protected work.
                _ = caller.ObserveSpeakerLogon();
                var reply = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(reply, checked((ushort)listener.Port));
                await pipe.WriteAsync(reply, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                if (stop.IsCancellationRequested) break;
                Console.Error.WriteLine("Denied bounded Speaker preparation: " + error.Message);
                // Only a public positive listener hint is returned. A failed
                // request never exposes enrollments, storage or signed grants.
                try { await pipe.WriteAsync(new byte[2], stop).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }

    private async Task<IPEndPoint> PrepareAsync(Guid enrollment, BrokerLogonIdentity logon, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (prepared.TryGetValue(enrollment, out var prior))
            {
                var snapshot = prior.Session.Owner.Snapshot();
                if (!prior.Session.Owner.RundownComplete)
                {
                    if (snapshot.Phase == AudioAuthorityPhase.Idle && DateTimeOffset.UtcNow >= prior.UnprovedUntil)
                        throw new UnauthorizedAccessException("Unproved speaker source is retiring.");
                    if (snapshot.Phase == AudioAuthorityPhase.Closed)
                        throw new UnauthorizedAccessException("Prior source is closed pending rundown.");
                    if (!prior.Session.Owner.Source.Identity.Subject.AsSpan().SequenceEqual(logon.UserSid))
                        throw new UnauthorizedAccessException("Speaker enrollment belongs to another logon.");
                    return prior.Session.Listener; // Duplicate never refreshes idle deadline.
                }
                prepared.Remove(enrollment);
            }
            if (prepared.Count >= 16) throw new InvalidOperationException("Bounded speaker preparation limit reached.");
        }
        var bind = new IPEndPoint(SelectLanAddress(), 0);
        // Conservative existing Game source. No H4 or automatic dual delivery.
        var preparedSession = await BrokerProtectedAudioAcquisition.OpenAsync(
            deployment, host, store, registry, rundown, logon, enrollment, serviceBoot,
            new AudioLeasePolicy(1_800_000, 30_000), new AudioWireContext(1, 1, 1), false,
            bind, new AudioCandidatePolicy(1500, 10_000, 8, 128, 65536, 32, 8, 8, 500),
            Clock100ns, cancellation, handover: false).ConfigureAwait(false);
        var owned = new Prepared(preparedSession, DateTimeOffset.UtcNow.AddSeconds(30));
        lock (gate)
        {
            if (prepared.ContainsKey(enrollment))
            {
                // An unexpected overlap never publishes two listeners as one.
                rundown.RequestSourceStop(preparedSession.Owner);
                throw new InvalidOperationException("Concurrent speaker ownership changed.");
            }
            prepared.Add(enrollment, owned);
            reapers.RemoveAll(t => t.IsCompleted);
            reapers.Add(ReapUnprovedAsync(enrollment, owned));
        }
        return preparedSession.Listener;
    }

    private async Task ReapUnprovedAsync(Guid enrollment, Prepared owned)
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        try
        {
            lock (gate)
                if (!prepared.TryGetValue(enrollment, out var current) || !ReferenceEquals(current, owned)) return;
            if (owned.Session.Owner.Snapshot().Phase == AudioAuthorityPhase.Idle)
                await rundown.StopSource(owned.Session.Owner).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Registry remains occupied/quarantined until actual native retirement.
            Console.Error.WriteLine("Unproved Speaker retirement unconfirmed: " + error.Message);
        }
    }

    private static IPAddress SelectLanAddress()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
        {
            var properties = adapter.GetIPProperties();
            if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            var address = properties.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any));
            if (address is not null) return address;
        }
        throw new InvalidOperationException("No supported active LAN interface for trusted Speaker.");
    }

    private static ulong Clock100ns()
    {
        QueryInterruptTimePrecise(out var value);
        return value;
    }

    [DllImport("kernel32.dll")]
    private static extern void QueryInterruptTimePrecise(out ulong interruptTime);
}

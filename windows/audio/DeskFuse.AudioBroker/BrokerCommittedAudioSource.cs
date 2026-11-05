using System.Runtime.Versioning;
using DeskFuse.Audio;

namespace DeskFuse.AudioBroker;

/// <summary>Retains the exact protected worker, endpoint and kernel export through rundown.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerCommittedAudioSource : IBrokerEnrollmentResourceOwner
{
    private static readonly TimeSpan EndpointLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PeriodLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MappingLimit = TimeSpan.FromSeconds(3);
    private readonly object gate = new();
    private readonly BrokerAudioOwner authority;
    private readonly BrokerAudioWorkerSession session;
    private readonly BrokerEnrollmentRundown rundown;
    private readonly CancellationTokenSource closing = new();
    private Task? preparation;
    private BrokerAudioOwner.Committed? preparationCommit;
    private Task? cancel;
    private PhoneDevice? device;
    private PcmGrant? pcm;
    private bool fenced;

    private BrokerCommittedAudioSource(BrokerAudioOwner authority, BrokerAudioWorkerSession session, BrokerEnrollmentRundown rundown)
    { this.authority = authority; this.session = session; this.rundown = rundown; }

    internal static BrokerCommittedAudioSource Bind(BrokerAudioOwner authority, BrokerAudioWorkerSession session,
        BrokerEnrollmentRundown rundown, BrokerHostIdentity host, Action? onTracked = null)
    {
        if (authority.Source.Intent.Backend != AudioSourceBackend.Committed || !authority.IsWorker(session.Worker))
            throw new UnauthorizedAccessException("A registered committed-source worker is required.");
        var resource = new BrokerCommittedAudioSource(authority, session, rundown);
        var dispatcher = new BrokerAudioControlDispatcher(authority, session.Worker, host,
            host.SignAudioRequestStatus, host.SignAudioSourceStopStatus, resource.PrepareAsync,
            resource.RequestRundown, () => session.PublishAuthorityAsync(authority, CancellationToken.None),
            session.SendReplyAsync, _ => resource.RequestRundown(), acquisitionOnly: true);
        rundown.RegisterAudio(authority, resource);
        onTracked?.Invoke();
        try { session.BindEvents(authority, dispatcher, resource.RequestRundown); }
        catch { resource.RequestRundown(); throw; }
        return resource;
    }

    public BrokerRegisteredWorker Worker => session.Worker;
    IBrokerRundownWorker IBrokerEnrollmentResourceOwner.Worker => Worker;

    private Task PrepareAsync(BrokerAudioOwner.Committed acquisition)
    {
        lock (gate)
        {
            if (fenced) throw new UnauthorizedAccessException("Source preparation is unavailable.");
            if (preparation is not null)
                return ReferenceEquals(preparationCommit, acquisition) ? preparation
                    : throw new UnauthorizedAccessException("A different source preparation was already claimed.");
            authority.ClaimPreparation(acquisition);
            preparationCommit = acquisition;
            return preparation = Task.Run(() => PrepareCore(acquisition));
        }
    }

    private void RequireLive(BrokerAudioOwner.Committed acquisition)
    {
        lock (gate) if (fenced) throw new UnauthorizedAccessException("Source preparation retired.");
        authority.RequirePreparation(acquisition);
    }

    private async Task PrepareCore(BrokerAudioOwner.Committed acquisition)
    {
        try
        {
            RequireLive(acquisition);
            var grant = authority.ControlSnapshot();
            var context = AudioSourceAttachment.Context(grant);
            if (!await session.PublishAuthorityAsync(authority, closing.Token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Preparing projection was superseded.");

            RequireLive(acquisition);
            var key = authority.Source.Intent.DeviceKey;
            var peer = key.ToString("N");
            try
            {
                device = DeviceInstance.Create(PhoneEndpointContract.InstancePath(peer), PhoneEndpointContract.HardwareId,
                    PhoneEndpointContract.DeviceFriendlyName("Phone"), "DeskFuse " + peer, allowExisting: false);
            }
            catch (DeviceCreationIncompleteException partial) { device = partial.Device; throw; }

            RequireLive(acquisition);
            using var resolve = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            var budget = authority.PreparationBudget(acquisition, EndpointLimit);
            resolve.CancelAfter(budget);
            var endpoint = await EndpointResolver.ResolveAsync(device.InstancePath, budget, resolve.Token, requireUnique: true)
                .ConfigureAwait(false) ?? throw new IOException("The owned speaker endpoint is unavailable.");
            var opened = await session.ExchangeSourceAsync(authority, acquisition, AudioWorkerFrameKind.OpenSource,
                context, AudioSourceAttachment.Endpoint(endpoint), authority.PreparationBudget(acquisition, PeriodLimit)).ConfigureAwait(false);
            var observation = AudioSourceAttachment.Opened(opened, key, endpoint);

            RequireLive(acquisition);
            pcm = new PcmGrant();
            RequireLive(acquisition);
            pcm.Attach(endpoint, observation.Source, Worker.ProcessHandle);
            var offered = AudioSourceAttachment.Handles(pcm.FileHandle, pcm.SectionHandle, pcm.EventHandle, pcm.Generation);
            observation = observation with { PcmGeneration = pcm.Generation };
            var expected = AudioAuthorityProtocol.Binding(authority.Source, observation);
            var mapped = await session.ExchangeSourceAsync(authority, acquisition, AudioWorkerFrameKind.AttachPcm,
                context, offered, authority.PreparationBudget(acquisition, MappingLimit)).ConfigureAwait(false);
            if (!mapped.AsSpan().SequenceEqual(expected))
                throw new UnauthorizedAccessException("Native mapped binding differs from the broker-owned PCM grant.");
            RequireLive(acquisition);
            if (!string.Equals(EndpointResolver.FindActiveEndpoint(device.InstancePath, requireUnique: true), endpoint, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The exact owned endpoint changed during PCM attachment.");
            PcmGrant.RequireEndpoint(endpoint, observation.Source);
            RequireLive(acquisition);
            if (!authority.CompletePreparation(acquisition, observation))
                throw new UnauthorizedAccessException("Source preparation no longer owns publication.");
        }
        catch { RequestRundown(); throw; }
    }

    private void RequestRundown()
    {
        CloseAdmission();
        rundown.RequestSourceStop(authority);
    }

    public void CloseAdmission()
    {
        authority.CloseAdmission();
        lock (gate)
        {
            fenced = true;
            cancel ??= closing.CancelAsync();
        }
    }

    public void RevokeAndJoin()
    {
        CloseAdmission();
        // Session Stop faults pending setup I/O before joining dispatcher jobs.
        // Keep the broker export and devnode until the exact consumer has exited.
        session.StopAndJoinAsync().GetAwaiter().GetResult();
        cancel!.GetAwaiter().GetResult();
        try { preparation?.GetAwaiter().GetResult(); }
        catch { /* Actual preparation has finished. Its partial resources remain here. */ }
        pcm?.Dispose();
        pcm = null;
        if (device is not null)
        {
            DeviceInstance.RemoveAsync(device.InstancePath).GetAwaiter().GetResult();
            device = null;
        }
    }
}

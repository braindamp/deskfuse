using System.Runtime.Versioning;

namespace DeskFuse.AudioBroker;

internal interface IBrokerEnrollmentRundown
{
    void CloseAdmission(Guid enrollment);
    void RevokeAndJoin(Guid enrollment);
    void CloseAllAdmission();
    void RevokeAndJoinAll();
}

// Implemented by the actual native owner when acquiring its source/grant.
// Normal return means confirmed source/kernel rundown, never just queued Stop.
[SupportedOSPlatform("windows")]
internal interface IBrokerEnrollmentResourceOwner
{
    BrokerRegisteredWorker Worker { get; }
    void CloseAdmission();
    void RevokeAndJoin(); // Must retain worker handles; this registry performs final job join.
}

/// <summary>Retains exact native owners across failed enrollment retirement.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerEnrollmentRundown(BrokerAuthorityState authority) : IBrokerEnrollmentRundown
{
    private sealed class Retained(Guid enrollment, IBrokerEnrollmentResourceOwner owner)
    {
        internal readonly Guid Enrollment = enrollment;
        internal readonly IBrokerEnrollmentResourceOwner Owner = owner;
        internal readonly object RetirementGate = new();
        internal bool Retired;
        internal Task? Stop;
    }
    internal sealed class Completed
    {
        private readonly BrokerRegisteredWorker worker;
        private Completed(BrokerRegisteredWorker worker) { this.worker = worker; }
        internal bool Matches(IBrokerRegisteredWorker expected) => ReferenceEquals(worker, expected);
        internal BrokerRegisteredWorker Worker => worker;
        internal static Completed FromRetiredOwner(BrokerEnrollmentRundown owner, BrokerRegisteredWorker worker)
        {
            lock (owner.gate)
            {
                if (!owner.owners.TryGetValue(worker.Registration, out var retained) || !ReferenceEquals(retained.Owner.Worker, worker) || !retained.Retired)
                    throw new UnauthorizedAccessException("Native rundown completion is not owned.");
                return new(worker);
            }
        }
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Retained> owners = [];
    private readonly HashSet<Guid> closed = [];
    private bool closing;

    internal void Track(Guid enrollment, IBrokerEnrollmentResourceOwner owner)
    {
        if (!authority.OwnsRegistration(enrollment, owner.Worker)) throw new UnauthorizedAccessException("Native owner has no matching trusted registration.");
        lock (gate)
        {
            if (closing || closed.Contains(enrollment) || owners.Count >= 128 || owners.ContainsKey(owner.Worker.Registration))
                throw new UnauthorizedAccessException("Native owner registration is closed or already retained.");
            owners.Add(owner.Worker.Registration, new(enrollment, owner));
        }
    }
    public void CloseAdmission(Guid enrollment)
    {
        authority.CloseEnrollmentAdmission(enrollment);
        IBrokerEnrollmentResourceOwner[] snapshot;
        lock (gate)
        {
            closed.Add(enrollment);
            snapshot = owners.Values.Where(v => v.Enrollment == enrollment).Select(v => v.Owner).ToArray();
        }
        foreach (var owner in snapshot) owner.CloseAdmission(); // Cheap local fences, no joins under the registry gate.
    }
    public void RevokeAndJoin(Guid enrollment)
    {
        CloseAdmission(enrollment);
        var workers = authority.CloseEnrollmentAdmission(enrollment);
        Retained[] snapshot;
        lock (gate)
        {
            if (workers.Any(w => !owners.TryGetValue(w.Registration, out var retained) ||
                    retained.Enrollment != enrollment || !ReferenceEquals(retained.Owner.Worker, w)))
                throw new InvalidOperationException("Native owner custody is incomplete; replacement remains quarantined.");
            snapshot = owners.Values.Where(v => v.Enrollment == enrollment).ToArray();
        }
        foreach (var owner in snapshot) Retire(owner);
    }
    // Negative source cleanup is retained independently of the control job that
    // requested it. Its own resource owner joins control jobs; it must never be
    // awaited from one of those jobs (which would join itself).
    internal void RequestSourceStop(BrokerAudioOwner audio) => _ = ObserveSourceStop(StopSource(audio));
    private static async Task ObserveSourceStop(Task actual)
    {
        try { await actual.ConfigureAwait(false); }
        catch { /* Retire latched Quarantined; exact resource custody/task remains retained for retry. */ }
    }
    internal Task StopSource(BrokerAudioOwner audio)
    {
        audio.CloseAdmission(); Retained? retained;
        lock (gate)
        {
            if (owners.TryGetValue(audio.Source.Identity.Worker, out retained) && audio.IsWorker(retained.Owner.Worker))
            {
                if (retained.Stop is { IsFaulted: false }) return retained.Stop;
                return retained.Stop = Task.Run(() => Retire(retained));
            }
        }
        audio.QuarantineRundown();
        throw new InvalidOperationException("Source native resource custody is incomplete.");
    }
    private void Retire(Retained retained)
    {
        lock (retained.RetirementGate)
        {
            try
            {
                if (!retained.Retired)
                {
                    retained.Owner.CloseAdmission();
                    authority.CloseRegistration(retained.Owner.Worker.Registration);
                    retained.Owner.RevokeAndJoin(); // Actual source/kernel/endpoint/control-job rundown.
                    retained.Owner.Worker.StopAndJoin(); // Actual exact job/process completion.
                    lock (gate) retained.Retired = true;
                }
                authority.ConfirmAudioRundown(Completed.FromRetiredOwner(this, retained.Owner.Worker));
            }
            catch { authority.QuarantineAudioRundown(retained.Owner.Worker); throw; }
        }
    }
    public void CloseAllAdmission()
    {
        lock (gate) closing = true;
        foreach (var id in authority.EnrollmentsWithWorkers()) CloseAdmission(id);
    }
    public void RevokeAndJoinAll()
    {
        CloseAllAdmission();
        foreach (var id in authority.EnrollmentsWithWorkers()) RevokeAndJoin(id);
        lock (gate)
        {
            foreach (var owner in owners.Values) owner.Owner.Worker.Dispose();
            owners.Clear();
        }
    }
}

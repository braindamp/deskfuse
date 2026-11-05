using System.Runtime.Versioning;

namespace DeskFuse.AudioBroker;

internal interface IBrokerEnrollmentRundown
{
    void CloseAdmission(Guid enrollment);
    void RevokeAndJoin(Guid enrollment);
    void CloseAllAdmission();
    void RevokeAndJoinAll();
}

[SupportedOSPlatform("windows")]
internal interface IBrokerRundownWorker : IBrokerAudioWorker, IDisposable
{
    void StopAndJoin();
}
// Normal return from resource rundown means confirmed source/kernel cleanup.
[SupportedOSPlatform("windows")]
internal interface IBrokerEnrollmentResourceOwner
{
    IBrokerRundownWorker Worker { get; }
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
        internal bool Released;
        internal Task? Stop;
    }
    internal sealed class Completed
    {
        private readonly IBrokerRundownWorker worker;
        private Completed(IBrokerRundownWorker worker) { this.worker = worker; }
        internal bool Matches(IBrokerRegisteredWorker expected) => ReferenceEquals(worker, expected);
        internal IBrokerRundownWorker Worker => worker;
        internal static Completed FromRetiredOwner(BrokerEnrollmentRundown owner, IBrokerRundownWorker worker)
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

    internal void RegisterAudio(BrokerAudioOwner audio, IBrokerEnrollmentResourceOwner owner)
    {
        var worker = owner.Worker;
        var enrollment = audio.Source.Identity.Enrollment;
        if (!audio.IsWorker(worker)) throw new UnauthorizedAccessException("Native owner has no matching worker.");
        lock (gate)
        {
            if (closing || closed.Contains(enrollment) || owners.Count >= 128 || owners.ContainsKey(worker.Registration))
                throw new UnauthorizedAccessException("Native owner registration is closed or already retained.");
            // Custody is present before the authority registry exposes a speaker.
            owners.Add(worker.Registration, new(enrollment, owner));
            try { authority.RegisterAudioOwner(worker, enrollment, audio); }
            catch { owners.Remove(worker.Registration); throw; }
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
        Retained[] snapshot;
        lock (gate)
        {
            // Sample registration and custody in the same turn as owner removal.
            var workers = authority.CloseEnrollmentAdmission(enrollment);
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
        if (audio.RundownComplete) return Task.CompletedTask;
        audio.QuarantineRundown();
        throw new InvalidOperationException("Source native resource custody is incomplete.");
    }
    private void Retire(Retained retained)
    {
        lock (retained.RetirementGate)
        {
            if (retained.Released) return;
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
                var worker = retained.Owner.Worker;
                var completed = Completed.FromRetiredOwner(this, worker);
                worker.Dispose();
                authority.ConfirmAudioRundown(completed);
                lock (gate)
                {
                    owners.Remove(worker.Registration);
                    retained.Released = true;
                }
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
        Retained[] remaining;
        lock (gate) remaining = owners.Values.ToArray();
        foreach (var owner in remaining) Retire(owner);
    }
}

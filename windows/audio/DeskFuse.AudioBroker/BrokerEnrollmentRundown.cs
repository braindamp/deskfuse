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
    private readonly object gate = new();
    private readonly Dictionary<Guid, (Guid Enrollment, IBrokerEnrollmentResourceOwner Owner)> owners = [];
    private readonly HashSet<Guid> closed = [], retired = [];
    private bool closing;

    internal void Track(Guid enrollment, IBrokerEnrollmentResourceOwner owner)
    {
        if (!authority.OwnsRegistration(enrollment, owner.Worker)) throw new UnauthorizedAccessException("Native owner has no matching trusted registration.");
        lock (gate)
        {
            if (closing || closed.Contains(enrollment) || owners.Count >= 128 || owners.ContainsKey(owner.Worker.Registration))
                throw new UnauthorizedAccessException("Native owner registration is closed or already retained.");
            owners.Add(owner.Worker.Registration, (enrollment, owner));
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
        IBrokerEnrollmentResourceOwner[] snapshot;
        lock (gate)
        {
            if (workers.Any(w => !owners.TryGetValue(w.Registration, out var retained) ||
                    retained.Enrollment != enrollment || !ReferenceEquals(retained.Owner.Worker, w)))
                throw new InvalidOperationException("Native owner custody is incomplete; replacement remains quarantined.");
            snapshot = owners.Values.Where(v => v.Enrollment == enrollment && !retired.Contains(v.Owner.Worker.Registration))
                .Select(v => v.Owner).ToArray();
        }
        foreach (var owner in snapshot)
        {
            owner.RevokeAndJoin(); // Throwing keeps the exact owner retained and replacement blocked.
            owner.Worker.StopAndJoin();
            lock (gate) retired.Add(owner.Worker.Registration);
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
            owners.Clear(); retired.Clear();
        }
    }
}

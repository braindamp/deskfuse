using System.Buffers.Binary;

namespace DeskFuse.AudioBroker;

/// <summary>
/// One registered worker's bounded control jobs. The reader only routes/captures;
/// actual signing, verification, native setup and writes run outside its gates.
/// The resource owner supplies actual preparation/publication and joins us before
/// retiring caller/process custody. This class never manufactures rundown facts.
/// </summary>
internal sealed class BrokerAudioControlDispatcher : IAudioWorkerEventOwner
{
    private sealed class Route(BrokerAudioOwner.Connection origin)
    { internal readonly BrokerAudioOwner.Connection Origin = origin; internal ulong LastRequest; }
    private sealed class Job(AudioNetworkMessage message, Route route, bool negative)
    {
        internal readonly AudioNetworkMessage Message = message;
        internal readonly Route Route = route;
        internal readonly bool Negative = negative;
        internal BrokerAudioOwner.StopVerification? Stop;
        internal Exception? Rejected;
        internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly BrokerAudioOwner owner;
    private readonly IBrokerAudioWorker worker;
    private readonly IAudioGrantSigner grants;
    private readonly Func<BrokerAudioOwner.RequestStatus, byte[]> signStatus;
    private readonly Func<BrokerAudioOwner.StopStatus, byte[]> signStopStatus;
    private readonly Func<BrokerAudioOwner.Committed, Task> prepareSource;
    private readonly Func<Task<bool>> publish;
    private readonly Func<byte[], bool, Task> send;
    private readonly Action<Exception> fail;
    private readonly Dictionary<Guid, Route> routes = [];
    private readonly HashSet<Job> jobs = [];
    private readonly SemaphoreSlim positive = new(1, 1);
    private AudioDeliveryDescriptor? nomination;
    private bool stopping;
    private Task? join;
    private Task? lifecycle;
    private bool lifecycleDirty;
    internal BrokerAudioControlDispatcher(BrokerAudioOwner owner, IBrokerAudioWorker worker, IAudioGrantSigner grants,
        Func<BrokerAudioOwner.RequestStatus, byte[]> signStatus, Func<BrokerAudioOwner.StopStatus, byte[]> signStopStatus,
        Func<BrokerAudioOwner.Committed, Task> prepareSource, Func<Task<bool>> publish,
        Func<byte[], bool, Task> send, Action<Exception> fail)
    {
        if (!owner.IsWorker(worker)) throw new UnauthorizedAccessException("Foreign control worker.");
        this.owner = owner; this.worker = worker; this.grants = grants; this.signStatus = signStatus; this.signStopStatus = signStopStatus;
        this.prepareSource = prepareSource; this.publish = publish; this.send = send; this.fail = fail;
    }
    public bool TryPost(AudioWorkerFrame frame)
    {
        Job job;
        lock (gate)
        {
            if (stopping) return false;
            if (frame.Kind == AudioWorkerFrameKind.ConnectionObserved)
            {
                var o = AudioWorkerNetworkProtocol.Observation(frame.Body);
                if (routes.ContainsKey(o.Connection)) return false;
                routes.Add(o.Connection, new(owner.ObserveConnection(worker, o.Connection, o.Native, o.Phone, o.Nonce))); return true;
            }
            if (frame.Kind == AudioWorkerFrameKind.ConnectionClosed)
            {
                if (!routes.Remove(AudioWorkerNetworkProtocol.Closed(frame.Body), out var route)) return false;
                owner.ConnectionClosed(route.Origin);
                lifecycleDirty = true;
                if (lifecycle is null || lifecycle.IsCompleted) lifecycle = Task.Run(PublishLifecycle);
                return true;
            }
            if (frame.Kind != AudioWorkerFrameKind.NetworkRequest) return false;
            var message = AudioWorkerNetworkProtocol.Parse(frame.Body);
            if (!routes.TryGetValue(message.Connection, out var origin) || origin.LastRequest == ulong.MaxValue || message.Request != origin.LastRequest + 1)
                return false;
            var negative = AudioWorkerNetworkProtocol.Negative(message.Kind);
            // Native forwarding credits use the same worker-wide budget. Public
            // candidate overload is dropped/rejected there, never overfills this pipe.
            if (jobs.Count(p => p.Negative == negative) >= (negative ? 1 : 8)) return false;
            job = new(message, origin, negative); origin.LastRequest = message.Request; jobs.Add(job);
            try
            {
                if (message.Kind == (byte)AudioNetworkKind.SignedSourceStop)
                    job.Stop = owner.CaptureSignedStop(origin.Origin, message.Body);
                else if (message.Kind == (byte)AudioNetworkKind.Stop)
                {
                    var b = message.Body;
                    owner.StopCurrentDelivery(origin.Origin, new(b.AsSpan(0, 16), bigEndian: true), U64(b, 16), b[24..56], b[56..88]);
                }
            }
            catch (Exception error) when (DomainError(error)) { job.Rejected = error; }
        }
        _ = Task.Run(() => Run(job)); return true;
    }
    private async Task Run(Job job)
    {
        try
        {
            byte[] body; byte kind;
            try
            {
                if (job.Rejected is not null) throw job.Rejected;
                body = await Execute(job).ConfigureAwait(false); kind = checked((byte)(job.Message.Kind + 128));
            }
            catch (Exception error) when (DomainError(error))
            {
                kind = 255; body = [(byte)(error is InvalidOperationException ? 2 : error is InvalidDataException ? 3 : error is IOException ? 4 : 1)];
            }
            // Even a closed TLS route receives its pipe completion. The native
            // owner then releases the retained forwarding credit and drops the
            // undeliverable network reply; closure cannot abandon actual work.
            await send(AudioWorkerNetworkProtocol.Encode(new(job.Message.Request, job.Message.Connection, kind, body), reply: true), job.Negative).ConfigureAwait(false);
        }
        catch (Exception error) { owner.CloseAdmission(); fail(error); }
        finally { lock (gate) jobs.Remove(job); job.Done.TrySetResult(true); }
    }
    private async Task<byte[]> Execute(Job job)
    {
        var m = job.Message; var origin = job.Route.Origin; var b = m.Body;
        if (m.Kind == (byte)AudioNetworkKind.SignedSourceStop)
        {
            if (!job.Stop!.Complete()) throw new UnauthorizedAccessException("Invalid source Stop signature.");
            await Publish().ConfigureAwait(false); return [(byte)AudioSourceStopState.Denied];
        }
        if (m.Kind == (byte)AudioNetworkKind.Stop)
        { await Publish().ConfigureAwait(false); return [(byte)AudioSourceStopState.Denied]; }
        if (m.Kind == (byte)AudioNetworkKind.Info) return b.Length == 0 ? owner.Source.Bytes : owner.BindingDescriptor();
        if (m.Kind == (byte)AudioNetworkKind.Status) return owner.SignedRequestStatus(origin, b, signStatus);
        if (m.Kind == (byte)AudioNetworkKind.SourceStopStatus) return owner.QueryStopStatus(b, signStopStatus);
        // The bounded positive queue never owns the independent negative lane.
        await positive.WaitAsync().ConfigureAwait(false);
        try
        {
            switch ((AudioNetworkKind)m.Kind)
            {
                case AudioNetworkKind.Prepare:
                    Route target; lock (gate) target = routes.TryGetValue(new(b.AsSpan(40), bigEndian: true), out var route) ? route : throw new UnauthorizedAccessException("Unobserved target.");
                    var prepared = owner.NominateDelivery(origin, target.Origin, U64(b, 0), b[8..40]);
                    lock (gate) nomination = prepared; return prepared.Bytes;
                case AudioNetworkKind.Issue:
                    var operation = (AudioOperationKind)b[0]; AudioDeliveryDescriptor? candidate = null;
                    if (operation is AudioOperationKind.AttachDelivery or AudioOperationKind.SupersedeDelivery)
                    {
                        lock (gate) candidate = nomination;
                        if (candidate is null || candidate.Connection?.Incarnation != new Guid(b.AsSpan(37), bigEndian: true) || !candidate.Intent.PhoneRequestNonce.AsSpan().SequenceEqual(b.AsSpan(1, 32)))
                            throw new UnauthorizedAccessException("No exact nominated delivery.");
                    }
                    var challenge = owner.Issue(origin, operation, b[1..33], checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(33))), candidate);
                    return EnrollmentFraming.Signed(challenge.Transcript, challenge.HostSignature);
                case AudioNetworkKind.Proof:
                    var proof = AudioWorkerNetworkProtocol.Proof(b);
                    var committed = owner.Admit(origin, proof.Nonce, proof.Signature) ?? throw new UnauthorizedAccessException("Audio proof rejected.");
                    if (committed.Kind == AudioOperationKind.AcquireSource && owner.Snapshot().Phase == AudioAuthorityPhase.Preparing)
                    {
                        try { await prepareSource(committed).ConfigureAwait(false); }
                        catch (Exception error) { owner.CloseAdmission(); fail(error); throw; }
                    }
                    await Publish().ConfigureAwait(false); return committed.SignedReceipt(grants);
                case AudioNetworkKind.AppliedResult:
                    var applied = AudioWorkerNetworkProtocol.Proof(b.AsSpan(32));
                    var retained = owner.GetAppliedResult(origin, b[..32], applied.Nonce, applied.Signature);
                    return retained?.SignedReceipt(grants) ?? throw new UnauthorizedAccessException("No retained applied result.");
                case AudioNetworkKind.Output:
                    owner.ObserveOutput(origin, b[..32], U64(b, 32), U64(b, 40), b[48] == 1);
                    await Publish().ConfigureAwait(false); return [];
                case AudioNetworkKind.Detach:
                    owner.ObserveDetach(origin, b[..32], U64(b, 32));
                    await Publish().ConfigureAwait(false); return [];
                default: throw new InvalidDataException("Unsupported native request.");
            }
        }
        finally { positive.Release(); }
    }
    private async Task Publish()
    {
        try { _ = await publish().ConfigureAwait(false); }
        catch (Exception error) { owner.CloseAdmission(); fail(error); throw; }
    }
    private async Task PublishLifecycle()
    {
        try
        {
            while (true)
            {
                lock (gate) lifecycleDirty = false;
                await Publish().ConfigureAwait(false);
                lock (gate) { if (!lifecycleDirty) { lifecycle = null; return; } }
            }
        }
        catch (Exception error) { owner.CloseAdmission(); fail(error); }
    }
    public Task StopAndJoinAsync()
    {
        lock (gate)
        {
            if (join is not null) return join;
            stopping = true; owner.CloseAdmission();
            var actual = jobs.Select(p => (Task)p.Done.Task).ToList();
            if (lifecycle is not null) actual.Add(lifecycle);
            return join = Task.WhenAll(actual);
        }
    }
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at));
    private static bool DomainError(Exception error) => error is UnauthorizedAccessException or InvalidOperationException or InvalidDataException or IOException;
}

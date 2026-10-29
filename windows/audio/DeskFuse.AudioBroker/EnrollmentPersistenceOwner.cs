using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace DeskFuse.AudioBroker;

internal interface IEnrollmentCommitSigner
{
    byte[] SignCommittedReceipt(TrustedBrokerEnrollmentStore.Committed committed);
    byte[] SignStatus(EnrollmentStatus status);
}

/// <summary>
/// Bounded setup-only serialization. Caller cancellation never cancels or detaches
/// an admitted native write. The returned task is the actual terminal operation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class EnrollmentPersistenceOwner : IAsyncDisposable
{
    internal sealed record Reply(byte[] Bytes, TrustedBrokerEnrollmentStore.StatusSnapshot Snapshot);
    private interface IWork { void Execute(); }
    private sealed class Work<T>(Func<T> action) : IWork
    {
        internal readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute() { try { Result.SetResult(action()); } catch (Exception error) { Result.SetException(error); } }
    }
    private readonly object gate = new();
    private readonly Channel<IWork> queue = Channel.CreateBounded<IWork>(new BoundedChannelOptions(8)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<EnrollmentCeremony, Task<Reply>> commits = [];
    private readonly Dictionary<Guid, Task<EnrollmentOutcome>> revokes = [];
    private readonly TrustedBrokerEnrollmentStore store;
    private readonly IEnrollmentCommitSigner signer;
    private readonly IBrokerEnrollmentRundown rundown;
    private readonly Task reader;
    private Task? shutdown;
    private bool closing;

    internal EnrollmentPersistenceOwner(TrustedBrokerEnrollmentStore store, IEnrollmentCommitSigner signer, IBrokerEnrollmentRundown rundown)
    {
        this.store = store; this.signer = signer; this.rundown = rundown;
        reader = Task.Run(async () => { await foreach (var work in queue.Reader.ReadAllAsync()) work.Execute(); });
    }

    internal Task<Reply> CommitAsync(EnrollmentCeremony ceremony)
    {
        lock (gate)
        {
            if (commits.TryGetValue(ceremony, out var existing)) return existing;
            if (commits.Count >= 4096) throw new InvalidOperationException("Enrollment operation registry is full.");
            var task = Enqueue(() => Commit(ceremony)); commits.Add(ceremony, task); return task;
        }
    }
    private Reply Commit(EnrollmentCeremony ceremony)
    {
        var preparation = ceremony.PrepareCommit();
        store.Stage(preparation);
        var offer = ceremony.Offer;
        if (offer.Action == EnrollmentAction.Replace)
        {
            ceremony.RecheckPrepared(preparation);
            var old = store.ReadActive(offer.OldEnrollmentId);
            if (!CryptographicOperations.FixedTimeEquals(old.PeerIdentity, offer.PeerIdentity) ||
                old.SubjectUserSid is null || !CryptographicOperations.FixedTimeEquals(old.SubjectUserSid, offer.SubjectUserSid))
                throw new UnauthorizedAccessException("Replacement changed the protected peer or subject association.");
            store.Deny(old.EnrollmentId); rundown.CloseAdmission(old.EnrollmentId);
            store.Revoke(old.EnrollmentId); rundown.RevokeAndJoin(old.EnrollmentId);
        }
        var admission = ceremony.AdmitCommit(preparation); // Last UI/logon/deadline check.
        try
        {
            store.Commit(admission);
            ceremony.CompleteCommit(admission, EnrollmentCommitCompletion.Durable);
        }
        catch
        {
            ceremony.CompleteCommit(admission, EnrollmentCommitCompletion.Unconfirmed);
            throw;
        }
        var receipt = store.GetReceipt(offer.EnrollmentId, signer.SignCommittedReceipt);
        return new(receipt, store.CaptureCommittedResponse(offer.EnrollmentId));
    }

    internal Task<TrustedBrokerEnrollmentStore.SpeakerAssociation> SpeakerAssociationAsync(Guid enrollment)
    {
        lock (gate) return Enqueue(() => store.GetSpeakerAssociation(enrollment));
    }

    internal Task<EnrollmentOutcome> CancelAsync(EnrollmentCeremony ceremony)
    {
        if (ceremony.CancelBeforeCommit()) return Task.FromResult(EnrollmentOutcome.Aborted);
        return RevokeAsync(ceremony.Offer.EnrollmentId);
    }
    internal Task<EnrollmentOutcome> RevokeAsync(Guid id)
    {
        store.Deny(id); // Immediate even if the bounded queue is full or persistence is blocked.
        Exception? closeFailure = null;
        try { rundown.CloseAdmission(id); } catch (Exception error) { closeFailure = error; }
        lock (gate)
        {
            if (revokes.TryGetValue(id, out var current) && !current.IsFaulted) return current;
            var task = Enqueue(() =>
            {
                store.Revoke(id);
                if (closeFailure is not null) throw new InvalidOperationException("Native admission closure is unconfirmed.", closeFailure);
                rundown.RevokeAndJoin(id);
                return EnrollmentOutcome.Revoked;
            });
            revokes[id] = task; return task;
        }
    }
    internal Task<Reply> StatusAsync(byte[] signedRequest)
    {
        var requestCopy = signedRequest.ToArray();
        lock (gate) return Enqueue(() =>
        {
            var signed = EnrollmentFraming.ParseSigned(requestCopy); var request = EnrollmentProtocol.ParseStatusRequest(signed.Message);
            var snapshot = store.CaptureStatus(request, signed.Message, signed.Signature);
            var status = new EnrollmentStatus(request.Binding, request.RequestNonce, snapshot.Outcome);
            var bytes = EnrollmentFraming.Signed(EnrollmentProtocol.Status(status), signer.SignStatus(status));
            if (!store.AdmitResponse(snapshot)) throw new OperationCanceledException("Enrollment state changed during signing.");
            return new Reply(bytes, snapshot);
        });
    }
    internal bool AdmitResponse(Reply reply) => store.AdmitResponse(reply.Snapshot);
    internal Task<Reply> ReceiptAsync(Guid id)
    {
        lock (gate) return Enqueue(() => new Reply(store.GetReceipt(id, signer.SignCommittedReceipt), store.CaptureCommittedResponse(id)));
    }

    private Task<T> Enqueue<T>(Func<T> action)
    {
        if (closing) throw new ObjectDisposedException(nameof(EnrollmentPersistenceOwner));
        var work = new Work<T>(action);
        if (!queue.Writer.TryWrite(work)) throw new InvalidOperationException("Bounded enrollment persistence queue is full.");
        return work.Result.Task;
    }
    internal Task StopAsync()
    {
        lock (gate)
        {
            if (shutdown is not null && !shutdown.IsFaulted) return shutdown;
            closing = true; store.CloseAdmission(); queue.Writer.TryComplete();
            // Retain the owner/store on failure. A retry may finish native rundown;
            // no lease is disposed while queued I/O or native owners are uncertain.
            return shutdown = Task.Run(async () =>
            {
                rundown.CloseAllAdmission();
                await reader.ConfigureAwait(false);
                rundown.RevokeAndJoinAll();
                store.Dispose();
            });
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}

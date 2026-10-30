namespace DeskFuse.AudioBroker;

/// <summary>
/// Sole post-bootstrap pipe reader. One regular authority projection and one
/// restrictive projection may be retained; their actual I/O outlives callers.
/// The event sink only performs bounded routing, never crypto/native work.
/// </summary>
internal sealed class AudioWorkerControlPump
{
    private sealed class Pending(byte[] bytes, bool closed)
    {
        internal readonly byte[] Bytes = bytes;
        internal readonly bool Closed = closed;
        internal readonly TaskCompletionSource<bool> Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly AudioWorkerControl control;
    private readonly Func<AudioWorkerFrame, bool> post;
    private readonly Action<Exception> onFailure;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<(Guid Source, ulong Revision), Pending> pending = [];
    private readonly Task reader;
    private Exception? failed;
    private bool stopping;
    private bool stopRequested;
    private Task? stopWrite;
    private Task? join;
    private sealed class Reply(bool negative)
    {
        internal readonly bool Negative = negative;
        internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly HashSet<Reply> replies = [];
    internal AudioWorkerControlPump(AudioWorkerControl control, Func<AudioWorkerFrame, bool> tryPost, Action<Exception> onFailure)
    { this.control = control; post = tryPost; this.onFailure = onFailure; reader = ReadLoop(); }

    internal Task<bool> PublishAsync(byte[] bytes)
    {
        if (bytes.Length > AudioAuthorityProtocol.MaxDescriptor) throw new InvalidDataException("Oversized native projection.");
        var snapshot = bytes.ToArray(); var grant = AudioAuthorityProtocol.ParseControl(snapshot);
        Pending value;
        lock (gate)
        {
            RequireLive();
            var key = (grant.SourceGrant, grant.Revision);
            if (pending.TryGetValue(key, out var same))
            {
                if (!same.Bytes.AsSpan().SequenceEqual(snapshot)) throw new InvalidDataException("Conflicting native projection revision.");
                return same.Done.Task;
            }
            if (pending.Values.Any(p => p.Closed == grant.Closed)) throw new InvalidOperationException("Native projection slot is busy.");
            value = new(snapshot, grant.Closed); pending.Add(key, value);
        }
        _ = CompleteProjection(value, (grant.SourceGrant, grant.Revision));
        return value.Done.Task;
    }
    private async Task CompleteProjection(Pending value, (Guid Source, ulong Revision) key)
    {
        Exception? failure = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await control.WriteAsync(AudioWorkerFrameKind.Authority, value.Bytes, deadline.Token).ConfigureAwait(false);
            await value.Ack.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) { lock (gate) failure = failed ?? error; Fail(failure); }
        finally
        {
            // WriteAsync has actually completed/cancelled before releasing the
            // retained bytes. A cancelled caller never removes this operation.
            lock (gate) pending.Remove(key);
            if (value.Ack.Task.IsFaulted) _ = value.Ack.Task.Exception;
            if (failure is null) value.Done.TrySetResult(true); else value.Done.TrySetException(failure);
        }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var frame = await control.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame.Kind == AudioWorkerFrameKind.AuthorityObserved)
                {
                    var grant = AudioAuthorityProtocol.ParseControl(frame.Body);
                    lock (gate)
                    {
                        if (!pending.TryGetValue((grant.SourceGrant, grant.Revision), out var value) ||
                            !value.Bytes.AsSpan().SequenceEqual(frame.Body))
                            throw new InvalidDataException("Native acknowledgement has no exact retained projection.");
                        value.Ack.TrySetResult(true);
                    }
                }
                else if (frame.Kind is AudioWorkerFrameKind.Hello or AudioWorkerFrameKind.Ready or
                    AudioWorkerFrameKind.Authority or AudioWorkerFrameKind.Stop or AudioWorkerFrameKind.NetworkReply or
                    AudioWorkerFrameKind.ConfigureListener || !post(frame))
                    throw new InvalidDataException("Unroutable native control event.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }
    internal Task SendStopAsync()
    {
        lock (gate)
        {
            // This does not wait for a regular projection ACK. Existing frame
            // writes remain ordered and are bounded by their real I/O timeout.
            if (stopping) return Task.FromException(new IOException("Native control retired."));
            stopRequested = true; return stopWrite ??= WriteStop();
        }
    }
    private async Task WriteStop()
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await control.WriteAsync(AudioWorkerFrameKind.Stop, ReadOnlyMemory<byte>.Empty, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) { Fail(error); throw; }
    }
    internal Task SendReplyAsync(byte[] body, bool negative)
    {
        if (body.Length >= AudioWorkerControl.MaxFrame) throw new InvalidDataException("Oversized native reply.");
        var snapshot = body.ToArray(); _ = AudioWorkerNetworkProtocol.Parse(snapshot, reply: true);
        Reply value;
        lock (gate)
        {
            RequireLive();
            if (replies.Count(p => p.Negative == negative) >= (negative ? 1 : 8))
                throw new InvalidOperationException("Native reply capacity is occupied.");
            value = new(negative); replies.Add(value);
        }
        _ = WriteReply(value, snapshot); return value.Done.Task;
    }
    private async Task WriteReply(Reply value, byte[] body)
    {
        Exception? failure = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await control.WriteAsync(AudioWorkerFrameKind.NetworkReply, body, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; Fail(error); }
        finally
        {
            lock (gate) replies.Remove(value);
            if (failure is null) value.Done.TrySetResult(true); else value.Done.TrySetException(failure);
        }
    }
    internal Task StopAndJoinAsync()
    {
        lock (gate)
        {
            if (join is not null) return join;
            stopping = true;
            var work = pending.Values.Select(p => p.Done.Task).Cast<Task>().Append(reader).ToList();
            work.AddRange(replies.Select(p => p.Done.Task));
            if (stopWrite is not null) work.Add(stopWrite);
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
            foreach (var p in pending.Values) p.Ack.TrySetException(new IOException("Native control retired."));
            return join = Join(work);
        }
    }
    private async Task Join(List<Task> work)
    {
        try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Actual operations are joined, never abandoned. */ }
        lifetime.Dispose();
    }
    private void Fail(Exception error)
    {
        bool notify;
        lock (gate)
        {
            notify = failed is null && !stopping; failed ??= error;
            foreach (var value in pending.Values) value.Ack.TrySetException(error);
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
        }
        if (notify) onFailure(error);
    }
    private void RequireLive()
    { if (stopping || stopRequested || failed is not null) throw new IOException("Native control is terminal.", failed); }
}

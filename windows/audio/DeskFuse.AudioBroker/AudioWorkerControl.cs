using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DeskFuse.AudioBroker;

internal enum AudioWorkerFrameKind : byte {
    Hello = 1, Ready = 2, Stop = 3, Authority = 4, AuthorityObserved = 5,
    ConfigureListener = 6, ListenerConfigured = 7, ConnectionObserved = 8, ConnectionClosed = 9,
    NetworkRequest = 10, NetworkReply = 11, RundownObserved = 12,
}
internal sealed record AudioWorkerFrame(AudioWorkerFrameKind Kind, byte[] Body);

/// <summary>Bounded local native control, distinct from enrollment/network framing.</summary>
internal sealed class AudioWorkerControl(IEnrollmentChannel channel)
{
    internal const int MaxFrame = 8192, HelloBytes = 61;
    private readonly SemaphoreSlim receive = new(1, 1), send = new(1, 1);
    private int failed;

    internal async ValueTask<AudioWorkerFrame> ReadAsync(CancellationToken ct)
    {
        await receive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireLive();
            var header = new byte[4]; await Exact(header, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is < 1 or > MaxFrame) throw new InvalidDataException("Invalid native control length.");
            var bytes = new byte[length]; await Exact(bytes, ct).ConfigureAwait(false);
            var kind = (AudioWorkerFrameKind)bytes[0];
            if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown native control kind.");
            return new(kind, bytes.AsSpan(1).ToArray());
        }
        catch { Interlocked.Exchange(ref failed, 1); throw; }
        finally { receive.Release(); }
    }
    internal async ValueTask WriteAsync(AudioWorkerFrameKind kind, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || body.Length >= MaxFrame) throw new InvalidDataException("Invalid native control frame.");
        var bytes = new byte[body.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)body.Length + 1); bytes[4] = (byte)kind; body.Span.CopyTo(bytes.AsSpan(5));
        await send.WaitAsync(ct).ConfigureAwait(false);
        try { RequireLive(); await channel.WriteAsync(bytes, ct).ConfigureAwait(false); }
        catch { Interlocked.Exchange(ref failed, 1); throw; }
        finally { send.Release(); }
    }
    private async ValueTask Exact(Memory<byte> bytes, CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        {
            var count = await channel.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (count <= 0 || count > bytes.Length) throw new EndOfStreamException("Native control closed.");
            bytes = bytes[count..];
        }
    }
    private void RequireLive()
    { if (Volatile.Read(ref failed) != 0) throw new IOException("Native control is terminal; a fresh launch is required."); }

    internal static byte[] Hello(Guid registration, byte[] serviceBoot, uint servicePid, ulong serviceCreated)
    {
        if (registration == Guid.Empty || serviceBoot is not { Length: 32 } || serviceBoot.All(b => b == 0) || servicePid == 0 || serviceCreated == 0)
            throw new InvalidDataException("Missing native worker/service incarnation.");
        var bytes = new byte[HelloBytes]; bytes[0] = 1;
        if (!registration.TryWriteBytes(bytes.AsSpan(1, 16), bigEndian: true, out var written) || written != 16)
            throw new InvalidDataException("Invalid native worker registration.");
        serviceBoot.CopyTo(bytes, 17); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(49), servicePid);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(53), serviceCreated); return bytes;
    }
}

/// <summary>Bounded event owner whose actual work is joined before caller handles retire.</summary>
internal interface IAudioWorkerEventOwner
{
    bool TryPost(AudioWorkerFrame frame);
    Task StopAndJoinAsync();
}
/// <summary>Process-bound bootstrap and serialized native control; grants no resource by itself.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerAudioWorkerSession
{
    private readonly AudioWorkerControlPump pump;
    private readonly BrokerCallerBinding caller;
    private readonly byte[] boot;
    private readonly object publicationGate = new();
    private Task<bool>? publication;
    private Task<bool>? terminalPublication;
    private IAudioWorkerEventOwner? eventOwner;
    private Task? stopOperation;
    private BrokerAudioOwner? audioOwner;
    private bool stopping;
    private bool stopped;
    internal BrokerRegisteredWorker Worker { get; }
    internal IBrokerObservedCaller Caller => caller;
    private BrokerAudioWorkerSession(BrokerRegisteredWorker worker, BrokerCallerBinding caller, AudioWorkerControl control, byte[] boot)
    {
        Worker = worker; this.caller = caller; this.boot = boot;
        pump = new(control, frame => {
            if (!caller.Matches(worker)) throw new UnauthorizedAccessException("Native event caller changed.");
            return Volatile.Read(ref eventOwner)?.TryPost(frame) == true;
        }, _ => Volatile.Read(ref audioOwner)?.CloseAdmission());
    }

    internal void BindEvents(BrokerAudioOwner owner, IAudioWorkerEventOwner events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (publicationGate)
        {
            RequireOwner(owner);
            if (stopping || eventOwner is not null) throw new InvalidOperationException("Native event owner is already bound or stopping.");
            audioOwner = owner; Volatile.Write(ref eventOwner, events);
        }
    }
    private void RequireOwner(BrokerAudioOwner owner)
    {
        if (!owner.IsWorker(Worker) || audioOwner is not null && !ReferenceEquals(audioOwner, owner))
            throw new UnauthorizedAccessException("Audio publication is outside its retained source/worker owner.");
    }
    internal Task SendReplyAsync(byte[] bytes, bool negative) => pump.SendReplyAsync(bytes, negative);

    internal static async Task<BrokerAudioWorkerSession> ConnectAsync(BrokerRegisteredWorker worker, byte[] serviceBoot, CancellationToken ct)
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("Native bootstrap requires SYSTEM.");
        if (worker.Kind != BrokerWorkerKind.NativeResource || worker.ControlPipe is not { } pipe)
            throw new UnauthorizedAccessException("An actual fixed native worker is required.");
        using var process = Process.GetCurrentProcess();
        if (!BrokerWorkerLauncher.Native.GetProcessTimes(process.SafeHandle, out var creation, out _, out _, out _))
            throw BrokerWorkerLauncher.Native.Error("Cannot observe the service incarnation.");
        var created = unchecked((ulong)(uint)creation.dwLowDateTime | ((ulong)(uint)creation.dwHighDateTime << 32));
        var hello = AudioWorkerControl.Hello(worker.Registration, serviceBoot, checked((uint)process.Id), created);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct); setup.CancelAfter(TimeSpan.FromSeconds(3));
        BrokerCallerBinding? caller = null;
        try
        {
            for (var attempts = 0; attempts < 8; attempts++)
            {
                await pipe.WaitForConnectionAsync(setup.Token).ConfigureAwait(false);
                caller = pipe.ObserveConnectedCaller();
                if (caller.Matches(worker)) break;
                caller.Dispose(); caller = null; pipe.Disconnect(); // No protocol I/O started.
            }
            if (caller is null || !caller.Matches(worker)) throw new UnauthorizedAccessException("Native pipe caller did not match its retained worker.");
            var control = new AudioWorkerControl(pipe);
            await control.WriteAsync(AudioWorkerFrameKind.Hello, hello, setup.Token).ConfigureAwait(false);
            var ready = await control.ReadAsync(setup.Token).ConfigureAwait(false);
            if (ready.Kind != AudioWorkerFrameKind.Ready || !ready.Body.AsSpan().SequenceEqual(hello) || !caller.Matches(worker))
                throw new UnauthorizedAccessException("Native readiness did not confirm the exact bootstrap.");
            return new(worker, caller, control, hello.AsSpan(17, 32).ToArray());
        }
        catch (Exception failure)
        {
            caller?.Dispose();
            try { worker.StopAndJoin(); }
            catch (Exception cleanup) { throw new BrokerWorkerLaunchIncompleteException(worker, failure, cleanup); }
            throw;
        }
    }

    internal Task<bool> PublishAuthorityAsync(BrokerAudioOwner owner, CancellationToken ct)
    {
        Task<bool> operation;
        lock (publicationGate)
        {
            RequireOwner(owner);
            audioOwner = owner;
            if (ct.IsCancellationRequested) { QueueTerminal(owner); return Task.FromCanceled<bool>(ct); }
            var snapshot = owner.ControlSnapshot();
            if (snapshot.Closed) operation = QueueTerminal(owner);
            else
            {
                if (stopping) throw new InvalidOperationException("Native owner is stopping.");
                // Coalesce newer source observations into this one retained
                // writer operation rather than allocating competing publishers.
                operation = publication is { IsCompleted: false } pending ? pending : StartPublication(owner);
            }
        }
        return AwaitCaller(owner, operation, ct);
    }
    private Task<bool> QueueTerminal(BrokerAudioOwner owner)
    {
        lock (publicationGate)
        {
            RequireOwner(owner); owner.CloseAdmission(); stopping = true;
            return terminalPublication ??= PublishCore(owner, owner.ControlSnapshot());
        }
    }
    private async Task<bool> AwaitCaller(BrokerAudioOwner owner, Task<bool> operation, CancellationToken ct)
    {
        try
        {
            var result = await operation.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _ = QueueTerminal(owner); throw; }
    }
    private async Task<bool> PublishCore(BrokerAudioOwner owner, AudioControlGrant snapshot)
    {
        try
        {
            if (!snapshot.Boot.AsSpan().SequenceEqual(boot)) throw new UnauthorizedAccessException("Native service boot changed.");
            var bytes = AudioAuthorityProtocol.Control(snapshot);
            if (!owner.IsControlCurrent(snapshot)) return false;
            await pump.PublishAsync(bytes).ConfigureAwait(false);
            if (!caller.Matches(Worker))
                throw new UnauthorizedAccessException("Native authority acknowledgement changed context.");
            var current = owner.IsControlCurrent(snapshot);
            // This confirms only native authorization-state observation. N4
            // source setup and actual resource rundown require their own facts.
            // A newer benign state supersedes this reply; it does not restart
            // the healthy source. The serialized N3 owner publishes the latest.
            return current;
        }
        catch { owner.CloseAdmission(); throw; }
    }
    private Task<bool> StartPublication(BrokerAudioOwner owner)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        publication = completion.Task; _ = PublishLatest(owner, completion); return completion.Task;
    }
    private async Task PublishLatest(BrokerAudioOwner owner, TaskCompletionSource<bool> completion)
    {
        try
        {
            while (true)
            {
                var snapshot = owner.ControlSnapshot();
                var current = snapshot.Closed ? await QueueTerminal(owner).ConfigureAwait(false) : await PublishCore(owner, snapshot).ConfigureAwait(false);
                lock (publicationGate)
                {
                    // Publish task completion while holding the same admission
                    // gate used by coalescing callers: no completion-gap update
                    // can join a task which already stopped checking revisions.
                    if (current && owner.IsControlCurrent(snapshot)) { completion.TrySetResult(true); return; }
                }
            }
        }
        catch (Exception error) { owner.CloseAdmission(); completion.TrySetException(error); }
    }

    // Must be called by the serialized resource owner after native-resource
    // retirement, not under an authority lock. Failure retains this owner.
    internal Task StopAndJoinAsync()
    {
        lock (publicationGate)
        {
            if (stopped) return Task.CompletedTask;
            if (stopOperation is { IsFaulted: false }) return stopOperation;
            stopping = true; audioOwner?.CloseAdmission(); var pending = new[] { publication, terminalPublication }.OfType<Task>().Distinct().ToArray();
            // One retained cold operation, shared by cancellation/shutdown.
            // No process wait or pipe I/O occurs under publicationGate.
            return stopOperation = Task.Run(() => StopCore(pending));
        }
    }
    private async Task StopCore(Task[] pending)
    {
        try { await pump.SendStopAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        Worker.StopAndJoin(); // Actual retained job/process completion, regardless of cooperative reply.
        if (eventOwner is not null) await eventOwner.StopAndJoinAsync().ConfigureAwait(false);
        await pump.StopAndJoinAsync().ConfigureAwait(false);
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* Actual retained I/O has completed. */ }
        caller.Dispose(); lock (publicationGate) stopped = true;
    }
}

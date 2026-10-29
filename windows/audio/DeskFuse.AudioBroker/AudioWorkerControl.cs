using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DeskFuse.AudioBroker;

internal enum AudioWorkerFrameKind : byte { Hello = 1, Ready = 2, Stop = 3 }
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

/// <summary>N1 only: actual worker/process-bound bootstrap. This grants no audio resource.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrokerAudioWorkerSession
{
    private readonly AudioWorkerControl control;
    private readonly BrokerCallerBinding caller;
    private bool stopped;
    internal BrokerRegisteredWorker Worker { get; }
    internal IBrokerObservedCaller Caller => caller;
    private BrokerAudioWorkerSession(BrokerRegisteredWorker worker, BrokerCallerBinding caller, AudioWorkerControl control)
    { Worker = worker; this.caller = caller; this.control = control; }

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
            return new(worker, caller, control);
        }
        catch (Exception failure)
        {
            caller?.Dispose();
            try { worker.StopAndJoin(); }
            catch (Exception cleanup) { throw new BrokerWorkerLaunchIncompleteException(worker, failure, cleanup); }
            throw;
        }
    }

    // Must be called by the serialized resource owner after native-resource
    // retirement, not under an authority lock. Failure retains this owner.
    internal async Task StopAndJoinAsync()
    {
        if (stopped) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await control.WriteAsync(AudioWorkerFrameKind.Stop, ReadOnlyMemory<byte>.Empty, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        Worker.StopAndJoin(); // Actual retained job/process completion, regardless of cooperative reply.
        caller.Dispose(); stopped = true;
    }
}

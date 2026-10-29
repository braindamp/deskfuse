namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Operator intent and liveness only. No peer timestamp or completion assertion is authority.</summary>
public sealed record RunnerControl(int Version, Guid RunId, Guid RunnerNonce, long Revision,
    WorkerIdentity? Probe = null, bool ProofReady = false, int Acknowledge = 0, bool Close = false)
{
    public void Validate(OwnerRuntimeContract runtime)
    {
        if (Version != 1 || RunId != runtime.RunId || RunnerNonce != runtime.Runner.Nonce || Revision < 1 ||
            Acknowledge is < 0 or > 3 || (ProofReady && Probe is null))
            throw new InvalidDataException("Invalid runner control record.");
        if (Probe is { } probe)
        {
            probe.Validate();
            if (probe.Role != WorkerRole.Probe || probe.Nonce != runtime.ProbeNonce)
                throw new InvalidDataException("Wrong probe launch identity.");
        }
    }
}

/// <summary>Bounded mutable mailbox in an already private, retained run directory.
/// The stable file object is also the lock. A torn write fails closed; never replace it.</summary>
public static class RunnerControlMailbox
{
    public const string FileName = "runner-control.json";
    public const int MaximumBytes = 4096;

    public static RunnerControl Read(string privateRunDirectory)
    {
        using var file = Open(privateRunDirectory, FileName);
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid control record length.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return ContractJson.Read<RunnerControl>(bytes);
    }

    public static void Write(string privateRunDirectory, RunnerControl value)
    {
        var bytes = ContractJson.Write(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversized control record.");
        using var file = Open(privateRunDirectory, FileName);
        file.Write(bytes);
        file.SetLength(bytes.Length);
        file.Flush(flushToDisk: true);
    }

    internal static FileStream Open(string directory, string leaf)
    {
        // Trusted setup creates this exact private file. A runtime peer cannot
        // create/adopt a missing mailbox or select an arbitrary target name.
        var path = Path.Combine(directory, leaf);
        for (var i = 0; ; i++)
        {
            try { return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && i < 20) { Thread.Sleep(10); }
        }
    }
}

/// <summary>Watchdog progress for the surviving runner. Status is never a substitute for the retirement journal.</summary>
public sealed record WatchStatus(int Version, Guid RunId, WorkerIdentity Watch, long Revision, long ObservedAtMs,
    bool CleanupObservationExpired, RunSnapshot State)
{
    public const string FileName = "watch-status.json";
    public static WatchStatus Read(string privateRunDirectory)
    {
        using var file = RunnerControlMailbox.Open(privateRunDirectory, FileName);
        if (file.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid watchdog status length.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return ContractJson.Read<WatchStatus>(bytes);
    }

    public void Write(string privateRunDirectory)
    {
        var bytes = ContractJson.Write(this);
        if (bytes.Length > 16384) throw new InvalidDataException("Oversized watchdog status.");
        using var file = RunnerControlMailbox.Open(privateRunDirectory, FileName);
        file.Write(bytes); file.SetLength(bytes.Length); file.Flush(flushToDisk: true);
    }
}

/// <summary>One watchdog's observation of monotonic runner progress, using its own clock.</summary>
public sealed class RunnerProgress(OwnerRuntimeContract runtime, long armedAtMs)
{
    private RunnerControl? previous;
    public long LastHeartbeatMs { get; private set; } = armedAtMs;
    public bool Observe(RunnerControl value, long nowMs)
    {
        value.Validate(runtime);
        if (nowMs < LastHeartbeatMs || previous is { } old &&
            (value.Revision < old.Revision || value.Revision == old.Revision && value != old ||
             old.Probe is not null && value.Probe != old.Probe || value.Acknowledge < old.Acknowledge ||
             old.ProofReady && !value.ProofReady || old.Close && !value.Close))
            throw new InvalidDataException("Runner control regressed or changed without a new revision.");
        if (previous?.Revision == value.Revision) return false;
        previous = value;
        LastHeartbeatMs = nowMs;
        return true;
    }
}

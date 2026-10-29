namespace DeskFuse.DriverTestOwner.Core;

/// <summary>
/// Persistent maintenance admission. The platform layer protects this root;
/// releasing a process lock never clears an unresolved durable reservation.
/// Registry calls are made outside journal transitions (root-lock before state-lock).
/// </summary>
public sealed class RunRegistry(string protectedRoot)
{
    private readonly string root = Path.GetFullPath(protectedRoot);

    public RunJournal Reserve(RunManifest manifest)
    {
        Directory.CreateDirectory(root);
        using var held = ExclusiveFileLock.Acquire(Path.Combine(root, "admission.lock"));
        var runs = Path.Combine(root, "runs");
        Directory.CreateDirectory(runs);
        foreach (var directory in Directory.EnumerateDirectories(runs))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                throw new InvalidDataException("Unknown maintenance directory; no new admission.");
            if (!new RunJournal(directory).Snapshot().Retired)
                throw new InvalidOperationException("An earlier maintenance run remains unresolved.");
        }
        var active = ReadActive();
        if (active is Guid previous)
        {
            if (!Open(previous).Snapshot().Retired) throw new InvalidOperationException("Active maintenance is unresolved.");
            File.Delete(Path.Combine(root, "active.json"));
        }
        if (manifest.Version == 2) BindApproval(manifest);
        var journal = RunJournal.Create(Path.Combine(runs, manifest.RunId.ToString("N")), manifest);
        using (var file = new FileStream(Path.Combine(root, "active.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(ContractJson.Write(manifest.RunId));
            file.Flush(flushToDisk: true);
        }
        return journal;
    }

    public RunJournal Open(Guid runId)
    {
        if (runId == Guid.Empty) throw new InvalidDataException("Missing run identity.");
        return new RunJournal(Path.Combine(root, "runs", runId.ToString("N")));
    }

    // The platform owner must first establish that this entire root is protected
    // from untrusted writers/reparse replacement. Only the local elevated operator
    // may publish approval records there; this class is not that operator path.
    public T WithBoundApproval<T>(RunManifest manifest, Func<PackageApproval, T> commit)
    {
        if (manifest.Version != 2 || manifest.PackageAdmission is null)
            throw new InvalidDataException("No live package approval binding.");
        using var held = ExclusiveFileLock.Acquire(Path.Combine(root, "admission.lock"));
        if (ReadActive() != manifest.RunId) throw new InvalidOperationException("Run is no longer active.");
        var durable = Open(manifest.RunId);
        _ = durable.Snapshot(); // Verify manifest identity against the durable journal.
        if (!ContractJson.Write(durable.Manifest).AsSpan().SequenceEqual(ContractJson.Write(manifest)))
            throw new InvalidDataException("Requested admission does not match the durable run manifest.");
        var (approval, hash) = ReadApproval(manifest.PackageAdmission.ApprovalId);
        MatchApproval(manifest, approval, hash);
        var markerPath = BindingPath(approval.ApprovalId);
        var marker = ReadBounded<ApprovalBindingRecord>(markerPath);
        if (marker != new ApprovalBindingRecord(1, approval.ApprovalId, manifest.RunId, hash))
            throw new InvalidDataException("Approval binding does not match this run.");
        if (File.Exists(RevokedPath(approval.ApprovalId)))
            throw new InvalidOperationException("Package approval has been revoked.");
        // The callback must only commit the short durable state transition;
        // it must never execute trust checks or native PnP work.
        return commit(approval);
    }

    // Called by a future trusted elevated operator path. The shared admission
    // lock orders revocation against already admitted stage/install work.
    public void RevokeApproval(Guid approvalId)
    {
        if (approvalId == Guid.Empty) throw new InvalidDataException("Missing approval identity.");
        using var held = ExclusiveFileLock.Acquire(Path.Combine(root, "admission.lock"));
        var path = RevokedPath(approvalId);
        if (File.Exists(path)) return;
        WriteNew(path, ContractJson.Write(new ApprovalRevocationRecord(1, approvalId)));
    }

    private sealed record ApprovalBindingRecord(int Version, Guid ApprovalId, Guid RunId, string ApprovalSha256);
    private sealed record ApprovalRevocationRecord(int Version, Guid ApprovalId);

    private void BindApproval(RunManifest manifest)
    {
        var binding = manifest.PackageAdmission ?? throw new InvalidDataException("Missing package binding.");
        var (approval, hash) = ReadApproval(binding.ApprovalId);
        MatchApproval(manifest, approval, hash);
        if (File.Exists(RevokedPath(binding.ApprovalId))) throw new InvalidOperationException("Package approval is revoked.");
        // CreateNew + flush burns the immutable approval before creating a run.
        // A crash after this point cannot allow a second run to reuse it. An
        // orphan marker needs administrative reconciliation, not automatic reuse.
        WriteNew(BindingPath(binding.ApprovalId), ContractJson.Write(
            new ApprovalBindingRecord(1, binding.ApprovalId, manifest.RunId, hash)));
    }

    private (PackageApproval Approval, string Hash) ReadApproval(Guid id)
    {
        var path = Path.Combine(root, "approvals", id.ToString("N") + ".json");
        var bytes = ReadBoundedBytes(path);
        var approval = ContractJson.Read<PackageApproval>(bytes);
        approval.Validate();
        if (approval.ApprovalId != id || approval.State != PackageApprovalState.Available || approval.BoundRunId is not null)
            throw new InvalidDataException("Approval record is not an immutable available authorization.");
        return (approval, ContractJson.Hash(bytes));
    }

    private static void MatchApproval(RunManifest manifest, PackageApproval approval, string hash)
    {
        var binding = manifest.PackageAdmission ?? throw new InvalidDataException("Missing package binding.");
        binding.Validate();
        if (approval.ApprovalId != binding.ApprovalId || approval.Profile != binding.Profile ||
            approval.PackageContentDigest != binding.PackageContentDigest || hash != binding.ApprovalSha256)
            throw new InvalidDataException("Approval differs from the run manifest.");
    }

    private string BindingPath(Guid id) => Path.Combine(root, "approvals", id.ToString("N") + ".bound.json");
    private string RevokedPath(Guid id) => Path.Combine(root, "approvals", id.ToString("N") + ".revoked.json");
    private static T ReadBounded<T>(string path)
    {
        var bytes = ReadBoundedBytes(path);
        return ContractJson.Read<T>(bytes);
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 32768) throw new InvalidDataException("Oversized admission record.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    public bool ReleaseRetired(Guid expectedRunId)
    {
        using var held = ExclusiveFileLock.Acquire(Path.Combine(root, "admission.lock"));
        if (!Open(expectedRunId).Snapshot().Retired)
            throw new InvalidOperationException("Recovery must be durably Retired before releasing admission.");
        if (ReadActive() != expectedRunId) return false;
        File.Delete(Path.Combine(root, "active.json"));
        return true;
    }

    private Guid? ReadActive()
    {
        var path = Path.Combine(root, "active.json");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 256) throw new InvalidDataException("Invalid active-run record.");
        var id = ContractJson.Read<Guid>(bytes);
        if (id == Guid.Empty) throw new InvalidDataException("Invalid active-run identity.");
        return id;
    }
}

internal static class ExclusiveFileLock
{
    public static FileStream Acquire(string path)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(10); }
        }
    }
}

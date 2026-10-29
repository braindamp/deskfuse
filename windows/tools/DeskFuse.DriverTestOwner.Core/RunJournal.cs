using System.Text;

namespace DeskFuse.DriverTestOwner.Core;

/// <summary>
/// Append/flush is the acknowledgement boundary. Native calls never run under
/// this lock. The caller validates platform ACLs/paths before opening this store.
/// </summary>
public sealed class RunJournal
{
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private const int MaxRecordBytes = 32768;
    public const int MaxManifestBytes = 1024 * 1024;
    private static readonly string InitialHash = new('0', 64);
    private readonly string directory;
    private readonly string manifestHash;
    public RunManifest Manifest { get; }

    public RunJournal(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        // A self-contained helper plus the approved Windows probe has hundreds
        // of dependencies. The immutable manifest has its own bounded budget;
        // journal transitions remain small and retain their original limit.
        using var file = new FileStream(Path.Combine(this.directory, "run.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaxManifestBytes) throw new InvalidDataException("Oversized or empty run manifest.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        Manifest = ContractJson.Read<RunManifest>(bytes);
        Manifest.Validate(this.directory);
        manifestHash = ContractJson.Hash(bytes);
    }

    public static RunJournal Create(string directory, RunManifest manifest)
    {
        manifest.Validate(directory);
        var manifestBytes = ContractJson.Write(manifest);
        if (manifestBytes.Length > MaxManifestBytes) throw new InvalidDataException("Oversized run manifest.");
        Directory.CreateDirectory(directory);
        // CreateNew, never replace: a run GUID cannot be adopted or reused.
        using (var file = new FileStream(Path.Combine(directory, "run.json"), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            file.Write(manifestBytes);
            file.Flush(flushToDisk: true);
        }
        var journal = new RunJournal(directory);
        journal.Update(state => (RunEvent.Of(EventKind.Created, new CreatedData(journal.manifestHash)), 0), allowEmpty: true);
        return journal;
    }

    public RunSnapshot Snapshot() => Read(state => state.Snapshot());

    internal T Read<T>(Func<RunState, T> read)
    {
        using var held = Lock();
        var (state, _) = Replay();
        return read(state);
    }

    internal T Update<T>(Func<RunState, (RunEvent? Event, T Result)> decide, bool allowEmpty = false)
    {
        using var held = Lock();
        var (state, previous) = Replay(allowEmpty);
        var decision = decide(state);
        if (decision.Event is null) return decision.Result;
        var sequence = checked(state.Sequence + 1);
        // Validate the production transition before publishing bytes. This state
        // is local to the transaction; an append failure never updates a cache.
        state.Apply(sequence, decision.Event, manifestHash);
        var hash = RecordHash(sequence, previous, decision.Event);
        var bytes = ContractJson.Write(new JournalRecord(sequence, previous, decision.Event, hash));
        if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("Oversized journal record.");
        using var stream = new FileStream(Path.Combine(directory, "journal.jsonl"), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        if (stream.Length + bytes.Length + 1 > MaxJournalBytes) throw new InvalidDataException("Journal size limit reached.");
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
        return decision.Result;
    }

    private FileStream Lock() => ExclusiveFileLock.Acquire(Path.Combine(directory, "state.lock"));

    private (RunState State, string Hash) Replay(bool allowEmpty = false)
    {
        var state = new RunState(Manifest.RunId);
        var path = Path.Combine(directory, "journal.jsonl");
        if (!File.Exists(path))
        {
            if (allowEmpty) return (state, InitialHash);
            throw new InvalidDataException("Missing run journal; recovery is incomplete.");
        }
        var info = new FileInfo(path);
        if (info.Length > MaxJournalBytes) throw new InvalidDataException("Oversized run journal.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n') throw new InvalidDataException("Empty or torn journal.");
        var previous = InitialHash;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', offset);
            var length = newline - offset;
            if (length <= 0 || length > MaxRecordBytes) throw new InvalidDataException("Invalid journal framing.");
            var record = ContractJson.Read<JournalRecord>(bytes.AsSpan(offset, length));
            if (record.Sequence != state.Sequence + 1 || record.PreviousHash != previous ||
                record.Hash != RecordHash(record.Sequence, previous, record.Event))
                throw new InvalidDataException("Journal sequence/hash mismatch.");
            state.Apply(record.Sequence, record.Event, manifestHash);
            previous = record.Hash;
            offset = newline + 1;
        }
        return (state, previous);
    }

    private static string RecordHash(long sequence, string previous, RunEvent entry) =>
        ContractJson.Hash(Encoding.UTF8.GetBytes(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + previous + "\n" + entry.Kind + "\n" + entry.Payload));
}

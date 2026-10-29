namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Read-only owner result for a surviving UI, never an alternative retirement predicate.</summary>
public sealed record RecoveryReport(int Version, Guid RunId, WorkerIdentity Worker, bool Boot, RunSnapshot State)
{
    public const string FileName = "recovery-status.json";
    public void Write(string privateRunDirectory)
    {
        var bytes = ContractJson.Write(this);
        if (bytes.Length > 16384) throw new InvalidDataException("Oversized recovery result.");
        using var file = RunnerControlMailbox.Open(privateRunDirectory, FileName);
        file.Write(bytes); file.SetLength(bytes.Length); file.Flush(flushToDisk: true);
    }
}

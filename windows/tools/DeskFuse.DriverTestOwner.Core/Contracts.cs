using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskFuse.DriverTestOwner.Core;

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static T Read<T>(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Missing JSON value.");
    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record SafetyPolicy(int Version, long[] PromptAtMs, long[] AckDeadlineMs,
    long ProofReadyDeadlineMs, long CloseAtMs, long CleanupObservationDeadlineMs,
    long HeartbeatTimeoutMs, long AutomaticRetryWindowMs)
{
    public void Validate()
    {
        if (Version != 1 || PromptAtMs is not { Length: 3 } || AckDeadlineMs is not { Length: 3 } ||
            !PromptAtMs.SequenceEqual(new long[] { 60000, 180000, 300000 }) ||
            !AckDeadlineMs.SequenceEqual(new long[] { 85000, 205000, 325000 }) ||
            ProofReadyDeadlineMs != 60000 || CloseAtMs != 325000 ||
            CleanupObservationDeadlineMs != 350000 || HeartbeatTimeoutMs != 10000 ||
            AutomaticRetryWindowMs != 1200000)
            throw new InvalidDataException("Unreviewed or invalid driver-test safety policy.");
    }
}

public sealed record RunManifest(int Version, Guid RunId, string InstanceId, string HardwareId,
    Guid ClassGuid, string PolicySha256, Dictionary<string, string> BundleSha256,
    string WatchTask, string BootTask, PackageAdmissionBinding? PackageAdmission = null)
{
    public static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    public static string DeviceId(Guid runId) => $@"ROOT\DeskFuseFocusIdd\{runId:N}";
    public static string WatchName(Guid runId) => $"DeskFuse-FocusIdd-Watch-{runId:N}";
    public static string BootName(Guid runId) => $"DeskFuse-FocusIdd-Recover-{runId:N}";

    public void Validate(string directory)
    {
        if (Version is not (1 or 2) || (Version == 1 && PackageAdmission is not null) ||
            (Version == 2 && PackageAdmission is null) || RunId == Guid.Empty || Path.GetFileName(Path.GetFullPath(directory)) != RunId.ToString("N") ||
            !string.Equals(InstanceId, DeviceId(RunId), StringComparison.OrdinalIgnoreCase) ||
            HardwareId != @"Root\FocusIdd" || ClassGuid != DisplayClass ||
            WatchTask != WatchName(RunId) || BootTask != BootName(RunId) || !ContractJson.IsHash(PolicySha256) ||
            BundleSha256 is not { Count: > 0 } ||
            BundleSha256.Keys.Select(key => key.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != BundleSha256.Count ||
            BundleSha256.Any(p =>
                !ContractJson.IsHash(p.Value) || string.IsNullOrWhiteSpace(p.Key) || Path.IsPathRooted(p.Key) ||
                p.Key.Split('/', '\\').Any(segment => segment is "" or "." or ".." || segment.Contains(':'))))
            throw new InvalidDataException("Invalid driver-test run identity or bundle manifest.");
        PackageAdmission?.Validate();
    }
}

public enum WorkerRole { Creator, Recovery, Watch, Probe, Runner }
public sealed record WorkerIdentity(int Pid, long CreationFileTime, Guid Nonce, WorkerRole Role)
{
    public void Validate()
    {
        if (Pid <= 0 || CreationFileTime <= 0 || Nonce == Guid.Empty || !Enum.IsDefined(Role))
            throw new InvalidDataException("Invalid worker identity.");
    }
}

public enum NativeOperation { StagePackage, CreateElement, SetHardwareId, RegisterDevice, InstallDevice, RemoveDevice }
public enum CreatorPermit { Available, Consumed, Relinquished, Revoked }
public sealed record NativeResult(bool Succeeded, int ErrorCode = 0, bool RebootRequired = false,
    bool ParentAndSubtreeAbsent = false, bool EnumerationSucceeded = false, bool OwnershipConflict = false);
public sealed record AbsenceObservation(Guid RunId, string InstanceId, bool ParentAndSubtreeAbsent,
    bool EnumerationSucceeded);
public sealed record PendingOperation(Guid Id, WorkerIdentity Worker, NativeOperation Kind, long IntentSequence);
public sealed record RunSnapshot(Guid RunId, long Sequence, bool Armed, bool ProofReady, bool Closing,
    bool Retired, string? IncompleteReason, bool RebootRequired, CreatorPermit Permit,
    WorkerIdentity? Creator, bool CreatorExited, WorkerIdentity? Probe, bool ProbeStopped,
    PendingOperation[] Pending, int[] Acknowledged, long? ArmedAtMs, long LastCreationReturnSequence,
    long FinalRemovalIntentSequence, long FinalRemovalReturnSequence, WorkerIdentity? Watch);

internal enum EventKind { Created, Armed, WorkerRegistered, OperationIntent, OperationReturned,
    CreatorRelinquished, WorkerExited, ProbeRegistered, ProbeStopped, ProofReady,
    Acknowledged, Closing, Retired }
internal sealed record RunEvent(EventKind Kind, string Payload)
{
    public static RunEvent Of<T>(EventKind kind, T payload) =>
        new(kind, JsonSerializer.Serialize(payload, ContractJson.Options));
    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, ContractJson.Options)
        ?? throw new InvalidDataException("Missing journal event payload.");
}
internal sealed record JournalRecord(long Sequence, string PreviousHash, RunEvent Event, string Hash);
internal sealed record CreatedData(string ManifestHash);
public sealed record RecoveryConfirmation(Guid RunId, WorkerIdentity Watch, string BootTask,
    bool TaskIdentityVerified, bool ProtectedBundleVerified);
internal sealed record ArmedData(long AtMs, WorkerIdentity Watch);
internal sealed record IntentData(Guid Id, WorkerIdentity Worker, NativeOperation Kind);
internal sealed record ReturnData(Guid Id, WorkerIdentity Worker, NativeResult Result);
internal sealed record AckData(int Index, long ElapsedMs);
internal sealed record ClosingData(string Reason);

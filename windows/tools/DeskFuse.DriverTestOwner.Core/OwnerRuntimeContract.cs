namespace DeskFuse.DriverTestOwner.Core;

/// <summary>Immutable trusted-setup data, hashed as bundle/runtime.json in the run manifest.</summary>
public sealed record OwnerRuntimeContract(int Version, Guid RunId, Guid WatchNonce, Guid BootNonce,
    Guid CreatorNonce, Guid ProbeNonce, WorkerIdentity Runner, int Width, int Height,
    double Dpr, int DiagonalMm, int Refresh)
{
    public const string BundleName = "runtime.json";
    public const string ProbeImage = "probe/FocusWindow.Agent.exe";

    public void Validate(RunManifest manifest)
    {
        Runner.Validate();
        var nonces = new[] { WatchNonce, BootNonce, CreatorNonce, ProbeNonce, Runner.Nonce };
        if (Version != 1 || RunId != manifest.RunId || Runner.Role != WorkerRole.Runner ||
            nonces.Any(id => id == Guid.Empty) || nonces.Distinct().Count() != nonces.Length ||
            Width is < 320 or > 16384 || Height is < 320 or > 16384 ||
            !double.IsFinite(Dpr) || Dpr is < 1 or > 5 || DiagonalMm is < 50 or > 1000 || Refresh is < 1 or > 240 ||
            !manifest.BundleSha256.ContainsKey(BundleName) || !manifest.BundleSha256.ContainsKey(ProbeImage))
            throw new InvalidDataException("Invalid prepared driver-test runtime contract.");
    }

    public OwnerCommand Watch(Guid namespaceId) => new(OwnerRole.Watch, namespaceId, RunId, WatchNonce);
    public OwnerCommand BootRecovery(Guid namespaceId) => new(OwnerRole.Recover, namespaceId, RunId, BootNonce, Boot: true);
    public OwnerCommand Provision(Guid namespaceId) => new(OwnerRole.Provision, namespaceId, RunId, CreatorNonce);
}

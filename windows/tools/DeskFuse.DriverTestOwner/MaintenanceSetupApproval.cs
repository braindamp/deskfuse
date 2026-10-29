using DeskFuse.DriverTestOwner.Core;
using DeskFuse.WindowsSecurity;
using System.Runtime.Versioning;

namespace DeskFuse.DriverTestOwner;

/// <summary>Independent local-setup authorization, stored privately before any preparation.
/// Code hashes must be explicitly approved by trusted local setup, never imported as authority from a receipt.</summary>
[SupportedOSPlatform("windows")]
internal sealed record MaintenanceSetupApproval(int Version, Guid RunId, Guid PackageApprovalId,
    string ApprovedByWindowsSid, Dictionary<string, string> CodeSha256, OwnerRuntimeContract Runtime)
{
    internal const string BundleName = "setup-approval.json";
    internal static string RecordPath(Guid runId) => "approvals/setup-" + runId.ToString("N") + ".json";

    internal void Validate()
    {
        if (Version != 1 || RunId == Guid.Empty || PackageApprovalId == Guid.Empty ||
            string.IsNullOrWhiteSpace(ApprovedByWindowsSid) || Runtime is null || Runtime.RunId != RunId || CodeSha256 is null)
            throw new InvalidDataException("Invalid local maintenance authorization.");
        BundleIdentity.RequireHelperProfile(CodeSha256);
        BundleIdentity.RequireExactFiles(CodeSha256, CodeSha256.Keys);
        foreach (var required in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "runner/maintenance-runner.psm1",
            "runner/safe-driver-proof.ps1", "runner/safety-watchdog.ps1", "runner/safety-boot-recovery.ps1",
            "runner/rollback-focusidd.ps1", "runner/run-proof.ps1", "runner/driver-install-testpc.ps1" })
            if (!CodeSha256.ContainsKey(required)) throw new InvalidDataException("The approved self-contained runner/helper profile is incomplete.");
        foreach (var name in CodeSha256.Keys)
        {
            _ = SecurePrivateStorageRoot.ResolveRelative("unused", name);
            if (name != name.Replace('\\', '/') || name.Equals(BundleName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(OwnerRuntimeContract.BundleName, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("package/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Code authorization contains generated data or package payload.");
        }
        var hashes = new Dictionary<string, string>(CodeSha256, StringComparer.OrdinalIgnoreCase)
        { [OwnerRuntimeContract.BundleName] = ContractJson.Hash(ContractJson.Write(Runtime)) };
        Runtime.Validate(new(2, RunId, RunManifest.DeviceId(RunId), @"Root\FocusIdd", RunManifest.DisplayClass,
            new string('0', 64), hashes, RunManifest.WatchName(RunId), RunManifest.BootName(RunId),
            new(PackageProfile.LocalTest, PackageApprovalId, new string('0', 64), new string('0', 64), new string('0', 64))));
    }

    internal void RequireManifest(RunManifest manifest)
    {
        Validate();
        var expected = new Dictionary<string, string>(CodeSha256, StringComparer.OrdinalIgnoreCase)
        {
            [OwnerRuntimeContract.BundleName] = ContractJson.Hash(ContractJson.Write(Runtime)),
            [BundleName] = ContractJson.Hash(ContractJson.Write(this))
        };
        var actual = manifest.BundleSha256.Where(p => !p.Key.StartsWith("package/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        BundleIdentity.RequireExactFiles(expected, actual.Keys);
        if (RunId != manifest.RunId || manifest.PackageAdmission?.ApprovalId != PackageApprovalId ||
            expected.Any(p => actual[p.Key] != p.Value))
            throw new InvalidDataException("Run manifest differs from the independent setup authorization.");
    }
}

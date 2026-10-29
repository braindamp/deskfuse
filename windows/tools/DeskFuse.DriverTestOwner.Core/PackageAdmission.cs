using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeskFuse.DriverTestOwner.Core;

public enum PackageProfile { BuildOnly, LocalTest, Release }
public enum PackageApprovalState { Available, BoundToRun, Revoked }
public sealed record PackageFile(string Path, string Sha256);

/// <summary>Identity of the fixed FocusIdd x64 package, including its final catalog.</summary>
public sealed record PackageIdentity(PackageFile[] Files)
{
    private static readonly string[] Expected = ["FocusIdd.cat", "FocusIdd.dll", "FocusIdd.inf"];

    public string Digest()
    {
        if (Files is not { Length: 3 } || Files.Any(f => f is null || f.Path is null))
            throw new InvalidDataException("Unexpected FocusIdd package payload.");
        var ordered = Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        for (var i = 0; i < ordered.Length; i++)
        {
            var file = ordered[i];
            if (file is null || !string.Equals(file.Path, Expected[i], StringComparison.OrdinalIgnoreCase) ||
                !ContractJson.IsHash(file.Sha256))
                throw new InvalidDataException("Invalid or duplicate FocusIdd package member.");
            // The profile defines the canonical spelling; caller-controlled case cannot alter the digest.
            var name = Encoding.UTF8.GetBytes(Expected[i]);
            BinaryPrimitives.WriteInt32BigEndian(length, name.Length);
            hash.AppendData(length);
            hash.AppendData(name);
            hash.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>Protected operator authorization; this record alone is not Windows trust proof.</summary>
public sealed record PackageApproval(int SchemaVersion, Guid ApprovalId, string Component, string Architecture,
    PackageProfile Profile, string PackageContentDigest, PackageFile[] Files, string CatalogRelativePath,
    string CatalogRawSha256, string[] AllowedSignerCertificateDerSha256, int VerificationPolicyVersion,
    string ApprovedByWindowsSid, PackageApprovalState State, Guid? BoundRunId)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || ApprovalId == Guid.Empty || Component != "FocusIdd.Umdf" || Architecture != "x64" ||
            Profile is not (PackageProfile.LocalTest or PackageProfile.Release) || VerificationPolicyVersion != 1 ||
            string.IsNullOrWhiteSpace(ApprovedByWindowsSid) || !Enum.IsDefined(State) ||
            (State == PackageApprovalState.Available && BoundRunId is not null) ||
            (State == PackageApprovalState.BoundToRun && (BoundRunId is null || BoundRunId == Guid.Empty)) ||
            (BoundRunId == Guid.Empty) || !ContractJson.IsHash(PackageContentDigest) ||
            !ContractJson.IsHash(CatalogRawSha256) ||
            !string.Equals(CatalogRelativePath, "FocusIdd.cat", StringComparison.OrdinalIgnoreCase) ||
            AllowedSignerCertificateDerSha256 is not { Length: > 0 } ||
            AllowedSignerCertificateDerSha256.Any(s => !ContractJson.IsHash(s)) ||
            AllowedSignerCertificateDerSha256.Distinct(StringComparer.Ordinal).Count() != AllowedSignerCertificateDerSha256.Length)
            throw new InvalidDataException("Invalid package approval.");
        var identity = new PackageIdentity(Files);
        if (identity.Digest() != PackageContentDigest ||
            Files.Single(f => string.Equals(f.Path, "FocusIdd.cat", StringComparison.OrdinalIgnoreCase)).Sha256 != CatalogRawSha256)
            throw new InvalidDataException("Approval does not match its package identity.");
    }
}

/// <summary>Binding recorded in a v2 run manifest. It is not a substitute for fresh verification.</summary>
public sealed record PackageAdmissionBinding(PackageProfile Profile, Guid ApprovalId, string ApprovalSha256,
    string PackageContentDigest, string VerificationRecordSha256)
{
    public void Validate()
    {
        if (Profile is not (PackageProfile.LocalTest or PackageProfile.Release) || ApprovalId == Guid.Empty ||
            !ContractJson.IsHash(ApprovalSha256) || !ContractJson.IsHash(PackageContentDigest) ||
            !ContractJson.IsHash(VerificationRecordSha256))
            throw new InvalidDataException("Invalid live package admission binding.");
    }
}

/// <summary>
/// Implemented by the protected platform owner. Verification must use the bound
/// approval, locked package and machine trust; a serialized peer verdict is not authority.
/// No production implementation is present until those platform checks exist.
/// </summary>
public interface IPackageAdmissionVerifier
{
    // Verification/locked package custody must not hold the short authorization
    // gate: revocation must be able to fence subsequent work while trust or a
    // previously admitted native operation is stalled.
    IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation);
}

public interface IPackageAdmissionLease : IDisposable
{
    // Under the short protected registry gate, recheck the current approval,
    // run binding and revocation tombstone, then commit the journal transition.
    // Release that gate before native dispatch; retain package custody until return.
    T CommitAdmission<T>(Func<T> commit);
}

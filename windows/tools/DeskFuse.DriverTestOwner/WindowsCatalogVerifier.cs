using System.Security.Cryptography;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

internal sealed record CatalogMemberProof(string RelativePath, string RawSha256, string CatalogHashSha256);
internal sealed record CatalogVerificationEvidence(PackageProfile Profile, string PackageContentDigest,
    string CatalogRawSha256, string SignerCertificateDerSha256, int VerificationPolicyVersion,
    CatalogMemberProof[] Members);
internal sealed record VerifiedSigner(byte[] CertificateDer);

// This seam is internal. The protected owner constructs the native backend;
// test implementations are not a boundary for untrusted caller input.
internal interface IWindowsCatalogTrustBackend
{
    void RequireSystemContext();
    void RequireExpectedCatalog(FileStream inf, string catalogName);
    VerifiedSigner VerifyCatalog(FileStream catalog, PackageProfile profile);
    (VerifiedSigner Signer, string CatalogHash) VerifyMember(FileStream catalog, FileStream member, PackageProfile profile);
    bool IsLocalMachineTrustedPublisher(byte[] certificateDer);
}

/// <summary>
/// Verifies exact package bytes and Windows trust using files already held open
/// under protected custody. The caller retains custody through native use and
/// separately verifies protected approval, identity, ACLs, and one-run binding.
/// This result is evidence only; it does not itself authorize PnP operations.
/// </summary>
internal sealed class WindowsCatalogVerifier
{
    private readonly IWindowsCatalogTrustBackend backend;
    internal WindowsCatalogVerifier() : this(OperatingSystem.IsWindows() ? new NativeWindowsCatalogTrustBackend()
        : throw new PlatformNotSupportedException("Windows trust is required.")) { }
    internal WindowsCatalogVerifier(IWindowsCatalogTrustBackend backend) => this.backend = backend;

    internal CatalogVerificationEvidence Verify(PackageApproval candidate, IReadOnlyDictionary<string, FileStream> heldFiles)
    {
        // Copy mutable arrays so caller edits cannot change approval during this check.
        var approval = ContractJson.Read<PackageApproval>(ContractJson.Write(candidate));
        approval.Validate();
        backend.RequireSystemContext();
        if (heldFiles.Count != 3) throw new InvalidDataException("Expected exactly three held FocusIdd files.");
        var files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in heldFiles)
        {
            if (!files.TryAdd(entry.Key, entry.Value) || entry.Value is null ||
                !string.Equals(Path.GetFileName(entry.Value.Name), entry.Key, StringComparison.OrdinalIgnoreCase) ||
                !entry.Value.CanRead || !entry.Value.CanSeek || entry.Value.SafeFileHandle.IsInvalid || entry.Value.SafeFileHandle.IsClosed)
                throw new InvalidDataException("Invalid held package file.");
        }
        foreach (var file in approval.Files)
        {
            if (!files.TryGetValue(file.Path, out var stream)) throw new InvalidDataException("Missing approved package file.");
            stream.Position = 0;
            string actual;
            try { actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
            finally { stream.Position = 0; }
            if (actual != file.Sha256) throw new InvalidDataException($"Approved package member changed: {file.Path}.");
        }

        backend.RequireExpectedCatalog(files["FocusIdd.inf"], approval.CatalogRelativePath);
        var catalog = files[approval.CatalogRelativePath];
        var catalogSigner = backend.VerifyCatalog(catalog, approval.Profile);
        var signerHash = CertificateHash(catalogSigner);
        if (!approval.AllowedSignerCertificateDerSha256.Contains(signerHash, StringComparer.Ordinal))
            throw new InvalidDataException("The Windows-verified catalog signer is not approved.");
        if (approval.Profile == PackageProfile.LocalTest && !backend.IsLocalMachineTrustedPublisher(catalogSigner.CertificateDer))
            throw new InvalidDataException("The approved test signer is absent from LocalMachine Trusted Publishers.");

        var proofs = new List<CatalogMemberProof>(2);
        foreach (var file in approval.Files.Where(f => !string.Equals(f.Path, approval.CatalogRelativePath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var (memberSigner, catalogHash) = backend.VerifyMember(catalog, files[file.Path], approval.Profile);
            if (CertificateHash(memberSigner) != signerHash || !ContractJson.IsHash(catalogHash))
                throw new InvalidDataException("Catalog member proof or verified signer does not match the catalog.");
            var canonicalName = string.Equals(file.Path, "FocusIdd.dll", StringComparison.OrdinalIgnoreCase)
                ? "FocusIdd.dll" : "FocusIdd.inf";
            proofs.Add(new CatalogMemberProof(canonicalName, file.Sha256, catalogHash));
        }
        if (proofs.Count != 2) throw new InvalidDataException("Missing FocusIdd catalog membership proof.");
        return new(approval.Profile, approval.PackageContentDigest, approval.CatalogRawSha256, signerHash,
            approval.VerificationPolicyVersion, proofs.ToArray());
    }

    private static string CertificateHash(VerifiedSigner signer)
    {
        if (signer.CertificateDer is not { Length: > 0 and <= 1048576 })
            throw new InvalidDataException("Windows returned no bounded signer certificate.");
        return ContractJson.Hash(signer.CertificateDer);
    }
}

using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

internal sealed record PackageVerificationRecord(int Version, Guid RunId, Guid ApprovalId, string ApprovalSha256,
    string Component, string Architecture, PackageProfile Profile, string PackageContentDigest,
    string CatalogRawSha256, string SignerCertificateDerSha256, int VerificationPolicyVersion,
    string VerificationContext, string Outcome, CatalogMemberProof[] Members)
{
    // The record is evidence about a successful verification, not a peer's
    // authorization. Only the protected owner constructs it from Windows results.
    internal static PackageVerificationRecord FromEvidence(Guid runId, Guid approvalId, string approvalSha256,
        CatalogVerificationEvidence evidence)
    {
        if (runId == Guid.Empty || approvalId == Guid.Empty || !ContractJson.IsHash(approvalSha256))
            throw new InvalidDataException("Invalid verification record identity.");
        if (evidence.Profile is not (PackageProfile.LocalTest or PackageProfile.Release) ||
            !ContractJson.IsHash(evidence.PackageContentDigest) || !ContractJson.IsHash(evidence.CatalogRawSha256) ||
            !ContractJson.IsHash(evidence.SignerCertificateDerSha256) || evidence.VerificationPolicyVersion != 1 ||
            evidence.Members is not { Length: 2 } || evidence.Members.Any(m => m is null ||
                !ContractJson.IsHash(m.RawSha256) || !ContractJson.IsHash(m.CatalogHashSha256)) ||
            !evidence.Members.Select(m => m.RelativePath).SequenceEqual(new[] { "FocusIdd.dll", "FocusIdd.inf" }))
            throw new InvalidDataException("Invalid completed Windows verification evidence.");
        return new(1, runId, approvalId, approvalSha256, "FocusIdd.Umdf", "x64", evidence.Profile,
            evidence.PackageContentDigest, evidence.CatalogRawSha256, evidence.SignerCertificateDerSha256,
            evidence.VerificationPolicyVersion, "Windows.System.MachineTrust.v1", "Verified", evidence.Members.ToArray());
    }

    internal void RequireMatches(RunManifest manifest, PackageApproval approval, CatalogVerificationEvidence fresh)
    {
        var binding = manifest.PackageAdmission ?? throw new InvalidDataException("Missing package admission binding.");
        var expected = FromEvidence(manifest.RunId, binding.ApprovalId, binding.ApprovalSha256, fresh);
        if (Version != 1 || RunId != expected.RunId || ApprovalId != expected.ApprovalId ||
            ApprovalSha256 != expected.ApprovalSha256 || Component != expected.Component || Architecture != expected.Architecture ||
            Profile != approval.Profile || Profile != binding.Profile || Profile != fresh.Profile ||
            PackageContentDigest != approval.PackageContentDigest || PackageContentDigest != binding.PackageContentDigest ||
            PackageContentDigest != fresh.PackageContentDigest || CatalogRawSha256 != approval.CatalogRawSha256 ||
            CatalogRawSha256 != fresh.CatalogRawSha256 || SignerCertificateDerSha256 != fresh.SignerCertificateDerSha256 ||
            VerificationPolicyVersion != approval.VerificationPolicyVersion || VerificationPolicyVersion != fresh.VerificationPolicyVersion ||
            VerificationContext != expected.VerificationContext || Outcome != expected.Outcome ||
            Members is not { Length: 2 } || !Members.SequenceEqual(fresh.Members))
            throw new InvalidDataException("Protected verification record does not match the approved run and fresh Windows proof.");
    }
}

/// <summary>
/// Only a protected SYSTEM owner may implement this scope. It must hold the
/// verified ancestor and run/package identities, prevent untrusted mutation
/// and validate owner/DACL/reparse/stream conditions for the entire lifetime.
/// The current non-authorizing inspection cannot construct this scope.
/// </summary>
internal interface IProtectedAdmissionStore
{
    RunRegistry Registry { get; }
    IProtectedAdmissionScope Acquire(RunManifest manifest);
}

internal interface IProtectedAdmissionScope : IDisposable
{
    LockedPackage OpenPackage(PackageApproval approval);
    byte[] ReadVerificationRecord();
    void ValidateHeldIdentity();
}

internal sealed record PreparedPackageVerification(PackageAdmissionBinding Binding, byte[] RecordBytes);

/// <summary>
/// Called by trusted setup after final package copying/signing and protected
/// candidate approval. The caller writes RecordBytes with create-new semantics
/// in the same protected namespace before reserving the resulting run manifest.
/// Fresh verification at each creation boundary remains mandatory.
/// </summary>
internal static class PackageAdmissionPreparation
{
    internal static PreparedPackageVerification Prepare(Guid runId, ReadOnlySpan<byte> protectedApprovalBytes,
        LockedPackage package, WindowsCatalogVerifier verifier)
    {
        if (runId == Guid.Empty || protectedApprovalBytes.Length is 0 or > 32768)
            throw new InvalidDataException("Invalid prospective run or approval record.");
        var approval = ContractJson.Read<PackageApproval>(protectedApprovalBytes);
        approval.Validate();
        if (approval.State != PackageApprovalState.Available || approval.BoundRunId is not null ||
            !ContractJson.Write(approval).AsSpan().SequenceEqual(protectedApprovalBytes))
            throw new InvalidDataException("The protected approval is not a canonical candidate authorization record.");
        package.ValidateStreams();
        var evidence = verifier.Verify(approval, package.Files);
        var approvalHash = ContractJson.Hash(protectedApprovalBytes);
        var bytes = ContractJson.Write(PackageVerificationRecord.FromEvidence(runId, approval.ApprovalId, approvalHash, evidence));
        return new(new PackageAdmissionBinding(approval.Profile, approval.ApprovalId, approvalHash,
            approval.PackageContentDigest, ContractJson.Hash(bytes)), bytes);
    }
}

/// <summary>
/// Composes one-run approval, protected package custody, immutable verification
/// record and fresh Windows verification. No production protected-store factory
/// or live entrypoint exists until secure root creation/lifetime is established.
/// </summary>
internal sealed class PackageAdmissionOwner(IProtectedAdmissionStore store,
    WindowsCatalogVerifier verifier) : IPackageAdmissionVerifier
{
    public IPackageAdmissionLease AcquireForCreation(RunManifest manifest, NativeOperation? operation)
    {
        if (operation == NativeOperation.RemoveDevice || (operation is not null && !Enum.IsDefined(operation.Value)))
            throw new InvalidOperationException("Package admission is for creation only.");
        // The record contains mutable arrays/maps. Do not allow a caller to alter
        // the identity used by this lease after its initial protected check.
        manifest = ContractJson.Read<RunManifest>(ContractJson.Write(manifest));
        var registry = store.Registry;
        var binding = manifest.PackageAdmission ?? throw new InvalidDataException("Missing package binding.");
        binding.Validate();
        // Make a private snapshot of the protected record, not a caller's object.
        var approval = registry.WithBoundApproval(manifest,
            value => ContractJson.Read<PackageApproval>(ContractJson.Write(value)));
        var scope = store.Acquire(manifest);
        LockedPackage? package = null;
        try
        {
            scope.ValidateHeldIdentity();
            package = scope.OpenPackage(approval);
            var bytes = scope.ReadVerificationRecord();
            if (bytes.Length is 0 or > 32768 || ContractJson.Hash(bytes) != binding.VerificationRecordSha256)
                throw new InvalidDataException("Verification record does not match the run manifest.");
            var record = ContractJson.Read<PackageVerificationRecord>(bytes);
            // Forbid duplicate fields, alternate encodings and ambiguous serialization.
            if (!ContractJson.Write(record).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Verification record is not canonical.");
            var fresh = verifier.Verify(approval, package.Files);
            record.RequireMatches(manifest, approval, fresh);
            package.ValidateStreams();
            scope.ValidateHeldIdentity();
            return new Lease(registry, manifest, approval, scope, package);
        }
        catch
        {
            package?.Dispose();
            scope.Dispose();
            throw;
        }
    }

    private sealed class Lease(RunRegistry registry, RunManifest manifest, PackageApproval approval,
        IProtectedAdmissionScope scope, LockedPackage package) : IPackageAdmissionLease
    {
        private bool disposed;
        private bool committed;

        public T CommitAdmission<T>(Func<T> commit)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (committed) throw new InvalidOperationException("Admission lease was already committed.");
            // The scope keeps protected identity and file custody alive while the
            // short registry gate serializes this commit against revocation.
            scope.ValidateHeldIdentity();
            package.ValidateStreams();
            var result = registry.WithBoundApproval(manifest, current =>
            {
                if (!ContractJson.Write(current).AsSpan().SequenceEqual(ContractJson.Write(approval)))
                    throw new InvalidDataException("Approval changed before admission.");
                return commit();
            });
            committed = true;
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { package.Dispose(); }
            finally { scope.Dispose(); }
        }
    }
}

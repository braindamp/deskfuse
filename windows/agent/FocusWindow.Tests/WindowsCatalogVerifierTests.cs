using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using DeskFuse.DriverTestOwner;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class WindowsCatalogVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deskfuse-trust-test-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeBackend backend = new();
    private readonly PackageApproval approval;

    private sealed class FakeBackend : IWindowsCatalogTrustBackend
    {
        internal readonly byte[] Der = [1, 2, 3, 4, 5];
        internal bool MachineContext = true, Publisher = true, RejectCatalog, RejectMember, WrongMemberSigner;
        internal readonly List<string> Seen = [];
        public void RequireSystemContext() { if (!MachineContext) throw new UnauthorizedAccessException(); }
        public void RequireExpectedCatalog(FileStream inf, string catalogName) { }
        public VerifiedSigner VerifyCatalog(FileStream catalog, PackageProfile profile)
        {
            Seen.Add("cat:" + Path.GetFileName(catalog.Name));
            if (RejectCatalog) throw new WindowsTrustException(unchecked((int)0x800B0109), "Rejected");
            return new(Der);
        }
        public (VerifiedSigner Signer, string CatalogHash) VerifyMember(FileStream catalog, FileStream member, PackageProfile profile)
        {
            Seen.Add("member:" + Path.GetFileName(member.Name));
            if (RejectMember) throw new WindowsTrustException(unchecked((int)0x800B0100), "Rejected");
            return (new(WrongMemberSigner ? [9, 9] : Der), new string('a', 64));
        }
        public bool IsLocalMachineTrustedPublisher(byte[] certificateDer) => Publisher;
    }

    public WindowsCatalogVerifierTests()
    {
        Directory.CreateDirectory(root);
        var members = new List<PackageFile>();
        foreach (var name in new[] { "FocusIdd.inf", "FocusIdd.cat", "FocusIdd.dll" })
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, name + " test-only bytes");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(name, stream);
            members.Add(new(name, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
            stream.Position = 0;
        }
        var package = members.ToArray();
        approval = new(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(package).Digest(), package, "FocusIdd.cat", package.Single(x => x.Path.EndsWith(".cat")).Sha256,
            [ContractJson.Hash(backend.Der)], 1, "S-1-5-21-1", PackageApprovalState.BoundToRun, Guid.NewGuid());
    }

    [Fact]
    public void Matching_approval_checks_exact_catalog_then_both_members_and_retains_held_files()
    {
        var result = new WindowsCatalogVerifier(backend).Verify(approval, files);
        Assert.Equal(ContractJson.Hash(backend.Der), result.SignerCertificateDerSha256);
        Assert.Equal(new[] { "cat:FocusIdd.cat", "member:FocusIdd.dll", "member:FocusIdd.inf" }, backend.Seen);
        Assert.Equal(2, result.Members.Length);
        Assert.All(files.Values, stream => Assert.True(stream.CanRead));
    }

    [Fact]
    public void A_different_file_or_unapproved_signer_is_denied()
    {
        var wrongSigner = approval with { AllowedSignerCertificateDerSha256 = [new string('b', 64)] };
        Assert.Throws<InvalidDataException>(() => new WindowsCatalogVerifier(backend).Verify(wrongSigner, files));
        var changed = approval.Files.Select(f => f.Path == "FocusIdd.inf" ? f with { Sha256 = new string('a', 64) } : f).ToArray();
        var wrongBytes = approval with { Files = changed, PackageContentDigest = new PackageIdentity(changed).Digest() };
        backend.Seen.Clear();
        Assert.Throws<InvalidDataException>(() => new WindowsCatalogVerifier(backend).Verify(wrongBytes, files));
        Assert.Empty(backend.Seen);
    }

    [Fact]
    public void Wrong_catalog_or_member_and_wrong_verified_member_signer_never_fall_back()
    {
        var verifier = new WindowsCatalogVerifier(backend);
        backend.RejectCatalog = true;
        Assert.Throws<WindowsTrustException>(() => verifier.Verify(approval, files));
        Assert.Single(backend.Seen);
        backend.RejectCatalog = false;
        backend.RejectMember = true;
        backend.Seen.Clear();
        Assert.Throws<WindowsTrustException>(() => verifier.Verify(approval, files));
        Assert.Equal(2, backend.Seen.Count);
        backend.RejectMember = false;
        backend.WrongMemberSigner = true;
        Assert.Throws<InvalidDataException>(() => verifier.Verify(approval, files));
    }

    [Fact]
    public void Local_test_requires_machine_publisher_and_machine_context_release_never_downgrades()
    {
        var verifier = new WindowsCatalogVerifier(backend);
        backend.MachineContext = false;
        Assert.Throws<UnauthorizedAccessException>(() => verifier.Verify(approval, files));
        Assert.Empty(backend.Seen);
        backend.MachineContext = true;
        backend.Publisher = false;
        Assert.Throws<InvalidDataException>(() => verifier.Verify(approval, files));
        Assert.Single(backend.Seen);
        backend.Seen.Clear();
        verifier.Verify(approval with { Profile = PackageProfile.Release }, files);
        Assert.Equal(3, backend.Seen.Count);
        backend.RejectCatalog = true;
        Assert.Throws<WindowsTrustException>(() => verifier.Verify(approval with { Profile = PackageProfile.Release }, files));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Real_wintrust_rejects_an_unsigned_catalog_without_store_or_device_changes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var native = new NativeWindowsCatalogTrustBackend();
        var failure = Assert.Throws<WindowsTrustException>(() => native.VerifyCatalog(files["FocusIdd.cat"], PackageProfile.Release));
        Assert.NotEqual(0, failure.TrustStatus);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Real_catalog_hash_api_supports_the_documented_two_call_sizing_contract()
    {
        if (!OperatingSystem.IsWindows()) return;
        var subsystem = WindowsTrustInterop.DriverSubsystem;
        Assert.True(WindowsTrustInterop.CryptCATAdminAcquireContext2(out var admin, ref subsystem, "SHA256", nint.Zero, 0));
        try
        {
            var file = files["FocusIdd.inf"];
            file.Position = 0;
            uint length = 0;
            Assert.True(WindowsTrustInterop.CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle, ref length, null, 0));
            Assert.Equal(32u, length);
            var hash = new byte[length];
            file.Position = 0;
            Assert.True(WindowsTrustInterop.CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle, ref length, hash, 0));
            Assert.Equal(32u, length);
            Assert.Contains(hash, value => value != 0);
        }
        finally { Assert.True(WindowsTrustInterop.CryptCATAdminReleaseContext(admin, 0)); }
    }

    [Theory]
    [InlineData(0x00000040u)]
    [InlineData(0x01000000u)]
    [SupportedOSPlatform("windows")]
    public void Offline_or_unknown_non_root_revocation_evidence_is_rejected(uint error)
    {
        using var memory = new NativeAllocations();
        var signer = ChainFixture(memory, error, hasRevocationInfo: true, revocationResult: 0);
        Assert.Throws<InvalidDataException>(() => NativeWindowsCatalogTrustBackend.ValidateChainRevocation(signer));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Revocation_must_have_been_checked_and_report_success_for_non_root_certificates()
    {
        using var memory = new NativeAllocations();
        Assert.Throws<InvalidDataException>(() => NativeWindowsCatalogTrustBackend.ValidateChainRevocation(
            ChainFixture(memory, 0, hasRevocationInfo: false, revocationResult: 0)));
        Assert.Throws<InvalidDataException>(() => NativeWindowsCatalogTrustBackend.ValidateChainRevocation(
            ChainFixture(memory, 0, hasRevocationInfo: true, revocationResult: 4)));
        Assert.Equal(new byte[] { 4, 5, 6 }, NativeWindowsCatalogTrustBackend.ValidateChainRevocation(
            ChainFixture(memory, 0, hasRevocationInfo: true, revocationResult: 0)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Singleton_self_signed_root_does_not_invent_a_non_root_revocation_check()
    {
        using var memory = new NativeAllocations();
        Assert.Equal(new byte[] { 4, 5, 6 }, NativeWindowsCatalogTrustBackend.ValidateChainRevocation(
            ChainFixture(memory, 0, hasRevocationInfo: false, revocationResult: 0, singletonRoot: true)));
    }

    [SupportedOSPlatform("windows")]
    private static nint ChainFixture(NativeAllocations memory, uint leafError, bool hasRevocationInfo, uint revocationResult,
        bool singletonRoot = false)
    {
        var der = memory.Bytes([4, 5, 6]);
        var cert = memory.Structure(new WindowsTrustInterop.CertContextPrefix { Encoded = der, EncodedLength = 3 });
        var revocation = hasRevocationInfo ? memory.Structure(new WindowsTrustInterop.RevocationInfoPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.RevocationInfoPrefix>(), Result = revocationResult
        }) : nint.Zero;
        var leaf = memory.Structure(new WindowsTrustInterop.ChainElementPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.ChainElementPrefix>(), CertContext = cert,
            ErrorStatus = leafError, RevocationInfo = revocation
        });
        var root = memory.Structure(new WindowsTrustInterop.ChainElementPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.ChainElementPrefix>(), CertContext = cert,
            InfoStatus = 8
        });
        var elements = memory.Bytes(new byte[nint.Size * 2]);
        Marshal.WriteIntPtr(elements, 0, singletonRoot ? root : leaf);
        Marshal.WriteIntPtr(elements, nint.Size, root);
        var simple = memory.Structure(new WindowsTrustInterop.SimpleChainPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.SimpleChainPrefix>(), ElementCount = singletonRoot ? 1u : 2u, Elements = elements
        });
        var chains = memory.Bytes(new byte[nint.Size]);
        Marshal.WriteIntPtr(chains, simple);
        var chain = memory.Structure(new WindowsTrustInterop.ChainContextPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.ChainContextPrefix>(), ChainCount = 1, Chains = chains
        });
        return memory.Structure(new WindowsTrustInterop.ProviderSignerPrefix
        {
            Size = (uint)Marshal.SizeOf<WindowsTrustInterop.ProviderSignerPrefix>(), ChainContext = chain
        });
    }

    public void Dispose()
    {
        foreach (var stream in files.Values) stream.Dispose();
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent || !Path.GetFileName(root).StartsWith("deskfuse-trust-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

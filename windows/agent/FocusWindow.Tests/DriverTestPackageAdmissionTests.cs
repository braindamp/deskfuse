using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverTestPackageAdmissionTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);
    private static PackageFile[] Files() => [new("FocusIdd.inf", A), new("FocusIdd.cat", B), new("FocusIdd.dll", C)];

    private static PackageApproval Approval()
    {
        var files = Files();
        return new(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(files).Digest(), files, "FocusIdd.cat", B, [A], 1,
            "S-1-5-21-1", PackageApprovalState.Available, null);
    }

    [Fact]
    public void Identity_is_independent_of_order_and_filename_case_but_binds_final_catalog_bytes()
    {
        var digest = new PackageIdentity(Files()).Digest();
        Assert.Equal(digest, new PackageIdentity([new("FOCUSIDD.DLL", C), new("focusidd.INF", A), new("focusidd.CAT", B)]).Digest());
        Assert.NotEqual(digest, new PackageIdentity([new("FocusIdd.dll", C), new("FocusIdd.inf", A), new("FocusIdd.cat", A)]).Digest());
    }

    [Theory]
    [InlineData("../FocusIdd.inf")]
    [InlineData("FocusIdd.inf:stream")]
    [InlineData("C:\\FocusIdd.inf")]
    [InlineData("FocusIdd.cat")]
    [InlineData("other.inf")]
    public void Unexpected_duplicate_or_unsafe_members_are_rejected(string path)
    {
        var files = Files();
        files[0] = new(path, A);
        Assert.Throws<InvalidDataException>(() => new PackageIdentity(files).Digest());
    }

    [Fact]
    public void Approval_binds_profile_files_catalog_and_full_der_hash()
    {
        var approval = Approval();
        approval.Validate();
        Assert.Throws<InvalidDataException>(() => (approval with { Profile = PackageProfile.BuildOnly }).Validate());
        Assert.Throws<InvalidDataException>(() => (approval with { CatalogRawSha256 = A }).Validate());
        Assert.Throws<InvalidDataException>(() => (approval with { AllowedSignerCertificateDerSha256 = ["abc"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (approval with { PackageContentDigest = A }).Validate());
        Assert.Throws<InvalidDataException>(() => (approval with { State = PackageApprovalState.BoundToRun }).Validate());
    }

    [Fact]
    public void Manifest_v2_requires_a_live_profile_and_all_binding_hashes()
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(), id.ToString("N"));
        var binding = new PackageAdmissionBinding(PackageProfile.Release, Guid.NewGuid(), A, B, C);
        var manifest = new RunManifest(2, id, RunManifest.DeviceId(id), @"Root\FocusIdd", RunManifest.DisplayClass,
            A, new() { ["helper.exe"] = B }, RunManifest.WatchName(id), RunManifest.BootName(id), binding);
        manifest.Validate(directory);
        Assert.Throws<InvalidDataException>(() => (manifest with { PackageAdmission = null }).Validate(directory));
        Assert.Throws<InvalidDataException>(() => (manifest with { PackageAdmission = binding with { Profile = PackageProfile.BuildOnly } }).Validate(directory));
        Assert.Throws<InvalidDataException>(() => (manifest with { Version = 1 }).Validate(directory));
        (manifest with { Version = 1, PackageAdmission = null }).Validate(directory);
    }
}

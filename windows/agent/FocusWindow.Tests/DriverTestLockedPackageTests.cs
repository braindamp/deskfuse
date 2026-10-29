using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverTestLockedPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deskfuse-package-test-" + Guid.NewGuid().ToString("N"));
    private PackageApproval Prepare()
    {
        Directory.CreateDirectory(root);
        var names = new[] { "FocusIdd.cat", "FocusIdd.dll", "FocusIdd.inf" };
        foreach (var name in names) File.WriteAllText(Path.Combine(root, name), "package-" + name);
        var files = names.Select(name => new PackageFile(name, ContractJson.Hash(File.ReadAllBytes(Path.Combine(root, name))))).ToArray();
        return new(1, Guid.NewGuid(), "FocusIdd.Umdf", "x64", PackageProfile.LocalTest,
            new PackageIdentity(files).Digest(), files, "FocusIdd.cat", files[0].Sha256,
            [new string('a', 64)], 1, "S-1-5-21-1", PackageApprovalState.Available, null);
    }

    [Fact]
    public void Retains_read_only_exact_members_until_lease_is_disposed()
    {
        var approval = Prepare();
        using (var lease = LockedPackage.Open(root, approval))
        {
            Assert.Equal(3, lease.Files.Count);
            Assert.All(lease.Files.Values, file => { Assert.True(file.CanRead); Assert.False(file.CanWrite); });
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(root, "FocusIdd.inf"), "substituted"));
                Assert.Throws<IOException>(() => File.Delete(Path.Combine(root, "FocusIdd.cat")));
            }
        }
        File.WriteAllText(Path.Combine(root, "FocusIdd.inf"), "changed after lease");
        Assert.Throws<InvalidDataException>(() => LockedPackage.Open(root, approval));
    }

    [Fact]
    public void Extra_or_changed_payload_is_denied_and_open_handles_are_released()
    {
        var approval = Prepare();
        File.WriteAllText(Path.Combine(root, "other.dll"), "unexpected");
        Assert.Throws<InvalidDataException>(() => LockedPackage.Open(root, approval));
        File.Delete(Path.Combine(root, "other.dll"));
        File.WriteAllText(Path.Combine(root, "FocusIdd.dll"), "changed");
        Assert.Throws<InvalidDataException>(() => LockedPackage.Open(root, approval));
        File.Delete(Path.Combine(root, "FocusIdd.cat"));
    }

    [Fact]
    public void Existing_named_ntfs_stream_is_rejected_without_reading_unapproved_content()
    {
        if (!OperatingSystem.IsWindows()) return;
        var approval = Prepare();
        File.WriteAllText(Path.Combine(root, "FocusIdd.inf") + ":unexpected", "unapproved stream");
        Assert.Throws<InvalidDataException>(() => LockedPackage.Open(root, approval));
    }

    [Fact]
    public void Sharing_the_unnamed_stream_does_not_substitute_for_protected_file_acl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var approval = Prepare();
        using var lease = LockedPackage.Open(root, approval);
        lease.ValidateStreams();
        File.WriteAllText(Path.Combine(root, "FocusIdd.dll") + ":created-after-open", "unapproved stream");
        Assert.Throws<InvalidDataException>(lease.ValidateStreams);
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

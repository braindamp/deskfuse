using DeskFuse.DriverTestOwner;
using Xunit;

namespace FocusWindow.Tests;

public sealed class BundleIdentityTests
{
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["DeskFuse.DriverTestOwner.exe"] = new('a', 64),
        ["package/FocusIdd.cat"] = new('b', 64),
        ["package/FocusIdd.inf"] = new('c', 64),
        ["package/FocusIdd.dll"] = new('d', 64)
    };

    [Fact]
    public void Production_profile_requires_the_fixed_helper_entrypoint_and_dependencies()
    {
        var packageOnly = Expected.Where(entry => entry.Key.StartsWith("package/"))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireHelperProfile(packageOnly));
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireHelperProfile(Expected));
        var complete = new Dictionary<string, string>(Expected)
        {
            ["DeskFuse.DriverTestOwner.dll"] = new('a', 64),
            ["DeskFuse.DriverTestOwner.Core.dll"] = new('a', 64),
            ["DeskFuse.WindowsSecurity.dll"] = new('a', 64),
            ["DeskFuse.DriverTestOwner.deps.json"] = new('a', 64),
            ["DeskFuse.DriverTestOwner.runtimeconfig.json"] = new('a', 64)
        };
        BundleIdentity.RequireHelperProfile(complete);
        foreach (var file in complete.Keys.Where(key => !key.StartsWith("package/")).ToArray())
        {
            var missing = new Dictionary<string, string>(complete);
            missing.Remove(file);
            Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireHelperProfile(missing));
        }
    }

    [Fact]
    public void File_set_binds_every_helper_and_package_file_independent_of_enumeration_order()
    {
        BundleIdentity.RequireExactFiles(Expected, Expected.Keys.Reverse().Select(s => s.ToUpperInvariant()));
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireExactFiles(Expected, Expected.Keys.Skip(1)));
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireExactFiles(Expected, Expected.Keys.Append("injected.dll")));
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireExactFiles(Expected, Expected.Keys.Append("PACKAGE/FOCUSIDD.DLL")));
    }

    [Theory]
    [InlineData("../helper.exe")]
    [InlineData("helper.exe:alternate")]
    [InlineData("C:\\helper.exe")]
    [InlineData("helper.exe.")]
    [InlineData("helper.exe ")]
    [InlineData("dir//helper.exe")]
    public void Ambiguous_and_unsafe_manifest_paths_are_rejected(string path)
    {
        var expected = new Dictionary<string, string> { [path] = new('a', 64) };
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireExactFiles(expected, [path]));
    }

    [Fact]
    public void Slash_and_case_aliases_do_not_create_two_independently_authorized_files()
    {
        var duplicate = new Dictionary<string, string>(Expected)
        {
            ["package\\FOCUSIDD.dll"] = new('d', 64)
        };
        Assert.Throws<InvalidDataException>(() => BundleIdentity.RequireExactFiles(duplicate, duplicate.Keys));
    }
}

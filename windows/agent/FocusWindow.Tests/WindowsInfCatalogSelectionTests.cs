using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner;
using Xunit;

namespace FocusWindow.Tests;

public sealed class WindowsInfCatalogSelectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deskfuse-inf-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("CatalogFile=FocusIdd.cat", "FocusIdd.cat")]
    [InlineData("CatalogFile=FocusIdd.cat\nCatalogFile.NTx86=other.cat", "FocusIdd.cat")]
    [InlineData("CatalogFile=FocusIdd.cat\nCatalogFile.NTamd64=other.cat", "other.cat")]
    [SupportedOSPlatform("windows")]
    public void SetupAPI_resolves_the_catalog_for_the_actual_target(string catalogLines, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "FocusIdd.inf");
        File.WriteAllText(path, "[Version]\nSignature=\"$Windows NT$\"\nClass=Display\n" +
            "ClassGuid={4D36E968-E325-11CE-BFC1-08002BE10318}\nProvider=Example\nDriverVer=01/01/2025,1.0.0.0\n" + catalogLines + "\n");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(expected, WindowsInfCatalogSelection.SelectedCatalog(stream), ignoreCase: true);
        var native = new NativeWindowsCatalogTrustBackend();
        if (string.Equals(expected, "FocusIdd.cat", StringComparison.OrdinalIgnoreCase))
            native.RequireExpectedCatalog(stream, "FocusIdd.cat");
        else
            Assert.Throws<InvalidDataException>(() => native.RequireExpectedCatalog(stream, "FocusIdd.cat"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Missing_catalog_is_denied()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "FocusIdd.inf");
        File.WriteAllText(path, "[Version]\nSignature=\"$Windows NT$\"\nClass=Display\nProvider=Example\nDriverVer=01/01/2025,1.0.0.0\n");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsAny<Exception>(() => WindowsInfCatalogSelection.SelectedCatalog(stream));
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

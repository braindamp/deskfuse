using DeskFuse.DriverTestOwner;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverTestSdkFactAttribute : FactAttribute
{
    public DriverTestSdkFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKFUSE_DRIVER_TEST_ABI_FILE")) &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Skip = "Run tools/build-native-contract.ps1 with MSVC x64 to supply real SDK evidence.";
    }
}

public sealed class DriverTestInteropTests
{
    [DriverTestSdkFact]
    public void SetupAPI_declarations_match_the_actual_MSVC_x64_SDK_fixture()
    {
        var path = Environment.GetEnvironmentVariable("DESKFUSE_DRIVER_TEST_ABI_FILE");
        Assert.False(string.IsNullOrEmpty(path), "CI must supply the native x64 ABI fixture.");
        InteropContract.Validate(File.ReadAllBytes(path!));
    }
}

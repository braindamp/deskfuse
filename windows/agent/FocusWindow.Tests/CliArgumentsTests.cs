using FocusWindow.Core;
using Xunit;

namespace FocusWindow.Tests;

public class CliArgumentsTests
{
    [Fact]
    public void Workspace_args_accepts_value_starting_with_double_dash()
    {
        var args = new[]
        {
            "--workspace-app", @"C:\Apps\App.exe",
            "--workspace-args", "--new-window",
            "--fps", "60",
        };

        Assert.Equal(@"C:\Apps\App.exe", CliArguments.Value(args, "--workspace-app"));
        Assert.Equal("--new-window", CliArguments.Value(args, "--workspace-args"));
        Assert.Equal(60, CliArguments.Int(args, "--fps", 30));
    }

    [Fact]
    public void Missing_value_stops_at_the_next_known_option()
    {
        var args = new[] { "--workspace-args", "--fps", "60" };

        Assert.Null(CliArguments.Value(args, "--workspace-args"));
        Assert.Equal("60", CliArguments.Value(args, "--fps"));
    }

    [Fact]
    public void Equals_form_accepts_a_value_that_is_itself_a_switch()
    {
        var args = new[] { "--workspace-args=--version" };

        Assert.Equal("--version", CliArguments.Value(args, "--workspace-args"));
    }

    [Fact]
    public void Missing_final_value_returns_null_and_numeric_fallbacks_apply()
    {
        var args = new[] { "--fps" };

        Assert.Null(CliArguments.Value(args, "--fps"));
        Assert.Equal(30, CliArguments.Int(args, "--fps", 30));
        Assert.Equal(2.5, CliArguments.Double(args, "--dpr", 2.5));
    }
}

using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T15 — tests for <see cref="AdminElevationMessage"/>, the copy-paste PowerShell
/// admin-elevation error block (design §3).
///
/// Newline choice: <see cref="AdminElevationMessage.Build"/> joins lines with a
/// literal "\n" so these tests can split / assert on individual lines identically
/// on every OS (no Environment.NewLine ambiguity).
/// </summary>
public class AdminElevationMessageTests
{
    private const string InstallConfigPath = @"C:\ProgramData\FreeBird\config.json";

    [Fact]
    public void Build_install_ContainsExactErrorLine()
    {
        var result = AdminElevationMessage.Build("install", InstallConfigPath);

        result.Should().Contain("ERROR: 'fb service install' requires Administrator privileges.");
    }

    [Fact]
    public void Build_install_ContainsExactElevatedPowershellLine()
    {
        var result = AdminElevationMessage.Build("install", InstallConfigPath);

        result.Should().Contain("Run this in an elevated PowerShell:");
    }

    [Fact]
    public void Build_install_ContainsExactStartProcessLineWithConfig()
    {
        var result = AdminElevationMessage.Build("install", InstallConfigPath);

        result.Should().Contain(
            @"  Start-Process fb -ArgumentList 'service install --config C:\ProgramData\FreeBird\config.json' -Verb RunAs");
    }

    [Fact]
    public void Build_install_DoesNotDoubleEscapeQuotes()
    {
        var result = AdminElevationMessage.Build("install", InstallConfigPath);

        result.Should().NotContain("''");
        result.Should().NotContain(@"\'");
    }

    [Fact]
    public void Build_uninstall_NoConfig_OmitsConfigPortion()
    {
        var result = AdminElevationMessage.Build("uninstall", null);

        result.Should().Contain(@"  Start-Process fb -ArgumentList 'service uninstall' -Verb RunAs");
        result.Should().NotContain("--config");
    }

    [Fact]
    public void Build_start_DefaultConfigPath_OmitsConfigPortion()
    {
        var result = AdminElevationMessage.Build("start");

        result.Should().Contain(@"  Start-Process fb -ArgumentList 'service start' -Verb RunAs");
        result.Should().NotContain("--config");
    }

    [Fact]
    public void Build_uninstall_InterpolatesSubcommandIntoErrorLine()
    {
        var result = AdminElevationMessage.Build("uninstall", null);

        result.Should().Contain("ERROR: 'fb service uninstall' requires Administrator privileges.");
    }
}

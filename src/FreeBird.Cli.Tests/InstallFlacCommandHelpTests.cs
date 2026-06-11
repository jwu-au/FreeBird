using System.Linq;
using FluentAssertions;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T14 — structural assertions on the install-flac subcommand: name, option set,
/// hidden URL flag, and wiring into <see cref="CliRoot"/>.
/// </summary>
public class InstallFlacCommandHelpTests
{
    [Fact]
    public void Build_HasName_InstallFlac()
    {
        var cmd = InstallFlacCommand.Build();
        cmd.Name.Should().Be("install-flac");
    }

    [Fact]
    public void Build_HasTargetAndUrlOptions()
    {
        var cmd = InstallFlacCommand.Build();
        cmd.Options.Should().Contain(o => o.Name == "--target");
        cmd.Options.Should().Contain(o => o.Name == "--url");
    }

    [Fact]
    public void Build_UrlOption_IsHidden()
    {
        var cmd = InstallFlacCommand.Build();
        var url = cmd.Options.First(o => o.Name == "--url");
        url.Hidden.Should().BeTrue();
    }

    [Fact]
    public void CliRoot_IncludesInstallFlacSubcommand()
    {
        var root = CliRoot.Build();
        root.Subcommands.Should().Contain(c => c.Name == "install-flac");
    }
}

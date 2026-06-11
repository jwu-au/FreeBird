using System.CommandLine;
using System.Linq;
using FluentAssertions;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T11 — asserts the v3.1 flac provisioning flags are present on both `scan` and `watch`,
/// and that `--flac-url` is marked Hidden (so it does not show in --help output).
/// </summary>
public class CliRootHelpTests
{
    private static Command GetSubcommand(string name)
    {
        var root = CliRoot.Build();
        return root.Subcommands.First(c => c.Name == name);
    }

    private static Option? FindOption(Command cmd, string optName)
        => cmd.Options.FirstOrDefault(o => o.Name == optName);

    [Theory]
    [InlineData("scan")]
    [InlineData("watch")]
    public void Subcommand_Has_FlacBin_Option_Visible(string subcommand)
    {
        var cmd = GetSubcommand(subcommand);
        var opt = FindOption(cmd, "--flac-bin");
        opt.Should().NotBeNull("--flac-bin must be present on " + subcommand);
        opt!.Hidden.Should().BeFalse("--flac-bin is a user-facing flag");
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("watch")]
    public void Subcommand_Has_NoAutoDownload_Option_Visible(string subcommand)
    {
        var cmd = GetSubcommand(subcommand);
        var opt = FindOption(cmd, "--no-auto-download");
        opt.Should().NotBeNull("--no-auto-download must be present on " + subcommand);
        opt!.Hidden.Should().BeFalse("--no-auto-download is a user-facing flag");
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("watch")]
    public void Subcommand_Has_FlacUrl_Option_Hidden(string subcommand)
    {
        var cmd = GetSubcommand(subcommand);
        var opt = FindOption(cmd, "--flac-url");
        opt.Should().NotBeNull("--flac-url must exist on " + subcommand + " (advanced override)");
        opt!.Hidden.Should().BeTrue("--flac-url is advanced/internal and must not appear in --help");
    }
}

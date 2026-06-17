using System;
using System.Linq;
using System.Reflection;
using System.CommandLine;
using FluentAssertions;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T22 — asserts the <c>service</c> command tree is wired inline in <see cref="CliRoot.Build"/>:
/// 7 visible leaves (init/install/uninstall/start/stop/restart/status) + 1 hidden <c>run</c>.
/// Also guards the additive contract: scan/watch/install-flac stay present and CliRoot exposes
/// no Autofac <c>IContainer</c> field (no Build(IContainer) seam).
/// </summary>
public class ServiceCommandTreeTests
{
    private static Command GetServiceCommand()
    {
        var root = CliRoot.Build();
        return root.Subcommands.First(c => c.Name == "service");
    }

    [Fact]
    public void Service_HasExactly_SevenVisibleLeaves()
    {
        var service = GetServiceCommand();

        var visible = service.Subcommands.Where(c => !c.Hidden).Select(c => c.Name).ToHashSet();

        visible.Should().BeEquivalentTo(new[]
        {
            "init", "install", "uninstall", "start", "stop", "restart", "status",
        });
        visible.Count.Should().Be(7);
    }

    [Fact]
    public void Service_Run_Exists_And_IsHidden()
    {
        var service = GetServiceCommand();

        var run = service.Subcommands.FirstOrDefault(c => c.Name == "run");

        run.Should().NotBeNull("the SCM entrypoint 'run' must be registered even though hidden");
        run!.Hidden.Should().BeTrue("'run' is the SCM-only entrypoint and must not appear in --help");
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("watch")]
    [InlineData("install-flac")]
    public void Root_StillHas_ExistingSubcommands(string name)
    {
        var root = CliRoot.Build();

        root.Subcommands.Should().Contain(c => c.Name == name,
            $"T22 must be additive — '{name}' must remain on the root command");
    }

    [Fact]
    public void CliRoot_HasNo_AutofacContainer_Field()
    {
        var fields = typeof(CliRoot).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        fields.Any(f => f.FieldType.Name.Contains("IContainer"))
            .Should().BeFalse("CliRoot must not hold an Autofac IContainer field (no Build(IContainer) seam)");
    }
}

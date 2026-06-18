using System;
using System.IO;
using FluentAssertions;
using FreeBird.Cli.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// Regression tests for <see cref="ServiceConfigPath.Resolve"/>.
///
/// <para>Bug (v3.5.0): <c>fb service install</c> reported
/// "Config file not found: %ProgramData%\FreeBird\config.json" — the literal
/// <c>%ProgramData%</c> token from <see cref="FreeBird.Core.Service.ConfigDefaults.DefaultConfigPath"/>
/// was passed straight to the existence check / SCM BinPath / RunCommand loader
/// without ever being expanded. Only <c>fb service init</c> happened to work
/// because it expanded the default itself.</para>
///
/// <para>The fix funnels every config-path consumer through this helper, which
/// (1) returns the expanded ProgramData default for a null/empty request and
/// (2) expands environment-variable tokens (<c>%ProgramData%</c>,
/// <c>%USERPROFILE%</c>, …) in any supplied path. A path with no tokens is
/// returned unchanged.</para>
///
/// <para>These tests avoid relying on <c>%ProgramData%</c> (which only expands on
/// Windows) by using a custom env var that exists on every OS, so they are
/// deterministic on macOS / Linux / Windows.</para>
/// </summary>
public class ServiceConfigPathTests
{
    [Fact]
    public void Resolve_NullRequest_ReturnsExpandedProgramDataDefault()
    {
        var result = ServiceConfigPath.Resolve(null);

        result.Should().NotContain("%", "the default must be expanded, never a literal token");
        result.Should().EndWith(Path.Combine("FreeBird", "config.json"));
        Path.IsPathRooted(result).Should().BeTrue("the resolved default must be an absolute path");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyOrWhitespaceRequest_ReturnsExpandedProgramDataDefault(string request)
    {
        var result = ServiceConfigPath.Resolve(request);

        result.Should().NotContain("%");
        result.Should().EndWith(Path.Combine("FreeBird", "config.json"));
        Path.IsPathRooted(result).Should().BeTrue();
    }

    [Fact]
    public void Resolve_RequestWithEnvToken_ExpandsTheToken()
    {
        const string varName = "FB_TEST_CONFIG_ROOT";
        var rootValue = Path.Combine(Path.GetTempPath(), "fb-cfg-root");
        Environment.SetEnvironmentVariable(varName, rootValue);
        try
        {
            var result = ServiceConfigPath.Resolve($"%{varName}%/FreeBird/config.json");

            result.Should().NotContain("%", "the token must be expanded");
            result.Should().StartWith(rootValue);
            result.Should().Contain("config.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Resolve_RequestWithoutToken_ReturnsPathUnchanged()
    {
        var plain = Path.Combine(Path.GetTempPath(), "custom", "myconfig.json");

        var result = ServiceConfigPath.Resolve(plain);

        result.Should().Be(plain, "a path with no env tokens must pass through verbatim");
    }

    [Fact]
    public void Resolve_UnknownToken_LeftAsIs_ButDoesNotThrow()
    {
        // Environment.ExpandEnvironmentVariables leaves unknown %TOKEN% untouched;
        // we only assert it does not throw and returns a non-null value.
        var result = ServiceConfigPath.Resolve("%FB_DEFINITELY_NOT_SET_98765%/config.json");

        result.Should().NotBeNull();
        result.Should().Contain("config.json");
    }
}

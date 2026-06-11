using FluentAssertions;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests;

public class FlacOptionsBinderTests
{
    [Fact]
    public void Resolve_AllDefaults_ReturnsNullsAndFalse()
    {
        var result = FlacOptionsBinder.Resolve(null, null, false, null, null);
        result.FlacBinOverride.Should().BeNull();
        result.AutoInstallUrl.Should().BeNull();
        result.DisableAutoInstall.Should().BeFalse();
    }

    [Fact]
    public void Resolve_FlacBinFlag_TakesPrecedence()
    {
        var result = FlacOptionsBinder.Resolve("/custom/flac", null, false, null, null);
        result.FlacBinOverride.Should().Be("/custom/flac");
    }

    [Fact]
    public void Resolve_FlacBinFlag_EmptyString_TreatedAsNull()
    {
        var result = FlacOptionsBinder.Resolve("  ", null, false, null, null);
        result.FlacBinOverride.Should().BeNull();
    }

    [Fact]
    public void Resolve_FlacUrlFlag_WinsOverEnv()
    {
        var result = FlacOptionsBinder.Resolve(null, "https://flag.example/flac.zip", false, "https://env.example/flac.zip", null);
        result.AutoInstallUrl.Should().Be("https://flag.example/flac.zip");
    }

    [Fact]
    public void Resolve_FlacUrlEnvOnly_UsedAsFallback()
    {
        var result = FlacOptionsBinder.Resolve(null, null, false, "https://env.example/flac.zip", null);
        result.AutoInstallUrl.Should().Be("https://env.example/flac.zip");
    }

    [Fact]
    public void Resolve_FlacUrlFlagEmpty_FallsBackToEnv()
    {
        var result = FlacOptionsBinder.Resolve(null, "  ", false, "https://env.example/flac.zip", null);
        result.AutoInstallUrl.Should().Be("https://env.example/flac.zip");
    }

    [Fact]
    public void Resolve_NoAutoDownloadFlag_True()
    {
        var result = FlacOptionsBinder.Resolve(null, null, true, null, null);
        result.DisableAutoInstall.Should().BeTrue();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("YES")]
    public void Resolve_NoAutoDownloadEnv_VariousTruthyValues_True(string env)
    {
        var result = FlacOptionsBinder.Resolve(null, null, false, null, env);
        result.DisableAutoInstall.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Resolve_NoAutoDownloadEnv_FalsyOrUnknown_False(string? env)
    {
        var result = FlacOptionsBinder.Resolve(null, null, false, null, env);
        result.DisableAutoInstall.Should().BeFalse();
    }

    [Fact]
    public void Resolve_FlagAndEnv_Both_Combined_FlagOrEnv()
    {
        // Flag true + env false → true (flag wins via OR)
        FlacOptionsBinder.Resolve(null, null, true, null, "0").DisableAutoInstall.Should().BeTrue();
        // Flag false + env true → true (env wins via OR)
        FlacOptionsBinder.Resolve(null, null, false, null, "1").DisableAutoInstall.Should().BeTrue();
    }
}

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

    // --- platform default URL fallback (the bug fix) ---
    // The binder is platform-agnostic: callers pass the platform-appropriate default URL
    // (Windows -> the pinned Xiph ZIP; macOS/Linux -> null). When the user supplies no
    // --flac-url and no FREEBIRD_FLAC_URL, the binder falls back to that default so that
    // `scan`/`watch` trigger the Windows auto-installer just like `install-flac` does.

    [Fact]
    public void Resolve_NoUrl_WithDefaultUrl_FallsBackToDefault()
    {
        // Simulates Windows: caller passes the pinned default URL.
        var result = FlacOptionsBinder.Resolve(
            null, null, false, null, null, defaultUrl: InstallFlacRunner.DefaultUrl);
        result.AutoInstallUrl.Should().Be(InstallFlacRunner.DefaultUrl);
    }

    [Fact]
    public void Resolve_NoUrl_NullDefaultUrl_StaysNull()
    {
        // Simulates macOS/Linux: caller passes null default (auto-install is NoOp there).
        var result = FlacOptionsBinder.Resolve(
            null, null, false, null, null, defaultUrl: null);
        result.AutoInstallUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExplicitUrlFlag_OverridesDefaultUrl()
    {
        var result = FlacOptionsBinder.Resolve(
            null, "https://flag.example/flac.zip", false, null, null,
            defaultUrl: InstallFlacRunner.DefaultUrl);
        result.AutoInstallUrl.Should().Be("https://flag.example/flac.zip");
    }

    [Fact]
    public void Resolve_EnvUrl_OverridesDefaultUrl()
    {
        var result = FlacOptionsBinder.Resolve(
            null, null, false, "https://env.example/flac.zip", null,
            defaultUrl: InstallFlacRunner.DefaultUrl);
        result.AutoInstallUrl.Should().Be("https://env.example/flac.zip");
    }

    [Fact]
    public void Resolve_NoAutoDownload_StillRecordsDefaultUrl_ButInstallDisabled()
    {
        // The default URL is still surfaced, but DisableAutoInstall short-circuits the
        // resolver before it is ever used — so --no-auto-download wins regardless.
        var result = FlacOptionsBinder.Resolve(
            null, null, true, null, null, defaultUrl: InstallFlacRunner.DefaultUrl);
        result.DisableAutoInstall.Should().BeTrue();
    }
}

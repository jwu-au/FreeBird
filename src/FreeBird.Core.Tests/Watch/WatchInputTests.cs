using System;
using System.IO;
using FluentAssertions;
using FreeBird.Core.Watch;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchInput"/> — the per-directory watch descriptor used by
/// the v3.4 multi-input watch supervisor for log enrichment (BaseName) and
/// directory identity (Path).
/// </summary>
public sealed class WatchInputTests
{
    [Fact]
    public void FromPath_WithSimpleDir_ExtractsBaseName()
    {
        var input = WatchInput.FromPath(Path.Combine(Path.GetTempPath(), "cache1"));

        input.BaseName.Should().Be("cache1");
    }

    [Fact]
    public void FromPath_WithTrailingSlash_StripsAndExtractsBaseName()
    {
        var withTrailing = Path.Combine(Path.GetTempPath(), "cache1") + Path.DirectorySeparatorChar;

        var input = WatchInput.FromPath(withTrailing);

        input.BaseName.Should().Be("cache1");
    }

    [Fact]
    public void FromPath_WithRelativePath_ResolvesToAbsolute()
    {
        var input = WatchInput.FromPath("./foo");

        input.Path.Should().StartWith(Directory.GetCurrentDirectory());
        Path.IsPathRooted(input.Path).Should().BeTrue();
    }

    [Fact]
    public void FromPath_WithRootPath_UsesRootSentinel()
    {
        var root = Path.DirectorySeparatorChar.ToString();

        var input = WatchInput.FromPath(root);

        input.BaseName.Should().Be("<root>");
    }

    [Fact]
    public void FromPath_WithEmptyString_Throws()
    {
        var act = () => WatchInput.FromPath(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromPath_WithNull_Throws()
    {
        // ArgumentNullException is a subclass of ArgumentException; assert the base type
        // to keep the test robust to ArgumentException.ThrowIfNullOrEmpty's exact choice.
        var act = () => WatchInput.FromPath(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromPath_DotComponents_Resolved()
    {
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var withDots = Path.Combine(tmp, "a", "..", "b");

        var input = WatchInput.FromPath(withDots);

        var expected = Path.Combine(tmp, "b");
        input.Path.Should().Be(expected);
    }

    [Fact]
    public void WatchInputs_WithSamePath_AreEqual()
    {
        var a = new WatchInput { Path = "/tmp/cache1", BaseName = "cache1" };
        var b = new WatchInput { Path = "/tmp/cache1", BaseName = "cache1" };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void WatchTaskState_AllValues_Present()
    {
        var values = Enum.GetValues<WatchTaskState>();

        values.Should().Contain(new[]
        {
            WatchTaskState.Initializing,
            WatchTaskState.Active,
            WatchTaskState.Dead,
            WatchTaskState.Resurrecting,
        });
    }
}

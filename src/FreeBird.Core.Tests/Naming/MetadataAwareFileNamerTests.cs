using System;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;

namespace FreeBird.Core.Tests.Naming;

public sealed class MetadataAwareFileNamerTests
{
    // ---- helpers ----------------------------------------------------------

    private static IMetadataOptions OptsWithTemplate(string template) =>
        new ScanOptions("in", "out") { NamingTemplate = template };

    private static MetadataAwareFileNamer Build(string template = "{artist} - {title}") =>
        new(new NamingTemplateRenderer(), OptsWithTemplate(template));

    // ---- tests ------------------------------------------------------------

    [Fact]
    public void WithSongInfo_DefaultTemplate_RendersArtistDashTitle()
    {
        var namer = Build();
        var meta = new SongInfo(3367798042L, "终不负", new[] { "树离suliii_" });

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, meta);

        name.Should().Be("树离suliii_ - 终不负.flac");
    }

    [Fact]
    public void WithNullMetadata_UsesMusicIdFallback()
    {
        // Use a non-default template to prove the template is IGNORED on fallback.
        var namer = Build("{artist} - {title}");

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, metadata: null);

        name.Should().Be("3367798042.flac");
    }

    [Fact]
    public void CustomTemplate_RespectedWhenMetadataPresent()
    {
        // Template intentionally contains a "/" which the sanitizer must convert to
        // full-width U+FF0F to keep the filename single-segment + safe.
        var namer = Build("{album}/{title}");
        var meta = new SongInfo(123L, "title", new[] { "a" }, Album: "album-name");

        var name = namer.GetTargetName("/cache/123.uc", AudioFormat.Flac, meta);

        // U+FF0F = '／'
        name.Should().Be("album-name／title.flac");
    }

    [Fact]
    public void Sanitization_AppliedToFinalName()
    {
        var namer = Build("{title}");
        var meta = new SongInfo(123L, "A/B", new[] { "x" });

        var name = namer.GetTargetName("/cache/123.uc", AudioFormat.Mp3, meta);

        // '/' → full-width '／', extension appended.
        name.Should().Be("A／B.mp3");
    }

    [Theory]
    [InlineData(AudioFormat.Flac, ".flac")]
    [InlineData(AudioFormat.Mp3, ".mp3")]
    [InlineData(AudioFormat.M4a, ".m4a")]
    public void Extension_FromAudioFormat_Enum(AudioFormat format, string expectedExt)
    {
        var namer = Build("{title}");
        var meta = new SongInfo(1L, "t", new[] { "a" });

        var name = namer.GetTargetName("/cache/1.uc", format, meta);

        name.Should().EndWith(expectedExt);
    }

    [Fact]
    public void UnknownFormat_Throws()
    {
        var namer = Build();
        var meta = new SongInfo(1L, "t", new[] { "a" });

        var act = () => namer.GetTargetName("/cache/1.uc", AudioFormat.Unknown, meta);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MultiArtist_FilenameContext_JoinedWithSpaceAmpSpace()
    {
        var namer = Build("{artist}");
        var meta = new SongInfo(1L, "t", new[] { "A", "B" });

        var name = namer.GetTargetName("/cache/1.uc", AudioFormat.Flac, meta);

        name.Should().Be("A & B.flac");
    }

    [Fact]
    public void MusicId_ParsedFrom_BareStem()
    {
        var namer = Build("{musicId}");
        var meta = new SongInfo(0L, "t", new[] { "a" });   // SongInfo.MusicId unused on render side

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, meta);

        name.Should().Be("3367798042.flac");
    }

    [Fact]
    public void MusicId_ParsedFrom_CompositeStem()
    {
        // Real-world cache filename shape: <musicId>-_-_<bitrate>-_-_<other>.uc
        var namer = Build("{musicId}");
        var meta = new SongInfo(0L, "t", new[] { "a" });

        var name = namer.GetTargetName("/cache/3367798042-_-_5999-_-_xxx.uc", AudioFormat.Mp3, meta);

        name.Should().Be("3367798042.mp3");
    }

    [Fact]
    public void NonNumericStem_FallsBackToZero_OnNullMetadata()
    {
        // Defensive: a path without leading digits still produces a deterministic name.
        var namer = Build();

        var name = namer.GetTargetName("/cache/not-numeric.uc", AudioFormat.Flac, metadata: null);

        name.Should().Be("0.flac");
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependency()
    {
        var renderer = new NamingTemplateRenderer();
        var opts = OptsWithTemplate("{title}");

        ((Action)(() => new MetadataAwareFileNamer(null!, opts))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new MetadataAwareFileNamer(renderer, null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EmptyOrWhitespaceSourcePath_Throws()
    {
        var namer = Build();
        var meta = new SongInfo(1L, "t", new[] { "a" });

        ((Action)(() => namer.GetTargetName("", AudioFormat.Flac, meta))).Should().Throw<ArgumentException>();
        ((Action)(() => namer.GetTargetName("   ", AudioFormat.Flac, meta))).Should().Throw<ArgumentException>();
    }
}

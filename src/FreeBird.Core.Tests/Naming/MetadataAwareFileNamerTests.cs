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
    //
    // v3 T19a: MetadataAwareFileNamer no longer takes IMetadataOptions in its ctor.
    // The per-call template is passed as a method parameter to GetTargetName; tests
    // that want a non-default template thread it through the call site instead.

    private static MetadataAwareFileNamer Build() =>
        new(new NamingTemplateRenderer());

    // Default template used by callers when no per-run template is supplied.
    private const string DefaultTemplate = "{artist} - {title}";

    // ---- tests ------------------------------------------------------------

    [Fact]
    public void WithSongInfo_DefaultTemplate_RendersArtistDashTitle()
    {
        var namer = Build();
        var meta = new SongInfo(3367798042L, "终不负", new[] { "树离suliii_" });

        // null template => default ({artist} - {title})
        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, meta);

        name.Should().Be("树离suliii_ - 终不负.flac");
    }

    [Fact]
    public void WithNullMetadata_UsesMusicIdFallback()
    {
        var namer = Build();

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, metadata: null);

        name.Should().Be("3367798042.flac");
    }

    [Fact]
    public void CustomTemplate_RespectedWhenMetadataPresent()
    {
        // Template intentionally contains a "/" which the sanitizer must convert to
        // full-width U+FF0F to keep the filename single-segment + safe.
        var namer = Build();
        var meta = new SongInfo(123L, "title", new[] { "a" }, Album: "album-name");

        var name = namer.GetTargetName("/cache/123.uc", AudioFormat.Flac, meta, "{album}/{title}");

        // U+FF0F = '／'
        name.Should().Be("album-name／title.flac");
    }

    [Fact]
    public void Sanitization_AppliedToFinalName()
    {
        var namer = Build();
        var meta = new SongInfo(123L, "A/B", new[] { "x" });

        var name = namer.GetTargetName("/cache/123.uc", AudioFormat.Mp3, meta, "{title}");

        // '/' → full-width '／', extension appended.
        name.Should().Be("A／B.mp3");
    }

    [Theory]
    [InlineData(AudioFormat.Flac, ".flac")]
    [InlineData(AudioFormat.Mp3, ".mp3")]
    [InlineData(AudioFormat.M4a, ".m4a")]
    public void Extension_FromAudioFormat_Enum(AudioFormat format, string expectedExt)
    {
        var namer = Build();
        var meta = new SongInfo(1L, "t", new[] { "a" });

        var name = namer.GetTargetName("/cache/1.uc", format, meta, "{title}");

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
        var namer = Build();
        var meta = new SongInfo(1L, "t", new[] { "A", "B" });

        var name = namer.GetTargetName("/cache/1.uc", AudioFormat.Flac, meta, "{artist}");

        name.Should().Be("A & B.flac");
    }

    [Fact]
    public void MusicId_ParsedFrom_BareStem()
    {
        var namer = Build();
        var meta = new SongInfo(0L, "t", new[] { "a" });   // SongInfo.MusicId unused on render side

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Flac, meta, "{musicId}");

        name.Should().Be("3367798042.flac");
    }

    [Fact]
    public void MusicId_ParsedFrom_CompositeStem()
    {
        // Real-world cache filename shape: <musicId>-_-_<bitrate>-_-_<other>.uc
        var namer = Build();
        var meta = new SongInfo(0L, "t", new[] { "a" });

        var name = namer.GetTargetName("/cache/3367798042-_-_5999-_-_xxx.uc", AudioFormat.Mp3, meta, "{musicId}");

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
        ((Action)(() => new MetadataAwareFileNamer(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EmptyOrWhitespaceSourcePath_Throws()
    {
        var namer = Build();
        var meta = new SongInfo(1L, "t", new[] { "a" });

        ((Action)(() => namer.GetTargetName("", AudioFormat.Flac, meta))).Should().Throw<ArgumentException>();
        ((Action)(() => namer.GetTargetName("   ", AudioFormat.Flac, meta))).Should().Throw<ArgumentException>();
    }

    // -------------------------------------------------------------------------
    // v3 T19a: per-run template threading via method parameter
    // -------------------------------------------------------------------------

    [Fact]
    public void GetTargetName_WithCustomTemplate_UsesProvidedTemplate()
    {
        // The custom template wins over the default; tokens are rendered verbatim
        // and the sanitizer only touches segment-illegal characters (musicId stays).
        var namer = Build();
        var meta = new SongInfo(42L, "My Title", new[] { "A" });

        var name = namer.GetTargetName("/cache/42.uc", AudioFormat.Flac, meta, "{title} [{musicId}]");

        name.Should().Be("My Title [42].flac");
    }

    [Fact]
    public void GetTargetName_TemplateNull_FallsBackToDefault()
    {
        // null namingTemplate => the implementation MUST use its own default,
        // which is "{artist} - {title}" per the IFileNamer XML doc.
        var namer = Build();
        var meta = new SongInfo(42L, "T", new[] { "A" });

        var name = namer.GetTargetName("/cache/42.uc", AudioFormat.Flac, meta, namingTemplate: null);

        name.Should().Be(DefaultTemplate
            .Replace("{artist}", "A")
            .Replace("{title}", "T") + ".flac");
    }

    [Fact]
    public void GetTargetName_TemplateIgnoredOnFallback()
    {
        // Spec §10: when metadata is null the output is always {musicId}.{ext}
        // regardless of which template the caller passed.
        var namer = Build();

        var name = namer.GetTargetName("/cache/3367798042.uc", AudioFormat.Mp3, metadata: null, namingTemplate: "{album}|{title}");

        name.Should().Be("3367798042.mp3");
    }
}

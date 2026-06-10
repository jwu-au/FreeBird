using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Naming;

namespace FreeBird.Core.Tests.Naming;

/// <summary>
/// Tests for <see cref="NamingTemplateRenderer"/>.
///
/// Pinned behaviors per design spec §6:
/// - Pure literal substitution of known tokens: {artist}, {title}, {album}, {musicId}.
/// - Unknown tokens (e.g. {foo}, {ARTIST}, {artist_name}) pass through unchanged.
/// - Empty field values produce 'unknown-*' fallbacks (except musicId which is always present).
/// - musicId argument is required even when song is null (caller always knows it from the source filename).
/// - Single-pass regex substitution: a value that contains a token-like substring is NOT re-substituted.
/// - No escape syntax: {{artist}} renders as {<rendered-artist>} — outer braces remain literal.
/// </summary>
public class NamingTemplateRendererTests
{
    private static readonly NamingTemplateRenderer Renderer = new();

    [Fact]
    public void Render_DefaultTemplate_RealSong1_Cn()
    {
        var song = new SongInfo(3367798042L, "终不负", new[] { "树离suliii_" }, "不必完美");
        Renderer.Render(NamingTemplateRenderer.DefaultTemplate, song, song.MusicId)
            .Should().Be("树离suliii_ - 终不负");
    }

    [Fact]
    public void Render_CustomAlbumTitleTemplate_RendersBothTokens()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, "My Album");
        Renderer.Render("{album}/{title}", song, song.MusicId)
            .Should().Be("My Album/T");
    }

    [Fact]
    public void Render_NullSong_AllStringTokensFallback_MusicIdFromArg()
    {
        // When song is null, artist/title/album use unknown-* fallbacks
        // and musicId comes from the explicit musicId arg.
        Renderer.Render("{artist} - {title} ({album}) [{musicId}]", song: null, musicId: 9876543210L)
            .Should().Be("unknown-artist - unknown-title (unknown-album) [9876543210]");
    }

    [Fact]
    public void Render_EmptyArtists_UsesUnknownArtist()
    {
        var song = new SongInfo(1L, "T", System.Array.Empty<string>(), null);
        Renderer.Render("{artist} - {title}", song, song.MusicId)
            .Should().Be("unknown-artist - T");
    }

    [Fact]
    public void Render_AllArtistsBlank_UsesUnknownArtist()
    {
        // Per task brief: artist list empty OR all blank → unknown-artist
        var song = new SongInfo(1L, "T", new[] { "", "   ", "\t" }, null);
        Renderer.Render("{artist} - {title}", song, song.MusicId)
            .Should().Be("unknown-artist - T");
    }

    [Fact]
    public void Render_MultipleArtists_JoinedWithAmpersand()
    {
        var song = new SongInfo(2074977815L, "Ride It",
            new[] { "Rohith Pai Kasturi", "IAMPRANN", "IAMPRATHEEK" }, "Ride It");
        Renderer.Render("{artist} - {title}", song, song.MusicId)
            .Should().Be("Rohith Pai Kasturi & IAMPRANN & IAMPRATHEEK - Ride It");
    }

    [Fact]
    public void Render_EmptyTitle_UsesUnknownTitle()
    {
        var song = new SongInfo(1L, "", new[] { "A" }, null);
        Renderer.Render("{artist} - {title}", song, song.MusicId)
            .Should().Be("A - unknown-title");
    }

    [Fact]
    public void Render_WhitespaceTitle_UsesUnknownTitle()
    {
        var song = new SongInfo(1L, "   ", new[] { "A" }, null);
        Renderer.Render("{artist} - {title}", song, song.MusicId)
            .Should().Be("A - unknown-title");
    }

    [Fact]
    public void Render_NullAlbum_UsesUnknownAlbum()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("{album}/{title}", song, song.MusicId)
            .Should().Be("unknown-album/T");
    }

    [Fact]
    public void Render_EmptyAlbum_UsesUnknownAlbum()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, "");
        Renderer.Render("{album}/{title}", song, song.MusicId)
            .Should().Be("unknown-album/T");
    }

    [Fact]
    public void Render_MusicIdToken_AlwaysAvailable_EvenWhenAllElseEmpty()
    {
        var song = new SongInfo(3367798042L, "", System.Array.Empty<string>(), null);
        Renderer.Render("{musicId}", song, song.MusicId).Should().Be("3367798042");
    }

    [Fact]
    public void Render_UnknownPlaceholder_PassesThroughLiterally()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("{foo} - {title}", song, song.MusicId)
            .Should().Be("{foo} - T");
    }

    [Fact]
    public void Render_UppercasePlaceholder_PassesThroughLiterally()
    {
        // {ARTIST} is NOT a known token (we only match lowercase + camelCase 'musicId').
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("{ARTIST} - {title}", song, song.MusicId)
            .Should().Be("{ARTIST} - T");
    }

    [Fact]
    public void Render_DoubleBraces_OuterBracesRemain_InnerTokenRendered()
    {
        // Spec §6 test 8: {{artist}} → {<rendered-artist>} (no escape syntax)
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("{{artist}}", song, song.MusicId)
            .Should().Be("{A}");
    }

    [Fact]
    public void Render_TemplateWithNoTokens_ReturnsUnchanged()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("just a plain string with no braces", song, song.MusicId)
            .Should().Be("just a plain string with no braces");
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        var song = new SongInfo(1L, "T", new[] { "A" }, null);
        Renderer.Render("", song, song.MusicId).Should().Be("");
    }

    [Fact]
    public void Render_TokenValueContainsTokenSubstring_NotRecursivelyExpanded()
    {
        // Single-pass regex: artist value 'weird{title}name' is emitted as-is,
        // NOT re-scanned for {title}. This is the documented contract.
        var song = new SongInfo(1L, "Song", new[] { "weird{title}name" }, null);
        Renderer.Render("{artist}", song, song.MusicId)
            .Should().Be("weird{title}name");
    }

    [Fact]
    public void DefaultTemplate_IsAsSpecified()
    {
        NamingTemplateRenderer.DefaultTemplate.Should().Be("{artist} - {title}");
    }

    [Fact]
    public void FilenameArtistSeparator_IsSpaceAmpSpace()
    {
        NamingTemplateRenderer.FilenameArtistSeparator.Should().Be(" & ");
    }
}

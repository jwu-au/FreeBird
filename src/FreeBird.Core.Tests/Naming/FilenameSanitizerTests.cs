using System.Text;
using FluentAssertions;
using FreeBird.Core.Naming;

namespace FreeBird.Core.Tests.Naming;

public class FilenameSanitizerTests
{
    [Theory]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("AC/DC", "AC／DC")]
    [InlineData("Song: Subtitle", "Song： Subtitle")]
    [InlineData("a<b>c", "a＜b＞c")]
    [InlineData("path|pipe", "path｜pipe")]
    [InlineData("who?", "who？")]
    [InlineData("back\\slash", "back＼slash")]
    [InlineData("quoted\"text\"", "quoted_text_")]
    [InlineData("star*name", "star_name")]
    [InlineData("...foo.bar", "foo.bar")]
    [InlineData("foo.bar...", "foo.bar")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("...", "unknown")]
    [InlineData("CON", "CON_safe")]
    [InlineData("con", "con_safe")]
    [InlineData("PRN", "PRN_safe")]
    [InlineData("COM1", "COM1_safe")]
    [InlineData("LPT9", "LPT9_safe")]
    [InlineData("COM10", "COM10")]  // only 1-9 reserved
    [InlineData("connor", "connor")]  // CON is prefix only
    [InlineData("NUL", "NUL_safe")]
    [InlineData("树离suliii_", "树离suliii_")]  // CJK passes through
    [InlineData("Hall & Oates", "Hall & Oates")]  // ampersand allowed
    public void Sanitize_BehavesPerSpec(string? input, string expected)
    {
        FilenameSanitizer.Sanitize(input!).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_LongInput_TruncatesTo200WithEllipsis()
    {
        var input = new string('a', 300);
        var result = FilenameSanitizer.Sanitize(input);
        result.Length.Should().Be(200);
        result.Should().EndWith("…");
        result[0..199].Should().Be(new string('a', 199));
    }

    [Fact]
    public void Sanitize_AtBoundary200_NotTruncated()
    {
        var input = new string('a', 200);
        FilenameSanitizer.Sanitize(input).Should().Be(input);
    }

    [Fact]
    public void Sanitize_AtBoundary201_Truncated()
    {
        var input = new string('a', 201);
        var result = FilenameSanitizer.Sanitize(input);
        result.Length.Should().Be(200);
    }

    [Fact]
    public void Sanitize_ControlChars_Removed()
    {
        FilenameSanitizer.Sanitize("foo\u0001\u0007\u001Fbar\u007F").Should().Be("foobar");
    }

    [Fact]
    public void Sanitize_NfdInput_NfcOutput()
    {
        // Combining char: 'e' + combining acute (U+0301) vs precomposed 'é' (U+00E9)
        var nfd = "caf\u0065\u0301";  // 'cafe' + combining acute
        var result = FilenameSanitizer.Sanitize(nfd);
        result.Should().Be("caf\u00e9");  // precomposed 'é'
        result.IsNormalized(NormalizationForm.FormC).Should().BeTrue();
    }

    [Fact]
    public void Sanitize_NullInput_ReturnsUnknown()
    {
        FilenameSanitizer.Sanitize(null!).Should().Be("unknown");
    }
}

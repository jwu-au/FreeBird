using FluentAssertions;
using FreeBird.Core.Naming;
using Xunit;

namespace FreeBird.Core.Tests.Naming;

/// <summary>
/// Unit tests for <see cref="MusicIdExtractor"/>. Covers canonical bare-id stems,
/// composite NetEase cache stems (real samples taken from the user's NetEase Mac
/// cache on 2026-06-10), and degenerate inputs.
/// </summary>
public class MusicIdExtractorTests
{
    [Theory]
    // Canonical NetEase cache names (whole stem is numeric).
    [InlineData("3367798042.uc", 3367798042L)]
    [InlineData("3367798042.uc!", 3367798042L)]
    [InlineData("/abs/path/3367798042.uc", 3367798042L)]

    // Composite stems (REAL filenames from the user's NetEase cache 2026-06-10):
    [InlineData("2692390754-_-_5999-_-_dc6cd7a8de4502494004fde6ad4c6632.uc!", 2692390754L)]
    [InlineData("2018444878-_-_5999-_-_a44cfc6c53e54a70ee475c56b360699b.uc!", 2018444878L)]
    [InlineData("3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2.uc!", 3367798042L)]
    [InlineData("3358413188-_-_5999-_-_c1ebbd38b93c00c74880a3140e249b55.uc!", 3358413188L)]

    // Two-segment composite (existing FilesystemSkipDecider test pattern):
    [InlineData("3367798042-_-_5999.uc", 3367798042L)]

    // Sub-path forms.
    [InlineData("/Users/jwu13/Library/Containers/.../Caches/2692390754-_-_5999-_-_dc6cd7.uc!", 2692390754L)]
    public void TryExtract_KnownGood_ReturnsTrueWithMusicId(string sourcePath, long expected)
    {
        MusicIdExtractor.TryExtract(sourcePath, out var musicId).Should().BeTrue();
        musicId.Should().Be(expected);
    }

    [Theory]
    [InlineData("nodigits.uc")]
    [InlineData("-_-_5999-_-_abc.uc!")]       // leading separator, no digits first
    [InlineData("abc-12345.uc")]              // digits but not leading
    public void TryExtract_NoLeadingDigits_ReturnsFalseWithZero(string sourcePath)
    {
        MusicIdExtractor.TryExtract(sourcePath, out var musicId).Should().BeFalse();
        musicId.Should().Be(0L);
    }

    [Theory]
    [InlineData("3367798042.uc", "3367798042")]
    [InlineData("2692390754-_-_5999-_-_xxx.uc!", "2692390754")]
    [InlineData("nodigits.uc", null)]
    public void TryExtractAsString_RoundTrip(string sourcePath, string? expected)
    {
        MusicIdExtractor.TryExtractAsString(sourcePath).Should().Be(expected);
    }

    [Fact]
    public void TryExtract_And_TryExtractAsString_AgreeOnSuccess()
    {
        // Both overloads must accept/reject the same set of inputs.
        foreach (var sample in new[]
        {
            "3367798042.uc",
            "2692390754-_-_5999-_-_dc6cd7.uc!",
            "nodigits.uc",
        })
        {
            var byLong = MusicIdExtractor.TryExtract(sample, out var idLong);
            var byStr = MusicIdExtractor.TryExtractAsString(sample);
            (byLong, byStr is not null).Should().Be((byLong, byLong));
            if (byLong)
            {
                idLong.ToString().Should().Be(byStr);
            }
        }
    }
}

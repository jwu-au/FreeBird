using System;
using FluentAssertions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Decoding;

public class StemBasedFileNamerTests
{
    private readonly StemBasedFileNamer _sut = new();

    [Theory]
    [InlineData("12345-abc.uc", AudioFormat.Mp3, "12345-abc.mp3")]
    [InlineData("12345-abc.uc!", AudioFormat.Flac, "12345-abc.flac")]
    [InlineData("12345-abc.UC", AudioFormat.Mp3, "12345-abc.mp3")]
    [InlineData("12345-abc.Uc!", AudioFormat.Flac, "12345-abc.flac")]
    [InlineData("song.uc", AudioFormat.M4a, "song.m4a")]
    [InlineData("weird.name.uc", AudioFormat.Mp3, "weird.name.mp3")]
    [InlineData("weird.name.uc!", AudioFormat.Flac, "weird.name.flac")]
    public void GetTargetName_StripsUcSuffix_AppendsFormatExt(string input, AudioFormat fmt, string expected)
    {
        _sut.GetTargetName(input, fmt, null).Should().Be(expected);
    }

    [Theory]
    [InlineData("/some/dir/song.uc", AudioFormat.Mp3, "song.mp3")]
    [InlineData("C:\\Users\\foo\\song.uc!", AudioFormat.Flac, "song.flac")]
    [InlineData("~/Music/cache/12345.uc", AudioFormat.M4a, "12345.m4a")]
    public void GetTargetName_StripsDirectoryComponents(string input, AudioFormat fmt, string expected)
    {
        _sut.GetTargetName(input, fmt, null).Should().Be(expected);
    }

    [Theory]
    [InlineData("nothing.txt", AudioFormat.Mp3, "nothing.txt.mp3")]
    [InlineData("noext", AudioFormat.Flac, "noext.flac")]
    [InlineData("file.mp3", AudioFormat.Mp3, "file.mp3.mp3")]
    public void GetTargetName_NonUcSuffix_AppendsFormatExtWithoutStrip(string input, AudioFormat fmt, string expected)
    {
        _sut.GetTargetName(input, fmt, null).Should().Be(expected);
    }

    [Fact]
    public void GetTargetName_UnknownFormat_Throws()
    {
        Action act = () => _sut.GetTargetName("file.uc", AudioFormat.Unknown, null);
        act.Should().Throw<ArgumentException>().WithMessage("*unknown format*");
    }

    // T15.6: GetStem public static helper — same suffix-stripping logic, used by FileProcessor quarantine.
    // Non-uc inputs are returned as-is (preserves any other extension); only .uc / .uc! are stripped.
    [Theory]
    [InlineData("foo.uc", "foo")]
    [InlineData("foo.uc!", "foo")]
    [InlineData("path/foo.uc", "foo")]
    [InlineData("path\\foo.uc!", "foo")]
    [InlineData("Foo.UC!", "Foo")]
    [InlineData("Foo.Uc", "Foo")]
    [InlineData("/a/b/c/song.uc", "song")]
    [InlineData("C:\\Users\\x\\song.uc!", "song")]
    [InlineData("foo", "foo")]
    [InlineData("weird.name.uc!", "weird.name")]
    [InlineData("nothing.txt", "nothing.txt")]
    public void GetStem_StripsUcSuffixAndDirectory(string input, string expected)
    {
        StemBasedFileNamer.GetStem(input).Should().Be(expected);
    }

    [Fact]
    public void GetStem_NullOrWhitespace_Throws()
    {
        Action a1 = () => StemBasedFileNamer.GetStem(null!);
        Action a2 = () => StemBasedFileNamer.GetStem("");
        Action a3 = () => StemBasedFileNamer.GetStem("   ");
        a1.Should().Throw<ArgumentException>();
        a2.Should().Throw<ArgumentException>();
        a3.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void GetTargetName_NullOrWhitespaceInput_Throws(string? input)
    {
        Action act = () => _sut.GetTargetName(input!, AudioFormat.Mp3, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetTargetName_OnlyUcSuffix_ProducesExtOnlyName()
    {
        // edge: filename is literally ".uc" — stripping yields empty stem, result is just ".mp3"
        _sut.GetTargetName(".uc", AudioFormat.Mp3, null).Should().Be(".mp3");
    }

    [Fact]
    public void GetTargetName_AllSupportedFormats_HaveExtensions()
    {
        foreach (var fmt in new[] { AudioFormat.Mp3, AudioFormat.Flac, AudioFormat.M4a })
        {
            Action act = () => _sut.GetTargetName("x.uc", fmt, null);
            act.Should().NotThrow($"format {fmt} should be supported");
        }
    }
}

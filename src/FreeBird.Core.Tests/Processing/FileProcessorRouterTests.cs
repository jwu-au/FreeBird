using System;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Processing;
using FreeBird.Core.Tagging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// Task 14 — <see cref="FileProcessorRouter"/> selects the processor by source extension:
/// <c>.ncm</c> → <see cref="NcmFileProcessor"/>; everything else (.uc/.uc!/...) → the
/// default <see cref="FileProcessor"/>.
///
/// Both processors are <c>sealed</c> with all-interface ctors, so we construct REAL
/// instances backed by Moq'd dependencies. This proves the router returns the actual
/// wired processors (a non-vacuous test) without touching the filesystem.
/// </summary>
public class FileProcessorRouterTests
{
    private static FileProcessor MakeUcProcessor()
    {
        return new FileProcessor(
            new Mock<IXorDecoder>().Object,
            new Mock<IFormatSniffer>().Object,
            new Mock<IFileNamer>().Object,
            new Mock<ICompositeIntegrityChecker>().Object,
            new Mock<IAtomicFileWriter>().Object,
            new Mock<IMetadataResolver>().Object,
            new Mock<ITagWriter>().Object,
            new ResolutionMarkerSerializer(new LoggerConfiguration().CreateLogger()),
            new Mock<FreeBird.Core.Watch.IOutputPathMutexPool>().Object,
            new LoggerConfiguration().CreateLogger(),
            new FakeTimeProvider());
    }

    private static NcmFileProcessor MakeNcmProcessor()
    {
        return new NcmFileProcessor(
            new Mock<INcmDecoder>().Object,
            new Mock<IFormatSniffer>().Object,
            new Mock<ICompositeIntegrityChecker>().Object,
            new Mock<IAtomicFileWriter>().Object,
            new Mock<ITagWriter>().Object,
            new Mock<ICoverWriter>().Object,
            new ResolutionMarkerSerializer(new LoggerConfiguration().CreateLogger()),
            new Mock<FreeBird.Core.Watch.IOutputPathMutexPool>().Object,
            new LoggerConfiguration().CreateLogger(),
            new FakeTimeProvider());
    }

    private static FileProcessorRouter MakeSut(out FileProcessor uc, out NcmFileProcessor ncm)
    {
        uc = MakeUcProcessor();
        ncm = MakeNcmProcessor();
        return new FileProcessorRouter(uc, ncm);
    }

    [Theory]
    [InlineData("song.ncm")]
    [InlineData("/some/dir/song.ncm")]
    [InlineData("song.NCM")]
    [InlineData("song.NcM")]
    public void Select_NcmExtension_ReturnsNcmProcessor(string path)
    {
        var sut = MakeSut(out _, out var ncm);
        sut.Select(path).Should().BeSameAs(ncm);
    }

    [Theory]
    [InlineData("song.uc")]
    [InlineData("song.uc!")]
    [InlineData("song.UC")]
    [InlineData("song.flac")]
    [InlineData("song")]
    [InlineData("/some/dir/2692390754-_-_5999-_-_dc6cd7a8.uc!")]
    public void Select_NonNcmExtension_ReturnsUcProcessor(string path)
    {
        var sut = MakeSut(out var uc, out _);
        sut.Select(path).Should().BeSameAs(uc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Select_NullOrWhitespace_Throws(string? path)
    {
        var sut = MakeSut(out _, out _);
        Action act = () => sut.Select(path!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullUcProcessor_Throws()
    {
        var ncm = MakeNcmProcessor();
        Action act = () => _ = new FileProcessorRouter(null!, ncm);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullNcmProcessor_Throws()
    {
        var uc = MakeUcProcessor();
        Action act = () => _ = new FileProcessorRouter(uc, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

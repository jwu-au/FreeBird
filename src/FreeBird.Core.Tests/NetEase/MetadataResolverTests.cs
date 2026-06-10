using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.NetEase;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FreeBird.Core.Tests.NetEase;

public class MetadataResolverTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (MetadataResolver Resolver, ListSink Sink) ResolverWith(INetEaseApiClient api)
    {
        var sink = new ListSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (new MetadataResolver(api, logger), sink);
    }

    private static SongInfo SampleSong(long id = 3367798042L) =>
        new(id, "终不负", new[] { "Rohith Pai Kasturi" }, "Alpha");

    private const string SamplePath = "/tmp/3367798042.uc";

    // ------------------------------------------------------------------
    // T11 spec tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Offline_ReturnsFallback_OfflineMode_AndApiNeverInvoked()
    {
        var apiMock = new Mock<INetEaseApiClient>(MockBehavior.Strict);
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out") with { Offline = true };

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("offline-mode");
        apiMock.Verify(
            x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnlineSuccess_PassesThroughSongInfo()
    {
        var song = SampleSong();
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock.Setup(x => x.GetSongDetailAsync(3367798042L, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new NetEaseApiResult.Success(song));
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out");

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Success>()
              .Which.Song.Should().BeSameAs(song);
    }

    [Fact]
    public async Task NotFound_MapsToMetadataEmpty()
    {
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock.Setup(x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new NetEaseApiResult.NotFound(3367798042L));
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out");

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-empty");
    }

    [Fact]
    public async Task Timeout_MapsToMetadataFetchFailed()
    {
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock.Setup(x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new NetEaseApiResult.Timeout(TimeSpan.FromSeconds(10)));
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out");

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-fetch-failed");
    }

    [Fact]
    public async Task NetworkError_MapsToMetadataFetchFailed_AndLogsWarning()
    {
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock.Setup(x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new NetEaseApiResult.NetworkError("connection refused"));
        var (resolver, sink) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out");

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-fetch-failed");
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public async Task DeserializationError_MapsToMetadataDeserializeFailed_AndLogsError()
    {
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock.Setup(x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new NetEaseApiResult.DeserializationError("missing songs field"));
        var (resolver, sink) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out");

        var result = await resolver.ResolveAsync(SamplePath, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-deserialize-failed");
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Error);
    }

    // ------------------------------------------------------------------
    // Extra guard: musicId extraction failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task NonNumericStem_FallsBackTo_MetadataEmpty()
    {
        var apiMock = new Mock<INetEaseApiClient>(MockBehavior.Strict);
        var (resolver, sink) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out"); // online

        var result = await resolver.ResolveAsync("/tmp/not-a-musicid.uc", opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-empty");
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Warning);
        apiMock.Verify(
            x => x.GetSongDetailAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // v3.0.0 hotfix regression: real NetEase Mac cache files use composite
    // stems like "<musicId>-_-_<bitrate>-_-_<hash>.uc!". The resolver used
    // to bail to Fallback("metadata-empty") for any non-bare-numeric stem,
    // which meant --naming-template and --write-tags silently did nothing
    // on the user's actual cache. These tests pin the fix in place.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_CompositeStem_CallsApiInsteadOfBailing()
    {
        var song = SampleSong();
        var apiMock = new Mock<INetEaseApiClient>();
        apiMock
            .Setup(c => c.GetSongDetailAsync(3367798042L, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NetEaseApiResult.Success(song));
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out"); // online

        var path = "/cache/3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2.uc!";
        var result = await resolver.ResolveAsync(path, opts, CancellationToken.None);

        apiMock.Verify(
            c => c.GetSongDetailAsync(3367798042L, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Should().BeOfType<MetadataResolution.Success>()
              .Which.Song.Should().BeSameAs(song);
    }

    [Fact]
    public async Task ResolveAsync_NonNumericStem_StillBailsAsFallback()
    {
        // MockBehavior.Strict ensures the API is NOT called for unparseable stems.
        var apiMock = new Mock<INetEaseApiClient>(MockBehavior.Strict);
        var (resolver, _) = ResolverWith(apiMock.Object);
        var opts = new ScanOptions("in", "out"); // online

        var path = "/cache/nodigits.uc";
        var result = await resolver.ResolveAsync(path, opts, CancellationToken.None);

        result.Should().BeOfType<MetadataResolution.Fallback>()
              .Which.SidecarReason.Should().Be("metadata-empty");
    }
}

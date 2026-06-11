using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using FreeBird.Core.Provisioning;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Provisioning;

public class WindowsFlacAutoInstallerTests
{
    private const string ValidUrl = "https://ftp.osuosl.org/pub/xiph/releases/flac/flac-1.5.0-win.zip";
    private const string TargetDir = "/app";

    private static (WindowsFlacAutoInstaller installer, Mock<IHttpDownloader> downloader, MockFileSystem fs) Build(
        Func<Uri, Stream, CancellationToken, Task<long>>? respond = null,
        Dictionary<string, MockFileData>? files = null)
    {
        var downloader = new Mock<IHttpDownloader>();
        if (respond is not null)
        {
            downloader.Setup(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<Uri, Stream, IProgress<long>?, CancellationToken>((u, s, p, ct) => respond(u, s, ct));
        }
        else
        {
            downloader.Setup(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0L);
        }

        var fs = new MockFileSystem(files ?? new());
        fs.Directory.CreateDirectory(TargetDir);

        return (
            new WindowsFlacAutoInstaller(downloader.Object, fs, new Mock<ILogger>().Object),
            downloader,
            fs);
    }

    [Fact]
    public async Task InstallAsync_DownloadSucceeds_ReturnsFailed_WithExtractionNotImplemented()
    {
        // T08 deliberate state: download works, but extraction not done yet → Failed.
        var (installer, downloader, fs) = Build(respond: async (u, dest, ct) =>
        {
            var bytes = new byte[1024];
            await dest.WriteAsync(bytes, ct);
            return bytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("extraction not yet implemented");
        downloader.Verify(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_DownloadSucceeds_CleansUpTempFile()
    {
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(new byte[100], ct);
            return 100;
        });

        await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse("temp file should be cleaned up after install attempt");
    }

    [Fact]
    public async Task InstallAsync_DownloadThrowsHttpDownloadException_ReturnsFailed_AndCleansUp()
    {
        var (installer, _, fs) = Build(respond: (u, dest, ct) => throw new HttpDownloadException("network down"));

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("download failed");
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_DownloadThrowsHttpDownloadException_WithStatusCode_ReturnsFailed()
    {
        var (installer, _, _) = Build(respond: (u, dest, ct) => throw new HttpDownloadException("HTTP 404", 404));

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>();
    }

    [Fact]
    public async Task InstallAsync_CancelledDuringDownload_PropagatesCancellation_AndCleansUp()
    {
        using var cts = new CancellationTokenSource();
        var (installer, _, fs) = Build(respond: (u, dest, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0L);
        });

        var act = async () => await installer.InstallAsync(TargetDir, ValidUrl, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_EmptyTargetDir_ReturnsFailed_NoDownloadAttempt()
    {
        var (installer, downloader, _) = Build();
        var result = await installer.InstallAsync("", ValidUrl, CancellationToken.None);
        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("target directory");
        downloader.Verify(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_EmptyUrl_ReturnsFailed_NoDownloadAttempt()
    {
        var (installer, downloader, _) = Build();
        var result = await installer.InstallAsync(TargetDir, "", CancellationToken.None);
        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("URL");
        downloader.Verify(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_InvalidUrl_ReturnsFailed_NoDownloadAttempt()
    {
        var (installer, downloader, _) = Build();
        var result = await installer.InstallAsync(TargetDir, "not-a-url", CancellationToken.None);
        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("invalid URL");
        downloader.Verify(d => d.DownloadAsync(It.IsAny<Uri>(), It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_StaleTempFileExists_DeletesItBeforeDownload()
    {
        var staleContent = new byte[] { 1, 2, 3 };
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        var (installer, downloader, fs) = Build(
            respond: async (u, dest, ct) => { await dest.WriteAsync(new byte[10], ct); return 10; },
            files: new Dictionary<string, MockFileData> { [tempPath] = new MockFileData(staleContent) });

        await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        // After successful download + cleanup, temp shouldn't have stale bytes
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public void Ctor_NullDownloader_Throws()
    {
        var act = () => new WindowsFlacAutoInstaller(null!, new MockFileSystem(), new Mock<ILogger>().Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("downloader");
    }

    [Fact]
    public void Ctor_NullFileSystem_Throws()
    {
        var act = () => new WindowsFlacAutoInstaller(new Mock<IHttpDownloader>().Object, null!, new Mock<ILogger>().Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("fs");
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new WindowsFlacAutoInstaller(new Mock<IHttpDownloader>().Object, new MockFileSystem(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }
}

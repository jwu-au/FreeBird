using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using FreeBird.Core.Provisioning;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Provisioning;

public class WindowsFlacAutoInstallerTests : IDisposable
{
    private const string ValidUrl = "https://ftp.osuosl.org/pub/xiph/releases/flac/flac-1.5.0-win.zip";
    private const string TargetDir = "/app";

    private static readonly string[] Win64FilesAll = new[]
    {
        "flac-1.5.0-win/Win64/flac.exe",
        "flac-1.5.0-win/Win64/metaflac.exe",
        "flac-1.5.0-win/Win64/libFLAC.dll",
        "flac-1.5.0-win/Win64/libFLAC++.dll",
    };

    // Snapshot the canonical SHA so each test can mutate ExpectedSha256 freely; we restore on Dispose.
    private readonly string _originalSha = WindowsFlacAutoInstaller.ExpectedSha256;

    public void Dispose() => WindowsFlacAutoInstaller.ExpectedSha256 = _originalSha;

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

    private static string ComputeSha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static byte[] BuildValidZipWithEntries(params string[] entryPaths)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in entryPaths)
            {
                var entry = archive.CreateEntry(path);
                using var es = entry.Open();
                es.WriteByte(0x00); // minimal content
            }
        }
        return ms.ToArray();
    }

    // -------- T09 tests --------

    [Fact]
    public async Task InstallAsync_ZipMissingRequiredEntry_ReturnsFailed()
    {
        // Only one of four required Win64 entries — missing-entry path should fail with a clear reason.
        var zipBytes = BuildValidZipWithEntries("flac-1.5.0-win/Win64/flac.exe");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, _) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("required entry missing");
    }

    [Fact]
    public async Task InstallAsync_ShaMismatch_ReturnsFailed_AndCleansUpTemp()
    {
        var zipBytes = BuildValidZipWithEntries("flac-1.5.0-win/Win64/flac.exe");
        WindowsFlacAutoInstaller.ExpectedSha256 = new string('0', 64); // intentional mismatch
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        var failed = result.Should().BeOfType<FlacInstallResult.Failed>().Which;
        failed.Reason.Should().Contain("SHA256 mismatch");
        failed.Reason.Should().Contain("expected");
        failed.Reason.Should().Contain("got");
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse("temp file must be removed on SHA mismatch to avoid reuse");
    }

    [Fact]
    public async Task InstallAsync_ZipWithAbsolutePathEntry_RejectsAsUnsafe()
    {
        // Absolute Unix path inside ZIP — must be rejected even though the file IS structurally a ZIP.
        var zipBytes = BuildValidZipWithEntries("/etc/passwd");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("unsafe zip entry");
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_ZipWithTraversalEntry_RejectsAsUnsafe()
    {
        var zipBytes = BuildValidZipWithEntries("../../../etc/passwd");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("unsafe zip entry");
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_ValidZip_AllFourFilesExtracted_ReturnsInstalled()
    {
        var zipBytes = BuildValidZipWithEntries(Win64FilesAll);
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        var installed = result.Should().BeOfType<FlacInstallResult.Installed>().Which;
        installed.FlacPath.Should().Be(Path.Combine(TargetDir, "flac.exe"));
        installed.MetaflacPath.Should().Be(Path.Combine(TargetDir, "metaflac.exe"));

        foreach (var fn in new[] { "flac.exe", "metaflac.exe", "libFLAC.dll", "libFLAC++.dll" })
        {
            fs.File.Exists(Path.Combine(TargetDir, fn)).Should().BeTrue($"{fn} should be extracted");
        }
    }

    [Fact]
    public async Task InstallAsync_NotAZipFile_ReturnsFailed()
    {
        var notZipBytes = new byte[] { 1, 2, 3, 4 };
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(notZipBytes);
        var (installer, _, _) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(notZipBytes, ct);
            return notZipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("zip validation failed");
    }

    [Fact]
    public async Task InstallAsync_ConfirmsPinnedExpectedShaConstant_IsCanonicalValue()
    {
        // T18 will grep for this literal in source; assert here it isn't accidentally mutated by another test
        // (Dispose ordering is per-test in xUnit, but this is the canonical value preserved across the plan).
        WindowsFlacAutoInstaller.ExpectedSha256.Should().Be(
            "53f1500f0d6e7c61379d7fee50d4a9f7f504c650009506d9ba015530d76c0dde",
            "the pinned SHA must not drift without updating the URL in tandem");
    }

    // -------- T10 tests --------

    [Fact]
    public async Task InstallAsync_ValidZipWithAllFourFiles_ReturnsInstalled_AndFilesExist()
    {
        var zipBytes = BuildValidZipWithEntries(Win64FilesAll);
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) => { await dest.WriteAsync(zipBytes, ct); return zipBytes.Length; });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        var installed = result.Should().BeOfType<FlacInstallResult.Installed>().Which;
        installed.FlacPath.Should().Be(Path.Combine(TargetDir, "flac.exe"));
        installed.MetaflacPath.Should().Be(Path.Combine(TargetDir, "metaflac.exe"));

        foreach (var fn in new[] { "flac.exe", "metaflac.exe", "libFLAC.dll", "libFLAC++.dll" })
        {
            fs.File.Exists(Path.Combine(TargetDir, fn)).Should().BeTrue($"{fn} should be extracted");
        }
    }

    [Fact]
    public async Task InstallAsync_Success_DeletesTempZip()
    {
        var zipBytes = BuildValidZipWithEntries(Win64FilesAll);
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) => { await dest.WriteAsync(zipBytes, ct); return zipBytes.Length; });

        await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        fs.File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_ZipMissingRequiredEntry_ReturnsFailed_AndCleansUpAll()
    {
        // Build ZIP with only 3 of 4 required entries
        var zipBytes = BuildValidZipWithEntries("flac-1.5.0-win/Win64/flac.exe", "flac-1.5.0-win/Win64/metaflac.exe", "flac-1.5.0-win/Win64/libFLAC.dll");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) => { await dest.WriteAsync(zipBytes, ct); return zipBytes.Length; });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Failed>()
            .Which.Reason.Should().Contain("required entry missing");

        // Partial extractions should be cleaned up
        foreach (var fn in new[] { "flac.exe", "metaflac.exe", "libFLAC.dll", "libFLAC++.dll" })
        {
            fs.File.Exists(Path.Combine(TargetDir, fn)).Should().BeFalse($"partial {fn} should be cleaned up");
        }
        fs.File.Exists(Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName)).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_ZipExtraEntries_ExtractsOnlyRequiredFour()
    {
        // Extra entries (Win32/, share/man/) shouldn't be extracted; only Win64/ targets land.
        // Mirrors the real upstream Xiph layout where everything is wrapped in flac-1.5.0-win/.
        var allEntries = Win64FilesAll.Concat(new[] { "flac-1.5.0-win/Win32/flac.exe", "flac-1.5.0-win/share/man/flac.1" }).ToArray();
        var zipBytes = BuildValidZipWithEntries(allEntries);
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) => { await dest.WriteAsync(zipBytes, ct); return zipBytes.Length; });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        result.Should().BeOfType<FlacInstallResult.Installed>();
        foreach (var fn in new[] { "flac.exe", "metaflac.exe", "libFLAC.dll", "libFLAC++.dll" })
        {
            fs.File.Exists(Path.Combine(TargetDir, fn)).Should().BeTrue();
        }
        // Win32/flac.exe should NOT have been extracted to /app/Win32/flac.exe
        fs.File.Exists(Path.Combine(TargetDir, "Win32", "flac.exe")).Should().BeFalse();
        fs.Directory.Exists(Path.Combine(TargetDir, "Win32")).Should().BeFalse();
    }

    // -------- T08 carried-forward tests (updated to satisfy T09 SHA gate) --------

    [Fact]
    public async Task InstallAsync_DownloadSucceeds_CleansUpTempFile()
    {
        var zipBytes = BuildValidZipWithEntries("flac-1.5.0-win/Win64/flac.exe");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
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
        var zipBytes = BuildValidZipWithEntries("flac-1.5.0-win/Win64/flac.exe");
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var staleContent = new byte[] { 1, 2, 3 };
        var tempPath = Path.Combine(TargetDir, WindowsFlacAutoInstaller.TempZipName);
        var (installer, _, fs) = Build(
            respond: async (u, dest, ct) => { await dest.WriteAsync(zipBytes, ct); return zipBytes.Length; },
            files: new Dictionary<string, MockFileData> { [tempPath] = new MockFileData(staleContent) });

        await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        // After successful download + cleanup, temp shouldn't exist
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

    /// <summary>
    /// v3.3.1 regression guard — locks the ZIP entry path schema against the real upstream layout.
    /// The official Xiph flac-1.5.0-win.zip wraps all entries in a top-level "flac-1.5.0-win/" directory.
    /// If a future FLAC release ever changes this prefix (e.g. "flac-1.5.1-win/"), Win64Files lookup keys
    /// must be updated in lockstep.
    /// </summary>
    [Fact]
    public async Task InstallAsync_RealUpstreamZipLayout_AllFourEntriesFound()
    {
        // Build ZIP mirroring the *exact* upstream layout, including the wrapper directory
        // entry plus the four required Win64 binaries (and Win32 + docs we ignore).
        var zipBytes = BuildValidZipWithEntries(
            "flac-1.5.0-win/",                               // wrapper dir entry
            "flac-1.5.0-win/Win64/",                         // Win64 dir entry
            "flac-1.5.0-win/Win64/flac.exe",
            "flac-1.5.0-win/Win64/metaflac.exe",
            "flac-1.5.0-win/Win64/libFLAC.dll",
            "flac-1.5.0-win/Win64/libFLAC++.dll",
            "flac-1.5.0-win/Win32/",                         // Win32 dir entry — must be ignored
            "flac-1.5.0-win/Win32/flac.exe",                 // 32-bit — must be ignored
            "flac-1.5.0-win/AUTHORS",
            "flac-1.5.0-win/README-for-these-builds.txt",
            "flac-1.5.0-win/manuals/",
            "flac-1.5.0-win/manuals/flac.html"
        );
        WindowsFlacAutoInstaller.ExpectedSha256 = ComputeSha(zipBytes);
        var (installer, _, fs) = Build(respond: async (u, dest, ct) =>
        {
            await dest.WriteAsync(zipBytes, ct);
            return zipBytes.Length;
        });

        var result = await installer.InstallAsync(TargetDir, ValidUrl, CancellationToken.None);

        // All four Win64 binaries land in TargetDir with leaf-only names.
        result.Should().BeOfType<FlacInstallResult.Installed>();
        foreach (var fn in new[] { "flac.exe", "metaflac.exe", "libFLAC.dll", "libFLAC++.dll" })
        {
            fs.File.Exists(Path.Combine(TargetDir, fn))
                .Should().BeTrue($"{fn} should have been extracted from flac-1.5.0-win/Win64/{fn}");
        }
        // Win32 binaries and docs must NOT be extracted.
        fs.File.Exists(Path.Combine(TargetDir, "Win32", "flac.exe")).Should().BeFalse();
        fs.File.Exists(Path.Combine(TargetDir, "AUTHORS")).Should().BeFalse();
    }
}

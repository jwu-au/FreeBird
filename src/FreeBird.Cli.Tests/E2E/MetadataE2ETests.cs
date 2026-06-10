using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Cli.Tests.E2E.Stubs;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using Xunit;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// v3 T20 — full end-to-end metadata pipeline tests. Each test:
///  1) Materializes a real XOR-encrypted .uc fixture in a temp input dir.
///  2) Stands up a <see cref="StubNetEaseServer"/> on a loopback port (unless offline).
///  3) Points the production <c>NetEaseApiClient</c> at the stub via the
///     <c>FB_NETEASE_BASEURL</c> env var (test seam added in this commit).
///  4) Drives <see cref="ScanRunner.RunAsync"/> through the real Autofac graph
///     (no mocks, no Process.Start of the fb binary).
///  5) Asserts on filename, sidecar presence/reason, request counts, and (test 5)
///     embedded FLAC tags via <c>metaflac --list</c>.
///
/// Marked [Trait("Category","E2E")] for opt-out via
/// <c>dotnet test --filter "Category!=E2E"</c>. Marked [Collection("ConsoleRedirect")]
/// because ScanRunner writes to Console and other suites already serialize on this
/// collection; we also mutate the FB_NETEASE_BASEURL process-global env var here.
/// </summary>
[Collection("ConsoleRedirect")]
[Trait("Category", "E2E")]
public class MetadataE2ETests
{
    private const string EnvVarName = "FB_NETEASE_BASEURL";

    private static async Task<(string inputDir, string outputDir, string ucPath)> SetupAsync(
        string ucName = "3367798042.uc")
    {
        var input = WatchE2EHelpers.CreateTempPath("meta-in");
        var output = WatchE2EHelpers.CreateTempPath("meta-out");
        var ucPath = await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleFlacPath, input, ucName);
        return (input, output, ucPath);
    }

    private static void CleanupDirs(params string[] dirs)
    {
        foreach (var d in dirs) { WatchE2EHelpers.CleanupDir(d); }
    }

    [Fact]
    public async Task Scan_WithMockedNetEase_Renames_To_ArtistDashTitle()
    {
        var (input, output, _) = await SetupAsync("3367798042.uc");
        using var stub = new StubNetEaseServer
        {
            ResponseJson = """{"songs":[{"id":3367798042,"name":"TestTitle","artists":[{"id":1,"name":"TestArtist"}],"album":{"id":1,"name":"TestAlbum"}}],"code":200}""",
        };
        Environment.SetEnvironmentVariable(EnvVarName, stub.BaseUrl.ToString().TrimEnd('/'));
        try
        {
            var exit = await ScanRunner.RunAsync(
                input, output,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                namingTemplate: "{artist} - {title}",
                offline: false,
                apiTimeoutSeconds: 10,
                apiRateLimit: 0,
                writeTags: false);

            exit.Should().Be(ScanRunner.ExitOk);
            var expected = Path.Combine(output, "TestArtist - TestTitle.flac");
            File.Exists(expected).Should().BeTrue("happy-path metadata rename should produce '<artist> - <title>.flac'");
            stub.RequestCount.Should().BeGreaterThanOrEqualTo(1);
            stub.ReceivedPaths.Should().Contain(p => p.Contains("id=3367798042"));
            File.Exists(expected + ".txt").Should().BeFalse("OK path with metadata must not write a sidecar");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);
            CleanupDirs(input, output);
        }
    }

    [Fact]
    public async Task Scan_Offline_Renames_To_MusicIdOnly()
    {
        var (input, output, _) = await SetupAsync("3367798042.uc");
        // Stand up the stub anyway to PROVE offline mode never calls out.
        using var stub = new StubNetEaseServer();
        Environment.SetEnvironmentVariable(EnvVarName, stub.BaseUrl.ToString().TrimEnd('/'));
        try
        {
            var exit = await ScanRunner.RunAsync(
                input, output,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                namingTemplate: "{artist} - {title}",
                offline: true,
                apiTimeoutSeconds: 10,
                apiRateLimit: 0,
                writeTags: false);

            exit.Should().Be(ScanRunner.ExitOk);
            var expected = Path.Combine(output, "3367798042.flac");
            File.Exists(expected).Should().BeTrue("offline mode falls back to {musicId}.{ext} naming");
            File.Exists(expected + ".txt").Should().BeFalse("offline-mode is opt-in and never writes a sidecar (spec §10)");
            stub.RequestCount.Should().Be(0, "offline mode must NEVER reach the network");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);
            CleanupDirs(input, output);
        }
    }

    [Fact]
    public async Task Scan_When_Api_Returns_EmptySongs_WritesMarker_WithMetadataEmptyStatus()
    {
        var (input, output, _) = await SetupAsync("3367798042.uc");
        // Spec §10: NetEase returns 200 with an empty songs array for unknown ids.
        // NetEaseApiClient maps that to NotFound -> MetadataResolver -> Fallback("metadata-empty").
        // v3.0.1 T04: the .flac.txt sidecar is gone; the outcome is in the JSON marker.
        using var stub = new StubNetEaseServer
        {
            ResponseJson = """{"songs":[],"code":200}""",
            StatusCode = 200,
        };
        Environment.SetEnvironmentVariable(EnvVarName, stub.BaseUrl.ToString().TrimEnd('/'));
        try
        {
            var exit = await ScanRunner.RunAsync(
                input, output,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                namingTemplate: "{artist} - {title}",
                offline: false,
                apiTimeoutSeconds: 10,
                apiRateLimit: 0,
                writeTags: false);

            exit.Should().Be(ScanRunner.ExitOk);
            var expected = Path.Combine(output, "3367798042.flac");
            File.Exists(expected).Should().BeTrue("empty-songs fallback uses {musicId}.{ext}");
            File.Exists(expected + ".txt").Should().BeFalse("v3.0.1: .flac.txt sidecar is gone — outcome is in the JSON marker");
            // Marker stem is "3367798042" (the source filename was "3367798042.uc").
            var markerPath = Path.Combine(output, ResolutionMarkerSerializer.MarkerSubdir, "3367798042.json");
            File.Exists(markerPath).Should().BeTrue("metadata-empty fallback writes a JSON marker");
            var markerJson = await File.ReadAllTextAsync(markerPath);
            var marker = JsonSerializer.Deserialize<ResolutionMarker>(markerJson, ResolutionMarkerJson.Options);
            marker.Should().NotBeNull();
            marker!.Status.Should().Be(MarkerStatus.MetadataEmpty);
            marker.Reason.Should().Be("metadata-empty");
            marker.RetryAfter.Should().NotBeNull();
            (marker.RetryAfter!.Value - marker.ResolvedAt).TotalSeconds
                .Should().BeApproximately(TimeSpan.FromDays(7).TotalSeconds, 2.0);
            stub.RequestCount.Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);
            CleanupDirs(input, output);
        }
    }

    [Fact]
    public async Task Scan_When_Api_TimesOut_WritesMarker_WithMetadataFetchFailedStatus()
    {
        var (input, output, _) = await SetupAsync("3367798042.uc");
        // 3-second stub delay vs. 1-second --api-timeout: the per-request linked CTS fires
        // and NetEaseApiClient returns Timeout -> Fallback("metadata-fetch-failed").
        // v3.0.1 T04: the .flac.txt sidecar is gone; the outcome is in the JSON marker.
        using var stub = new StubNetEaseServer
        {
            ResponseJson = """{"songs":[],"code":200}""",
            ResponseDelay = TimeSpan.FromSeconds(3),
        };
        Environment.SetEnvironmentVariable(EnvVarName, stub.BaseUrl.ToString().TrimEnd('/'));
        try
        {
            var exit = await ScanRunner.RunAsync(
                input, output,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                namingTemplate: "{artist} - {title}",
                offline: false,
                apiTimeoutSeconds: 1,
                apiRateLimit: 0,
                writeTags: false);

            exit.Should().Be(ScanRunner.ExitOk);
            var expected = Path.Combine(output, "3367798042.flac");
            File.Exists(expected).Should().BeTrue("timeout still produces a decoded file with fallback name");
            File.Exists(expected + ".txt").Should().BeFalse("v3.0.1: .flac.txt sidecar is gone — outcome is in the JSON marker");
            var markerPath = Path.Combine(output, ResolutionMarkerSerializer.MarkerSubdir, "3367798042.json");
            File.Exists(markerPath).Should().BeTrue("timeout fallback writes a JSON marker");
            var markerJson = await File.ReadAllTextAsync(markerPath);
            var marker = JsonSerializer.Deserialize<ResolutionMarker>(markerJson, ResolutionMarkerJson.Options);
            marker.Should().NotBeNull();
            marker!.Status.Should().Be(MarkerStatus.MetadataFetchFailed);
            marker.Reason.Should().Be("metadata-fetch-failed");
            marker.RetryAfter.Should().NotBeNull();
            (marker.RetryAfter!.Value - marker.ResolvedAt).TotalSeconds
                .Should().BeApproximately(TimeSpan.FromHours(1).TotalSeconds, 2.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);
            CleanupDirs(input, output);
        }
    }

    [SkippableFact]
    public async Task Scan_WithWriteTags_AndFlacOutput_TagsAreEmbedded()
    {
        Skip.IfNot(IsMetaflacAvailable(), "metaflac binary not available on PATH; install via `brew install flac`");

        var (input, output, _) = await SetupAsync("3367798042.uc");
        using var stub = new StubNetEaseServer
        {
            ResponseJson = """{"songs":[{"id":3367798042,"name":"TestTitle","artists":[{"id":1,"name":"TestArtist"}],"album":{"id":1,"name":"TestAlbum"}}],"code":200}""",
        };
        Environment.SetEnvironmentVariable(EnvVarName, stub.BaseUrl.ToString().TrimEnd('/'));
        try
        {
            var exit = await ScanRunner.RunAsync(
                input, output,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                namingTemplate: "{artist} - {title}",
                offline: false,
                apiTimeoutSeconds: 10,
                apiRateLimit: 0,
                writeTags: true);

            exit.Should().Be(ScanRunner.ExitOk);
            var expected = Path.Combine(output, "TestArtist - TestTitle.flac");
            File.Exists(expected).Should().BeTrue();

            var (outText, _) = RunProcess("metaflac", "--list", expected);
            outText.Should().Contain("ARTIST=TestArtist");
            outText.Should().Contain("TITLE=TestTitle");
            outText.Should().Contain("ALBUM=TestAlbum");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, null);
            CleanupDirs(input, output);
        }
    }

    private static bool IsMetaflacAvailable()
    {
        try
        {
            var (_, exit) = RunProcess("metaflac", "--version");
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    // Uses ProcessStartInfo.ArgumentList so each arg is passed verbatim (no shell-style
    // quoting). On macOS/Linux UseShellExecute=false means there's no shell to escape
    // against, but paths containing embedded quotes or spaces would still break the
    // Arguments-as-string path; ArgumentList sidesteps that entirely.
    private static (string stdout, int exitCode) RunProcess(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{fileName} did not exit within 10s");
        }
        return (stdout, p.ExitCode);
    }
}

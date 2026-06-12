using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T10 — fb watch CLI subcommand option parsing + validation tests.
/// Uses <see cref="WatchCommand.HandlerOverride"/> so the real WatchRunner / watch loop
/// never runs during these tests. Shares <c>ConsoleRedirect</c> collection with
/// <see cref="WatchRunnerTests"/> to prevent races on <c>Console.Out</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public class WatchCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public WatchCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-watchcli-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<(int exit, string stdout, string stderr)> InvokeAsync(params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var outBuf = new StringWriter();
        var errBuf = new StringWriter();
        Console.SetOut(outBuf);
        Console.SetError(errBuf);
        try
        {
            var exit = await CliRoot.InvokeAsync(args);
            return (exit, outBuf.ToString(), errBuf.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    // 1. Happy path — required options + defaults → handler invoked with correct paths.
    // (Uses HandlerOverride so the real WatchRunner / watch loop never executes.)
    [Fact]
    public async Task Watch_RequiredOptionsOnly_Succeeds_InvokesHandler()
    {
        WatchOptions? captured = null;
        var origHandler = WatchCommand.HandlerOverride;
        WatchCommand.HandlerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var (exit, _, _) = await InvokeAsync("watch", _inputDir, "--output", _outputDir);

            exit.Should().Be(0);
            captured.Should().NotBeNull();
            captured!.InputDirs.Should().BeEquivalentTo(new[] { _inputDir });
            captured.OutputDir.Should().Be(_outputDir);
        }
        finally
        {
            WatchCommand.HandlerOverride = origHandler;
        }
    }

    // 2. Missing positional input arg → exit 2 (usage error).
    [Fact]
    public async Task Watch_MissingInputArg_Exit2()
    {
        var (exit, _, _) = await InvokeAsync("watch", "--output", _outputDir);
        exit.Should().NotBe(0);
    }

    // 3. Missing --output → exit 2.
    [Fact]
    public async Task Watch_MissingOutput_Exit2()
    {
        var (exit, _, _) = await InvokeAsync("watch", _inputDir);
        exit.Should().NotBe(0);
    }

    // 4. --poll-interval "5s" parsed as 5 seconds.
    [Fact]
    public async Task Watch_PollIntervalNs_ParsedAsSeconds()
    {
        WatchOptions.ParseDuration("5s").Should().Be(TimeSpan.FromSeconds(5));
        WatchOptions.ParseDuration("30s").Should().Be(TimeSpan.FromSeconds(30));
        WatchOptions.ParseDuration("90").Should().Be(TimeSpan.FromSeconds(90));
    }

    // 5. --poll-interval "2m" parsed as 2 minutes.
    [Fact]
    public async Task Watch_PollIntervalNm_ParsedAsMinutes()
    {
        WatchOptions.ParseDuration("2m").Should().Be(TimeSpan.FromMinutes(2));
        WatchOptions.ParseDuration("60m").Should().Be(TimeSpan.FromMinutes(60));
    }

    // 6. --poll-interval invalid forms → exit 2.
    [Theory]
    [InlineData("5h")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task Watch_PollIntervalInvalid_Exit2(string raw)
    {
        var (exit, _, _) = await InvokeAsync("watch", _inputDir, "--output", _outputDir, "--poll-interval", raw);
        exit.Should().Be(2);
    }

    // 7. --verbose + --quiet mutually exclusive.
    [Fact]
    public async Task Watch_VerboseAndQuiet_Mutex_Exit2()
    {
        var (exit, _, stderr) = await InvokeAsync("watch", _inputDir, "--output", _outputDir, "--verbose", "--quiet");
        exit.Should().Be(2);
        stderr.Should().Contain("mutually exclusive");
    }

    // 8. --log-file + --no-log-file mutually exclusive.
    [Fact]
    public async Task Watch_LogFileAndNoLogFile_Mutex_Exit2()
    {
        var (exit, _, stderr) = await InvokeAsync("watch", _inputDir, "--output", _outputDir, "--log-file", "/tmp/x.log", "--no-log-file");
        exit.Should().Be(2);
        stderr.Should().Contain("mutually exclusive");
    }

    // 9. --poll-interval out of range (0, 61m) → exit 2.
    [Theory]
    [InlineData("0s")]
    [InlineData("61m")]
    public async Task Watch_PollIntervalOutOfRange_Exit2(string raw)
    {
        var (exit, _, _) = await InvokeAsync("watch", _inputDir, "--output", _outputDir, "--poll-interval", raw);
        exit.Should().Be(2);
    }

    // 10. All options present — verify WatchOptions has expected values.
    [Fact]
    public async Task Watch_AllOptions_ParsedCorrectly()
    {
        WatchOptions? captured = null;
        var origHandler = WatchCommand.HandlerOverride;
        WatchCommand.HandlerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var (exit, _, _) = await InvokeAsync(
                "watch", _inputDir,
                "--output", _outputDir,
                "--integrity", "l3",
                "--concurrency", "8",
                "--on-collision", "overwrite",
                "--poll-interval", "10s",
                "--stability-checks", "3",
                "--min-file-size", "2048",
                "--skip-initial-scan",
                "--log-file", "/tmp/custom.log",
                "--verbose");

            exit.Should().Be(0);
            captured.Should().NotBeNull();
            captured!.InputDirs.Should().BeEquivalentTo(new[] { _inputDir });
            captured.OutputDir.Should().Be(_outputDir);
            captured.Integrity.Should().Be(IntegrityLevel.L3);
            captured.Concurrency.Should().Be(8);
            captured.Collision.Should().Be(CollisionPolicy.Overwrite);
            captured.PollInterval.Should().Be(TimeSpan.FromSeconds(10));
            captured.StabilityChecks.Should().Be(3);
            captured.MinFileSize.Should().Be(2048L);
            captured.SkipInitialScan.Should().BeTrue();
            captured.LogFilePath.Should().Be("/tmp/custom.log");
            captured.NoLogFile.Should().BeFalse();
            captured.Verbose.Should().BeTrue();
            captured.Quiet.Should().BeFalse();
        }
        finally
        {
            WatchCommand.HandlerOverride = origHandler;
        }
    }

    // 11. Defaults match spec when only required options provided.
    [Fact]
    public async Task Watch_Defaults_MatchSpec()
    {
        WatchOptions? captured = null;
        var origHandler = WatchCommand.HandlerOverride;
        WatchCommand.HandlerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var (exit, _, _) = await InvokeAsync("watch", _inputDir, "--output", _outputDir);

            exit.Should().Be(0);
            captured.Should().NotBeNull();
            captured!.Integrity.Should().Be(IntegrityLevel.Auto);
            captured.Concurrency.Should().Be(4);
            captured.Collision.Should().Be(CollisionPolicy.Skip);
            captured.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            captured.StabilityChecks.Should().Be(2);
            captured.MinFileSize.Should().Be(1024L);
            captured.SkipInitialScan.Should().BeFalse();
            captured.LogFilePath.Should().BeNull();
            captured.NoLogFile.Should().BeFalse();
            captured.Verbose.Should().BeFalse();
            captured.Quiet.Should().BeFalse();
        }
        finally
        {
            WatchCommand.HandlerOverride = origHandler;
        }
    }

    // --- T19b: --naming-template / --offline / --api-timeout / --api-rate-limit / --write-tags ---

    private async Task<(int exit, string stdout, string stderr, WatchOptions? captured)>
        InvokeWatchAsync(params string[] args)
    {
        WatchOptions? captured = null;
        var origHandler = WatchCommand.HandlerOverride;
        WatchCommand.HandlerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var (exit, stdout, stderr) = await InvokeAsync(args);
            return (exit, stdout, stderr, captured);
        }
        finally
        {
            WatchCommand.HandlerOverride = origHandler;
        }
    }

    [Fact]
    public async Task Watch_MetadataDefaults_AreSpecCorrect()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync("watch", _inputDir, "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.NamingTemplate.Should().Be("{artist} - {title}");
        captured.Offline.Should().BeFalse();
        captured.ApiTimeoutSeconds.Should().Be(10);
        captured.ApiRateLimit.Should().Be(0);
        // v3.3: --write-tags now defaults to true.
        captured.WriteTags.Should().BeTrue();
    }

    [Fact]
    public async Task Watch_NoWriteTagsFlag_DisablesTagWriting()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--no-write-tags");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        // v3.3: --no-write-tags opt-out flag overrides default --write-tags=true.
        captured!.WriteTags.Should().BeFalse();
    }

    [Fact]
    public async Task Watch_AllMetadataFlagsParsed()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir,
            "--naming-template", "{album}/{title}",
            "--offline",
            "--api-timeout", "30",
            "--api-rate-limit", "7",
            "--write-tags");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.NamingTemplate.Should().Be("{album}/{title}");
        captured.Offline.Should().BeTrue();
        captured.ApiTimeoutSeconds.Should().Be(30);
        captured.ApiRateLimit.Should().Be(7);
        captured.WriteTags.Should().BeTrue();
    }

    [Fact]
    public async Task Watch_NamingTemplate_Empty_Exit2()
    {
        var (exit, _, stderr, _) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--naming-template", "");

        exit.Should().Be(2);
        stderr.Should().Contain("template");
    }

    [Fact]
    public async Task Watch_NamingTemplate_NoPlaceholder_Exit2()
    {
        var (exit, _, stderr, _) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--naming-template", "justastring");

        exit.Should().Be(2);
        stderr.Should().Contain("template");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Watch_ApiTimeout_OutOfRange_Exit2(int seconds)
    {
        var (exit, _, stderr, _) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--api-timeout", seconds.ToString());

        exit.Should().Be(2);
        stderr.Should().Contain("api-timeout");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Watch_ApiRateLimit_OutOfRange_Exit2(int rate)
    {
        var (exit, _, stderr, _) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--api-rate-limit", rate.ToString());

        exit.Should().Be(2);
        stderr.Should().Contain("api-rate-limit");
    }

    [Fact]
    public async Task Watch_Offline_Switch_NoArg_ParsesTrue()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync(
            "watch", _inputDir, "--output", _outputDir, "--offline");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.Offline.Should().BeTrue();
    }
}

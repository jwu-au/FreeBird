using System;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using Autofac.Builder;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T15.6 D1 + D2 coverage: --quiet flag, verbose+quiet conflict, --integrity l3 fail-fast.
/// T19b additions: --naming-template / --offline / --api-timeout / --api-rate-limit / --write-tags
/// parsing + validation (run via <see cref="CliRoot"/> with <see cref="ScanRunner.RunnerOverride"/>
/// to capture the resolved <see cref="ScanOptions"/> without running the orchestrator).
/// </summary>
[Collection("ConsoleRedirect")]
public class ScanRunnerCliFlagsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public ScanRunnerCliFlagsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-cliflags-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- D1: --quiet flag ---

    [Fact]
    public async Task RunAsync_QuietFlag_OnlyWarningsAndAbove_StillExitOk()
    {
        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.Off,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false,
            quiet: true);

        exit.Should().Be(ScanRunner.ExitOk);
    }

    [Fact]
    public async Task RunAsync_BothVerboseAndQuiet_ReturnsBadArgs()
    {
        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.Off,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: true,
            quiet: true);

        exit.Should().Be(ScanRunner.ExitBadArgs);
    }

    // --- D2: --integrity l3 without flac on PATH must exit 2 at startup ---
    // Reverse-skippable: this test only meaningfully runs when flac is NOT available.
    // On dev machines where flac IS installed, the test passes vacuously (skipped logically).

    [Fact]
    public async Task RunAsync_L3WithoutFlac_FailsFast_ReturnsBadArgs()
    {
        if (FlacBinaryAvailable())
        {
            // flac is on PATH — startup probe would succeed; the failure path under test cannot trigger.
            // We assert the precondition is honored and let the test pass; manual / CI-without-flac
            // coverage exercises the failure branch.
            return;
        }

        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.L3,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false,
            quiet: false);

        exit.Should().Be(ScanRunner.ExitBadArgs);
    }

    private static bool FlacBinaryAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "flac",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return false; }
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // --- T19b: --naming-template / --offline / --api-timeout / --api-rate-limit / --write-tags ---

    private async Task<(int exit, string stdout, string stderr, ScanOptions? captured)>
        InvokeScanAsync(params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var outBuf = new StringWriter();
        var errBuf = new StringWriter();
        Console.SetOut(outBuf);
        Console.SetError(errBuf);
        ScanOptions? captured = null;
        var origOverride = ScanRunner.RunnerOverride;
        ScanRunner.RunnerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var exit = await CliRoot.InvokeAsync(args);
            return (exit, outBuf.ToString(), errBuf.ToString(), captured);
        }
        finally
        {
            ScanRunner.RunnerOverride = origOverride;
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Scan_Defaults_AreSpecCorrect()
    {
        var (exit, _, _, captured) = await InvokeScanAsync("scan", _inputDir, "-o", _outputDir);

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
    public async Task Scan_NoWriteTagsFlag_DisablesTagWriting()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--no-write-tags");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        // v3.3: --no-write-tags opt-out flag overrides default --write-tags=true.
        captured!.WriteTags.Should().BeFalse();
    }

    [Fact]
    public async Task Scan_AllFlagsParsed()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir,
            "--naming-template", "{musicId}_{title}",
            "--offline",
            "--api-timeout", "25",
            "--api-rate-limit", "5",
            "--write-tags");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.NamingTemplate.Should().Be("{musicId}_{title}");
        captured.Offline.Should().BeTrue();
        captured.ApiTimeoutSeconds.Should().Be(25);
        captured.ApiRateLimit.Should().Be(5);
        captured.WriteTags.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_NamingTemplate_Empty_Exit2()
    {
        var (exit, _, stderr, _) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--naming-template", "");

        exit.Should().Be(2);
        stderr.Should().Contain("template");
    }

    [Fact]
    public async Task Scan_NamingTemplate_NoPlaceholder_Exit2()
    {
        var (exit, _, stderr, _) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--naming-template", "justastring");

        exit.Should().Be(2);
        stderr.Should().Contain("template");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Scan_ApiTimeout_OutOfRange_Exit2(int seconds)
    {
        var (exit, _, stderr, _) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--api-timeout", seconds.ToString());

        exit.Should().Be(2);
        stderr.Should().Contain("api-timeout");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Scan_ApiRateLimit_OutOfRange_Exit2(int rate)
    {
        var (exit, _, stderr, _) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--api-rate-limit", rate.ToString());

        exit.Should().Be(2);
        stderr.Should().Contain("api-rate-limit");
    }

    [Fact]
    public async Task Scan_Offline_Switch_NoArg_ParsesTrue()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir, "-o", _outputDir, "--offline");

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.Offline.Should().BeTrue();
    }

    // --- T15: FlacOptionsBinder wiring through ScanRunner ---
    //
    // We assert FlacResolverOptions inside the container reflects CLI --flac-bin / --flac-url /
    // --no-auto-download values. Done via AdditionalContainerSetup which runs AFTER ScanRunner
    // installs its post-CoreModule FlacResolverOptions override; we register a probe scoped to
    // last-wins-after-that and resolve the override at scope time. Simplest pattern: run the
    // real container build, then read FlacResolverOptions out of the scope captured via probe.

    [Fact]
    public async Task ScanRunner_FlacBinFlag_PopulatesFlacResolverOptions()
    {
        FreeBird.Core.Provisioning.FlacResolverOptions? captured = null;
        ScanRunner.AdditionalContainerSetup = b =>
        {
            // Register a probe that captures the FlacResolverOptions at resolution time.
            // The runner's post-Module override has ALREADY been registered by the time
            // this hook runs, so Resolve here sees the CLI-driven values.
            b.RegisterBuildCallback(scope =>
            {
                captured = scope.Resolve<FreeBird.Core.Provisioning.FlacResolverOptions>();
            });
        };
        try
        {
            var exit = await ScanRunner.RunAsync(
                _inputDir, _outputDir,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true,
                flacBin: "/custom/path/to/flac",
                flacUrl: null,
                noAutoDownload: true);

            exit.Should().Be(ScanRunner.ExitOk);
            captured.Should().NotBeNull();
            captured!.FlacBinOverride.Should().Be("/custom/path/to/flac");
            captured.DisableAutoInstall.Should().BeTrue();
        }
        finally
        {
            ScanRunner.AdditionalContainerSetup = null;
        }
    }

    [Fact]
    public async Task ScanRunner_NoFlacFlags_DefaultsRemain()
    {
        FreeBird.Core.Provisioning.FlacResolverOptions? captured = null;
        ScanRunner.AdditionalContainerSetup = b =>
        {
            b.RegisterBuildCallback(scope =>
            {
                captured = scope.Resolve<FreeBird.Core.Provisioning.FlacResolverOptions>();
            });
        };
        try
        {
            // Ensure env vars are unset so we test the pure-default path
            var prevUrl = Environment.GetEnvironmentVariable("FREEBIRD_FLAC_URL");
            var prevNoAuto = Environment.GetEnvironmentVariable("FREEBIRD_NO_AUTO_DOWNLOAD");
            Environment.SetEnvironmentVariable("FREEBIRD_FLAC_URL", null);
            Environment.SetEnvironmentVariable("FREEBIRD_NO_AUTO_DOWNLOAD", null);
            try
            {
                var exit = await ScanRunner.RunAsync(
                    _inputDir, _outputDir,
                    IntegrityLevel.Off,
                    concurrency: 1,
                    collision: CollisionPolicy.Skip,
                    verbose: false,
                    quiet: true);

                exit.Should().Be(ScanRunner.ExitOk);
                captured.Should().NotBeNull();
                captured!.FlacBinOverride.Should().BeNull();
                captured.AutoInstallUrl.Should().BeNull();
                captured.DisableAutoInstall.Should().BeFalse();
                captured.AppBaseDirectory.Should().NotBeNullOrEmpty();
            }
            finally
            {
                Environment.SetEnvironmentVariable("FREEBIRD_FLAC_URL", prevUrl);
                Environment.SetEnvironmentVariable("FREEBIRD_NO_AUTO_DOWNLOAD", prevNoAuto);
            }
        }
        finally
        {
            ScanRunner.AdditionalContainerSetup = null;
        }
    }

    [Fact]
    public async Task ScanRunner_EnvVar_FlacUrl_FlowsThrough_WhenNoFlag()
    {
        FreeBird.Core.Provisioning.FlacResolverOptions? captured = null;
        ScanRunner.AdditionalContainerSetup = b =>
        {
            b.RegisterBuildCallback(scope =>
            {
                captured = scope.Resolve<FreeBird.Core.Provisioning.FlacResolverOptions>();
            });
        };
        var prevUrl = Environment.GetEnvironmentVariable("FREEBIRD_FLAC_URL");
        Environment.SetEnvironmentVariable("FREEBIRD_FLAC_URL", "https://mirror.example.com/flac.zip");
        try
        {
            var exit = await ScanRunner.RunAsync(
                _inputDir, _outputDir,
                IntegrityLevel.Off,
                concurrency: 1,
                collision: CollisionPolicy.Skip,
                verbose: false,
                quiet: true);

            exit.Should().Be(ScanRunner.ExitOk);
            captured.Should().NotBeNull();
            captured!.AutoInstallUrl.Should().Be("https://mirror.example.com/flac.zip");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FREEBIRD_FLAC_URL", prevUrl);
            ScanRunner.AdditionalContainerSetup = null;
        }
    }
}

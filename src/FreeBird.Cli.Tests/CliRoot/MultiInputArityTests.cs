using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests.CliRootTests;

/// <summary>
/// T14+T15 (v3.4): `fb scan` and `fb watch` accept one or more positional
/// input directories (<c>Argument&lt;List&lt;string&gt;&gt;</c> with
/// <see cref="System.CommandLine.ArgumentArity.OneOrMore"/>). Single-input
/// invocation must remain backward compatible.
/// </summary>
[Collection("GlobalStaticState")]
public class MultiInputArityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir1;
    private readonly string _inputDir2;
    private readonly string _inputDir3;
    private readonly string _outputDir;

    public MultiInputArityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-multi-arity-{Guid.NewGuid():N}");
        _inputDir1 = Path.Combine(_tempDir, "in1");
        _inputDir2 = Path.Combine(_tempDir, "in2");
        _inputDir3 = Path.Combine(_tempDir, "in3");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir1);
        Directory.CreateDirectory(_inputDir2);
        Directory.CreateDirectory(_inputDir3);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- helpers ---

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

    private async Task<(int exit, string stdout, string stderr, WatchOptions? captured)>
        InvokeWatchAsync(params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var outBuf = new StringWriter();
        var errBuf = new StringWriter();
        Console.SetOut(outBuf);
        Console.SetError(errBuf);
        WatchOptions? captured = null;
        var origHandler = WatchCommand.HandlerOverride;
        WatchCommand.HandlerOverride = opts => { captured = opts; return Task.FromResult(0); };
        try
        {
            var exit = await CliRoot.InvokeAsync(args);
            return (exit, outBuf.ToString(), errBuf.ToString(), captured);
        }
        finally
        {
            WatchCommand.HandlerOverride = origHandler;
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    // --- scan ---

    [Fact]
    public async Task Scan_AcceptsSingleInputDir_BackwardCompat()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir1, "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.InputDirectories.Should().HaveCount(1);
        // ScanRunner normalizes via Path.GetFullPath; compare on full path.
        captured.InputDirectories[0].Should().Be(Path.GetFullPath(_inputDir1));
    }

    [Fact]
    public async Task Scan_AcceptsMultipleInputDirs()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir1, _inputDir2, _inputDir3,
            "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.InputDirectories.Should().HaveCount(3);
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir1));
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir2));
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir3));
    }

    [Fact]
    public async Task Scan_ZeroInputDirs_ParseError()
    {
        var (exit, _, _, _) = await InvokeScanAsync(
            "scan", "--output", _outputDir);

        // System.CommandLine should reject zero positional inputs when arity is OneOrMore.
        // v3.4.1: the previous `captured.Should().BeNull()` assertion was removed because
        // ScanRunner.RunnerOverride is a process-wide static and xUnit runs different test
        // classes in parallel — parallel classes (e.g. ScanRunnerEmptyDirTests) could leak
        // a captured ScanOptions into this test on ubuntu CI. The real signal here is just
        // that System.CommandLine rejected the invocation (exit != 0).
        exit.Should().NotBe(0);
    }

    // --- watch ---

    [Fact]
    public async Task Watch_AcceptsSingleInputDir_BackwardCompat()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync(
            "watch", _inputDir1, "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.InputDirs.Should().BeEquivalentTo(new[] { _inputDir1 });
    }

    [Fact]
    public async Task Watch_AcceptsMultipleInputDirs()
    {
        var (exit, _, _, captured) = await InvokeWatchAsync(
            "watch", _inputDir1, _inputDir2, _inputDir3,
            "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.InputDirs.Should().BeEquivalentTo(new[] { _inputDir1, _inputDir2, _inputDir3 });
    }

    // --- interleaved options edge case ---
    // Documents System.CommandLine's parsing behavior for
    // `fb scan dir1 dir2 --integrity l3 dir3 --output X` — i.e. when an option
    // appears between positional inputs. Captures whatever the parser does so
    // future System.CommandLine upgrades don't silently change behavior.

    [Fact]
    public async Task Scan_OptionAfterInputs_OptionParsedAndRemainingArgsTreatedAsInputs()
    {
        var (exit, _, _, captured) = await InvokeScanAsync(
            "scan", _inputDir1, _inputDir2,
            "--integrity", "l3",
            _inputDir3,
            "--output", _outputDir);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.Integrity.Should().Be(IntegrityLevel.L3);
        // System.CommandLine 2.0 beta with OneOrMore arity stops collecting positionals
        // at the first `--option` token but resumes for any trailing positionals that
        // appear after the option's value(s). Either way, dir1+dir2 are inputs and
        // dir3 should also be an input (after integrity consumes its value).
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir1));
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir2));
        captured.InputDirectories.Should().Contain(Path.GetFullPath(_inputDir3));
    }
}

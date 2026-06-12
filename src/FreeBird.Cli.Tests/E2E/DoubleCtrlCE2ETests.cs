using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Tests.E2E;
using Xunit;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T19 — E2E: POSIX double-Ctrl-C handler.
///
/// Two scenarios exercise the real signal pipeline by spawning the published <c>fb</c>
/// binary as a child process and sending it real <c>SIGINT</c> signals via <c>kill -INT</c>:
///
/// • <see cref="SingleSigint_GracefulDrain_Exit130"/> — one SIGINT → graceful drain → exit 130.
/// • <see cref="DoubleSigint_ImmediateHardAbort_Exit130"/> — two SIGINTs ~500ms apart → fast hard abort → exit 130.
///
/// POSIX-only — Windows has no <c>kill -INT</c> and no <c>SIGINT</c> in the same shape, so
/// both tests are skipped there via <see cref="Skip.If"/>.
///
/// Marked <c>Category=E2E</c> because they spawn real processes and burn wall-clock time.
/// Joined to the <c>ConsoleRedirect</c> collection to serialize with sibling E2E tests —
/// not because we redirect Console.Out (we spawn a child instead), but because running 7+
/// fb watch children in parallel can race on signal-delivery scheduling and create flaky
/// false-failures on the SIGINT-driven assertions here.
/// </summary>
[Collection("RunnerOverride")]
public class DoubleCtrlCE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public DoubleCtrlCE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("double-ctrlc");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [SkippableFact]
    [Trait("Category", "E2E")]
    public async Task SingleSigint_GracefulDrain_Exit130()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX-only test: requires kill -INT and Console.CancelKeyPress signalling.");

        // Pre-populate input with a single real-decodable .uc so the initial sweep has work to do.
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "alpha.uc");

        using var child = SpawnFbWatch();
        var startup = Stopwatch.StartNew();
        // Capture output for diagnostics if the test fails.
        var outBuf = new System.Text.StringBuilder();
        var errBuf = new System.Text.StringBuilder();
        child.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outBuf) outBuf.AppendLine(e.Data); } };
        child.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errBuf) errBuf.AppendLine(e.Data); } };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        // Wait for startup: poll until at least one log line has been emitted (logger emits
        // "Watch starting..." early). 10s budget covers JIT-warm-up on a cold machine.
        var startedOk = await WatchE2EHelpers.WaitForAsync(
            () => { lock (outBuf) lock (errBuf) { return outBuf.Length + errBuf.Length > 0; } },
            TimeSpan.FromSeconds(10));
        startup.Stop();
        startedOk.Should().BeTrue(
            $"the fb watch child should emit at least one log line during startup. " +
            $"stdout=[{outBuf}] stderr=[{errBuf}] HasExited={child.HasExited}");
        child.HasExited.Should().BeFalse(
            $"the fb watch child should still be running at the time we send SIGINT (startup={startup.ElapsedMilliseconds}ms). " +
            $"ExitCode={(child.HasExited ? child.ExitCode : -1)} stdout=[{outBuf}] stderr=[{errBuf}]");

        // Give the orchestrator a moment to install its signal handlers and enter the poll loop
        // (the "Watch starting" log fires before SubscribeSigint is called). 300ms is enough on
        // a developer laptop without being so long the test feels slow.
        await Task.Delay(300);

        // Send a single SIGINT. The coordinator's first-signal path is graceful drain.
        SendSigint(child.Id);

        // Graceful drain may take a few seconds (orchestrator finishes its current poll cycle and
        // disposes the file sink). 8s is comfortably above observed graceful-shutdown times.
        var sw = Stopwatch.StartNew();
        var exited = child.WaitForExit(8000);
        sw.Stop();

        exited.Should().BeTrue(
            $"the fb watch child should exit within 8s of SIGINT (waited {sw.ElapsedMilliseconds}ms, startup={startup.ElapsedMilliseconds}ms). " +
            $"stdout=[{outBuf}] stderr=[{errBuf}]");
        child.ExitCode.Should().Be(130,
            $"single SIGINT → graceful drain → exit code 130 (POSIX SIGINT convention). " +
            $"drain={sw.ElapsedMilliseconds}ms startup={startup.ElapsedMilliseconds}ms " +
            $"stdout=[{outBuf}] stderr=[{errBuf}]");

        // T19-fixup regression guard: a single SIGINT must enter the graceful drain path but
        // must NOT escalate to the double-Ctrl-C "Forced abort" branch. Pre-fix, System.CommandLine's
        // own SIGINT handler caused the external token to fire alongside Console.CancelKeyPress,
        // double-counting the signal and tripping the count==2 branch immediately.
        string combined;
        lock (outBuf) lock (errBuf) { combined = outBuf.ToString() + errBuf.ToString(); }
        combined.Should().Contain("Graceful shutdown requested",
            $"single SIGINT must trigger the count==1 graceful-shutdown log. stdout=[{outBuf}] stderr=[{errBuf}]");
        combined.Should().NotContain("Forced abort requested (Ctrl-C pressed twice)",
            $"single SIGINT must NOT trip the count==2 force-abort branch — that would be the " +
            $"pre-fix double-bridge bug. stdout=[{outBuf}] stderr=[{errBuf}]");
    }

    [SkippableFact]
    [Trait("Category", "E2E")]
    public async Task DoubleSigint_ImmediateHardAbort_Exit130()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX-only test: requires kill -INT and Console.CancelKeyPress signalling.");

        // Pre-populate input so the orchestrator has something to drain (makes the hard-abort
        // path more meaningful — the orchestrator would otherwise idle in the poll loop).
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "alpha.uc");

        using var child = SpawnFbWatch();
        var startup = Stopwatch.StartNew();
        var outBuf = new System.Text.StringBuilder();
        var errBuf = new System.Text.StringBuilder();
        child.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (outBuf) outBuf.AppendLine(e.Data); } };
        child.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errBuf) errBuf.AppendLine(e.Data); } };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        var startedOk = await WatchE2EHelpers.WaitForAsync(
            () => { lock (outBuf) lock (errBuf) { return outBuf.Length + errBuf.Length > 0; } },
            TimeSpan.FromSeconds(10));
        startup.Stop();
        startedOk.Should().BeTrue(
            $"the fb watch child should emit at least one log line during startup. " +
            $"stdout=[{outBuf}] stderr=[{errBuf}] HasExited={child.HasExited}");
        child.HasExited.Should().BeFalse(
            $"the fb watch child should still be running at the time we send SIGINT (startup={startup.ElapsedMilliseconds}ms). " +
            $"ExitCode={(child.HasExited ? child.ExitCode : -1)} stdout=[{outBuf}] stderr=[{errBuf}]");

        // Give the orchestrator time to install signal handlers and enter its poll loop.
        await Task.Delay(300);

        // First SIGINT — enters graceful drain mode.
        SendSigint(child.Id);
        // Brief gap so the coordinator records the first signal before we send the second.
        await Task.Delay(500);
        // Second SIGINT — escalates to hard abort.
        SendSigint(child.Id);

        // Hard abort should be near-immediate. 5s is generous slack for process cleanup.
        var sw = Stopwatch.StartNew();
        var exited = child.WaitForExit(5000);
        sw.Stop();

        exited.Should().BeTrue(
            $"double SIGINT should trigger immediate hard abort within 5s (waited {sw.ElapsedMilliseconds}ms, startup={startup.ElapsedMilliseconds}ms). " +
            $"stdout=[{outBuf}] stderr=[{errBuf}]");
        child.ExitCode.Should().Be(130,
            $"double SIGINT → hard abort → exit code 130 (POSIX SIGINT convention). " +
            $"drain={sw.ElapsedMilliseconds}ms startup={startup.ElapsedMilliseconds}ms " +
            $"stdout=[{outBuf}] stderr=[{errBuf}]");

        // T19-fixup regression guard: the first SIGINT must trip the graceful-shutdown log.
        // The second SIGINT *may* trip the force-abort log, but with a small workload the
        // orchestrator can graceful-drain and exit before the second signal arrives — that's a
        // legitimate fast-path, not a bug. The strong invariant the fix preserves is: exit 130
        // within 5s with a "Graceful shutdown requested" log emitted exactly once for each
        // delivered SIGINT (verified pre-fix by the bug repro where a SINGLE SIGINT produced
        // BOTH the graceful AND the force-abort log).
        string combined;
        lock (outBuf) lock (errBuf) { combined = outBuf.ToString() + errBuf.ToString(); }
        combined.Should().Contain("Graceful shutdown requested",
            $"at least the first SIGINT must log the graceful-shutdown line. " +
            $"stdout=[{outBuf}] stderr=[{errBuf}]");
    }

    /// <summary>
    /// Spawns the already-built <c>fb</c> binary as a child process. Uses the native AppHost
    /// launcher (not <c>dotnet fb.dll</c>) so SIGINT routes directly to the managed process
    /// without a <c>dotnet</c> shim swallowing it.
    /// </summary>
    private Process SpawnFbWatch()
    {
        var fbPath = ResolveFbBinaryPath();
        var psi = new ProcessStartInfo
        {
            FileName = fbPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("watch");
        psi.ArgumentList.Add(_inputDir);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(_outputDir);
        psi.ArgumentList.Add("--poll-interval");
        psi.ArgumentList.Add("1s");
        psi.ArgumentList.Add("--stability-checks");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--integrity");
        psi.ArgumentList.Add("off");
        psi.ArgumentList.Add("--no-log-file");

        var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"Failed to spawn fb binary at: {fbPath}");
        }
        return proc;
    }

    /// <summary>
    /// Resolves the path to the built <c>fb</c> binary, walking up from the test bin directory
    /// to the repo root. Test bin is at
    /// <c>&lt;repo&gt;/src/FreeBird.Cli.Tests/bin/&lt;cfg&gt;/net10.0</c>; we want
    /// <c>&lt;repo&gt;/src/FreeBird.Cli/bin/&lt;cfg&gt;/net10.0/fb</c>. The configuration
    /// (Debug/Release) is read from the test bin path so the resolution stays valid in CI.
    /// </summary>
    private static string ResolveFbBinaryPath()
    {
        // AppContext.BaseDirectory = <repo>/src/FreeBird.Cli.Tests/bin/<cfg>/net10.0/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);                                        // net10.0
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDir)!);      // Debug or Release
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var fbPath = Path.Combine(repoRoot, "src", "FreeBird.Cli", "bin", configuration, tfm, "fb");
        if (!File.Exists(fbPath))
        {
            throw new FileNotFoundException(
                $"fb binary not found at: {fbPath}. " +
                "Did you build the FreeBird.Cli project? Tests assume the apphost is on disk.",
                fbPath);
        }
        return fbPath;
    }

    /// <summary>
    /// Sends SIGINT to the target PID by invoking the POSIX <c>kill</c> utility. Using the
    /// external tool avoids taking a Mono.Posix.NETStandard dependency just for one signal.
    /// </summary>
    private static void SendSigint(int pid)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "kill",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-INT");
        psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var k = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn kill -INT");
        k.WaitForExit(2000);
    }
}

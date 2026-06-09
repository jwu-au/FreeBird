using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// Shared helpers for watch-mode E2E tests. These tests are wall-clock slow because they
/// drive the real <see cref="WatchRunner"/> against real temp directories — mark each one
/// with <c>[Trait("Category", "E2E")]</c> so developers can opt out via
/// <c>dotnet test --filter "Category!=E2E"</c>.
///
/// Pattern: each test creates temp input/output dirs, populates input with real
/// XOR-encrypted .uc files (via <see cref="TestFixtures.WriteFixtureAsUcAsync"/>),
/// launches the runner in the background with a time-bounded CTS, asserts on output,
/// and lets the runner unwind when the CTS fires.
/// </summary>
internal static class WatchE2EHelpers
{
    /// <summary>
    /// Builds a unique temp dir path (does NOT create it — caller decides).
    /// </summary>
    public static string CreateTempPath(string label)
    {
        return Path.Combine(Path.GetTempPath(), $"freebird-e2e-{label}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Launches <see cref="WatchRunner.RunAsync"/> on a background task. The returned
    /// task completes when the runner exits (either because <paramref name="runDuration"/>
    /// elapsed and the linked CTS fired, or because the orchestrator returned naturally).
    /// </summary>
    public static Task<int> StartWatchAsync(
        WatchOptions options,
        CancellationToken externalToken)
    {
        var runner = new WatchRunner();
        return Task.Run(() => runner.RunAsync(options, externalToken));
    }

    /// <summary>
    /// Polls for <paramref name="path"/> to exist, returning true if it appears before
    /// <paramref name="timeout"/>. Uses a 100ms poll interval — fast enough to be
    /// responsive in tests, slow enough not to peg a core.
    /// </summary>
    public static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path)) { return true; }
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> returns true or the deadline elapses.
    /// </summary>
    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) { return true; }
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// Best-effort recursive delete — swallows IO errors that can happen on Windows when
    /// the file-sink is still flushing log files in a background task.
    /// </summary>
    public static void CleanupDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Builds a <see cref="WatchOptions"/> populated with values suitable for E2E:
    /// no log file (to keep temp dirs tidy), real input/output dirs, and the caller's
    /// timing knobs.
    /// </summary>
    public static WatchOptions MakeOptions(
        string inputDir,
        string outputDir,
        TimeSpan pollInterval,
        int stabilityChecks,
        bool skipInitialScan = false,
        long minFileSize = 0L)
    {
        return new WatchOptions
        {
            InputDir = inputDir,
            OutputDir = outputDir,
            Integrity = FreeBird.Core.Models.IntegrityLevel.Off,
            Concurrency = 2,
            Collision = FreeBird.Core.Models.CollisionPolicy.Overwrite,
            PollInterval = pollInterval,
            StabilityChecks = stabilityChecks,
            MinFileSize = minFileSize,
            SkipInitialScan = skipInitialScan,
            NoLogFile = true,
            Verbose = false,
            Quiet = true,
        };
    }
}

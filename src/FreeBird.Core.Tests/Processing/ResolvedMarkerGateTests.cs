using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using Microsoft.Extensions.Time.Testing;
using Serilog;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// T1 (unify-marker-aware-skip) — unit tests for the new stateless
/// <see cref="IResolvedMarkerGate"/> / <see cref="ResolvedMarkerGate"/>, which
/// owns the marker short-circuit logic extracted verbatim from
/// <c>FilesystemSkipDecider.TryShortCircuitOnMarker</c> (Branch 3b).
///
/// Deterministic: uses <see cref="FakeTimeProvider"/> for the retry gate and a
/// real temp dir for marker + output files (round-tripped through the real
/// <see cref="ResolutionMarkerSerializer"/> so the on-disk format is authentic).
/// </summary>
public sealed class ResolvedMarkerGateTests : IDisposable
{
    private const string Template = "{artist} - {title}";

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-gate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "in"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        _tempDirs.Add(root);
        return root;
    }

    private static string OutDir(string root) => Path.Combine(root, "out");

    private static string CreateSource(string root, string fileName, byte[]? bytes = null)
    {
        var path = Path.Combine(root, "in", fileName);
        File.WriteAllBytes(path, bytes ?? new byte[] { 1, 2, 3 });
        return path;
    }

    private static (ResolvedMarkerGate gate, ResolutionMarkerSerializer serializer, FakeTimeProvider time) MakeSut()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().CreateLogger();
        var serializer = new ResolutionMarkerSerializer(logger);
        var time = new FakeTimeProvider();
        var gate = new ResolvedMarkerGate(serializer, logger, time);
        return (gate, serializer, time);
    }

    private static ResolutionMarker FreshResolvedMarker(string sourcePath, string outputName)
    {
        var info = new FileInfo(sourcePath);
        return new ResolutionMarker
        {
            Schema = 2,
            SourceStem = StemBasedFileNamer.GetStem(sourcePath),
            MusicId = "3367798042",
            SourcePath = sourcePath,
            SourceSize = info.Length,
            SourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ResolvedAt = DateTimeOffset.UtcNow,
            Status = MarkerStatus.Resolved,
            OutputName = outputName,
            Format = "Flac",
            Integrity = "L3",
            NamingTemplate = Template,
            Reason = null,
            RetryAfter = null,
        };
    }

    private static void WriteOutput(string root, string outputName) =>
        File.WriteAllBytes(Path.Combine(OutDir(root), outputName), new byte[] { 0xFF });

    // (a) resolved + fresh + output-exists => Skip(AlreadyDecodedViaMarker)
    [Fact]
    public void ResolvedFreshOutputExists_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, _) = MakeSut();
        serializer.WriteAtomic(OutDir(root), FreshResolvedMarker(src, outputName));
        WriteOutput(root, outputName);

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().NotBeNull();
        decision!.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
    }

    // (b1) failure-status + RetryAfter in the FUTURE => Skip (honor backoff)
    [Fact]
    public void FailureStatus_RetryAfterFuture_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, time) = MakeSut();
        var now = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        time.SetUtcNow(now);
        var marker = FreshResolvedMarker(src, outputName) with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = now.AddMinutes(30),
        };
        serializer.WriteAtomic(OutDir(root), marker);
        WriteOutput(root, outputName);

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().NotBeNull();
        decision!.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
    }

    // (b2) failure-status + RetryAfter ELAPSED => null (re-process)
    [Fact]
    public void FailureStatus_RetryAfterElapsed_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, time) = MakeSut();
        var now = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        time.SetUtcNow(now);
        var marker = FreshResolvedMarker(src, outputName) with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = now.AddMinutes(-30),
        };
        serializer.WriteAtomic(OutDir(root), marker);
        WriteOutput(root, outputName);

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().BeNull();
    }

    // (c) source size changed => null
    [Fact]
    public void SourceSizeChanged_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, _) = MakeSut();
        serializer.WriteAtomic(OutDir(root), FreshResolvedMarker(src, outputName));
        WriteOutput(root, outputName);

        // Grow the source AFTER the marker captured its size.
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4, 5, 6 });

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().BeNull();
    }

    // (d) naming-template changed => null
    [Fact]
    public void NamingTemplateChanged_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, _) = MakeSut();
        serializer.WriteAtomic(OutDir(root), FreshResolvedMarker(src, outputName));
        WriteOutput(root, outputName);

        var decision = gate.TryShortCircuit(src, OutDir(root), "{title}");

        decision.Should().BeNull();
    }

    // (e) marker references missing output => null
    [Fact]
    public void OutputMissing_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, _) = MakeSut();
        serializer.WriteAtomic(OutDir(root), FreshResolvedMarker(src, outputName));
        // intentionally do NOT write the output file

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().BeNull();
    }

    // (f) no marker file => null
    [Fact]
    public void NoMarker_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var (gate, _, _) = MakeSut();

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().BeNull();
    }

    // (g) unsupported schema => null
    [Fact]
    public void UnsupportedSchema_ReturnsNull()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        const string outputName = "3367798042.flac";
        var (gate, serializer, _) = MakeSut();
        serializer.WriteAtomic(OutDir(root), FreshResolvedMarker(src, outputName) with { Schema = 99 });
        WriteOutput(root, outputName);

        var decision = gate.TryShortCircuit(src, OutDir(root), Template);

        decision.Should().BeNull();
    }

    // The gate must auto-register via the IDependency convention.
    [Fact]
    public void Gate_ImplementsIDependency()
    {
        typeof(IResolvedMarkerGate).Should().BeAssignableTo<IDependency>();
    }
}

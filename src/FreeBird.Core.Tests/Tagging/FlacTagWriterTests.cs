using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Metadata;
using FreeBird.Core.Provisioning;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.Tagging;

public sealed class FlacTagWriterTests
{
    // ---- helpers ----------------------------------------------------------

    private static ILogger NullLogger()
    {
        // Mock<ILogger> returns null for ForContext<T>() by default, which then NREs
        // when production code chains a level call on it. Setting it to return the
        // mock itself keeps the call chain alive without writing actual log output.
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    // Default resolver returns the literal "metaflac" path — preserves prior tests
    // that asserted the subprocess was invoked as "metaflac".
    private static IFlacBinaryResolver DefaultResolver(string metaflacPath = "metaflac")
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution(metaflacPath, FlacBinaryProvenance.Path));
        m.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution("flac", FlacBinaryProvenance.Path));
        return m.Object;
    }

    private static IFlacBinaryResolver NotFoundResolver()
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(FlacResolution.NotFound);
        m.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(FlacResolution.NotFound);
        return m.Object;
    }

    private static FlacTagWriter Build(Mock<IProcessRunner> runner, IFlacBinaryResolver? resolver = null)
        => new(runner.Object, resolver ?? DefaultResolver(), NullLogger());

    private static Mock<IProcessRunner> RunnerReturning(int exit, string stdout = "", string stderr = "")
    {
        var m = new Mock<IProcessRunner>();
        m.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(new ProcessResult(exit, stdout, stderr));
        return m;
    }

    // ---- arg-shape tests --------------------------------------------------

    [Fact]
    public void BuildArgs_SingleArtist_ProducesExpectedSequence()
    {
        var song = new SongInfo(1, "Title", new[] { "Artist" }, Album: "Album");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Equal(
            "--remove-tag=ARTIST",
            "--remove-tag=TITLE",
            "--remove-tag=ALBUM",
            "--set-tag=ARTIST=Artist",
            "--set-tag=TITLE=Title",
            "--set-tag=ALBUM=Album",
            "/tmp/x.flac");
    }

    [Fact]
    public void BuildArgs_MultiArtist_JoinedWith_Slash()
    {
        var song = new SongInfo(1, "T", new[] { "A", "B" });

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Contain("--set-tag=ARTIST=A/B");
    }

    [Fact]
    public void BuildArgs_NullAlbum_OmitsAlbumTag()
    {
        var song = new SongInfo(1, "T", new[] { "A" });   // Album defaults to null

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().NotContain(a => a.StartsWith("--set-tag=ALBUM=", StringComparison.Ordinal));
        args.Should().EndWith("/tmp/x.flac");
    }

    [Fact]
    public void BuildArgs_EmptyAlbum_OmitsAlbumTag()
    {
        var song = new SongInfo(1, "T", new[] { "A" }, Album: "");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().NotContain(a => a.StartsWith("--set-tag=ALBUM=", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArgs_PreservesSpacesInFilenamePath()
    {
        var song = new SongInfo(1, "T", new[] { "A" });

        var args = FlacTagWriter.BuildArgs("/tmp/with space.flac", song);

        // Path is passed verbatim; IProcessRunner is responsible for quoting/escaping
        // (it uses ArgumentList which avoids shell interpretation entirely).
        args[^1].Should().Be("/tmp/with space.flac");
    }

    [Fact]
    public void BuildArgs_CjkContent_PassedThroughUnchanged()
    {
        var song = new SongInfo(1, "终不负", new[] { "树离suliii_" }, Album: "终不负");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Contain("--set-tag=ARTIST=树离suliii_");
        args.Should().Contain("--set-tag=TITLE=终不负");
        args.Should().Contain("--set-tag=ALBUM=终不负");
    }

    // ---- subprocess outcome tests -----------------------------------------

    [Fact]
    public async Task WriteAsync_ZeroExit_ReturnsSuccess()
    {
        var runner = RunnerReturning(0);
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Success>();
    }

    [Fact]
    public async Task WriteAsync_NonZeroExit_ReturnsFailed_TagWriteFailed()
    {
        var runner = RunnerReturning(1, stderr: "metaflac: invalid FLAC");
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-write-failed");
    }

    [Fact]
    public async Task WriteAsync_RunnerThrowsWin32Exception_ReturnsFailed_TagToolMissing()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new System.ComponentModel.Win32Exception("No such file"));
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-tool-missing");
    }

    [Fact]
    public async Task WriteAsync_RunnerThrowsOther_ReturnsFailed_TagIoError()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-io-error");
    }

    [Fact]
    public async Task WriteAsync_CallsMetaflac_AsFileName_AndPropagatesArgs()
    {
        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExe = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<string>, CancellationToken>((exe, args, _) =>
              {
                  capturedExe = exe;
                  capturedArgs = args;
              })
              .ReturnsAsync(new ProcessResult(0, "", ""));
        var sut = Build(runner);

        await sut.WriteAsync("/tmp/x.flac", new SongInfo(42, "T", new[] { "A" }), CancellationToken.None);

        capturedExe.Should().Be("metaflac");
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().StartWith(new[] { "--remove-tag=ARTIST", "--remove-tag=TITLE", "--remove-tag=ALBUM" });
        capturedArgs.Should().EndWith(new[] { "/tmp/x.flac" });
    }

    [Fact]
    public async Task WriteAsync_CancellationPropagates()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException());
        var sut = Build(runner);

        await ((Func<Task>)(async () =>
            await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- constructor guards ----------------------------------------------

    [Fact]
    public void Constructor_NullRunner_Throws()
        => ((Action)(() => _ = new FlacTagWriter(null!, DefaultResolver(), new Mock<ILogger>().Object)))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Constructor_NullResolver_Throws()
        => ((Action)(() => _ = new FlacTagWriter(new Mock<IProcessRunner>().Object, null!, new Mock<ILogger>().Object)))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Constructor_NullLogger_Throws()
        => ((Action)(() => _ = new FlacTagWriter(new Mock<IProcessRunner>().Object, DefaultResolver(), null!)))
            .Should().Throw<ArgumentNullException>();

    // ---- resolver-driven tests (T12) --------------------------------------

    [Fact]
    public async Task WriteAsync_ResolverNotFound_ThrowsFlacNotAvailableException()
    {
        // When the resolver returns NotFound, the writer should throw rather than
        // silently produce a 'tag-tool-missing' sidecar — callers (CompositeTagWriter
        // / FileProcessor in T13) translate the exception into pipeline policy.
        var runner = RunnerReturning(0);
        var sut = Build(runner, NotFoundResolver());

        Func<Task> act = () => sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        await act.Should().ThrowAsync<FlacNotAvailableException>();
    }

    [Fact]
    public async Task WriteAsync_UsesResolvedMetaflacPath_NotHardcodedString()
    {
        // Verify the process runner receives the resolver-supplied path. This is
        // the core T12 contract: no more hardcoded "metaflac" literal in production code.
        const string ResolvedPath = "/opt/homebrew/bin/metaflac";
        string? capturedExe = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<string>, CancellationToken>((exe, _, _) => capturedExe = exe)
              .ReturnsAsync(new ProcessResult(0, "", ""));
        var sut = Build(runner, DefaultResolver(metaflacPath: ResolvedPath));

        await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        capturedExe.Should().Be(ResolvedPath);
    }

    // ---- v3.3 T01: unrelated-tag preservation (RED) -----------------------

    private static readonly bool MetaflacAvailable = ProbeMetaflacOnce();

    private static bool ProbeMetaflacOnce()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "metaflac",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <c>metaflac</c> with the given args, throwing on non-zero exit so test
    /// setup failures surface clearly. Stdout is returned for callers that need to
    /// parse exported tags.
    /// </summary>
    private static string RunMetaflac(params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "metaflac",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
        .WithArgs(args))!;

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"metaflac failed (exit {p.ExitCode}) args=[{string.Join(' ', args)}] stderr={stderr}");
        }
        return stdout;
    }

    /// <summary>
    /// Returns all VorbisComment KEY=VALUE pairs in <paramref name="flacPath"/> by
    /// invoking <c>metaflac --export-tags-to=-</c>. Returned as a list so duplicate
    /// keys (legal in Vorbis) are preserved for inspection.
    /// </summary>
    private static IReadOnlyList<(string Key, string Value)> ReadAllTags(string flacPath)
    {
        var stdout = RunMetaflac("--export-tags-to=-", flacPath);
        var pairs = new List<(string, string)>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) { continue; }
            pairs.Add((line[..eq], line[(eq + 1)..]));
        }
        return pairs;
    }

    [SkippableFact]
    public async Task WriteAsync_PreservesUnrelatedTags_LikeGenreAndDateAndReplayGain()
    {
        // RED for v3.3 T01. The current FlacTagWriter passes `--remove-all-tags`
        // to metaflac, which nukes EVERY VorbisComment — including user-curated
        // GENRE/DATE/REPLAYGAIN/ENCODER that FreeBird itself never wrote. Before
        // v3.3 flips --write-tags default to true, the writer must be made
        // non-destructive (T02 fix: per-key --remove-tag for only the keys we
        // re-set). This test pins that contract.
        Skip.IfNot(MetaflacAvailable, "metaflac binary not on PATH");

        var tempDir = Path.Combine(Path.GetTempPath(), $"fb-t01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Copy the shared sample fixture to a writable temp file so we don't
            // mutate the original under bin/.
            var flacPath = Path.Combine(tempDir, "seeded.flac");
            File.Copy(Fx.SampleFlacPath, flacPath, overwrite: true);

            // Seed the file with VorbisComments that FreeBird does NOT manage.
            // Clearing first guarantees a known starting state regardless of
            // whatever the shared fixture happened to contain.
            RunMetaflac("--remove-all-tags", flacPath);
            RunMetaflac(
                "--set-tag=GENRE=Rock",
                "--set-tag=DATE=2020",
                "--set-tag=REPLAYGAIN_TRACK_GAIN=-7.50 dB",
                "--set-tag=ENCODER=lame 3.100",
                flacPath);

            // Sanity-check the seed actually took effect — if this fails, the
            // test's RED assertion below would be meaningless.
            var seeded = ReadAllTags(flacPath);
            seeded.Should().Contain(("GENRE", "Rock"));
            seeded.Should().Contain(("DATE", "2020"));
            seeded.Should().Contain(("REPLAYGAIN_TRACK_GAIN", "-7.50 dB"));
            seeded.Should().Contain(("ENCODER", "lame 3.100"));

            // Real subprocess + real metaflac on PATH. We deliberately do NOT
            // use Moq here: the destructive behavior lives in metaflac's
            // interpretation of --remove-all-tags, so we must invoke it for real.
            var resolver = new Mock<IFlacBinaryResolver>();
            resolver.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new FlacResolution("metaflac", FlacBinaryProvenance.Path));
            var sut = new FlacTagWriter(new SystemProcessRunner(), resolver.Object, NullLogger());

            var song = new SongInfo(
                MusicId: 12345,
                Title: "Test Title",
                Artists: new[] { "Test Artist A", "Test Artist B" },
                Album: "Test Album");

            var result = await sut.WriteAsync(flacPath, song, CancellationToken.None);
            result.Should().BeOfType<TagWriteResult.Success>(
                "the tag-write subprocess itself must succeed before we can talk about preservation");

            var tagsAfter = ReadAllTags(flacPath);

            // The write itself must have landed (this part already works today).
            tagsAfter.Should().Contain(("ARTIST", "Test Artist A/Test Artist B"));
            tagsAfter.Should().Contain(("TITLE", "Test Title"));
            tagsAfter.Should().Contain(("ALBUM", "Test Album"));

            // The four assertions below are the RED gate — they fail on current
            // main because --remove-all-tags wipes everything before --set-tag
            // rebuilds only ARTIST/TITLE/ALBUM. T02 will turn these green by
            // switching to per-key --remove-tag.
            tagsAfter.Should().Contain(("GENRE", "Rock"),
                "GENRE is user-curated and must survive a FreeBird tag write");
            tagsAfter.Should().Contain(("DATE", "2020"),
                "DATE is user-curated and must survive a FreeBird tag write");
            tagsAfter.Should().Contain(("REPLAYGAIN_TRACK_GAIN", "-7.50 dB"),
                "REPLAYGAIN_TRACK_GAIN is computed by external tools and must survive");
            tagsAfter.Should().Contain(("ENCODER", "lame 3.100"),
                "ENCODER is informational and must survive");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Tiny extension to fluently append a sequence of args onto a ProcessStartInfo's
/// ArgumentList — keeps RunMetaflac() above readable as a one-expression call.
/// </summary>
internal static class ProcessStartInfoArgsExtensions
{
    public static ProcessStartInfo WithArgs(this ProcessStartInfo psi, IEnumerable<string> args)
    {
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        return psi;
    }
}

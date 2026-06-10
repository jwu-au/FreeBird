using System.Text;
using FluentAssertions;
using FreeBird.Core.Models;
using FreeBird.Core.Sidecar;

namespace FreeBird.Core.Tests.Sidecar;

public sealed class TextSidecarReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly TextSidecarReader _reader = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sidecar-test-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        return path;
    }

    private async Task<string> WriteTextFileAsync(IEnumerable<string> lines)
    {
        var path = NewTempPath();
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
        return path;
    }

    // ----- Happy path -----

    [Fact]
    public async Task TryReadAsync_ValidV2Sidecar_Returns7FieldRecord()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02.0870900Z",
            "source:    /path/to/input/foo.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    flac -t failed: FRAME_CRC_MISMATCH after 53000 samples",
            "source_size: 4628511",
            "source_mtime: 2026-06-09T14:22:11.0000000Z",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.Timestamp.Should().Be(DateTimeOffset.Parse("2026-06-08T22:14:02.0870900Z"));
        rec.Source.Should().Be("/path/to/input/foo.uc");
        rec.Format.Should().Be(AudioFormat.Flac);
        rec.IntegrityLevel.Should().Be(IntegrityLevel.L3);
        rec.Reason.Should().Be("flac -t failed: FRAME_CRC_MISMATCH after 53000 samples");
        rec.SourceSize.Should().Be(4628511);
        rec.SourceMtime.Should().Be(DateTimeOffset.Parse("2026-06-09T14:22:11.0000000Z"));
        rec.UnknownFields.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_ValidV1Sidecar_Returns5FieldRecordWithNullExtensions()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02.0870900Z",
            "source:    /path/to/input/foo.uc",
            "format:    Mp3",
            "integrity: L1",
            "reason:    some-failure",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.SourceSize.Should().BeNull();
        rec.SourceMtime.Should().BeNull();
        rec.Format.Should().Be(AudioFormat.Mp3);
        rec.IntegrityLevel.Should().Be(IntegrityLevel.L1);
    }

    [Fact]
    public async Task TryReadAsync_UnknownFields_PreservedInDictionary()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
            "extra_field: hello world",
            "another: 42",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.UnknownFields.Should().NotBeNull();
        rec.UnknownFields!.Should().ContainKey("extra_field");
        rec.UnknownFields!["extra_field"].Should().Be("hello world");
        rec.UnknownFields!["another"].Should().Be("42");
    }

    [Fact]
    public async Task TryReadAsync_CaseInsensitiveKeys_ParseCorrectly()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "TIMESTAMP: 2026-06-08T22:14:02Z",
            "Source:    /a/b.uc",
            "format:    Flac",
            "INTEGRITY: L3",
            "Reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.Source.Should().Be("/a/b.uc");
        rec.Format.Should().Be(AudioFormat.Flac);
        rec.IntegrityLevel.Should().Be(IntegrityLevel.L3);
    }

    [Fact]
    public async Task TryReadAsync_IntegrityDash_ReturnsNullIntegrityLevel()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Unknown",
            "integrity: -",
            "reason:    unknown-format quarantine",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.IntegrityLevel.Should().BeNull();
        rec.Format.Should().Be(AudioFormat.Unknown);
    }

    [Fact]
    public async Task TryReadAsync_FilenameWithSpaces_PreservesSpaces()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /path/with spaces/foo bar.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    something failed here too",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.Source.Should().Be("/path/with spaces/foo bar.uc");
        rec.Reason.Should().Be("something failed here too");
    }

    [Fact]
    public async Task TryReadAsync_EmptyLinesAndCommentLikes_AreIgnored()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "",
            "timestamp: 2026-06-08T22:14:02Z",
            "",
            "no-colon-here-just-text",
            "source:    /a/b.uc",
            "   ",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.Source.Should().Be("/a/b.uc");
    }

    // ----- Malformed / error -----

    [Fact]
    public async Task TryReadAsync_NonexistentFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.txt");
        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_MissingTimestamp_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_MissingSource_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_MissingFormat_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_MissingReason_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnparseableTimestamp_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: not a date",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnknownFormatName_ReturnsNull()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Wav",
            "integrity: L3",
            "reason:    r",
        });

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnparseableSourceSize_TreatsAsMissing_RecordStillValid()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
            "source_size: not a number",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.SourceSize.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnparseableSourceMtime_TreatsAsMissing_RecordStillValid()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
            "source_mtime: not a date",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.SourceMtime.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_Cancelled_ThrowsOperationCancelledException()
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-08T22:14:02Z",
            "source:    /a/b.uc",
            "format:    Flac",
            "integrity: L3",
            "reason:    r",
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await _reader.TryReadAsync(path, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ----- I/O resilience -----

    [Fact]
    public async Task TryReadAsync_EmptyFile_ReturnsNull()
    {
        var path = NewTempPath();
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        var rec = await _reader.TryReadAsync(path);
        rec.Should().BeNull();
    }

    // ----- v3 reason tolerance (OA3) -----
    // The reader treats the `reason` VALUE as opaque freeform text; only field
    // NAMES are whitelisted. These cases lock in forward-tolerance for the new
    // v3 reason strings and any future reason values, including Unicode and
    // odd characters. No production code change is expected.

    [Theory]
    [InlineData("metadata-fetch-failed")]
    [InlineData("metadata-empty")]
    [InlineData("metadata-deserialize-failed")]
    [InlineData("tag-write-failed")]
    [InlineData("some-future-v4-reason")]
    [InlineData("reason with spaces and 'quotes' and \"dquotes\"")]
    [InlineData("中文原因")]
    public async Task TryReadAsync_ArbitraryReasonString_PreservedExactly(string reason)
    {
        var path = await WriteTextFileAsync(new[]
        {
            "timestamp: 2026-06-10T13:50:00.0000000Z",
            "source:    /tmp/foo.uc",
            "format:    Flac",
            "integrity: L3",
            $"reason:    {reason}",
            "source_size: 4096",
            "source_mtime: 2026-06-10T13:49:00.0000000Z",
        });

        var rec = await _reader.TryReadAsync(path);

        rec.Should().NotBeNull();
        rec!.Reason.Should().Be(reason);
        rec.Format.Should().Be(AudioFormat.Flac);
        rec.IntegrityLevel.Should().Be(IntegrityLevel.L3);
    }
}

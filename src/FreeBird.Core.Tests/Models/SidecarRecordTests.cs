using FluentAssertions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class SidecarRecordTests
{
    [Fact]
    public void Construct_AllFields_RoundTrips()
    {
        var ts = DateTimeOffset.Parse("2026-06-09T12:34:56Z");
        var mtime = DateTimeOffset.Parse("2026-06-09T10:00:00Z");
        var unknown = new Dictionary<string, string> { ["future"] = "value" };

        var rec = new SidecarRecord(
            Timestamp: ts,
            Source: "/in/song.uc",
            Format: AudioFormat.Flac,
            IntegrityLevel: IntegrityLevel.L3,
            Reason: "md5-mismatch",
            SourceSize: 4096,
            SourceMtime: mtime,
            UnknownFields: unknown);

        rec.Timestamp.Should().Be(ts);
        rec.Source.Should().Be("/in/song.uc");
        rec.Format.Should().Be(AudioFormat.Flac);
        rec.IntegrityLevel.Should().Be(IntegrityLevel.L3);
        rec.Reason.Should().Be("md5-mismatch");
        rec.SourceSize.Should().Be(4096);
        rec.SourceMtime.Should().Be(mtime);
        rec.UnknownFields.Should().BeSameAs(unknown);
    }

    [Fact]
    public void Construct_OnlyV1Fields_HasNullExtensions()
    {
        var ts = DateTimeOffset.Parse("2026-06-09T12:34:56Z");
        var rec = new SidecarRecord(
            Timestamp: ts,
            Source: "/in/song.uc",
            Format: AudioFormat.Mp3,
            IntegrityLevel: IntegrityLevel.L1,
            Reason: "structural-failed");

        rec.SourceSize.Should().BeNull();
        rec.SourceMtime.Should().BeNull();
        rec.UnknownFields.Should().BeNull();
    }

    [Fact]
    public void UnknownFields_DefaultsToNullDictionary()
    {
        var rec = new SidecarRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Source: "/in/x.uc",
            Format: AudioFormat.Mp3,
            IntegrityLevel: IntegrityLevel.Off,
            Reason: "r");

        rec.UnknownFields.Should().BeNull();
    }

    [Fact]
    public void UnknownFields_PreservesProvidedDictionary()
    {
        var unknown = new Dictionary<string, string>
        {
            ["x-future-1"] = "abc",
            ["x-future-2"] = "def",
        };
        var rec = new SidecarRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Source: "/in/x.uc",
            Format: AudioFormat.Mp3,
            IntegrityLevel: IntegrityLevel.L1,
            Reason: "r",
            UnknownFields: unknown);

        rec.UnknownFields.Should().NotBeNull();
        rec.UnknownFields!.Should().ContainKey("x-future-1").WhoseValue.Should().Be("abc");
        rec.UnknownFields!.Should().ContainKey("x-future-2").WhoseValue.Should().Be("def");
    }

    [Fact]
    public void IntegrityLevel_NullableMatchesUnknownFormat()
    {
        // v1 spec: when Format is Unknown, integrity is written as `-` in the sidecar
        // and is parsed back as null.
        var rec = new SidecarRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Source: "/in/mystery.uc",
            Format: AudioFormat.Unknown,
            IntegrityLevel: null,
            Reason: "unknown-format");

        rec.Format.Should().Be(AudioFormat.Unknown);
        rec.IntegrityLevel.Should().BeNull();
    }

    [Fact]
    public void Equality_BasedOnAllFields()
    {
        var ts = DateTimeOffset.Parse("2026-06-09T12:34:56Z");
        var mtime = DateTimeOffset.Parse("2026-06-09T10:00:00Z");

        var a = new SidecarRecord(ts, "/in/x.uc", AudioFormat.Flac, IntegrityLevel.L3, "r", 100, mtime);
        var b = new SidecarRecord(ts, "/in/x.uc", AudioFormat.Flac, IntegrityLevel.L3, "r", 100, mtime);
        var c = new SidecarRecord(ts, "/in/x.uc", AudioFormat.Flac, IntegrityLevel.L3, "r", 101, mtime);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }
}

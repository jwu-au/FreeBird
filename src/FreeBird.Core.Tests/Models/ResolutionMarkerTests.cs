using System;
using System.Text.Json;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class ResolutionMarkerTests
{
    // Deterministic timestamps used across round-trip tests.
    private static readonly DateTimeOffset SourceMtime =
        new DateTimeOffset(2026, 6, 10, 18, 42, 11, TimeSpan.Zero).AddTicks(7587980);

    private static readonly DateTimeOffset ResolvedAt =
        new DateTimeOffset(2026, 6, 10, 23, 18, 1, TimeSpan.Zero).AddTicks(1234567);

    private static ResolutionMarker BuildResolvedMarker() => new()
    {
        Schema = 1,
        SourceStem = "3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2",
        MusicId = "3367798042",
        SourcePath = "/Users/foo/3367798042-_-_5999-_-_a38658b6.uc!",
        SourceSize = 22481408L,
        SourceMtime = SourceMtime,
        ResolvedAt = ResolvedAt,
        Status = MarkerStatus.Resolved,
        OutputName = "树离suliii_ - 终不负.flac",
        Format = "Flac",
        Integrity = "L3",
        NamingTemplate = "{artist} - {title}",
        Reason = null,
        RetryAfter = null,
    };

    [Fact]
    public void RoundTrip_Resolved_PreservesAllFields()
    {
        var marker = BuildResolvedMarker();

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void RoundTrip_MetadataEmpty_PreservesAllFields()
    {
        var marker = BuildResolvedMarker() with
        {
            Status = MarkerStatus.MetadataEmpty,
            Reason = "metadata-empty",
            RetryAfter = ResolvedAt + TimeSpan.FromDays(7),
            OutputName = "3367798042.flac",
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void RoundTrip_MetadataFetchFailed_PreservesAllFields()
    {
        var marker = BuildResolvedMarker() with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = ResolvedAt + TimeSpan.FromHours(1),
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void RoundTrip_MetadataDeserializeFailed_PreservesAllFields()
    {
        var marker = BuildResolvedMarker() with
        {
            Status = MarkerStatus.MetadataDeserializeFailed,
            Reason = "metadata-deserialize-failed",
            RetryAfter = ResolvedAt + TimeSpan.FromHours(24),
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void RetryDelayFor_Resolved_ReturnsNull()
    {
        Assert.Null(ResolutionMarkerRetry.For(MarkerStatus.Resolved));
    }

    [Fact]
    public void RetryDelayFor_MetadataFetchFailed_Returns1Hour()
    {
        Assert.Equal(TimeSpan.FromHours(1), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed));
    }

    [Fact]
    public void RetryDelayFor_MetadataEmpty_Returns7Days()
    {
        Assert.Equal(TimeSpan.FromDays(7), ResolutionMarkerRetry.For(MarkerStatus.MetadataEmpty));
    }

    [Fact]
    public void RetryDelayFor_MetadataDeserializeFailed_Returns24Hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), ResolutionMarkerRetry.For(MarkerStatus.MetadataDeserializeFailed));
    }

    [Fact]
    public void Deserialize_UnknownFieldPresent_IsIgnored()
    {
        var json = """
        {
          "schema": 1,
          "source_stem": "3367798042-_-_5999-_-_abc",
          "music_id": "3367798042",
          "source_path": "/tmp/foo.uc!",
          "source_size": 1024,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "resolved",
          "output_name": "out.flac",
          "format": "Flac",
          "integrity": "L3",
          "naming_template": "{artist} - {title}",
          "reason": null,
          "retry_after": null,
          "future_field": "x"
        }
        """;

        var marker = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(marker);
        Assert.Equal("3367798042-_-_5999-_-_abc", marker!.SourceStem);
    }

    [Fact]
    public void Deserialize_MissingRequiredField_ThrowsJsonException()
    {
        // source_stem is omitted.
        var json = """
        {
          "schema": 1,
          "music_id": "3367798042",
          "source_path": "/tmp/foo.uc!",
          "source_size": 1024,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "resolved",
          "output_name": "out.flac",
          "format": "Flac",
          "integrity": "L3",
          "naming_template": "{artist} - {title}",
          "reason": null,
          "retry_after": null
        }
        """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options));
    }

    [Fact]
    public void Deserialize_KebabCaseStatusString_MapsToEnum()
    {
        var json = """
        {
          "schema": 1,
          "source_stem": "s",
          "music_id": "1",
          "source_path": "/p",
          "source_size": 1,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "metadata-empty",
          "output_name": "out.flac",
          "format": "Flac",
          "integrity": "L3",
          "naming_template": "{title}",
          "reason": "metadata-empty",
          "retry_after": "2026-06-17T23:18:01.1234567+00:00"
        }
        """;

        var marker = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(marker);
        Assert.Equal(MarkerStatus.MetadataEmpty, marker!.Status);
    }

    [Fact]
    public void Serialize_EnumStatus_WritesKebabCaseString()
    {
        var marker = BuildResolvedMarker() with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = ResolvedAt + TimeSpan.FromHours(1),
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);

        Assert.Contains("\"status\": \"metadata-fetch-failed\"", json);
    }

    [Fact]
    public void Deserialize_UnknownStatusString_ThrowsJsonException()
    {
        var json = """
        {
          "schema": 1,
          "source_stem": "s",
          "music_id": "1",
          "source_path": "/p",
          "source_size": 1,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "completely-unknown",
          "output_name": "out.flac",
          "format": "Flac",
          "integrity": "L3",
          "naming_template": "{title}",
          "reason": null,
          "retry_after": null
        }
        """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options));
    }

    [Fact]
    public void Serialize_NullReasonAndRetryAfter_ExplicitInOutput()
    {
        var marker = BuildResolvedMarker();

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);

        Assert.Contains("\"reason\": null", json);
        Assert.Contains("\"retry_after\": null", json);
    }

    // ---- v3.0.1 T04: tag-write fields (Option A additive schema) ----

    [Fact]
    public void RoundTrip_TagWriteFailed_PreservesTagWriteFields()
    {
        var marker = BuildResolvedMarker() with
        {
            TagWriteStatus = "failed",
            TagWriteReason = "tag-tool-missing",
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal("failed", roundTripped!.TagWriteStatus);
        Assert.Equal("tag-tool-missing", roundTripped.TagWriteReason);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void RoundTrip_DefaultTagWriteFields_BothNull()
    {
        var marker = BuildResolvedMarker();

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.TagWriteStatus);
        Assert.Null(roundTripped.TagWriteReason);
    }

    [Fact]
    public void Deserialize_PreV301Json_WithoutTagWriteFields_StillParses()
    {
        // Pre-v3.0.1 markers (v3.0.1 launch shape minus the two new fields) must
        // still deserialize cleanly. This pins down forward-compatibility — the
        // additive fields must not require [JsonRequired].
        var json = """
        {
          "schema": 1,
          "source_stem": "3367798042-_-_5999-_-_abc",
          "music_id": "3367798042",
          "source_path": "/tmp/foo.uc!",
          "source_size": 1024,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "resolved",
          "output_name": "out.flac",
          "format": "Flac",
          "integrity": "L3",
          "naming_template": "{artist} - {title}",
          "reason": null,
          "retry_after": null
        }
        """;

        var marker = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(marker);
        Assert.Null(marker!.TagWriteStatus);
        Assert.Null(marker.TagWriteReason);
    }

    [Fact]
    public void Serialize_NullTagWriteFields_ExplicitInOutput()
    {
        // DefaultIgnoreCondition.Never means nullable fields render as explicit
        // `null` in the JSON, not omitted. This makes pre/post-v3.0.1 marker
        // bytes diffable and keeps the schema visually self-documenting.
        var marker = BuildResolvedMarker();

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);

        Assert.Contains("\"tag_write_status\": null", json);
        Assert.Contains("\"tag_write_reason\": null", json);
    }
}

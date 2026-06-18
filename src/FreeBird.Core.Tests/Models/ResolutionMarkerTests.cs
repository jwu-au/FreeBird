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
        Assert.Null(ResolutionMarkerRetry.For(MarkerStatus.Resolved, attemptCount: 1, serverRetryAfter: null));
    }

    [Fact]
    public void RetryDelayFor_MetadataFetchFailed_FirstAttempt_Returns1Minute()
    {
        // T3: fetch-failed is now an exponential-by-attempt ladder {1m,5m,15m,1h,6h}.
        // First failure (attempt 1) is the shortest rung; the old flat-1h value is now
        // ladder[3] (the 4th attempt). serverRetryAfter is ignored for this status.
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, attemptCount: 1, serverRetryAfter: null));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, attemptCount: 2, serverRetryAfter: null));
        Assert.Equal(
            TimeSpan.FromHours(6),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, attemptCount: 99, serverRetryAfter: null));
    }

    [Fact]
    public void RetryDelayFor_MetadataEmpty_Returns7Days()
    {
        Assert.Equal(
            TimeSpan.FromDays(7),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataEmpty, attemptCount: 1, serverRetryAfter: null));
    }

    [Fact]
    public void RetryDelayFor_MetadataDeserializeFailed_Returns24Hours()
    {
        Assert.Equal(
            TimeSpan.FromHours(24),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataDeserializeFailed, attemptCount: 1, serverRetryAfter: null));
    }

    // T3: fetch-failed climbs the exponential ladder {1m,5m,15m,1h,6h} by 1-based
    // attempt and stays pinned at the 6h cap once the ladder is exhausted.
    [Fact]
    public void Backoff_FetchFailed_ExponentialByAttempt()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 1, null));
        Assert.Equal(TimeSpan.FromMinutes(5), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 2, null));
        Assert.Equal(TimeSpan.FromMinutes(15), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 3, null));
        Assert.Equal(TimeSpan.FromHours(1), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 4, null));
        Assert.Equal(TimeSpan.FromHours(6), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 5, null));
        Assert.Equal(TimeSpan.FromHours(6), ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 99, null));
    }

    // T3: rate-limited climbs its own base ladder {30s,2m,10m,30m,2h} by 1-based
    // attempt and caps at 2h (with no server Retry-After in play).
    [Fact]
    public void Backoff_RateLimited_ExponentialByAttempt()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 1, null));
        Assert.Equal(TimeSpan.FromMinutes(2), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 2, null));
        Assert.Equal(TimeSpan.FromMinutes(10), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 3, null));
        Assert.Equal(TimeSpan.FromMinutes(30), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 4, null));
        Assert.Equal(TimeSpan.FromHours(2), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 5, null));
        Assert.Equal(TimeSpan.FromHours(2), ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 99, null));
    }

    // T3: when the server states a Retry-After larger than the ladder rung, honor it
    // — never re-hammer the server before its stated wait elapses.
    [Fact]
    public void Backoff_RateLimited_HonorsServerRetryAfter_WhenLarger()
    {
        // attempt 1 ladder rung is 30s; server says 5m -> server wins.
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 1, TimeSpan.FromMinutes(5)));
    }

    // T3: when the server's Retry-After is smaller than the ladder rung, the ladder
    // wins — we never undercut our own escalating backoff.
    [Fact]
    public void Backoff_RateLimited_IgnoresServerRetryAfter_WhenSmaller()
    {
        // attempt 5 ladder rung is 2h; server says 1m -> ladder wins.
        Assert.Equal(
            TimeSpan.FromHours(2),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataRateLimited, 5, TimeSpan.FromMinutes(1)));
    }

    // T3: terminal statuses are flat — attemptCount and serverRetryAfter are ignored.
    [Fact]
    public void Backoff_TerminalStatuses_IgnoreAttemptAndRetryAfter()
    {
        Assert.Equal(
            TimeSpan.FromDays(7),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataEmpty, 99, TimeSpan.FromDays(365)));
        Assert.Equal(
            TimeSpan.FromHours(24),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataDeserializeFailed, 99, TimeSpan.FromDays(365)));
    }

    // T3: a zero or negative attemptCount is clamped to the first rung (1-based ladder).
    [Fact]
    public void Backoff_AttemptCountClampedToOne_WhenZeroOrNegative()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, 0, null));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            ResolutionMarkerRetry.For(MarkerStatus.MetadataFetchFailed, -3, null));
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

    // T2 (Schema 2): a hand-written Schema=1 marker that OMITS the three new
    // additive fields (attempt_count, output_size, output_mtime) must still
    // parse cleanly, with the new props defaulting to null. This is the headline
    // back-compat proof — the additive fields must NOT be [JsonRequired].
    [Fact]
    public void Marker_Schema1_MissingNewFields_StillParses()
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
          "retry_after": null
        }
        """;

        var marker = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(marker);
        Assert.Null(marker!.AttemptCount);
        Assert.Null(marker.OutputSize);
        Assert.Null(marker.OutputMtime);
    }

    // T2 (Schema 2): a Schema=2 marker carrying the three new fields round-trips
    // them exactly through the canonical serializer options.
    [Fact]
    public void Marker_Schema2_RoundTrips_NewFields()
    {
        var outputMtime = new DateTimeOffset(2026, 6, 11, 9, 5, 30, TimeSpan.Zero).AddTicks(4242424);
        var marker = BuildResolvedMarker() with
        {
            Schema = 2,
            AttemptCount = 3,
            OutputSize = 12345L,
            OutputMtime = outputMtime,
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped!.Schema);
        Assert.Equal(3, roundTripped.AttemptCount);
        Assert.Equal(12345L, roundTripped.OutputSize);
        Assert.Equal(outputMtime, roundTripped.OutputMtime);
    }

    // T2: the new MetadataRateLimited status serializes to "metadata-rate-limited"
    // and deserializes back to the same enum value.
    [Fact]
    public void MarkerStatus_RateLimited_KebabRoundTrips()
    {
        var marker = BuildResolvedMarker() with
        {
            Status = MarkerStatus.MetadataRateLimited,
            Reason = "metadata-rate-limited",
        };

        var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
        Assert.Contains("\"status\": \"metadata-rate-limited\"", json);

        var roundTripped = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);
        Assert.NotNull(roundTripped);
        Assert.Equal(MarkerStatus.MetadataRateLimited, roundTripped!.Status);
    }

    // T2: an unknown status string must still throw — proves the Read converter's
    // `_ => throw` forward-safety arm is preserved after adding the new value.
    [Fact]
    public void MarkerStatus_UnknownString_Throws()
    {
        var json = """
        {
          "schema": 2,
          "source_stem": "s",
          "music_id": "1",
          "source_path": "/p",
          "source_size": 1,
          "source_mtime": "2026-06-10T18:42:11.7587980+00:00",
          "resolved_at": "2026-06-10T23:18:01.1234567+00:00",
          "status": "totally-bogus",
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
}

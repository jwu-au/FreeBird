using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using FreeBird.Core.Service;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Service;

/// <summary>
/// T06 — Tests for <see cref="DefaultConfigTemplate"/>.
///
/// Coverage:
///   - Render(sampleInput, sampleOutput) returns a string that round-trips through
///     <see cref="JsonConfigLoader"/> to a valid <see cref="RootConfig"/>.
///   - Rendered text contains the official $schema URL.
///   - Optional <c>watch.flac</c> and <c>watch.api_rate_limit</c> blocks are present
///     as commented-out examples (loader skips // comments), so they parse to null defaults.
///   - All watch + service defaults equal <see cref="ConfigDefaults"/> after round-trip.
///   - Render escapes backslashes/quotes so Windows-style paths produce valid JSON.
///   - Normalised template shape matches the §4 reference (whitespace-tolerant).
/// </summary>
public class DefaultConfigTemplateTests
{
    private static JsonConfigLoader NewLoader()
        => new JsonConfigLoader(new Mock<ILogger>().Object);

    private const string SchemaUrl =
        "https://raw.githubusercontent.com/jwu-au/FreeBird/main/schemas/service.config.json";

    private static string Normalise(string s)
        => Regex.Replace(s, @"\s+", " ").Trim();

    [Fact]
    public void Render_ProducesValidJson_RoundTripsThroughLoader()
    {
        var rendered = DefaultConfigTemplate.Render("C:/cache", "D:/out");

        var cfg = NewLoader().LoadFromJson(rendered);

        cfg.Should().NotBeNull();
        cfg.Watch.Inputs.Should().BeEquivalentTo(new[] { "C:/cache" });
        cfg.Watch.Output.Should().Be("D:/out");

        // Defaults applied for everything else
        cfg.Watch.Integrity.Should().Be(IntegrityChoice.Auto);
        cfg.Watch.Concurrency.Should().Be(4);
        cfg.Watch.ApiConcurrency.Should().Be(4);
        cfg.Watch.PollIntervalSeconds.Should().Be(5);
        cfg.Watch.StabilityChecks.Should().Be(2);
        cfg.Watch.MinFileSize.Should().Be(1024);
        cfg.Watch.WriteTags.Should().BeTrue();
        cfg.Watch.LogFile.Should().BeNull();
    }

    [Fact]
    public void Render_IncludesSchemaUrl()
    {
        var rendered = DefaultConfigTemplate.Render("C:/cache", "D:/out");

        rendered.Should().Contain(SchemaUrl);

        // Round-trip surfaces it on RootConfig.Schema too.
        var cfg = NewLoader().LoadFromJson(rendered);
        cfg.Schema.Should().Be(SchemaUrl);
    }

    [Fact]
    public void Render_FlacBlockIsCommentedOut()
    {
        var rendered = DefaultConfigTemplate.Render("C:/cache", "D:/out");

        // Visible-but-commented: the rendered text shows the flac block to users
        // so they know it's an option, but it must not affect the parsed config.
        rendered.Should().Contain("// \"flac\"");
        rendered.Should().Contain("\"bin\"");
        rendered.Should().Contain("\"url\"");
        rendered.Should().Contain("\"disable_auto_install\"");

        var cfg = NewLoader().LoadFromJson(rendered);
        cfg.Watch.Flac.Should().BeNull();
    }

    [Fact]
    public void Render_ApiRateLimitIsCommentedOut()
    {
        var rendered = DefaultConfigTemplate.Render("C:/cache", "D:/out");

        rendered.Should().Contain("// \"api_rate_limit\"");

        var cfg = NewLoader().LoadFromJson(rendered);
        cfg.Watch.ApiRateLimit.Should().BeNull();
    }

    [Fact]
    public void Render_DefaultsMatchConfigDefaults()
    {
        var rendered = DefaultConfigTemplate.Render("C:/cache", "D:/out");
        var cfg = NewLoader().LoadFromJson(rendered);

        cfg.Service.Name.Should().Be(ConfigDefaults.DefaultServiceName);
        cfg.Service.DisplayName.Should().Be(ConfigDefaults.DefaultDisplayName);
        cfg.Service.Description.Should().Be(ConfigDefaults.DefaultDescription);
        cfg.Service.RestartPolicy.Should().Be(ConfigDefaults.DefaultRestartPolicy);
        cfg.Service.LogToEventLog.Should().BeTrue();
    }

    [Fact]
    public void Render_EscapesBackslashesAndQuotes()
    {
        var input = @"C:\Users\jw\cache";
        var output = @"D:\Music\Net""Ease"; // embedded quote — must be escaped too

        var rendered = DefaultConfigTemplate.Render(input, output);

        // Must not throw — proves backslashes/quotes are properly JSON-escaped.
        Action parse = () => JsonDocument.Parse(
            rendered,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        parse.Should().NotThrow<JsonException>();

        var cfg = NewLoader().LoadFromJson(rendered);
        cfg.Watch.Inputs[0].Should().Be(input);
        cfg.Watch.Output.Should().Be(output);
    }

    [Fact]
    public void Render_NormalizedTemplateShapeMatchesSpec()
    {
        // Golden-string check — whitespace-collapsed comparison so indentation tweaks
        // do not break the test. Substituted with simple placeholder paths.
        var rendered = DefaultConfigTemplate.Render("C:/in", "D:/out");

        var expected = $$"""
        {
          "$schema": "{{SchemaUrl}}",

          "watch": {
            "inputs": ["C:/in"],
            "output": "D:/out",
            "integrity": "auto",
            "concurrency": 4,
            "api_concurrency": 4,
            "poll_interval_seconds": 5,
            "stability_checks": 2,
            "min_file_size": 1024,
            "write_tags": true,
            "log_file": null
            // "flac": { "bin": "C:/Tools/flac/flac.exe", "url": null, "disable_auto_install": false }
            // "api_rate_limit": 0.5
          },

          "service": {
            "name": "{{ConfigDefaults.DefaultServiceName}}",
            "display_name": "{{ConfigDefaults.DefaultDisplayName}}",
            "description": "{{ConfigDefaults.DefaultDescription}}",
            "restart_policy": "backoff",
            "log_to_event_log": true
          }
        }
        """;

        Normalise(rendered).Should().Be(Normalise(expected));
    }
}

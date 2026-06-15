using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace FreeBird.Core.Tests.Service;

public class JsonSchemaShippedTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "FreeBird.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Could not find repo root (FreeBird.sln not found in ancestry).");
        }
        return dir;
    }

    [Fact]
    public void Schema_FileExists_AtRepoRoot()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        File.Exists(schemaPath).Should().BeTrue($"schema must ship at {schemaPath}");
    }

    [Fact]
    public void Schema_ParsesAsValidJson()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        var content = File.ReadAllText(schemaPath);
        var doc = JsonDocument.Parse(content);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void Schema_HasDraft2020_12_Identifier()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        var content = File.ReadAllText(schemaPath);
        var doc = JsonDocument.Parse(content);
        var schemaUrl = doc.RootElement.GetProperty("$schema").GetString();
        schemaUrl.Should().Contain("json-schema.org/draft/2020-12");
    }

    [Fact]
    public void Schema_DefinesWatchObject_WithRequiredFields()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var watchProps = doc.RootElement.GetProperty("properties").GetProperty("watch").GetProperty("properties");
        watchProps.TryGetProperty("inputs", out _).Should().BeTrue();
        watchProps.TryGetProperty("output", out _).Should().BeTrue();
    }

    [Fact]
    public void Schema_DefinesServiceObject()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var svcProps = doc.RootElement.GetProperty("properties").GetProperty("service").GetProperty("properties");
        svcProps.TryGetProperty("name", out _).Should().BeTrue();
        svcProps.TryGetProperty("display_name", out _).Should().BeTrue();
        svcProps.TryGetProperty("restart_policy", out _).Should().BeTrue();
    }

    // Round 5 fix M-4: schema must include flac.* and api_rate_limit
    [Fact]
    public void Schema_DefinesWatchFlacAndApiRateLimit_Round5M4()
    {
        var schemaPath = Path.Combine(FindRepoRoot(), "schemas", "service.config.json");
        var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var watchProps = doc.RootElement.GetProperty("properties").GetProperty("watch").GetProperty("properties");
        watchProps.TryGetProperty("flac", out _).Should().BeTrue("Round 5 M-4 fix requires flac schema");
        watchProps.TryGetProperty("api_rate_limit", out _).Should().BeTrue("Round 5 M-4 fix requires api_rate_limit schema");
    }
}

using System;
using System.IO;
using FluentAssertions;
using FreeBird.Core.Service;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Service;

/// <summary>
/// T05 — Tests for <see cref="JsonConfigLoader"/>.
///
/// Coverage:
///   - Happy paths: minimal/full configs, $schema tolerance, unknown fields (logged),
///     comments + trailing commas in source JSON.
///   - Required field errors: watch.inputs (missing + empty), watch.output.
///   - Enum validation: restart_policy, integrity (invalid → ConfigLoadException with accepted values).
///   - PollIntervalSeconds: int 5 OK; "5s" string rejected; 0/-1/3601 rejected.
///   - Round 5 M-4: watch.flac.* and watch.api_rate_limit fully validated.
///   - I/O: file roundtrip + nonexistent path.
///   - Defaults: service.* fields filled from ConfigDefaults when omitted.
/// </summary>
public class JsonConfigLoaderTests
{
    private static JsonConfigLoader NewLoader(out Mock<ILogger> log)
    {
        log = new Mock<ILogger>();
        return new JsonConfigLoader(log.Object);
    }

    private static JsonConfigLoader NewLoader()
        => new JsonConfigLoader(new Mock<ILogger>().Object);

    // ---------- Happy paths ----------

    [Fact]
    public void Load_ValidFullConfig_ReturnsPopulatedRootConfig()
    {
        // minimal valid: only required fields. Defaults must populate the rest.
        var json = """
        {
          "watch": {
            "inputs": ["C:/in"],
            "output": "C:/out"
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);

        cfg.Should().NotBeNull();
        cfg.Watch.Inputs.Should().BeEquivalentTo(new[] { "C:/in" });
        cfg.Watch.Output.Should().Be("C:/out");
        // Defaults for watch:
        cfg.Watch.Integrity.Should().Be(IntegrityChoice.Auto);
        cfg.Watch.Concurrency.Should().Be(4);
        cfg.Watch.ApiConcurrency.Should().Be(4);
        cfg.Watch.PollIntervalSeconds.Should().Be(5);
        cfg.Watch.StabilityChecks.Should().Be(2);
        cfg.Watch.MinFileSize.Should().Be(1024);
        cfg.Watch.WriteTags.Should().BeTrue();
        cfg.Watch.LogFile.Should().BeNull();
        cfg.Watch.Flac.Should().BeNull();
        cfg.Watch.ApiRateLimit.Should().BeNull();
        // Defaults for service.* (from ConfigDefaults):
        cfg.Service.Name.Should().Be(ConfigDefaults.DefaultServiceName);
        cfg.Service.DisplayName.Should().Be(ConfigDefaults.DefaultDisplayName);
        cfg.Service.Description.Should().Be(ConfigDefaults.DefaultDescription);
        cfg.Service.RestartPolicy.Should().Be(ConfigDefaults.DefaultRestartPolicy);
        cfg.Service.LogToEventLog.Should().BeTrue();
    }

    [Fact]
    public void Load_ValidConfigWithAllFields_ParsesEverything()
    {
        var json = """
        {
          "$schema": "https://example/schemas/service.config.json",
          "watch": {
            "inputs": ["C:/in1", "C:/in2"],
            "output": "C:/out",
            "integrity": "l3",
            "concurrency": 8,
            "api_concurrency": 6,
            "poll_interval_seconds": 10,
            "stability_checks": 3,
            "min_file_size": 2048,
            "write_tags": false,
            "log_file": "C:/logs/watch.log",
            "api_rate_limit": 1.5,
            "flac": {
              "bin": "C:/tools/flac.exe",
              "url": "https://mirror.example/flac.zip",
              "disable_auto_install": true
            }
          },
          "service": {
            "name": "FreeBirdCustom",
            "display_name": "Custom Name",
            "description": "Custom desc",
            "restart_policy": "restart",
            "log_to_event_log": false
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);

        cfg.Schema.Should().Be("https://example/schemas/service.config.json");
        cfg.Watch.Inputs.Should().BeEquivalentTo(new[] { "C:/in1", "C:/in2" });
        cfg.Watch.Output.Should().Be("C:/out");
        cfg.Watch.Integrity.Should().Be(IntegrityChoice.L3);
        cfg.Watch.Concurrency.Should().Be(8);
        cfg.Watch.ApiConcurrency.Should().Be(6);
        cfg.Watch.PollIntervalSeconds.Should().Be(10);
        cfg.Watch.StabilityChecks.Should().Be(3);
        cfg.Watch.MinFileSize.Should().Be(2048);
        cfg.Watch.WriteTags.Should().BeFalse();
        cfg.Watch.LogFile.Should().Be("C:/logs/watch.log");
        cfg.Watch.ApiRateLimit.Should().Be(1.5);
        cfg.Watch.Flac.Should().NotBeNull();
        cfg.Watch.Flac!.Bin.Should().Be("C:/tools/flac.exe");
        cfg.Watch.Flac.Url.Should().Be("https://mirror.example/flac.zip");
        cfg.Watch.Flac.DisableAutoInstall.Should().BeTrue();
        cfg.Service.Name.Should().Be("FreeBirdCustom");
        cfg.Service.DisplayName.Should().Be("Custom Name");
        cfg.Service.Description.Should().Be("Custom desc");
        cfg.Service.RestartPolicy.Should().Be(RestartPolicy.Restart);
        cfg.Service.LogToEventLog.Should().BeFalse();
    }

    [Fact]
    public void Load_DollarSchemaField_TolerantNotError()
    {
        var json = """
        {
          "$schema": "schemas/service.config.json",
          "watch": { "inputs": ["x"], "output": "y" }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);

        cfg.Schema.Should().Be("schemas/service.config.json");
    }

    [Fact]
    public void Load_UnknownField_LoggedAtInformation_ParseSucceeds()
    {
        var json = """
        {
          "watch": {
            "inputs": ["x"],
            "output": "y",
            "unknown_thing": true
          }
        }
        """;

        var loader = NewLoader(out var log);
        var cfg = loader.LoadFromJson(json);

        cfg.Watch.Inputs.Should().BeEquivalentTo(new[] { "x" });
        // Verify Information was called at least once mentioning the unknown field name.
        log.Verify(
            l => l.Information(
                It.Is<string>(s => s.Contains("Unknown") || s.Contains("unknown")),
                It.Is<string>(p => p.Contains("unknown_thing"))),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Load_ConfigWithComments_ParsesOk()
    {
        var json = """
        {
          // top-level comment
          "watch": {
            "inputs": ["x"],
            "output": "y" // trailing line comment
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Output.Should().Be("y");
    }

    [Fact]
    public void Load_ConfigWithTrailingCommas_ParsesOk()
    {
        var json = """
        {
          "watch": {
            "inputs": ["x"],
            "output": "y",
          },
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Output.Should().Be("y");
    }

    // ---------- Required field errors ----------

    [Fact]
    public void Load_MissingWatchInputs_ThrowsWithFieldName()
    {
        var json = """
        { "watch": { "output": "y" } }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.inputs");
    }

    [Fact]
    public void Load_EmptyWatchInputs_ThrowsWithFieldName()
    {
        var json = """
        { "watch": { "inputs": [], "output": "y" } }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.inputs");
    }

    [Fact]
    public void Load_MissingWatchOutput_ThrowsWithFieldName()
    {
        var json = """
        { "watch": { "inputs": ["x"] } }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.output");
    }

    // ---------- Enum validation ----------

    [Fact]
    public void Load_InvalidRestartPolicy_ThrowsWithFieldNameAndAccepted()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y" },
          "service": { "restart_policy": "potato" }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("service.restart_policy");
        ex.Message.Should().Contain("manual");
        ex.Message.Should().Contain("restart");
        ex.Message.Should().Contain("backoff");
    }

    [Fact]
    public void Load_InvalidIntegrity_ThrowsWithFieldNameAndAccepted()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "integrity": "medium" }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.integrity");
        ex.Message.Should().Contain("off");
        ex.Message.Should().Contain("l1");
        ex.Message.Should().Contain("l3");
        ex.Message.Should().Contain("auto");
    }

    [Fact]
    public void Load_ValidRestartPolicy_Backoff_Parses()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y" },
          "service": { "restart_policy": "backoff" }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Service.RestartPolicy.Should().Be(RestartPolicy.Backoff);
    }

    [Fact]
    public void Load_ValidIntegrity_L3_Parses()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "integrity": "l3" }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Integrity.Should().Be(IntegrityChoice.L3);
    }

    // ---------- PollInterval validation (corrected per 2de1c70 fixup) ----------

    [Fact]
    public void Load_PollIntervalSeconds_Int5_Parses()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "poll_interval_seconds": 5 }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.PollIntervalSeconds.Should().Be(5);
    }

    [Fact]
    public void Load_PollIntervalSeconds_String5s_Throws()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "poll_interval_seconds": "5s" }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.poll_interval_seconds");
        ex.Message.ToLowerInvariant().Should().Contain("expected number");
    }

    [Fact]
    public void Load_PollIntervalSeconds_Zero_Throws()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "poll_interval_seconds": 0 }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.poll_interval_seconds");
    }

    [Fact]
    public void Load_PollIntervalSeconds_Negative_Throws()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "poll_interval_seconds": -1 }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.poll_interval_seconds");
    }

    [Fact]
    public void Load_PollIntervalSeconds_Over3600_Throws()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "poll_interval_seconds": 3601 }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.poll_interval_seconds");
    }

    // ---------- Round 5 M-4: watch.flac.* ----------

    [Fact]
    public void Load_WatchFlacBin_Populates()
    {
        var json = """
        {
          "watch": {
            "inputs": ["x"], "output": "y",
            "flac": { "bin": "C:\\tools\\flac.exe" }
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Flac.Should().NotBeNull();
        cfg.Watch.Flac!.Bin.Should().Be("C:\\tools\\flac.exe");
    }

    [Fact]
    public void Load_WatchFlacOmitted_FlacIsNull()
    {
        var json = """
        { "watch": { "inputs": ["x"], "output": "y" } }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Flac.Should().BeNull();
    }

    [Fact]
    public void Load_WatchFlacUrl_Populates()
    {
        var json = """
        {
          "watch": {
            "inputs": ["x"], "output": "y",
            "flac": { "url": "https://mirror.example/flac.zip" }
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Flac.Should().NotBeNull();
        cfg.Watch.Flac!.Url.Should().Be("https://mirror.example/flac.zip");
    }

    [Fact]
    public void Load_WatchFlacDisableAutoInstall_Populates()
    {
        var json = """
        {
          "watch": {
            "inputs": ["x"], "output": "y",
            "flac": { "disable_auto_install": true }
          }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.Flac.Should().NotBeNull();
        cfg.Watch.Flac!.DisableAutoInstall.Should().BeTrue();
    }

    // ---------- Round 5 M-4: watch.api_rate_limit ----------

    [Fact]
    public void Load_WatchApiRateLimit_05_Populates()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "api_rate_limit": 0.5 }
        }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.ApiRateLimit.Should().Be(0.5);
    }

    [Fact]
    public void Load_WatchApiRateLimit_Omitted_IsNull()
    {
        var json = """
        { "watch": { "inputs": ["x"], "output": "y" } }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Watch.ApiRateLimit.Should().BeNull();
    }

    [Fact]
    public void Load_WatchApiRateLimit_Negative_Throws()
    {
        var json = """
        {
          "watch": { "inputs": ["x"], "output": "y", "api_rate_limit": -0.1 }
        }
        """;

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().LoadFromJson(json));
        ex.FieldName.Should().Be("watch.api_rate_limit");
    }

    // ---------- I/O ----------

    [Fact]
    public void Load_FromFile_ReadsCorrectly()
    {
        var json = """
        { "watch": { "inputs": ["from-file"], "output": "out-file" } }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"freebird-cfgtest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var cfg = NewLoader().Load(path);
            cfg.Watch.Inputs.Should().BeEquivalentTo(new[] { "from-file" });
            cfg.Watch.Output.Should().Be("out-file");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_NonexistentFile_ThrowsWithFieldName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"definitely-does-not-exist-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<ConfigLoadException>(() => NewLoader().Load(path));
        ex.FieldName.Should().Be("configFilePath");
        ex.Message.Should().Contain(path);
    }

    // ---------- Defaults ----------

    [Fact]
    public void Load_ServiceFieldsOmitted_DefaultsApplied()
    {
        var json = """
        { "watch": { "inputs": ["x"], "output": "y" } }
        """;

        var cfg = NewLoader().LoadFromJson(json);
        cfg.Service.Name.Should().Be(ConfigDefaults.DefaultServiceName);
        cfg.Service.DisplayName.Should().Be(ConfigDefaults.DefaultDisplayName);
        cfg.Service.Description.Should().Be(ConfigDefaults.DefaultDescription);
        cfg.Service.RestartPolicy.Should().Be(ConfigDefaults.DefaultRestartPolicy);
        cfg.Service.LogToEventLog.Should().BeTrue();
    }
}

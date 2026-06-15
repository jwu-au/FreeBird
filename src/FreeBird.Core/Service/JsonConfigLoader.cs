using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace FreeBird.Core.Service;

/// <summary>
/// T05 — <see cref="IConfigLoader"/> implementation backed by <c>System.Text.Json</c>.
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item>Snake-case property naming (<c>poll_interval_seconds</c>, etc.).</item>
///   <item>Tolerates JSON comments and trailing commas (for human-edited configs).</item>
///   <item>The top-level <c>$schema</c> property is tolerated (and surfaced via
///         <see cref="RootConfig.Schema"/>) — it exists only for IDE autocomplete.</item>
///   <item>Unknown fields under <c>watch</c>/<c>service</c>/<c>flac</c> are logged at
///         Information level; parse succeeds (forward-compatible).</item>
///   <item>Required: <c>watch.inputs</c> (≥ 1 entry) and <c>watch.output</c>.</item>
///   <item>Enum validation: <c>restart_policy</c> ∈ {manual|restart|backoff};
///         <c>integrity</c> ∈ {off|l1|l3|auto}.</item>
///   <item>Range validation: <c>poll_interval_seconds</c> ∈ [1, 3600];
///         <c>concurrency</c> ≥ 1; <c>api_concurrency</c> ≥ 1; <c>stability_checks</c> ≥ 1;
///         <c>min_file_size</c> ≥ 0; <c>api_rate_limit</c> ≥ 0 when present.</item>
///   <item>All failures throw <see cref="ConfigLoadException"/> with the JSON field
///         path (e.g. <c>"watch.poll_interval_seconds"</c>) and source line/column when
///         the parser provides them.</item>
/// </list>
/// </summary>
public sealed class JsonConfigLoader : IConfigLoader
{
    // The set of property names we KNOW about, used to surface unknown-field warnings.
    private static readonly HashSet<string> KnownRootProps = new(StringComparer.Ordinal)
    {
        "$schema", "watch", "service",
    };

    private static readonly HashSet<string> KnownWatchProps = new(StringComparer.Ordinal)
    {
        "inputs", "output", "integrity", "concurrency", "api_concurrency",
        "poll_interval_seconds", "stability_checks", "min_file_size", "write_tags",
        "log_file", "flac", "api_rate_limit",
    };

    private static readonly HashSet<string> KnownServiceProps = new(StringComparer.Ordinal)
    {
        "name", "display_name", "description", "restart_policy", "log_to_event_log",
    };

    private static readonly HashSet<string> KnownFlacProps = new(StringComparer.Ordinal)
    {
        "bin", "url", "disable_auto_install",
    };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger _logger;

    public JsonConfigLoader(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RootConfig Load(string configFilePath)
    {
        if (string.IsNullOrWhiteSpace(configFilePath))
        {
            throw new ConfigLoadException("configFilePath", "Config file path was null or empty.");
        }
        if (!File.Exists(configFilePath))
        {
            throw new ConfigLoadException(
                "configFilePath",
                $"Config file not found: '{configFilePath}'.");
        }

        string text;
        try
        {
            text = File.ReadAllText(configFilePath);
        }
        catch (IOException ex)
        {
            throw new ConfigLoadException(
                "configFilePath",
                $"Failed to read config file '{configFilePath}': {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ConfigLoadException(
                "configFilePath",
                $"Access denied reading config file '{configFilePath}': {ex.Message}",
                ex);
        }

        return LoadFromJson(text);
    }

    public RootConfig LoadFromJson(string jsonContent)
    {
        if (jsonContent is null)
        {
            throw new ConfigLoadException("(root)", "JSON content was null.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonContent, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigLoadException(
                "(root)",
                $"Invalid JSON: {ex.Message}",
                ex,
                line: ex.LineNumber.HasValue ? (int)(ex.LineNumber.Value + 1) : null,
                column: ex.BytePositionInLine.HasValue ? (int)(ex.BytePositionInLine.Value + 1) : null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigLoadException("(root)", $"Expected object at root, got {root.ValueKind}.");
            }

            // Warn on unknown root-level properties.
            foreach (var prop in root.EnumerateObject())
            {
                if (!KnownRootProps.Contains(prop.Name))
                {
                    _logger.Information("Unknown config field ignored: {FieldName}", prop.Name);
                }
            }

            var schema = TryGetString(root, "$schema");
            var watch = ParseWatch(GetRequiredObject(root, "watch", "watch"));
            var service = ParseService(TryGetProperty(root, "service"));

            return new RootConfig
            {
                Schema = schema,
                Watch = watch,
                Service = service,
            };
        }
    }

    // ---------- watch.* ----------

    private WatchConfig ParseWatch(JsonElement watch)
    {
        if (watch.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigLoadException("watch", $"Expected object, got {watch.ValueKind}.");
        }

        foreach (var prop in watch.EnumerateObject())
        {
            if (!KnownWatchProps.Contains(prop.Name))
            {
                _logger.Information("Unknown config field ignored: {FieldName}", "watch." + prop.Name);
            }
        }

        // inputs (required, non-empty array of strings)
        if (!watch.TryGetProperty("inputs", out var inputsEl) || inputsEl.ValueKind == JsonValueKind.Null)
        {
            throw new ConfigLoadException("watch.inputs", "Required field is missing.");
        }
        if (inputsEl.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigLoadException(
                "watch.inputs",
                $"Expected array of strings, got {inputsEl.ValueKind}.");
        }
        var inputs = new List<string>();
        var idx = 0;
        foreach (var item in inputsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ConfigLoadException(
                    $"watch.inputs[{idx}]",
                    $"Expected string, got {item.ValueKind}.");
            }
            inputs.Add(item.GetString()!);
            idx++;
        }
        if (inputs.Count == 0)
        {
            throw new ConfigLoadException("watch.inputs", "At least one input directory is required.");
        }

        // output (required, non-empty string)
        if (!watch.TryGetProperty("output", out var outputEl) || outputEl.ValueKind == JsonValueKind.Null)
        {
            throw new ConfigLoadException("watch.output", "Required field is missing.");
        }
        if (outputEl.ValueKind != JsonValueKind.String)
        {
            throw new ConfigLoadException(
                "watch.output",
                $"Expected string, got {outputEl.ValueKind}.");
        }
        var output = outputEl.GetString()!;

        // integrity (enum, default Auto)
        var integrity = IntegrityChoice.Auto;
        if (watch.TryGetProperty("integrity", out var integrityEl) && integrityEl.ValueKind != JsonValueKind.Null)
        {
            integrity = ParseIntegrity(integrityEl);
        }

        // concurrency (int >= 1, default 4)
        var concurrency = ParseIntInRange(watch, "concurrency", "watch.concurrency", min: 1, max: int.MaxValue, defaultValue: 4);

        // api_concurrency (int >= 1, default 4)
        var apiConcurrency = ParseIntInRange(watch, "api_concurrency", "watch.api_concurrency", min: 1, max: int.MaxValue, defaultValue: 4);

        // poll_interval_seconds (int 1..3600, default 5)
        var pollIntervalSeconds = ParseIntInRange(watch, "poll_interval_seconds", "watch.poll_interval_seconds", min: 1, max: 3600, defaultValue: 5);

        // stability_checks (int >= 1, default 2)
        var stabilityChecks = ParseIntInRange(watch, "stability_checks", "watch.stability_checks", min: 1, max: int.MaxValue, defaultValue: 2);

        // min_file_size (long >= 0, default 1024)
        var minFileSize = ParseLongInRange(watch, "min_file_size", "watch.min_file_size", min: 0, max: long.MaxValue, defaultValue: 1024);

        // write_tags (bool, default true)
        var writeTags = ParseBool(watch, "write_tags", "watch.write_tags", defaultValue: true);

        // log_file (string?, default null)
        var logFile = TryGetString(watch, "log_file");

        // api_rate_limit (double? >= 0)
        double? apiRateLimit = null;
        if (watch.TryGetProperty("api_rate_limit", out var rlEl) && rlEl.ValueKind != JsonValueKind.Null)
        {
            if (rlEl.ValueKind != JsonValueKind.Number)
            {
                throw new ConfigLoadException(
                    "watch.api_rate_limit",
                    $"Expected number, got {rlEl.ValueKind}.");
            }
            if (!rlEl.TryGetDouble(out var rlVal))
            {
                throw new ConfigLoadException("watch.api_rate_limit", "Could not parse as a double.");
            }
            if (rlVal < 0)
            {
                throw new ConfigLoadException(
                    "watch.api_rate_limit",
                    $"Value must be >= 0, got {rlVal}.");
            }
            apiRateLimit = rlVal;
        }

        // flac (optional sub-object)
        FlacConfig? flac = null;
        if (watch.TryGetProperty("flac", out var flacEl) && flacEl.ValueKind != JsonValueKind.Null)
        {
            flac = ParseFlac(flacEl);
        }

        return new WatchConfig
        {
            Inputs = inputs.ToArray(),
            Output = output,
            Integrity = integrity,
            Concurrency = concurrency,
            ApiConcurrency = apiConcurrency,
            PollIntervalSeconds = pollIntervalSeconds,
            StabilityChecks = stabilityChecks,
            MinFileSize = minFileSize,
            WriteTags = writeTags,
            LogFile = logFile,
            Flac = flac,
            ApiRateLimit = apiRateLimit,
        };
    }

    // ---------- watch.flac.* ----------

    private FlacConfig ParseFlac(JsonElement flac)
    {
        if (flac.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigLoadException("watch.flac", $"Expected object, got {flac.ValueKind}.");
        }

        foreach (var prop in flac.EnumerateObject())
        {
            if (!KnownFlacProps.Contains(prop.Name))
            {
                _logger.Information("Unknown config field ignored: {FieldName}", "watch.flac." + prop.Name);
            }
        }

        return new FlacConfig
        {
            Bin = TryGetString(flac, "bin"),
            Url = TryGetString(flac, "url"),
            DisableAutoInstall = ParseBool(flac, "disable_auto_install", "watch.flac.disable_auto_install", defaultValue: false),
        };
    }

    // ---------- service.* ----------

    private ServiceConfig ParseService(JsonElement? serviceOpt)
    {
        // Missing service block → use all ConfigDefaults via record initializer.
        if (!serviceOpt.HasValue)
        {
            return new ServiceConfig();
        }
        var service = serviceOpt.Value;
        if (service.ValueKind == JsonValueKind.Null)
        {
            return new ServiceConfig();
        }
        if (service.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigLoadException("service", $"Expected object, got {service.ValueKind}.");
        }

        foreach (var prop in service.EnumerateObject())
        {
            if (!KnownServiceProps.Contains(prop.Name))
            {
                _logger.Information("Unknown config field ignored: {FieldName}", "service." + prop.Name);
            }
        }

        var name = TryGetString(service, "name") ?? ConfigDefaults.DefaultServiceName;
        var displayName = TryGetString(service, "display_name") ?? ConfigDefaults.DefaultDisplayName;
        var description = TryGetString(service, "description") ?? ConfigDefaults.DefaultDescription;

        var restartPolicy = ConfigDefaults.DefaultRestartPolicy;
        if (service.TryGetProperty("restart_policy", out var rpEl) && rpEl.ValueKind != JsonValueKind.Null)
        {
            restartPolicy = ParseRestartPolicy(rpEl);
        }

        var logToEventLog = ParseBool(service, "log_to_event_log", "service.log_to_event_log", defaultValue: true);

        return new ServiceConfig
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            RestartPolicy = restartPolicy,
            LogToEventLog = logToEventLog,
        };
    }

    // ---------- Enum parsers ----------

    private static IntegrityChoice ParseIntegrity(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ConfigLoadException(
                "watch.integrity",
                $"Expected string, got {el.ValueKind}. Accepted values: off, l1, l3, auto.");
        }
        var raw = el.GetString() ?? "";
        return raw.ToLowerInvariant() switch
        {
            "off" => IntegrityChoice.Off,
            "l1" => IntegrityChoice.L1,
            "l3" => IntegrityChoice.L3,
            "auto" => IntegrityChoice.Auto,
            _ => throw new ConfigLoadException(
                "watch.integrity",
                $"Invalid value '{raw}'. Accepted values: off, l1, l3, auto."),
        };
    }

    private static RestartPolicy ParseRestartPolicy(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new ConfigLoadException(
                "service.restart_policy",
                $"Expected string, got {el.ValueKind}. Accepted values: manual, restart, backoff.");
        }
        var raw = el.GetString() ?? "";
        return raw.ToLowerInvariant() switch
        {
            "manual" => RestartPolicy.Manual,
            "restart" => RestartPolicy.Restart,
            "backoff" => RestartPolicy.Backoff,
            _ => throw new ConfigLoadException(
                "service.restart_policy",
                $"Invalid value '{raw}'. Accepted values: manual, restart, backoff."),
        };
    }

    // ---------- Primitive parsers ----------

    private static int ParseIntInRange(JsonElement parent, string propName, string fieldPath, int min, int max, int defaultValue)
    {
        if (!parent.TryGetProperty(propName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        if (el.ValueKind != JsonValueKind.Number)
        {
            throw new ConfigLoadException(
                fieldPath,
                $"Expected number, got {el.ValueKind}.");
        }
        if (!el.TryGetInt32(out var v))
        {
            throw new ConfigLoadException(fieldPath, "Could not parse as a 32-bit integer.");
        }
        if (v < min || v > max)
        {
            var maxStr = max == int.MaxValue ? "∞" : max.ToString();
            throw new ConfigLoadException(
                fieldPath,
                $"Value must be in [{min}, {maxStr}], got {v}.");
        }
        return v;
    }

    private static long ParseLongInRange(JsonElement parent, string propName, string fieldPath, long min, long max, long defaultValue)
    {
        if (!parent.TryGetProperty(propName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        if (el.ValueKind != JsonValueKind.Number)
        {
            throw new ConfigLoadException(
                fieldPath,
                $"Expected number, got {el.ValueKind}.");
        }
        if (!el.TryGetInt64(out var v))
        {
            throw new ConfigLoadException(fieldPath, "Could not parse as a 64-bit integer.");
        }
        if (v < min || v > max)
        {
            var maxStr = max == long.MaxValue ? "∞" : max.ToString();
            throw new ConfigLoadException(
                fieldPath,
                $"Value must be in [{min}, {maxStr}], got {v}.");
        }
        return v;
    }

    private static bool ParseBool(JsonElement parent, string propName, string fieldPath, bool defaultValue)
    {
        if (!parent.TryGetProperty(propName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        if (el.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.False)
        {
            return false;
        }
        throw new ConfigLoadException(
            fieldPath,
            $"Expected boolean, got {el.ValueKind}.");
    }

    private static string? TryGetString(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return el.GetString();
    }

    private static JsonElement GetRequiredObject(JsonElement parent, string propName, string fieldPath)
    {
        if (!parent.TryGetProperty(propName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            // The two specific child-field errors (watch.inputs / watch.output) are
            // detected inside ParseWatch — so for top-level `watch` we synthesize
            // those exact errors here for a missing block.
            if (string.Equals(fieldPath, "watch", StringComparison.Ordinal))
            {
                throw new ConfigLoadException("watch.inputs", "Required field is missing.");
            }
            throw new ConfigLoadException(fieldPath, "Required field is missing.");
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigLoadException(fieldPath, $"Expected object, got {el.ValueKind}.");
        }
        return el;
    }

    private static JsonElement? TryGetProperty(JsonElement parent, string propName)
    {
        if (parent.TryGetProperty(propName, out var el))
        {
            return el;
        }
        return null;
    }
}

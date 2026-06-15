namespace FreeBird.Core.Service;

/// <summary>
/// T06 — Emits the §4 reference JSON used by <c>fb service init</c>.
///
/// <para>The rendered template is guaranteed to round-trip through
/// <see cref="JsonConfigLoader"/> — every field name, default value, and accepted
/// enum value mirrors what the loader expects (see <c>KnownWatchProps</c> /
/// <c>KnownServiceProps</c> / <c>KnownFlacProps</c> in that class).</para>
///
/// <para>Optional <c>watch.flac</c> and <c>watch.api_rate_limit</c> blocks are emitted
/// as <c>//</c>-prefixed example lines so the user can see the available shape
/// without affecting defaults — the loader's
/// <see cref="System.Text.Json.JsonCommentHandling.Skip"/> setting drops them.</para>
///
/// <para>Static helper — no DI registration. Holds no mutable state, so it does not
/// count as a public static test seam (AGENTS.md §6.1).</para>
/// </summary>
public static class DefaultConfigTemplate
{
    /// <summary>
    /// Canonical <c>$schema</c> URL. Pinned to the GitHub raw path so editors
    /// (VS Code, Rider, JetBrains) auto-complete against the shipped schema.
    /// </summary>
    public const string SchemaUrl =
        "https://raw.githubusercontent.com/jwu-au/FreeBird/main/schemas/service.config.json";

    // {0} = JSON-escaped sample input path, {1} = JSON-escaped sample output path.
    // {2}/{3}/{4} = JSON-escaped service name/display_name/description.
    // Every literal JSON brace is doubled ({{ / }}) so string.Format leaves it intact.
    private const string Template = """
        {{
          "$schema": "https://raw.githubusercontent.com/jwu-au/FreeBird/main/schemas/service.config.json",

          "watch": {{
            "inputs": ["{0}"],
            "output": "{1}",
            "integrity": "auto",
            "concurrency": 4,
            "api_concurrency": 4,
            "poll_interval_seconds": 5,
            "stability_checks": 2,
            "min_file_size": 1024,
            "write_tags": true,
            "log_file": null
            // "flac": {{ "bin": "C:/Tools/flac/flac.exe", "url": null, "disable_auto_install": false }}
            // "api_rate_limit": 0.5
          }},

          "service": {{
            "name": "{2}",
            "display_name": "{3}",
            "description": "{4}",
            "restart_policy": "backoff",
            "log_to_event_log": true
          }}
        }}
        """;

    /// <summary>
    /// Build the default config text with the caller-supplied sample input + output paths
    /// embedded under <c>watch.inputs[0]</c> and <c>watch.output</c>.
    /// </summary>
    /// <param name="sampleInput">Raw path string for <c>watch.inputs[0]</c>; backslashes and
    /// double-quotes are JSON-escaped automatically.</param>
    /// <param name="sampleOutput">Raw path string for <c>watch.output</c>; same escaping rules.</param>
    public static string Render(string sampleInput, string sampleOutput)
    {
        if (sampleInput is null)
        {
            throw new ArgumentNullException(nameof(sampleInput));
        }
        if (sampleOutput is null)
        {
            throw new ArgumentNullException(nameof(sampleOutput));
        }

        return string.Format(
            Template,
            JsonEscape(sampleInput),
            JsonEscape(sampleOutput),
            JsonEscape(ConfigDefaults.DefaultServiceName),
            JsonEscape(ConfigDefaults.DefaultDisplayName),
            JsonEscape(ConfigDefaults.DefaultDescription));
    }

    private static string JsonEscape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

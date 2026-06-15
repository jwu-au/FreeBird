namespace FreeBird.Core.Service;

/// <summary>
/// Parses + validates the JSON service config file used by <c>fb service install</c>
/// into a typed <see cref="RootConfig"/> graph (T05).
///
/// <para>
/// Implementations are synchronous and fail-fast: any parse error, missing required
/// field, or out-of-range value throws <see cref="ConfigLoadException"/> carrying the
/// JSON field path (e.g. <c>"watch.inputs"</c>) and — when available — the source
/// line/column.
/// </para>
///
/// <para>
/// NOTE: this interface intentionally does NOT inherit <c>IDependency</c> yet.
/// DI registration is deferred to T14 (CliServiceModule), which decides whether
/// IConfigLoader gets bound via Core auto-scan or explicit Cli registration.
/// </para>
/// </summary>
public interface IConfigLoader
{
    /// <summary>
    /// Reads <paramref name="configFilePath"/> from disk and parses it into a
    /// validated <see cref="RootConfig"/>. Throws <see cref="ConfigLoadException"/>
    /// (FieldName = <c>"configFilePath"</c>) if the file does not exist or cannot
    /// be read.
    /// </summary>
    RootConfig Load(string configFilePath);

    /// <summary>
    /// Parses an in-memory JSON string into a validated <see cref="RootConfig"/>.
    /// Useful for tests and for callers that already have the JSON in hand.
    /// </summary>
    RootConfig LoadFromJson(string jsonContent);
}

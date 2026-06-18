namespace FreeBird.Core.Service;

/// <summary>
/// Default values for the JSON service config, matching design §2.7.
/// Strings use the literal <c>%ProgramData%</c> token — expansion is the host's job
/// (the Cli-side log-path resolver expands it at service start).
/// </summary>
public static class ConfigDefaults
{
    public const string DefaultServiceName = "FreeBird";
    public const string DefaultDisplayName = "FreeBird Music Cache Decoder";

    public const string DefaultDescription =
        "Continuously decrypts NetEase Cloud Music cache files into playable audio files. https://github.com/jwu-au/FreeBird";

    public const RestartPolicy DefaultRestartPolicy = RestartPolicy.Backoff;

    public const string DefaultLogFileTemplate = @"%ProgramData%\FreeBird\logs\watch-.log";
    public const string DefaultConfigPath = @"%ProgramData%\FreeBird\config.json";
}

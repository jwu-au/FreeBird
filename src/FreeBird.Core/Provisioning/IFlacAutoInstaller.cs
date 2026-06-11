using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Downloads and installs the flac binary suite (flac, metaflac, libFLAC, libFLAC++) on platforms that support auto-install.
/// On macOS/Linux, the implementation is a no-op that always returns Skipped (users use brew/apt instead).
/// On Windows, the implementation downloads flac-1.5.0-win.zip from Xiph OSUOSL, verifies SHA256, and extracts to the install directory.
/// </summary>
public interface IFlacAutoInstaller : IDependency
{
    /// <summary>
    /// Attempt to install flac binaries to the specified target directory.
    /// </summary>
    /// <param name="targetDirectory">Where to extract the binaries (typically AppContext.BaseDirectory).</param>
    /// <param name="sourceUrl">URL to download from (CLI flag > env var > hardcoded default; provided by caller).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One of Installed / NotSupported / Failed / Disabled.</returns>
    Task<FlacInstallResult> InstallAsync(string targetDirectory, string sourceUrl, CancellationToken ct);
}

/// <summary>Outcome of a flac auto-install attempt.</summary>
public abstract record FlacInstallResult
{
    private FlacInstallResult() { }

    /// <summary>Successfully downloaded, verified, and extracted.</summary>
    public sealed record Installed(string FlacPath, string MetaflacPath) : FlacInstallResult;

    /// <summary>Platform does not support auto-install (macOS/Linux). User must install via brew/apt.</summary>
    public sealed record NotSupported : FlacInstallResult
    {
        public static readonly NotSupported Instance = new();
    }

    /// <summary>Install attempted but failed (network, SHA mismatch, EACCES, etc).</summary>
    public sealed record Failed(string Reason) : FlacInstallResult;

    /// <summary>Install was disabled by --no-auto-download flag or FREEBIRD_NO_AUTO_DOWNLOAD env.</summary>
    public sealed record Disabled : FlacInstallResult
    {
        public static readonly Disabled Instance = new();
    }
}

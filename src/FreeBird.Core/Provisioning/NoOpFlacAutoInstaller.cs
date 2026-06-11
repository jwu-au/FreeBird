using Serilog;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Default IFlacAutoInstaller for non-Windows platforms. Always returns NotSupported —
/// macOS users should `brew install flac`, Linux users should `apt install flac` / equivalent.
/// Performs NO network IO and NO filesystem writes.
/// </summary>
public sealed class NoOpFlacAutoInstaller : IFlacAutoInstaller
{
    private readonly ILogger _log;

    public NoOpFlacAutoInstaller(ILogger log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<FlacInstallResult> InstallAsync(string targetDirectory, string sourceUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _log.Debug("NoOpFlacAutoInstaller: auto-install not supported on this platform; install flac via brew/apt/yum");
        return Task.FromResult<FlacInstallResult>(FlacInstallResult.NotSupported.Instance);
    }
}

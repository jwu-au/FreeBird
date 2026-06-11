using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Default <see cref="IFlacBinaryResolver"/> implementation.
///
/// Probe order (first hit wins):
///   1. CLI override (<c>--flac-bin</c> / <c>--metaflac-bin</c>) via <see cref="FlacResolverOptions"/>.
///   2. <c>&lt;AppContext.BaseDirectory&gt;/flac{.exe}</c> — covers both the bundled
///      binary shipped next to <c>fb</c> and a binary auto-downloaded by the
///      installer.
///   3. PATH lookup via <see cref="IPathEnvironment"/>.
///   4. Auto-install via <see cref="IFlacAutoInstaller"/> (Windows only;
///      NoOp on macOS/Linux). Skipped if <see cref="FlacResolverOptions.DisableAutoInstall"/>
///      is true or <see cref="FlacResolverOptions.AutoInstallUrl"/> is null/empty.
///
/// Returns <see cref="FlacResolution.NotFound"/> when none of the steps locate
/// the binary. No version check is performed — the design accepts whatever the user has.
/// </summary>
public sealed class FlacBinaryResolver : IFlacBinaryResolver
{
    private readonly IFileSystem _fs;
    private readonly IPathEnvironment _pathEnv;
    private readonly IFlacAutoInstaller _autoInstaller;
    private readonly FlacResolverOptions _options;
    private readonly ILogger _log;

    public FlacBinaryResolver(
        IFileSystem fs,
        IPathEnvironment pathEnv,
        IFlacAutoInstaller autoInstaller,
        FlacResolverOptions options,
        ILogger log)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _pathEnv = pathEnv ?? throw new ArgumentNullException(nameof(pathEnv));
        _autoInstaller = autoInstaller ?? throw new ArgumentNullException(nameof(autoInstaller));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<FlacResolution> ResolveFlacAsync(CancellationToken ct)
        => ResolveAsync("flac", ct);

    public Task<FlacResolution> ResolveMetaflacAsync(CancellationToken ct)
        => ResolveAsync("metaflac", ct);

    private async Task<FlacResolution> ResolveAsync(string baseName, CancellationToken ct)
    {
        var exeName = OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
        var overridePath = baseName == "flac" ? _options.FlacBinOverride : _options.MetaflacBinOverride;

        // 1. CLI override
        if (!string.IsNullOrEmpty(overridePath))
        {
            if (_fs.File.Exists(overridePath))
            {
                _log.Debug("Resolved {Bin} from CLI override: {Path}", baseName, overridePath);
                return new FlacResolution(overridePath, FlacBinaryProvenance.CliOverride);
            }

            // Override given but file missing — log warning, fall through to next probe.
            _log.Warning(
                "--{Bin}-bin override {Path} does not exist; falling back to default probe order",
                baseName, overridePath);
        }

        // 2. Next to fb executable
        var beside = _fs.Path.Combine(_options.AppBaseDirectory, exeName);
        if (_fs.File.Exists(beside))
        {
            _log.Debug("Resolved {Bin} next to fb: {Path}", baseName, beside);
            return new FlacResolution(beside, FlacBinaryProvenance.NextToExecutable);
        }

        // 3. PATH lookup
        var pathHit = _pathEnv.FindOnPath(exeName);
        if (pathHit is not null)
        {
            _log.Debug("Resolved {Bin} from PATH: {Path}", baseName, pathHit);
            return new FlacResolution(pathHit, FlacBinaryProvenance.Path);
        }

        // 4. Auto-install fallback (Windows-only impl; NoOp returns NotSupported elsewhere)
        if (_options.DisableAutoInstall)
        {
            _log.Debug("{Bin} not found; auto-install disabled by option", baseName);
            return FlacResolution.NotFound;
        }

        if (string.IsNullOrEmpty(_options.AutoInstallUrl))
        {
            _log.Debug("{Bin} not found; no AutoInstallUrl configured", baseName);
            return FlacResolution.NotFound;
        }

        _log.Information("flac not found; attempting auto-install from {Url}", _options.AutoInstallUrl);
        var installResult = await _autoInstaller
            .InstallAsync(_options.AppBaseDirectory, _options.AutoInstallUrl, ct)
            .ConfigureAwait(false);

        return installResult switch
        {
            FlacInstallResult.Installed inst => baseName == "flac"
                ? new FlacResolution(inst.FlacPath, FlacBinaryProvenance.NextToExecutable)
                : new FlacResolution(inst.MetaflacPath, FlacBinaryProvenance.NextToExecutable),
            FlacInstallResult.NotSupported => LogAndReturnNotFound(
                baseName,
                "platform does not support auto-install (macOS/Linux); install flac via brew or apt"),
            FlacInstallResult.Disabled => LogAndReturnNotFound(baseName, "auto-install disabled"),
            FlacInstallResult.Failed f => LogAndReturnNotFound(baseName, $"auto-install failed: {f.Reason}"),
            _ => throw new InvalidOperationException(
                $"Unhandled FlacInstallResult: {installResult.GetType().Name}"),
        };
    }

    private FlacResolution LogAndReturnNotFound(string baseName, string reason)
    {
        _log.Debug("{Bin} not found via probe; {Reason}", baseName, reason);
        return FlacResolution.NotFound;
    }
}

/// <summary>
/// Options injected at <see cref="FlacBinaryResolver"/> construction time.
/// </summary>
public sealed record FlacResolverOptions
{
    /// <summary>From <c>--flac-bin</c> CLI flag (or null when not provided).</summary>
    public string? FlacBinOverride { get; init; }

    /// <summary>
    /// Distinct override for <c>metaflac</c>. Independent of
    /// <see cref="FlacBinOverride"/> — the resolver does NOT fall back to the
    /// flac override when this is null, since flac and metaflac are separate
    /// binaries that may live in different places.
    /// </summary>
    public string? MetaflacBinOverride { get; init; }

    /// <summary>
    /// Directory next to the fb executable. Typically <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public required string AppBaseDirectory { get; init; }

    /// <summary>
    /// Download URL passed to <see cref="IFlacAutoInstaller"/> when probe steps miss.
    /// Resolution order (CLI flag &gt; env var &gt; hardcoded default) is performed by the caller.
    /// When null/empty, the resolver skips the auto-install step entirely.
    /// </summary>
    public string? AutoInstallUrl { get; init; }

    /// <summary>
    /// When true, the resolver will NOT invoke <see cref="IFlacAutoInstaller"/> even on
    /// platforms that support it. Maps to the <c>--no-auto-download</c> CLI flag /
    /// <c>FREEBIRD_NO_AUTO_DOWNLOAD</c> env var.
    /// </summary>
    public bool DisableAutoInstall { get; init; } = false;
}

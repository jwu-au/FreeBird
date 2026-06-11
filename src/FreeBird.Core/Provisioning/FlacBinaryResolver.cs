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
///      binary shipped next to <c>fb</c> and a binary auto-downloaded by T03's
///      installer.
///   3. PATH lookup via <see cref="IPathEnvironment"/>.
///
/// Returns <see cref="FlacResolution.NotFound"/> when none of the three locate
/// the binary. No auto-install is attempted here; that delegation lands in T03.
/// No version check is performed — the design accepts whatever the user has.
/// </summary>
public sealed class FlacBinaryResolver : IFlacBinaryResolver
{
    private readonly IFileSystem _fs;
    private readonly IPathEnvironment _pathEnv;
    private readonly FlacResolverOptions _options;
    private readonly ILogger _log;

    public FlacBinaryResolver(
        IFileSystem fs,
        IPathEnvironment pathEnv,
        FlacResolverOptions options,
        ILogger log)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _pathEnv = pathEnv ?? throw new ArgumentNullException(nameof(pathEnv));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<FlacResolution> ResolveFlacAsync(CancellationToken ct)
        => Task.FromResult(Resolve("flac", _options.FlacBinOverride));

    public Task<FlacResolution> ResolveMetaflacAsync(CancellationToken ct)
        => Task.FromResult(Resolve("metaflac", _options.MetaflacBinOverride));

    private FlacResolution Resolve(string baseName, string? overridePath)
    {
        var exeName = OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;

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

        _log.Debug("{Bin} not found via override, next-to-exe, or PATH", baseName);
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
}

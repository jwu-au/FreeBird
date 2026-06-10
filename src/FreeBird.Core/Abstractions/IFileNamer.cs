using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Maps an input .uc / .uc! source filename + detected format (+ optional resolved metadata)
/// to the final output filename.
/// </summary>
public interface IFileNamer : IDependency
{
    /// <summary>
    /// Compute the final output file name (no directory) for a decoded source.
    /// </summary>
    /// <param name="sourcePath">Full path to the original .uc/.uc! source file.</param>
    /// <param name="format">Sniffed audio format.</param>
    /// <param name="metadata">Resolved metadata, or null for offline/fallback mode (stem-based naming).</param>
    string GetTargetName(string sourcePath, AudioFormat format, SongInfo? metadata);
}

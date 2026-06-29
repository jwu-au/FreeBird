namespace FreeBird.Core.Abstractions;

/// <summary>
/// Selects the correct <see cref="IFileProcessor"/> for a given source file by its
/// extension. The <c>.uc</c>/<c>.uc!</c> NetEase cache files go through the default
/// <c>FileProcessor</c>; the encrypted <c>.ncm</c> container goes through
/// <c>NcmFileProcessor</c>.
///
/// Introduced in the .ncm feature (Task 14) so the scan/watch orchestrators no longer
/// inject a single <see cref="IFileProcessor"/> directly — they ask the router per file.
/// </summary>
public interface IFileProcessorRouter : IDependency
{
    /// <summary>
    /// Returns the processor that handles <paramref name="sourcePath"/>, chosen by its
    /// extension (case-insensitive). <c>.ncm</c> → the NCM processor; everything else
    /// (<c>.uc</c>, <c>.uc!</c>, …) → the default <c>.uc</c> processor.
    /// </summary>
    IFileProcessor Select(string sourcePath);
}

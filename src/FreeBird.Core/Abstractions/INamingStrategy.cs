using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Maps an input .uc / .uc! source filename + detected format to the final output filename.
/// </summary>
public interface INamingStrategy : IDependency
{
    /// <summary>
    /// Computes the output filename (no directory) for a given source file and detected format.
    /// Example: GetOutputFileName("12345-abc.uc", AudioFormat.Mp3) -> "12345-abc.mp3"
    /// </summary>
    string GetOutputFileName(string sourceFilePath, AudioFormat format);
}

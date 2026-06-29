using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Internal seam for <see cref="CompositeCoverWriter"/> tests. Allows mocking the
/// MP3/M4A cover backend without touching real files via TagLibSharp. The
/// production implementation is <see cref="Mp3M4aCoverWriter"/>.
///
/// A single backend handles both MP3 and M4A because <c>TagLib.File.Create</c>
/// auto-detects the container and exposes a uniform <c>Tag.Pictures</c> API.
/// </summary>
public interface IMp3M4aCoverWriter
{
    Task<TagWriteResult> WriteCoverAsync(string filePath, byte[] coverBytes, CancellationToken ct);
}

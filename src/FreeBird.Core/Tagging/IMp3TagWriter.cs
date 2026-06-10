using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Internal seam for <see cref="CompositeTagWriter"/> tests. Allows mocking the
/// MP3 backend without touching real files via TagLibSharp. The production
/// implementation is <see cref="Mp3TagWriter"/>.
/// </summary>
public interface IMp3TagWriter
{
    Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct);
}

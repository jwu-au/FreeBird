using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Internal seam for <see cref="CompositeTagWriter"/> tests. Allows mocking the
/// M4A backend without touching real files via TagLibSharp. The production
/// implementation is <see cref="M4aTagWriter"/>.
/// </summary>
public interface IM4aTagWriter
{
    Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct);
}

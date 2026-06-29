using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Internal seam for <see cref="CompositeCoverWriter"/> tests. Allows mocking the
/// FLAC cover backend without spinning up a real <c>metaflac</c> subprocess. The
/// production implementation is <see cref="FlacCoverWriter"/>.
///
/// Public (not internal) because <see cref="CompositeCoverWriter"/> is public and
/// its constructor parameter must match accessibility.
/// </summary>
public interface IFlacCoverWriter
{
    Task<TagWriteResult> WriteCoverAsync(string filePath, byte[] coverBytes, CancellationToken ct);
}

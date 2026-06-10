using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Internal seam for <see cref="CompositeTagWriter"/> tests. Allows mocking the
/// FLAC backend without spinning up a real <c>metaflac</c> subprocess. The
/// production implementation is <see cref="FlacTagWriter"/>.
///
/// Note: this is intentionally NOT an <c>IDependency</c> \u2014 the composite
/// resolves the concrete <see cref="FlacTagWriter"/> by interface only when the
/// container is wired that way; tests substitute via direct ctor injection.
///
/// Public (not internal) because <see cref="CompositeTagWriter"/> is public
/// and its constructor parameter must match accessibility.
/// </summary>
public interface IFlacTagWriter
{
    Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct);
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Decoding;

/// <summary>
/// Streaming XOR decoder for Netease Music cache files. Key = 0xA3 (163 dec).
/// XOR is self-inverse: same operation encrypts and decrypts.
/// Processes input in 64KB chunks; does not load the entire stream into memory.
/// Does not dispose the input or output streams — caller owns lifetime.
/// </summary>
public sealed class XorDecoder : IXorDecoder
{
    private const byte Key = 0xA3;
    private const int BufferSize = 64 * 1024;

    public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new byte[BufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                buffer[i] ^= Key;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

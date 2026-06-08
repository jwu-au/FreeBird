using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Decoding;

namespace FreeBird.Core.Tests.Decoding;

public class XorDecoderTests
{
    private const byte Key = 0xA3;
    private readonly XorDecoder _sut = new();

    [Fact]
    public async Task DecodeAsync_EmptyInput_ProducesEmptyOutput()
    {
        using var input = new MemoryStream(Array.Empty<byte>());
        using var output = new MemoryStream();

        await _sut.DecodeAsync(input, output);

        output.Length.Should().Be(0);
    }

    [Theory]
    [InlineData((byte)0x00, (byte)0xA3)]
    [InlineData((byte)0xA3, (byte)0x00)]
    [InlineData((byte)0xFF, (byte)0x5C)]
    [InlineData((byte)0x42, (byte)0xE1)]
    [InlineData((byte)0x12, (byte)0xB1)]
    public async Task DecodeAsync_SingleByte_IsXorred(byte input, byte expected)
    {
        using var inputStream = new MemoryStream(new[] { input });
        using var outputStream = new MemoryStream();

        await _sut.DecodeAsync(inputStream, outputStream);

        outputStream.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task DecodeAsync_ExactBufferSize_64KB()
    {
        var bytes = new byte[65536];
        new Random(42).NextBytes(bytes);
        var expected = bytes.Select(b => (byte)(b ^ Key)).ToArray();

        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();

        await _sut.DecodeAsync(input, output);

        output.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task DecodeAsync_BufferSizePlusOne_HandlesChunkBoundary()
    {
        var bytes = new byte[65537];
        new Random(7).NextBytes(bytes);
        var expected = bytes.Select(b => (byte)(b ^ Key)).ToArray();

        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();

        await _sut.DecodeAsync(input, output);

        output.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task DecodeAsync_LargeMultiChunk_3xBufferSize()
    {
        var bytes = new byte[196608];
        new Random(99).NextBytes(bytes);
        var expected = bytes.Select(b => (byte)(b ^ Key)).ToArray();

        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();

        await _sut.DecodeAsync(input, output);

        output.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task DecodeAsync_IsSelfInverse_RoundTrip()
    {
        var original = new byte[100_000];
        new Random(123).NextBytes(original);

        using var encrypted = new MemoryStream();
        await _sut.DecodeAsync(new MemoryStream(original), encrypted);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await _sut.DecodeAsync(encrypted, decrypted);

        decrypted.ToArray().Should().Equal(original);
    }

    [Fact]
    public async Task DecodeAsync_Cancellation_ThrowsOperationCanceled()
    {
        var bytes = new byte[10_000_000];
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _sut.DecodeAsync(input, output, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DecodeAsync_NullInput_ThrowsArgumentNullException()
    {
        using var output = new MemoryStream();

        Func<Task> act = () => _sut.DecodeAsync(null!, output);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DecodeAsync_NullOutput_ThrowsArgumentNullException()
    {
        using var input = new MemoryStream();

        Func<Task> act = () => _sut.DecodeAsync(input, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DecodeAsync_DoesNotDisposeStreams()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        using var output = new MemoryStream();

        await _sut.DecodeAsync(input, output);

        input.CanRead.Should().BeTrue();
        output.CanWrite.Should().BeTrue();
    }
}

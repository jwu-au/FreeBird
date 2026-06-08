using System;
using System.IO;
using System.Threading.Tasks;
using FreeBird.Core.Decoding;

namespace FreeBird.Core.Tests.Fixtures;

/// <summary>
/// Shared test helpers for audio fixtures.
/// </summary>
public static class Fixtures
{
    /// <summary>Absolute path to the sample MP3 fixture (copied to test output dir).</summary>
    public static string SampleMp3Path =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mp3");

    /// <summary>Absolute path to the sample FLAC fixture.</summary>
    public static string SampleFlacPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.flac");

    /// <summary>
    /// Reads a fixture file and produces an XOR-encrypted (.uc-style) byte array.
    /// Caller can then write it to a .uc file in a temp dir for end-to-end tests.
    /// </summary>
    public static async Task<byte[]> ReadFixtureAsEncryptedAsync(string fixturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Fixture not found: {fixturePath}", fixturePath);
        }

        var plain = await File.ReadAllBytesAsync(fixturePath);
        var encrypted = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            encrypted[i] = (byte)(plain[i] ^ 0xA3);
        }
        return encrypted;
    }

    /// <summary>
    /// Writes a fixture as an encrypted .uc file in the given directory.
    /// Returns the absolute path of the written .uc file.
    /// </summary>
    public static async Task<string> WriteFixtureAsUcAsync(string fixturePath, string outputDir, string ucFileName)
    {
        Directory.CreateDirectory(outputDir);
        var ucPath = Path.Combine(outputDir, ucFileName);
        var bytes = await ReadFixtureAsEncryptedAsync(fixturePath);
        await File.WriteAllBytesAsync(ucPath, bytes);
        return ucPath;
    }
}

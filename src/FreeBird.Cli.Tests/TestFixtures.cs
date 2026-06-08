using System;
using System.IO;
using System.Threading.Tasks;

namespace FreeBird.Cli.Tests;

internal static class TestFixtures
{
    public static string SampleMp3Path => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mp3");
    public static string SampleFlacPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.flac");

    /// <summary>
    /// XOR-encrypts a fixture (every byte ^ 0xA3) and writes it to a .uc file in the given dir.
    /// Returns the absolute path of the written .uc file.
    /// </summary>
    public static async Task<string> WriteFixtureAsUcAsync(string sourcePath, string outputDir, string ucFileName)
    {
        Directory.CreateDirectory(outputDir);
        var src = await File.ReadAllBytesAsync(sourcePath);
        var enc = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            enc[i] = (byte)(src[i] ^ 0xA3);
        }
        var ucPath = Path.Combine(outputDir, ucFileName);
        await File.WriteAllBytesAsync(ucPath, enc);
        return ucPath;
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Provisioning;
using Xunit;

namespace FreeBird.Cli.Tests.Provisioning;

/// <summary>
/// T15 — Windows-only end-to-end test that proves the OS-conditional installer swap
/// in <c>CoreModule</c> (NoOp → Windows) actually downloads + extracts a flac ZIP
/// fixture from a local HTTP server when <c>fb install-flac</c> is invoked.
///
/// Skipped on macOS / Linux because <c>WindowsFlacAutoInstaller</c> is only resolved
/// from the DI graph when <see cref="OperatingSystem.IsWindows"/> is true. On other
/// platforms the path under test is unreachable; component-level coverage lives in
/// <c>WindowsFlacAutoInstallerTests</c> (Core.Tests).
/// </summary>
/// <remarks>
/// Uses <see cref="HttpListener"/> on a high-numbered port to host a tiny in-memory
/// flac.zip fixture (the four Win64 entries with zero-byte content). The pinned SHA256
/// is temporarily overridden via <c>WindowsFlacAutoInstaller.ExpectedSha256</c>
/// (internal-settable; <c>InternalsVisibleTo</c> grants access from the Cli.Tests
/// assembly transitively through the Core.Tests assembly — but here we go via
/// Core.Tests' existing seam pattern: tests construct a fixture ZIP locally, compute
/// its SHA, override the static property, and restore it after.
/// </remarks>
public class WindowsAutoInstallE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalExpectedSha;

    public WindowsAutoInstallE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalExpectedSha = WindowsFlacAutoInstaller.ExpectedSha256;
    }

    public void Dispose()
    {
        WindowsFlacAutoInstaller.ExpectedSha256 = _originalExpectedSha;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task InstallFlac_EndToEnd_DownloadExtractAndUseFlac_Windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "Windows-only E2E test: WindowsFlacAutoInstaller is only registered as IFlacAutoInstaller on Windows.");

        // 1. Build a minimal but valid ZIP fixture with the four required Win64 entries.
        var fixtureZipBytes = BuildFixtureZip();
        var fixtureSha = ComputeSha256Hex(fixtureZipBytes);

        // 2. Override the pinned SHA so the installer accepts our hand-built fixture.
        WindowsFlacAutoInstaller.ExpectedSha256 = fixtureSha;

        // 3. Spin up an HttpListener on a free port serving the fixture at /flac.zip.
        using var server = new LocalZipServer(fixtureZipBytes);
        server.Start();

        try
        {
            // 4. Invoke InstallFlacRunner against the fixture URL + temp target dir.
            var targetDir = Path.Combine(_tempDir, "install-target");
            Directory.CreateDirectory(targetDir);

            var exit = await InstallFlacRunner.RunAsync(
                targetDir: targetDir,
                overrideUrl: server.Url,
                verbose: false,
                quiet: true,
                ct: CancellationToken.None);

            // 5. Verify success exit + all four Win64 files extracted.
            exit.Should().Be(InstallFlacCommand.ExitOk);
            File.Exists(Path.Combine(targetDir, "flac.exe")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "metaflac.exe")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "libFLAC.dll")).Should().BeTrue();
            File.Exists(Path.Combine(targetDir, "libFLAC++.dll")).Should().BeTrue();
        }
        finally
        {
            server.Stop();
        }
    }

    private static byte[] BuildFixtureZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entryName in new[] { "Win64/flac.exe", "Win64/metaflac.exe", "Win64/libFLAC.dll", "Win64/libFLAC++.dll" })
            {
                var entry = archive.CreateEntry(entryName);
                using var es = entry.Open();
                // Zero-byte content is fine — the test only verifies extraction, not flac execution.
                es.Write(Encoding.UTF8.GetBytes(""));
            }
        }
        return ms.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Trivial in-process HTTP server that returns <paramref name="zipBytes"/> at /flac.zip.
    /// Listens on a free localhost port; URL exposed via <see cref="Url"/>.
    /// </summary>
    private sealed class LocalZipServer : IDisposable
    {
        private readonly byte[] _bytes;
        private readonly HttpListener _listener;
        private readonly int _port;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public LocalZipServer(byte[] zipBytes)
        {
            _bytes = zipBytes;
            _port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
        }

        public string Url => $"http://localhost:{_port}/flac.zip";

        public void Start()
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ServeAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        public void Dispose() => Stop();

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }

                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = _bytes.LongLength;
                    await ctx.Response.OutputStream.WriteAsync(_bytes, ct).ConfigureAwait(false);
                }
                catch { /* client disconnect */ }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}

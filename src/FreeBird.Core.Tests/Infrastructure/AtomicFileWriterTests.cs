using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Infrastructure;

namespace FreeBird.Core.Tests.Infrastructure;

public class AtomicFileWriterTests : IDisposable
{
    private readonly AtomicFileWriter _sut = new();
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), $"fb-atomic-{Guid.NewGuid():N}");

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteAsync_CreatesFinalFile_WithExpectedContent()
    {
        var path = Path.Combine(_testDir, "out.txt");
        var content = Encoding.UTF8.GetBytes("hello atomic world");

        await _sut.WriteAsync(path, async (s, ct) =>
        {
            await s.WriteAsync(content, ct);
        }, overwrite: false);

        File.Exists(path).Should().BeTrue();
        (await File.ReadAllBytesAsync(path)).Should().Equal(content);
    }

    [Fact]
    public async Task WriteAsync_NoTmpFilesLeftBehind_OnSuccess()
    {
        var path = Path.Combine(_testDir, "out.txt");
        await _sut.WriteAsync(path, (s, ct) => s.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask(), overwrite: false);

        Directory.GetFiles(_testDir).Should().HaveCount(1);
        Directory.GetFiles(_testDir).Single().Should().EndWith("out.txt");
    }

    [Fact]
    public async Task WriteAsync_OverwriteFalse_FileExists_Throws()
    {
        var path = Path.Combine(_testDir, "out.txt");
        await File.WriteAllTextAsync(path, "existing");

        Func<Task> act = () => _sut.WriteAsync(path,
            (s, ct) => s.WriteAsync(new byte[] { 1 }, ct).AsTask(),
            overwrite: false);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        // Original file untouched
        (await File.ReadAllTextAsync(path)).Should().Be("existing");
    }

    [Fact]
    public async Task WriteAsync_OverwriteTrue_FileExists_ReplacesContent()
    {
        var path = Path.Combine(_testDir, "out.txt");
        await File.WriteAllTextAsync(path, "old");

        await _sut.WriteAsync(path,
            async (s, ct) => await s.WriteAsync(Encoding.UTF8.GetBytes("new"), ct),
            overwrite: true);

        (await File.ReadAllTextAsync(path)).Should().Be("new");
    }

    [Fact]
    public async Task WriteAsync_WriteActionThrows_TmpCleanedUp_FinalNotCreated()
    {
        var path = Path.Combine(_testDir, "out.txt");

        Func<Task> act = () => _sut.WriteAsync(path,
            (s, ct) => throw new InvalidOperationException("boom"),
            overwrite: false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(_testDir).Should().BeEmpty("all tmp files cleaned up");
    }

    [Fact]
    public async Task WriteAsync_Cancellation_TmpCleanedUp()
    {
        var path = Path.Combine(_testDir, "out.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _sut.WriteAsync(path,
            async (s, ct) => { ct.ThrowIfCancellationRequested(); await s.WriteAsync(new byte[] { 1 }, ct); },
            overwrite: false,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_testDir).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_DirectoryDoesNotExist_CreatesIt()
    {
        var subdir = Path.Combine(_testDir, "nested", "dir");
        var path = Path.Combine(subdir, "out.txt");

        await _sut.WriteAsync(path,
            (s, ct) => s.WriteAsync(new byte[] { 0xAA }, ct).AsTask(),
            overwrite: false);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_NullPath_Throws()
    {
        Func<Task> act = () => _sut.WriteAsync(null!,
            (s, ct) => Task.CompletedTask,
            overwrite: false);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteAsync_NullWriteAction_Throws()
    {
        var path = Path.Combine(_testDir, "out.txt");
        Func<Task> act = () => _sut.WriteAsync(path, null!, overwrite: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_PathWithNoDirectory_Throws()
    {
        // Just a bare filename has no directory component
        Func<Task> act = () => _sut.WriteAsync("justafile.txt",
            (s, ct) => Task.CompletedTask,
            overwrite: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*directory*");
    }

    [Fact]
    public async Task WriteAsync_LargeWrite_Succeeds()
    {
        var path = Path.Combine(_testDir, "large.bin");
        var data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);

        await _sut.WriteAsync(path,
            async (s, ct) => await s.WriteAsync(data, ct),
            overwrite: false);

        (await File.ReadAllBytesAsync(path)).Should().Equal(data);
    }
}

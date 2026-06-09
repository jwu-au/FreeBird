using System.Text;
using FluentAssertions;
using FreeBird.Core.Watch;

namespace FreeBird.Core.Tests.Watch;

public sealed class SizeStabilityCompletionDetectorTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly SizeStabilityCompletionDetector _detector = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stability-test-{Guid.NewGuid():N}.uc");
        _tempFiles.Add(path);
        return path;
    }

    private string CreateTempFile(byte[] content)
    {
        var path = NewTempPath();
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task IsStableAsync_NonexistentFile_ReturnsFalse()
    {
        var path = NewTempPath();

        var result = await _detector.IsStableAsync(path, stabilityChecks: 2);

        result.Should().BeFalse();
        // Forget should be a clean no-op (nothing tracked)
        var act = () => _detector.Forget(path);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task IsStableAsync_FirstObservation_WithChecks2_ReturnsFalse()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var result = await _detector.IsStableAsync(path, stabilityChecks: 2);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsStableAsync_TwoConsecutiveStableObservations_WithChecks2_ReturnsTrue()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var first = await _detector.IsStableAsync(path, stabilityChecks: 2);
        var second = await _detector.IsStableAsync(path, stabilityChecks: 2);

        first.Should().BeFalse();
        second.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_ThreeStableObservations_WithChecks3_ReturnsTrue()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var first = await _detector.IsStableAsync(path, stabilityChecks: 3);
        var second = await _detector.IsStableAsync(path, stabilityChecks: 3);
        var third = await _detector.IsStableAsync(path, stabilityChecks: 3);

        first.Should().BeFalse();
        second.Should().BeFalse();
        third.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_StableThenSizeChanges_ResetsCounter()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        // 1 observation
        var first = await _detector.IsStableAsync(path, stabilityChecks: 2);
        first.Should().BeFalse();

        // File grows -> reset counter
        File.AppendAllText(path, "more");

        var afterGrow = await _detector.IsStableAsync(path, stabilityChecks: 2);
        afterGrow.Should().BeFalse(); // counter reset to 1 -> not enough
        var stable = await _detector.IsStableAsync(path, stabilityChecks: 2);
        stable.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_StableThenMtimeChanges_ResetsCounter()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });
        var initialMtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, initialMtime);

        var first = await _detector.IsStableAsync(path, stabilityChecks: 2);
        first.Should().BeFalse();

        // Touch mtime without size change
        File.SetLastWriteTimeUtc(path, initialMtime.AddSeconds(10));

        var afterTouch = await _detector.IsStableAsync(path, stabilityChecks: 2);
        afterTouch.Should().BeFalse(); // counter reset
        var stable = await _detector.IsStableAsync(path, stabilityChecks: 2);
        stable.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_ChecksParamOne_AnyExistingFileImmediatelyStable()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var result = await _detector.IsStableAsync(path, stabilityChecks: 1);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_FileDeletedAfterStableObservation_NextCallReturnsFalse_AndForgets()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var first = await _detector.IsStableAsync(path, stabilityChecks: 2);
        first.Should().BeFalse();

        File.Delete(path);

        var afterDelete = await _detector.IsStableAsync(path, stabilityChecks: 2);
        afterDelete.Should().BeFalse();

        // Recreate the file and verify state was forgotten — should need 2 fresh observations again
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        var fresh1 = await _detector.IsStableAsync(path, stabilityChecks: 2);
        fresh1.Should().BeFalse();
        var fresh2 = await _detector.IsStableAsync(path, stabilityChecks: 2);
        fresh2.Should().BeTrue();
    }

    [Fact]
    public async Task IsStableAsync_CancelledToken_Throws()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _detector.IsStableAsync(path, stabilityChecks: 2, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IsStableAsync_NullPath_Throws()
    {
        var actNull = async () => await _detector.IsStableAsync(null!, stabilityChecks: 2);
        var actEmpty = async () => await _detector.IsStableAsync("", stabilityChecks: 2);
        var actWhitespace = async () => await _detector.IsStableAsync("   ", stabilityChecks: 2);

        await actNull.Should().ThrowAsync<ArgumentException>();
        await actEmpty.Should().ThrowAsync<ArgumentException>();
        await actWhitespace.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task IsStableAsync_ZeroOrNegativeChecks_Throws()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        var actZero = async () => await _detector.IsStableAsync(path, stabilityChecks: 0);
        var actNegative = async () => await _detector.IsStableAsync(path, stabilityChecks: -1);

        await actZero.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await actNegative.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Forget_NonexistentPath_DoesNothing()
    {
        var act = () => _detector.Forget(Path.Combine(Path.GetTempPath(), $"never-seen-{Guid.NewGuid():N}.uc"));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Forget_AfterIsStableObservation_RemovesState()
    {
        var path = CreateTempFile(new byte[] { 1, 2, 3 });

        // Build up 1 observation
        var first = await _detector.IsStableAsync(path, stabilityChecks: 2);
        first.Should().BeFalse();

        _detector.Forget(path);

        // After Forget, should require fresh 2 observations (i.e., 1st call after forget returns false)
        var afterForget1 = await _detector.IsStableAsync(path, stabilityChecks: 2);
        afterForget1.Should().BeFalse();
        var afterForget2 = await _detector.IsStableAsync(path, stabilityChecks: 2);
        afterForget2.Should().BeTrue();
    }

    [Fact]
    public void IsStableAsync_ConcurrentCallsOnDifferentPaths_ThreadSafe()
    {
        // Create 100 distinct temp files
        var paths = new string[100];
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i] = CreateTempFile(Encoding.UTF8.GetBytes($"content-{i}"));
        }

        var act = () => Parallel.For(0, paths.Length, i =>
        {
            // Multiple observations per file in parallel
            _ = _detector.IsStableAsync(paths[i], stabilityChecks: 2).GetAwaiter().GetResult();
            _ = _detector.IsStableAsync(paths[i], stabilityChecks: 2).GetAwaiter().GetResult();
        });

        act.Should().NotThrow();
    }
}

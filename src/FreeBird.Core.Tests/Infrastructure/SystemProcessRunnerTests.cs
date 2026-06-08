using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Infrastructure;

namespace FreeBird.Core.Tests.Infrastructure;

public class SystemProcessRunnerTests
{
    private readonly SystemProcessRunner _sut = new();

    [Fact]
    public async Task RunAsync_EchoCommand_CapturesStdout()
    {
        var (fileName, args) = GetEchoCommand("hello world");
        var result = await _sut.RunAsync(fileName, args);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("hello world");
        result.StandardError.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_IsCaptured()
    {
        var (fileName, args) = GetExitCommand(1);
        var result = await _sut.RunAsync(fileName, args);
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_StderrIsCaptured()
    {
        var (fileName, args) = GetStderrCommand("error-msg");
        var result = await _sut.RunAsync(fileName, args);
        result.StandardError.Should().Contain("error-msg");
    }

    [Fact]
    public async Task RunAsync_NonexistentExecutable_Throws()
    {
        Func<Task> act = () => _sut.RunAsync("this-binary-definitely-does-not-exist-xyz123", Array.Empty<string>());
        await act.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsProcessAndThrows()
    {
        var (fileName, args) = GetSleepCommand(seconds: 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> act = () => _sut.RunAsync(fileName, args, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_NullFileName_Throws()
    {
        Func<Task> act = () => _sut.RunAsync(null!, Array.Empty<string>());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_NullArguments_Throws()
    {
        Func<Task> act = () => _sut.RunAsync("echo", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // --- Cross-platform command helpers ---

    private static (string fileName, IReadOnlyList<string> args) GetEchoCommand(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd", new[] { "/c", "echo", text });
        }
        return ("/bin/sh", new[] { "-c", $"echo '{text}'" });
    }

    private static (string fileName, IReadOnlyList<string> args) GetExitCommand(int code)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd", new[] { "/c", $"exit {code}" });
        }
        return ("/bin/sh", new[] { "-c", $"exit {code}" });
    }

    private static (string fileName, IReadOnlyList<string> args) GetStderrCommand(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd", new[] { "/c", $"echo {text} 1>&2" });
        }
        return ("/bin/sh", new[] { "-c", $"echo '{text}' >&2" });
    }

    private static (string fileName, IReadOnlyList<string> args) GetSleepCommand(int seconds)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("powershell", new[] { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" });
        }
        return ("/bin/sleep", new[] { seconds.ToString() });
    }
}

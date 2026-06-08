using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Infrastructure;

/// <summary>
/// Default IProcessRunner backed by System.Diagnostics.Process.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // T15.6 M9: TCS-driven drain — the OutputDataReceived / ErrorDataReceived events fire
        // with a null Data sentinel once the corresponding stream is fully drained by the runtime.
        // Waiting on those sentinels (in addition to WaitForExitAsync) prevents a race where the
        // process has exited but the async reader hasn't flushed the final lines yet.
        var stdoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(true); }
            else { stdout.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(true); }
            else { stderr.AppendLine(e.Data); }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Now wait for both readers to drain their final EOF sentinel. Cap at 5 seconds in
            // case of a rare runtime hiccup so we never hang forever — partial output is fine.
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task)
                    .WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Drain timeout — not fatal. Fall through and return what we captured.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore kill errors; we're already cleaning up
            }
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            stdout.ToString().TrimEnd('\n', '\r'),
            stderr.ToString().TrimEnd('\n', '\r'));
    }
}

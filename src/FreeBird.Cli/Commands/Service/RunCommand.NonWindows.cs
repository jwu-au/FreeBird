using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T22 — non-Windows fallback body for <see cref="RunCommand.RunHostAsync"/>. This file is
/// compiled on macOS / Linux only (the Windows real body lives in <c>RunCommand.Windows.cs</c>,
/// which references the Windows-only <c>UseWindowsService()</c> / EventLog sink and is excluded
/// here). It is never reached at runtime because <see cref="RunCommand.HandleAsync"/> returns at
/// the <c>OperatingSystem.IsWindows()</c> guard before calling it; it exists solely so the
/// partial method has a body on every OS and the assembly compiles warning-free.
/// </summary>
public static partial class RunCommand
{
    // Off-Windows there is no SCM, so the process is never a Windows service.
    public static partial bool IsRunningAsWindowsService() => false;

    private static partial Task<int> RunHostAsync(RootConfig config, string configPath, CancellationToken ct)
    {
        // Unreachable on non-Windows (HandleAsync's OS guard returns first). Defensive throw so a
        // future refactor that bypasses the guard fails loudly rather than silently no-opping.
        throw new PlatformNotSupportedException(
            "fb service run hosting is Windows-only and must not be invoked on this OS.");
    }
}

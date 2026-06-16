using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Service;

namespace FreeBird.Cli.Service;

/// <summary>
/// Production <see cref="IServiceController"/> that drives the Windows Service Control
/// Manager (SCM) via <see cref="ServiceController"/> for lifecycle control plus
/// <see cref="Advapi32Interop"/> P/Invoke for the install / uninstall / failure-actions
/// and start-time operations that the managed type does not expose.
/// </summary>
/// <remarks>
/// <para>
/// Design notes (v3.5 Windows Service):
/// </para>
/// <list type="bullet">
///   <item><b>No <c>sc.exe</c> shelling</b> (§2.5) — every operation goes through the
///   managed <see cref="ServiceController"/> class or direct advapi32 P/Invoke.</item>
///   <item><b>No WMI / <c>System.Management</c></b> (M-3) — the running service's start
///   time is derived from <c>Process.GetProcessById(pid).StartTime</c>, where the PID
///   comes from <see cref="Advapi32Interop.QueryServiceStatusEx"/>.</item>
///   <item><b>Failure-actions policy</b> (§2.3) maps the Core
///   <see cref="RestartPolicy"/> onto an <see cref="Advapi32Interop.SC_ACTION"/> array:
///   <c>Manual</c> configures nothing (SCM default = take no action); <c>Restart</c>
///   restarts after a fixed 60s delay; <c>Backoff</c> restarts after 1m → 5m → 30m.
///   The reset period is 1 hour (3600s) in all configured cases.</item>
/// </list>
/// <para>
/// The type holds NO native handle as instance state: every SCM handle opened in a
/// method is closed via <see cref="Advapi32Interop.CloseServiceHandle"/> in a
/// <c>finally</c> on that same method, and every unmanaged buffer allocated with
/// <see cref="Marshal.AllocHGlobal(int)"/> is released with
/// <see cref="Marshal.FreeHGlobal(IntPtr)"/> in a matching <c>finally</c>.
/// </para>
/// <para>
/// The whole type is <c>[SupportedOSPlatform("windows")]</c> and the source file is
/// excluded from the macOS/Linux compile set in <c>FreeBird.Cli.csproj</c>; the DI
/// binding (T14) registers it only when <see cref="OperatingSystem.IsWindows()"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController : IServiceController
{
    // §2.3 failure-action delays (milliseconds) and reset window (seconds).
    private const uint RestartDelayFirstMs = 60_000;     // 1 minute
    private const uint RestartDelaySecondMs = 300_000;   // 5 minutes
    private const uint RestartDelayThirdMs = 1_800_000;  // 30 minutes
    private const uint FailureResetPeriodSeconds = 3600; // reset after 1 hour stable

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="req"/>'s <see cref="ServiceInstallRequest.BinPath"/> is passed
    /// verbatim to <c>CreateServiceW</c>'s <c>lpBinaryPathName</c>. Per the DTO contract
    /// the <b>caller</b> is responsible for quoting any executable path containing spaces
    /// (e.g. <c>"\"C:\\Program Files\\fb.exe\" run --config \"...\""</c>); this method does
    /// not re-quote, because a quoting heuristic here would risk double-quoting an
    /// already-quoted value.
    /// </remarks>
    public void Install(ServiceInstallRequest req)
    {
        IntPtr scmHandle = Advapi32Interop.OpenSCManagerW(
            null, null, Advapi32Interop.SC_MANAGER_CREATE_SERVICE);
        ThrowIfHandleNull(scmHandle, "open the Service Control Manager");
        try
        {
            // LocalSystem when no account is supplied: lpServiceStartName = null.
            string? startName = req.Account;
            string? password = req.Account is null ? null : req.Password;

            IntPtr serviceHandle = Advapi32Interop.CreateServiceW(
                scmHandle,
                req.ShortName,
                req.DisplayName,
                Advapi32Interop.SERVICE_ALL_ACCESS,
                Advapi32Interop.SERVICE_WIN32_OWN_PROCESS,
                Advapi32Interop.SERVICE_AUTO_START,
                Advapi32Interop.SERVICE_ERROR_NORMAL,
                req.BinPath,
                null,
                IntPtr.Zero,
                null,
                startName,
                password);
            ThrowIfHandleNull(serviceHandle, $"create the service '{req.ShortName}'");
            try
            {
                ApplyDescription(serviceHandle, req.Description);
                ApplyFailureActions(serviceHandle, req.RestartPolicy);
            }
            finally
            {
                Advapi32Interop.CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            Advapi32Interop.CloseServiceHandle(scmHandle);
        }
    }

    /// <inheritdoc/>
    public void Uninstall(string shortName)
    {
        IntPtr scmHandle = Advapi32Interop.OpenSCManagerW(
            null, null, Advapi32Interop.SC_MANAGER_CONNECT);
        ThrowIfHandleNull(scmHandle, "open the Service Control Manager");
        try
        {
            IntPtr serviceHandle = Advapi32Interop.OpenServiceW(
                scmHandle, shortName, Advapi32Interop.DELETE);
            ThrowIfHandleNull(serviceHandle, $"open the service '{shortName}'");
            try
            {
                if (!Advapi32Interop.DeleteService(serviceHandle))
                {
                    throw BuildWin32Exception(
                        Marshal.GetLastWin32Error(), $"delete the service '{shortName}'");
                }
            }
            finally
            {
                Advapi32Interop.CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            Advapi32Interop.CloseServiceHandle(scmHandle);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(string shortName, TimeSpan timeout, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var controller = new ServiceController(shortName);
            if (controller.Status != ServiceControllerStatus.Running &&
                controller.Status != ServiceControllerStatus.StartPending)
            {
                controller.Start();
            }
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }, ct);

    /// <inheritdoc/>
    public Task StopAsync(string shortName, TimeSpan timeout, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var controller = new ServiceController(shortName);
            if (controller.Status != ServiceControllerStatus.Stopped &&
                controller.Status != ServiceControllerStatus.StopPending)
            {
                controller.Stop();
            }
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }, ct);

    /// <inheritdoc/>
    public ServiceStatusInfo Query(string shortName)
    {
        ServiceController? match = ServiceController.GetServices()
            .FirstOrDefault(s => string.Equals(s.ServiceName, shortName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new ServiceStatusInfo(
                ShortName: shortName,
                DisplayName: shortName,
                State: ServiceState.NotInstalled,
                Account: null,
                StartType: null,
                StartedAtUtc: null,
                ConfigPath: null,
                LogFilePath: null);
        }

        using (match)
        {
            ServiceState state = MapState(match.Status);
            DateTimeOffset? startedAtUtc = state == ServiceState.Running
                ? TryGetStartedAtUtc(shortName)
                : null;

            return new ServiceStatusInfo(
                ShortName: match.ServiceName,
                DisplayName: match.DisplayName,
                State: state,
                Account: null,
                StartType: null,
                StartedAtUtc: startedAtUtc,
                ConfigPath: null,
                LogFilePath: null);
        }
    }

    /// <inheritdoc/>
    public bool Exists(string shortName) =>
        ServiceController.GetServices()
            .Any(s => string.Equals(s.ServiceName, shortName, StringComparison.OrdinalIgnoreCase));

    // --- helpers -----------------------------------------------------------

    private static void ApplyDescription(IntPtr serviceHandle, string description)
    {
        IntPtr descriptionPtr = Marshal.StringToHGlobalUni(description);
        try
        {
            var info = new Advapi32Interop.SERVICE_DESCRIPTIONW { lpDescription = descriptionPtr };
            IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Advapi32Interop.SERVICE_DESCRIPTIONW>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!Advapi32Interop.ChangeServiceConfig2W(
                        serviceHandle, Advapi32Interop.SERVICE_CONFIG_DESCRIPTION, infoPtr))
                {
                    throw BuildWin32Exception(
                        Marshal.GetLastWin32Error(), "set the service description");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptionPtr);
        }
    }

    private static void ApplyFailureActions(IntPtr serviceHandle, RestartPolicy policy)
    {
        Advapi32Interop.SC_ACTION[]? actions = BuildActions(policy);

        // Manual policy: leave the SCM default (take no action) — do not call
        // ChangeServiceConfig2W at all so we don't overwrite any existing config.
        if (actions is null)
        {
            return;
        }

        int actionSize = Marshal.SizeOf<Advapi32Interop.SC_ACTION>();
        IntPtr actionsPtr = Marshal.AllocHGlobal(actionSize * actions.Length);
        try
        {
            for (int i = 0; i < actions.Length; i++)
            {
                Marshal.StructureToPtr(actions[i], actionsPtr + (i * actionSize), false);
            }

            var failureActions = new Advapi32Interop.SERVICE_FAILURE_ACTIONSW
            {
                dwResetPeriod = FailureResetPeriodSeconds,
                lpRebootMsg = IntPtr.Zero,
                lpCommand = IntPtr.Zero,
                cActions = (uint)actions.Length,
                lpsaActions = actionsPtr,
            };

            IntPtr failureActionsPtr = Marshal.AllocHGlobal(
                Marshal.SizeOf<Advapi32Interop.SERVICE_FAILURE_ACTIONSW>());
            try
            {
                Marshal.StructureToPtr(failureActions, failureActionsPtr, false);
                if (!Advapi32Interop.ChangeServiceConfig2W(
                        serviceHandle, Advapi32Interop.SERVICE_CONFIG_FAILURE_ACTIONS, failureActionsPtr))
                {
                    throw BuildWin32Exception(
                        Marshal.GetLastWin32Error(), "configure the service failure actions");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(failureActionsPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actionsPtr);
        }
    }

    /// <summary>
    /// Maps a <see cref="RestartPolicy"/> to the SCM failure-action sequence, or
    /// <c>null</c> for <see cref="RestartPolicy.Manual"/> (no actions configured).
    /// </summary>
    private static Advapi32Interop.SC_ACTION[]? BuildActions(RestartPolicy policy) => policy switch
    {
        RestartPolicy.Manual => null,
        RestartPolicy.Restart =>
        [
            new() { Type = Advapi32Interop.SC_ACTION_RESTART, Delay = RestartDelayFirstMs },
        ],
        RestartPolicy.Backoff =>
        [
            new() { Type = Advapi32Interop.SC_ACTION_RESTART, Delay = RestartDelayFirstMs },
            new() { Type = Advapi32Interop.SC_ACTION_RESTART, Delay = RestartDelaySecondMs },
            new() { Type = Advapi32Interop.SC_ACTION_RESTART, Delay = RestartDelayThirdMs },
        ],
        _ => null,
    };

    /// <summary>
    /// Resolves the running service's start time via its host process. Returns
    /// <c>null</c> on any failure — the PID may already be gone, or the host process
    /// may be inaccessible to this token (M-3: no WMI).
    /// </summary>
    private static DateTimeOffset? TryGetStartedAtUtc(string shortName)
    {
        IntPtr scmHandle = Advapi32Interop.OpenSCManagerW(
            null, null, Advapi32Interop.SC_MANAGER_CONNECT);
        if (scmHandle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            IntPtr serviceHandle = Advapi32Interop.OpenServiceW(
                scmHandle, shortName, Advapi32Interop.SERVICE_QUERY_STATUS);
            if (serviceHandle == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                int bufferSize = Marshal.SizeOf<Advapi32Interop.SERVICE_STATUS_PROCESS>();
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    if (!Advapi32Interop.QueryServiceStatusEx(
                            serviceHandle,
                            Advapi32Interop.SC_STATUS_PROCESS_INFO,
                            buffer,
                            (uint)bufferSize,
                            out _))
                    {
                        return null;
                    }

                    var status = Marshal.PtrToStructure<Advapi32Interop.SERVICE_STATUS_PROCESS>(buffer);
                    uint processId = status.dwProcessId;
                    if (processId == 0)
                    {
                        return null;
                    }

                    try
                    {
                        DateTime startTimeUtc = Process.GetProcessById((int)processId)
                            .StartTime.ToUniversalTime();
                        return new DateTimeOffset(startTimeUtc, TimeSpan.Zero);
                    }
                    catch (ArgumentException)
                    {
                        // PID no longer maps to a running process.
                        return null;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process has exited between query and inspection.
                        return null;
                    }
                    catch (Win32Exception)
                    {
                        // Access denied reading the process start time.
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                Advapi32Interop.CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            Advapi32Interop.CloseServiceHandle(scmHandle);
        }
    }

    private static ServiceState MapState(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => ServiceState.Stopped,
        ServiceControllerStatus.StartPending => ServiceState.StartPending,
        ServiceControllerStatus.Running => ServiceState.Running,
        ServiceControllerStatus.StopPending => ServiceState.StopPending,
        ServiceControllerStatus.Paused => ServiceState.Paused,
        _ => ServiceState.Other,
    };

    private static void ThrowIfHandleNull(IntPtr handle, string operation)
    {
        if (handle == IntPtr.Zero)
        {
            throw BuildWin32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }

    /// <summary>
    /// Wraps a Win32 error in a friendly <see cref="InvalidOperationException"/>. When the
    /// error is <c>ERROR_ACCESS_DENIED</c> the message names the required
    /// Administrator privilege so the install/uninstall commands can surface a remedy.
    /// </summary>
    private static InvalidOperationException BuildWin32Exception(int errorCode, string operation)
    {
        var inner = new Win32Exception(errorCode);
        if (errorCode == Advapi32Interop.ERROR_ACCESS_DENIED)
        {
            return new InvalidOperationException(
                $"Failed to {operation}: this operation requires Administrator privileges.", inner);
        }
        return new InvalidOperationException(
            $"Failed to {operation}: {inner.Message}", inner);
    }
}

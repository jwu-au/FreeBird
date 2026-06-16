using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FreeBird.Cli.Service;

/// <summary>
/// Raw <c>advapi32.dll</c> P/Invoke surface for the Windows Service Control Manager
/// (SCM). Consumed exclusively by <see cref="WindowsServiceController"/> for the
/// install / uninstall / failure-actions / start-time code paths that
/// <see cref="System.ServiceProcess.ServiceController"/> does not expose.
/// </summary>
/// <remarks>
/// <para>
/// Design constraints (v3.5 Windows Service, §2.3 / risk row 6):
/// </para>
/// <list type="bullet">
///   <item>Every entry point uses the <c>W</c> (Unicode) variant with
///   <c>CharSet.Unicode</c> so service names / descriptions round-trip non-ASCII
///   correctly and we never bind to the ANSI thunks.</item>
///   <item><c>SetLastError = true</c> on every import so the caller can surface a
///   meaningful <see cref="System.ComponentModel.Win32Exception"/> via
///   <see cref="Marshal.GetLastWin32Error"/>.</item>
///   <item><c>DllImport</c> (not <c>LibraryImport</c>) is used deliberately: the
///   <see cref="SC_ACTION"/> array and the pointer-bearing
///   <see cref="SERVICE_FAILURE_ACTIONSW"/> struct are marshalled by hand through
///   <see cref="IntPtr"/> buffers, which the classic marshaller handles without the
///   strict source-generated rules that would otherwise need exhaustive Windows
///   testing this dev host (macOS) cannot provide.</item>
/// </list>
/// <para>
/// The whole type is <c>[SupportedOSPlatform("windows")]</c> and the source file is
/// excluded from the macOS/Linux compile set in <c>FreeBird.Cli.csproj</c>, so it
/// never participates in non-Windows builds.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Advapi32Interop
{
    private const string Advapi32 = "advapi32.dll";

    // --- SCM access rights -------------------------------------------------

    /// <summary>Required to connect to the SCM (implicit in any open).</summary>
    internal const uint SC_MANAGER_CONNECT = 0x0001;

    /// <summary>Required to call <see cref="CreateServiceW"/>.</summary>
    internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;

    /// <summary>All service-object access rights — used for create + reconfigure.</summary>
    internal const uint SERVICE_ALL_ACCESS = 0xF01FF;

    /// <summary>Right required to call <see cref="DeleteService"/>.</summary>
    internal const uint DELETE = 0x10000;

    /// <summary>Right required to call <see cref="QueryServiceStatusEx"/>.</summary>
    internal const uint SERVICE_QUERY_STATUS = 0x0004;

    // --- Service type / start / error control ------------------------------

    /// <summary>The service runs in its own process.</summary>
    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x10;

    /// <summary>Auto-start at boot (design §2.3 default for the FreeBird service).</summary>
    internal const uint SERVICE_AUTO_START = 0x2;

    /// <summary>Log the error, continue boot — the standard severity for app services.</summary>
    internal const uint SERVICE_ERROR_NORMAL = 0x1;

    // --- ChangeServiceConfig2W info levels ---------------------------------

    /// <summary><c>dwInfoLevel</c> for a <see cref="SERVICE_DESCRIPTIONW"/> payload.</summary>
    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;

    /// <summary><c>dwInfoLevel</c> for a <see cref="SERVICE_FAILURE_ACTIONSW"/> payload.</summary>
    internal const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;

    // --- QueryServiceStatusEx info level -----------------------------------

    /// <summary><c>InfoLevel</c> selecting a <see cref="SERVICE_STATUS_PROCESS"/> result.</summary>
    internal const int SC_STATUS_PROCESS_INFO = 0;

    // --- SC_ACTION types ----------------------------------------------------

    /// <summary>SCM takes no action on failure.</summary>
    internal const int SC_ACTION_NONE = 0;

    /// <summary>SCM restarts the service after the action delay.</summary>
    internal const int SC_ACTION_RESTART = 1;

    // --- Win32 error codes we special-case ---------------------------------

    /// <summary><c>ERROR_ACCESS_DENIED</c> — surfaced as an "Administrator" hint.</summary>
    internal const int ERROR_ACCESS_DENIED = 5;

    // --- P/Invoke signatures (all W / Unicode / SetLastError) --------------

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManagerW(
        string? lpMachineName,
        string? lpDatabaseName,
        uint dwDesiredAccess);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        uint dwDesiredAccess);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(IntPtr hService);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig2W(
        IntPtr hService,
        uint dwInfoLevel,
        IntPtr lpInfo);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(
        IntPtr hService,
        int InfoLevel,
        IntPtr lpBuffer,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr hSCObject);

    // --- Native structs -----------------------------------------------------

    /// <summary>
    /// Mirrors Win32 <c>SERVICE_STATUS_PROCESS</c>. Returned by
    /// <see cref="QueryServiceStatusEx"/> under <see cref="SC_STATUS_PROCESS_INFO"/>;
    /// the only field we read is <see cref="dwProcessId"/> (M-3 start-time lookup).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    /// <summary>
    /// Mirrors Win32 <c>SC_ACTION</c>: a single failure-recovery step. <see cref="Type"/>
    /// is one of the <c>SC_ACTION_*</c> constants; <see cref="Delay"/> is the wait before
    /// the action fires, in milliseconds.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    /// <summary>
    /// Mirrors Win32 <c>SERVICE_FAILURE_ACTIONSW</c>. <see cref="lpsaActions"/> points at a
    /// caller-owned contiguous block of <see cref="cActions"/> <see cref="SC_ACTION"/>
    /// structs; the string/pointer members we do not use are left as
    /// <see cref="IntPtr.Zero"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SERVICE_FAILURE_ACTIONSW
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    /// <summary>
    /// Mirrors Win32 <c>SERVICE_DESCRIPTIONW</c>. <see cref="lpDescription"/> points at a
    /// caller-owned Unicode string (allocated via
    /// <see cref="Marshal.StringToHGlobalUni(string)"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SERVICE_DESCRIPTIONW
    {
        public IntPtr lpDescription;
    }
}

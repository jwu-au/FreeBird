using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli.Service;
using FreeBird.Core.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// Arguments for <c>fb service install</c>. <see cref="ConfigPath"/> is required;
/// <see cref="ServiceAccount"/>/<see cref="ServicePassword"/> are optional overrides for
/// running the service under a specific (non-LocalSystem) account.
/// </summary>
public sealed record InstallArgs(string ConfigPath, string? ServiceAccount, string? ServicePassword);

/// <summary>
/// T18 — Core of <c>fb service install</c>: admin-gate → load+validate config →
/// check-not-already-installed → register with the SCM via
/// <see cref="IServiceController.Install(ServiceInstallRequest)"/> → create the Event Log
/// source → emit a user-profile-path warning when relevant.
///
/// <para>The unit-tested surface is the static <see cref="HandleAsync"/> method. Every
/// collaborator is injected (no Autofac container, no static seam), so all branches are
/// testable on any OS with mocked controllers — the real Windows SCM is never touched.</para>
///
/// <para>This is a static helper holding no mutable state, so it is NOT a public static
/// test seam (AGENTS.md §6). System.CommandLine wiring is provided inline by T22 in
/// <c>CliRoot.Build()</c>; <see cref="HandleAsync"/> stays the tested core.</para>
///
/// <para>Exit codes: 0 success, 1 not admin, 2 already installed, 3 config invalid/missing,
/// 4 SCM error.</para>
/// </summary>
public static class InstallCommand
{
    /// <summary>Event Log source name registered on install (design §).</summary>
    private const string EventLogSource = "FreeBird";

    /// <summary>Event Log target log registered on install (design §).</summary>
    private const string EventLogName = "Application";

    /// <summary>Default service account when no <c>--service-account</c> override is supplied.</summary>
    private const string LocalSystemAccount = "LocalSystem";

    /// <summary>Env var consulted (before the interactive prompt) for the service-account password.</summary>
    private const string PasswordEnvVar = "FB_SERVICE_PASSWORD";

    /// <summary>
    /// Matches a Windows user-profile path of the form <c>C:\Users\...</c> (any drive letter).
    /// Combined with the <c>\AppData\</c> / <c>%LocalAppData%</c> substring checks to flag
    /// inputs that LocalSystem typically cannot read.
    /// </summary>
    private static readonly Regex UsersPathRegex =
        new(@"[A-Za-z]:\\Users\\", RegexOptions.Compiled);

    public static async Task<int> HandleAsync(
        InstallArgs args,
        IElevationChecker elevation,
        IConfigLoader configLoader,
        IServiceController controller,
        IEventLogWriter eventLog,
        TextWriter stdout,
        TextWriter stderr,
        TextReader stdin,
        CancellationToken ct)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        // 1. Admin gate — never call controller.Install for an unelevated process.
        if (!elevation.IsAdmin())
        {
            await stderr.WriteAsync(AdminElevationMessage.Build("install", args.ConfigPath));
            return 1;
        }

        // 2. Load + validate config. ConfigLoadException (incl. file-missing, which the
        //    loader reports with FieldName "configFilePath") and a raw FileNotFound both
        //    map to exit 3.
        RootConfig config;
        try
        {
            config = configLoader.Load(args.ConfigPath);
        }
        catch (ConfigLoadException ex)
        {
            await stderr.WriteLineAsync(
                $"Config error in '{args.ConfigPath}' (field '{ex.FieldName}'): {ex.Message}");
            return 3;
        }
        catch (FileNotFoundException)
        {
            await stderr.WriteLineAsync($"Config file not found: {args.ConfigPath}");
            return 3;
        }

        // 3. Refuse to clobber an existing registration.
        if (controller.Exists(config.Service.Name))
        {
            await stderr.WriteLineAsync(
                $"Service '{config.Service.Name}' is already installed. Run 'fb service uninstall' first.");
            return 2;
        }

        // 4. Resolve the service-account password when an account override was supplied
        //    without an inline password. NEVER echo the password to stdout/stderr/logs.
        var account = string.IsNullOrEmpty(args.ServiceAccount) ? LocalSystemAccount : args.ServiceAccount;
        var password = args.ServicePassword;
        if (!string.IsNullOrEmpty(args.ServiceAccount) && string.IsNullOrEmpty(password))
        {
            password = await ResolvePasswordAsync(args.ServiceAccount, stdout, stdin);
        }

        // 5. Build the request from config + resolved account, then hand off to the SCM.
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var request = new ServiceInstallRequest(
            ShortName: config.Service.Name,
            DisplayName: config.Service.DisplayName,
            Description: config.Service.Description,
            BinPath: BuildBinPath(exePath, args.ConfigPath),
            Account: account,
            Password: password,
            RestartPolicy: config.Service.RestartPolicy);

        try
        {
            controller.Install(request);
        }
        catch (Exception ex)
        {
            // Any SCM failure maps to exit 4. Do not leak the password (it is not in ex).
            await stderr.WriteLineAsync($"SCM error: {ex.Message}");
            return 4;
        }

        // 6. Register the Event Log source (idempotent).
        eventLog.CreateSourceIfMissing(EventLogSource, EventLogName);

        // 7. User-profile heuristic — LocalSystem usually cannot read per-user cache paths.
        if (account == LocalSystemAccount && HasUserProfileInput(config.Watch.Inputs))
        {
            await stdout.WriteLineAsync(
                "WARNING: one or more watched inputs look like user-profile paths, but the " +
                $"service is configured to run as {LocalSystemAccount}, which typically cannot " +
                "read another user's profile (e.g. AppData) directories. If decryption finds no " +
                "files, re-install with --service-account <DOMAIN\\\\user> so the service runs " +
                "under an account that can see those paths.");
        }

        // 8. Success.
        await stdout.WriteLineAsync($"Service '{config.Service.Name}' installed successfully.");
        return 0;
    }

    /// <summary>
    /// Builds the SCM <c>BinaryPathName</c>: the FreeBird executable followed by
    /// <c>service run --config "&lt;configPath&gt;"</c>. Both the exe path and the config
    /// path are quoted so spaces are handled correctly. Extracted as a pure helper so it can
    /// be unit-tested with a fixed exe path (the real path comes from
    /// <c>Process.GetCurrentProcess().MainModule.FileName</c> in <see cref="HandleAsync"/>).
    /// </summary>
    public static string BuildBinPath(string exePath, string configPath)
    {
        return $"\"{exePath}\" service run --config \"{configPath}\"";
    }

    /// <summary>
    /// Resolves the service-account password: the <c>FB_SERVICE_PASSWORD</c> env var takes
    /// precedence (testable + scriptable); otherwise a single line is read from the injected
    /// stdin after writing a prompt to stdout. The password is never echoed back.
    /// </summary>
    private static async Task<string?> ResolvePasswordAsync(string account, TextWriter stdout, TextReader stdin)
    {
        var fromEnv = Environment.GetEnvironmentVariable(PasswordEnvVar);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        await stdout.WriteLineAsync($"Enter password for service account '{account}':");
        var typed = await stdin.ReadLineAsync();
        return typed;
    }

    /// <summary>
    /// True when any watched input looks like a per-user profile path: contains
    /// <c>\AppData\</c>, contains <c>%LocalAppData%</c>, or matches <c>&lt;drive&gt;:\Users\</c>.
    /// Comparisons are case-insensitive.
    /// </summary>
    private static bool HasUserProfileInput(string[] inputs)
    {
        return inputs.Any(input =>
            !string.IsNullOrEmpty(input)
            && (input.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
                || input.Contains("%LocalAppData%", StringComparison.OrdinalIgnoreCase)
                || UsersPathRegex.IsMatch(input)));
    }
}

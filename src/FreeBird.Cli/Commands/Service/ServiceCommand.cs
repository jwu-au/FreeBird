using System.CommandLine;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T22 — the <c>service</c> root command. It is a bare container: the eight child leaves
/// (init/install/uninstall/start/stop/restart/status + the hidden <c>run</c>) are wired inline
/// in <see cref="FreeBird.Cli.CliRoot.Build"/>, mirroring the scan/watch/install-flac precedent.
///
/// <para>This factory deliberately takes NO Autofac container parameter: the per-leaf handlers
/// build their own container/logger on demand inside their <c>SetAction</c> bodies, so there is
/// no container-injecting overload to grep for (AGENTS.md §6).</para>
/// </summary>
public static class ServiceCommand
{
    public static Command Create() => new(
        "service",
        "Manage the FreeBird Windows Service (install/uninstall/start/stop/restart/status). Windows-only.");
}

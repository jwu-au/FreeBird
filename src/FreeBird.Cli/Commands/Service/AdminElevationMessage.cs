namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// Builds the copy-paste PowerShell admin-elevation error block (design §3) shown
/// when an <c>fb service</c> subcommand requires Administrator privileges.
///
/// The block is three lines:
///   1. <c>ERROR: 'fb service &lt;subcommand&gt;' requires Administrator privileges.</c>
///   2. <c>Run this in an elevated PowerShell:</c>
///   3. (two leading spaces) <c>Start-Process fb -ArgumentList 'service &lt;subcommand&gt; [--config &lt;configPath&gt;]' -Verb RunAs</c>
///
/// The single quotes inside <c>-ArgumentList</c> are literal (PowerShell single-quoted
/// string), and the config path is emitted verbatim. Lines are joined with a literal
/// "\n" (not Environment.NewLine) so the rendered snippet is identical on every OS.
/// </summary>
public static class AdminElevationMessage
{
    public static string Build(string subcommand, string? configPath = null)
    {
        var argumentList = $"service {subcommand}";
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            argumentList += $" --config {configPath}";
        }

        var line1 = $"ERROR: 'fb service {subcommand}' requires Administrator privileges.";
        var line2 = "Run this in an elevated PowerShell:";
        var line3 = $"  Start-Process fb -ArgumentList '{argumentList}' -Verb RunAs";

        return string.Join("\n", line1, line2, line3);
    }
}

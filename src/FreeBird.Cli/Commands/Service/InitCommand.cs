using System;
using System.CommandLine;
using System.IO;
using System.IO.Abstractions;
using FreeBird.Core.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T17 — Core of <c>fb service init</c>: writes the §4 default JSON config template
/// to a target path so an operator can edit it and feed it to <c>fb service install</c>.
///
/// <para>The unit-tested surface is the static <see cref="Handle"/> method. All file I/O
/// is funnelled through an injected <see cref="IFileSystem"/> (the same abstraction used
/// by <c>LogPathResolver</c>), so every branch — including the default-path expansion —
/// is testable on any OS with a <c>MockFileSystem</c>, never touching the real
/// <c>%ProgramData%</c>.</para>
///
/// <para>This is a static helper holding no mutable state, so it is not a public static
/// test seam (AGENTS.md §6.1). The System.CommandLine wiring is provided as a thin
/// <see cref="Create"/> convenience, but T22 may wire the <c>init</c> leaf inline in
/// <c>CliRoot.Build()</c> instead — either way <see cref="Handle"/> stays the tested core.</para>
/// </summary>
public static class InitCommand
{
    // §4 placeholder paths embedded into the generated template for the user to edit.
    private const string SampleInput =
        @"C:\Users\<you>\AppData\Local\NetEase\CloudMusic\Cache\Cache";
    private const string SampleOutput = @"D:\Music\NetEase";

    /// <summary>
    /// Writes the default config template to <paramref name="outputPath"/> (or the
    /// <c>&lt;CommonApplicationData&gt;/FreeBird/config.json</c> default when null/empty).
    /// </summary>
    /// <returns>0 on success; 1 when the target exists and <paramref name="force"/> is false;
    /// 3 on an I/O error during the write.</returns>
    public static int Handle(
        string? outputPath,
        bool force,
        IFileSystem fs,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (fs is null)
        {
            throw new ArgumentNullException(nameof(fs));
        }
        if (stdout is null)
        {
            throw new ArgumentNullException(nameof(stdout));
        }
        if (stderr is null)
        {
            throw new ArgumentNullException(nameof(stderr));
        }

        var target = string.IsNullOrWhiteSpace(outputPath)
            ? BuildDefaultConfigPath(fs)
            : outputPath;

        // Never clobber an existing file unless --force was given.
        if (fs.File.Exists(target) && !force)
        {
            stderr.WriteLine(
                $"Config file already exists: {target}. Re-run with --force to overwrite it.");
            return 1;
        }

        var content = DefaultConfigTemplate.Render(SampleInput, SampleOutput);

        try
        {
            WriteAtomically(fs, target, content, force);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
        {
            // Map only I/O-ish failures to exit 3; truly unexpected exceptions propagate.
            stderr.WriteLine($"Failed to write config file '{target}': {ex.Message}");
            return 3;
        }

        stdout.WriteLine($"Wrote default config to {target}");
        return 0;
    }

    /// <summary>
    /// Builds the default config path: <c>&lt;CommonApplicationData&gt;/FreeBird/config.json</c>,
    /// mirroring <c>LogPathResolver.BuildProgramDataDefault</c>. Combined via the injected
    /// <see cref="IFileSystem.Path"/> so the separator stays platform-correct under tests.
    /// </summary>
    private static string BuildDefaultConfigPath(IFileSystem fs)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return fs.Path.Combine(programData, "FreeBird", "config.json");
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="target"/> atomically: it goes to
    /// a sibling temp file in the same parent directory, then is moved into place. This avoids
    /// leaving a half-written final file if the write is interrupted.
    /// </summary>
    private static void WriteAtomically(IFileSystem fs, string target, string content, bool overwrite)
    {
        var parent = fs.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            fs.Directory.CreateDirectory(parent);
        }

        var tempPath = $"{target}.{Guid.NewGuid():N}.tmp";
        fs.File.WriteAllText(tempPath, content);
        fs.File.Move(tempPath, target, overwrite: overwrite);
    }

    /// <summary>
    /// Thin System.CommandLine wiring for the <c>init</c> leaf. Provided for convenience;
    /// T22 may instead wire this inline in <c>CliRoot.Build()</c>. The action delegates to
    /// <see cref="Handle"/> with a real filesystem and the console streams.
    /// </summary>
    public static Command Create()
    {
        var outputOption = new Option<string?>("--output")
        {
            Description = "Path to write the config file to. Defaults to %ProgramData%/FreeBird/config.json.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite an existing config file.",
        };

        var command = new Command("init", "Write a default FreeBird service config file.")
        {
            outputOption,
            forceOption,
        };

        command.SetAction(parseResult =>
            Handle(
                parseResult.GetValue(outputOption),
                parseResult.GetValue(forceOption),
                new FileSystem(),
                Console.Out,
                Console.Error));

        return command;
    }
}

using System;

namespace FreeBird.Core.Watch;

/// <summary>
/// Represents a single input directory being watched. The BaseName is used for log enrichment
/// (e.g., "[watch=cache1] processing file X") so users can distinguish concurrent watches.
/// </summary>
public sealed record WatchInput
{
    public required string Path { get; init; }
    public required string BaseName { get; init; }

    /// <summary>Creates a WatchInput from an absolute path; BaseName = leaf folder name.</summary>
    public static WatchInput FromPath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        var full = System.IO.Path.GetFullPath(absolutePath);
        // Trim trailing separators so leaf-name extraction works for paths like /tmp/cache/
        full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        // Preserve root: if trimming made us empty, restore single separator
        if (full.Length == 0)
        {
            full = System.IO.Path.DirectorySeparatorChar.ToString();
        }
        var baseName = System.IO.Path.GetFileName(full);
        // For root paths, GetFileName returns empty; fall back to a stable label
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "<root>";
        }
        return new WatchInput { Path = full, BaseName = baseName };
    }
}

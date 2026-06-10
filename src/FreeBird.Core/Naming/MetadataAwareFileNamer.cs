using System;
using System.Globalization;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Naming;

/// <summary>
/// v3 file namer: composes <see cref="INamingTemplateRenderer"/> + <see cref="FilenameSanitizer"/>
/// + extension to produce the final output filename string.
///
/// Pure: no I/O, no network. Receives the already-resolved <see cref="SongInfo"/>
/// (or null for fallback) from the upstream pipeline (FileProcessor consumes
/// IMetadataResolver and passes the SongInfo, or null on Fallback).
///
/// Fallback rule (per spec §10): when <paramref name="metadata"/> is null, the
/// template is IGNORED and the filename is always <c>{musicId}.{ext}</c>.
/// </summary>
public sealed class MetadataAwareFileNamer : IFileNamer
{
    // The fallback template used when the caller does not pass a per-run namingTemplate.
    // Aliased to MetadataDefaults.NamingTemplate (the single source of truth) so this
    // namer and the CLI/options layer cannot drift apart.
    private const string DefaultNamingTemplate = MetadataDefaults.NamingTemplate;

    private readonly INamingTemplateRenderer _renderer;

    public MetadataAwareFileNamer(INamingTemplateRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    /// <inheritdoc />
    public string GetTargetName(string sourcePath, AudioFormat format, SongInfo? metadata, string? namingTemplate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (format == AudioFormat.Unknown)
        {
            throw new ArgumentException("Cannot generate filename for unknown format", nameof(format));
        }

        var ext = GetExtension(format);

        // Extract musicId from the .uc/.uc! stem. The stem is either:
        //   - A bare numeric id (most common: "3367798042.uc")
        //   - A composite cache name like "3367798042-_-_5999-_-_xxx.uc" — take leading digits.
        // MusicIdExtractor returns 0L on failure; that's still a deterministic fallback name.
        MusicIdExtractor.TryExtract(sourcePath, out var musicId);

        // Fallback path: spec §10 mandates {musicId}.{ext} regardless of template.
        if (metadata is null)
        {
            // musicId may be 0 if extraction failed; that's still a deterministic name.
            return musicId.ToString(CultureInfo.InvariantCulture) + ext;
        }

        // Success path: render template (per-run or default), sanitize, append extension.
        // v3 T19a: per-run template now flows in through the method parameter rather
        // than from a DI-injected IMetadataOptions; null falls back to the v1/v2 default.
        var template = namingTemplate ?? DefaultNamingTemplate;
        var rendered = _renderer.Render(template, metadata, musicId);
        var sanitized = FilenameSanitizer.Sanitize(rendered);
        return sanitized + ext;
    }

    private static string GetExtension(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.M4a => ".m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported format"),
    };
}

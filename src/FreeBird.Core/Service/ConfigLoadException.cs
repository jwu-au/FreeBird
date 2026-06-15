using System;

namespace FreeBird.Core.Service;

/// <summary>
/// Thrown by <see cref="IConfigLoader"/> implementations on any parse or validation
/// failure (T05). Carries the JSON field path that failed (e.g. <c>"watch.inputs"</c>)
/// and — when the underlying parser reports them — the source line/column for
/// IDE-quality error messages.
/// </summary>
public sealed class ConfigLoadException : Exception
{
    /// <summary>
    /// JSON field path (dot-delimited, snake_case) that failed validation,
    /// or <c>"configFilePath"</c> for file-level errors.
    /// </summary>
    public string FieldName { get; }

    /// <summary>1-based line in the source JSON, when available.</summary>
    public int? Line { get; }

    /// <summary>1-based column in the source JSON, when available.</summary>
    public int? Column { get; }

    public ConfigLoadException(string fieldName, string message, int? line = null, int? column = null)
        : base(FormatMessage(fieldName, message, line, column))
    {
        FieldName = fieldName;
        Line = line;
        Column = column;
    }

    public ConfigLoadException(string fieldName, string message, Exception inner, int? line = null, int? column = null)
        : base(FormatMessage(fieldName, message, line, column), inner)
    {
        FieldName = fieldName;
        Line = line;
        Column = column;
    }

    private static string FormatMessage(string fieldName, string message, int? line, int? column)
    {
        if (line.HasValue && column.HasValue)
        {
            return $"Config error at '{fieldName}' (line {line}, col {column}): {message}";
        }
        if (line.HasValue)
        {
            return $"Config error at '{fieldName}' (line {line}): {message}";
        }
        return $"Config error at '{fieldName}': {message}";
    }
}

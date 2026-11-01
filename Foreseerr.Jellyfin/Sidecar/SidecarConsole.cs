using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Foreseerr.Jellyfin.Sidecar;

/// <summary>
/// Reads Foreseerr's console lines (winston's format, coloured):
/// <c>2026-11-01T20:15:14.998Z [info][Server]: Server ready…</c>.
/// </summary>
internal static partial class SidecarConsole
{
    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Colours();

    [GeneratedRegex(@"^\S+ \[(?<level>[a-z]+)\](?<message>.*)$")]
    private static partial Regex Record();

    /// <summary>The line's level and text, without its colours and timestamp.</summary>
    /// <param name="fallback">The level of a line in another format.</param>
    public static (LogLevel Level, string Message) Parse(string line, LogLevel fallback)
    {
        var text = Colours().Replace(line, "").TrimEnd();
        var match = Record().Match(text);
        if (!match.Success) return (fallback, text);
        var level = match.Groups["level"].Value switch
        {
            "error" => LogLevel.Error,
            "warn" => LogLevel.Warning,
            "info" or "http" => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        return (level, match.Groups["message"].Value.TrimStart(':', ' '));
    }
}

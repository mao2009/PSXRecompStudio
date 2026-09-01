using System.Text.RegularExpressions;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Redacts absolute local filesystem paths from strings before they are persisted or
/// shared. Used at the persistence/log boundary so that persisted metadata never
/// leaks a local machine layout (drive letters, UNC shares, or POSIX roots).
///
/// The redaction replaces an absolute path with the placeholder <c>&lt;redacted&gt;</c>
/// while leaving the surrounding failure category and detail text intact, so a message
/// like <c>Could not find file 'C:\Users\foo\rom\game.chd'.</c> survives as
/// <c>Could not find file '&lt;redacted&gt;'.</c> and a POSIX message
/// <c>/home/foo/rom/game.chd could not be read</c> survives as
/// <c>&lt;redacted&gt; could not be read</c>.
/// </summary>
[Test]
internal static class PathRedactor
{
    /// <summary>Placeholder substituted for every matched absolute path.</summary>
    public const string Redacted = "<redacted>";

    private static readonly Regex[] Patterns =
    [
        // Windows absolute path with a drive letter: C:\... or C:/...
        new(@"[A-Za-z]:[\\/](?:[^ \t\r\n\""'<>|])*", RegexOptions.Compiled),
        // UNC share: \\server\share\...
        new(@"\\\\(?:[^\\ \t\r\n\""'<>]+\\?)+", RegexOptions.Compiled),
        // POSIX absolute path with at least two components: /foo/bar/...
        new(@"/(?:[^/ \t\r\n\""'<>]+/)+[^/ \t\r\n\""'<>]*", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Returns <paramref name="text"/> with every absolute local path replaced by
    /// <see cref="Redacted"/>. Null and empty input are returned unchanged.
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, Redacted);
        }

        return result;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Fixed public codes for refusals whose cause must stay identifiable without the internal message. Only a code listed in
/// <see cref="All"/> is ever published; an unlisted code falls back to the generic code of the exception type (fail closed).
/// </summary>
[Infrastructure]
public static class PublicCodes
{
    /// <summary>A symbolic link, junction or reparse point was refused where a plain file or directory was required.</summary>
    public const string LinkRefused = "LINK_REFUSED";

    /// <summary>The manifest's artifact path is not the one-level-above-runtime form it must have.</summary>
    public const string ArtifactPathInvalid = "MANIFEST_ARTIFACT_PATH_INVALID";

    /// <summary>A path resolved outside the directory it is permitted to live in.</summary>
    public const string PathOutsidePermittedDirectory = "PATH_OUTSIDE_PERMITTED_DIRECTORY";

    /// <summary>An identity probe could not read the file because this libc does not export stat (the one known cause).</summary>
    public const string IdentityStatNotExported = "IDENTITY_STAT_NOT_EXPORTED";

    /// <summary>An identity probe could not read the file for a cause other than the known one.</summary>
    public const string IdentityUnavailable = "IDENTITY_UNAVAILABLE";

    /// <summary>Every code that may be published.</summary>
    public static readonly IReadOnlyCollection<string> All = new[]
    {
        LinkRefused, ArtifactPathInvalid, PathOutsidePermittedDirectory, IdentityStatNotExported, IdentityUnavailable,
    };
}

/// <summary>
/// Creates the refusals whose cause is published as a fixed code. <see cref="InvalidDataException"/> is sealed, so the code
/// rides in <see cref="Exception.Data"/> under a fixed key: the exception type, its message and every <c>catch</c> that
/// matches it stay exactly as they were. The message stays internal; only a listed code is ever published.
/// </summary>
[Infrastructure]
public static class PublicRefusal
{
    /// <summary>The <see cref="Exception.Data"/> key that holds the public code.</summary>
    public const string DataKey = "psxrecomp.publicCode";

    /// <summary>An <see cref="InvalidDataException"/> with <paramref name="publicCode"/> attached as its public code.</summary>
    public static InvalidDataException Create(string publicCode, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicCode);
        var exception = new InvalidDataException(message);
        exception.Data[DataKey] = publicCode;
        return exception;
    }
}

/// <summary>
/// The boundary for lines a CI log can show. A public line is built from an explicit allowlist of keys and fixed codes:
/// raw exception messages, file paths (absolute or otherwise), digests, ROM-derived counts and guest addresses are never
/// part of a public line. The full detail stays in private diagnostics, which are not uploaded. Refusals name the rule
/// that failed and never the matched text.
/// </summary>
[Infrastructure]
public static class PublicLog
{
    /// <summary>The only keys a firmware-run public line may carry. Any other key is refused before the line is written.</summary>
    public static readonly IReadOnlyList<string> FirmwareRunKeys = new[]
    {
        "kind",
        "bios",
        "biosMode",
        "declaredProvenance",
        "state",
        "diagnosticCode",
        "mixedFallback",
        "fallbackSegmentInstructionBudget",
        "fallbackMaxTransitions",
    };

    // Absolute machine paths: a drive, a POSIX root, a Git Bash drive, a rooted Windows path, a UNC share, a file URI.
    // The rules have no word-boundary lookbehind. The logical text has its escapes removed, so a boundary that a control byte
    // changes would otherwise let a root through ("done" + LF + "/home/..."). A rule that matches too much is a false positive;
    // a rule that matches too little publishes a path, so the rules err on the side of matching.
    // A slash drive needs a following character: at the end of held text "https:/" is undecided and stays held. A "://" is a URL
    // scheme when a letter precedes the scheme ("https://"), so the double-slash form needs a word start before its drive letter.
    private static readonly Regex AbsolutePath = new(
        @"[A-Za-z]:\\"
        + @"|[A-Za-z]:/(?=[^/])"
        + @"|(?<![A-Za-z0-9])[A-Za-z]://"
        + @"|/(?:home|Users|root|tmp|var|private|mnt|Volumes|opt|etc|usr|srv|github|runner|Applications|System|Library|media|run|proc|dev|cygdrive|lib|bin|data|workspace|builds|sbin|boot|nix)/"
        + @"|/[A-Za-z]/[A-Za-z0-9._$ -]"
        + @"|\\(?:Users|home|Windows|Program Files|Documents and Settings)"
        + @"|\\[A-Za-z0-9._ ()$-]{1,28}\\"
        + @"|(?<![A-Za-z0-9:/])/[A-Za-z0-9._-]{1,24}/[A-Za-z0-9._-]"
        + @"|\\\\\?\\"
        + @"|\\\\[A-Za-z0-9._$-]"
        + @"|(?<![A-Za-z0-9:])//[A-Za-z0-9._$-]"
        + @"|(?<![A-Za-z0-9])file://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The PSX CD-ROM device prefix "cdrom:" before a backslash is a guest path, not a machine path. Only that token is replaced, and
    // only by "cdrom_": the backslash that follows stays, so a rooted rule still sees "\Users\" after it, and the colon is gone, so no
    // drive rule forms. No letter before "cdrom" is exempt ("cdroC:\" is still refused).
    // The token must start the text or follow a separator (whitespace, = , ; ' ( [ { "), a word start. "_cdrom:\" is not exempt.
    private static readonly Regex GuestDevicePrefix = new(
        @"(?<![^\s=,;'(\[{""])cdrom:(?=\\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string WithoutGuestDevicePrefix(string text) => GuestDevicePrefix.Replace(text, "cdrom_");

    /// <summary>
    /// The rule for digest-shaped runs in the published text, where escape digits are kept. Returns the rule name, never the text.
    /// </summary>
    public static string? DigestRefusal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HexRun.IsMatch(text) ? "digest-shaped hex run" : null;
    }

    // A run of 32 or more hex digits: a digest, or a digest-shaped value that is not allowlisted here.
    private static readonly Regex HexRun = new(@"[0-9A-Fa-f]{32,}", RegexOptions.CultureInvariant);

    /// <summary>The fixed public code for an exception. Its message is never used.</summary>
    public static string CodeFor(Exception exception)
    {
        if (exception.Data[PublicRefusal.DataKey] is string code && PublicCodes.All.Contains(code))
        {
            return code;
        }

        return GenericCodeFor(exception);
    }

    private static string GenericCodeFor(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "FILE_NOT_FOUND",
        UnauthorizedAccessException => "ACCESS_DENIED",
        IOException => "IO_ERROR",
        InvalidDataException => "INVALID_DATA",
        FormatException => "INVALID_FORMAT",
        ArgumentException => "INVALID_ARGUMENT",
        InvalidOperationException => "INVALID_STATE",
        _ => "INTERNAL_ERROR",
    };

    /// <summary>A public failure line: the component, the fixed code and the exception type name. No message, no path.</summary>
    public static string FailureLine(string component, Exception exception) =>
        $"{component}: failed (code {CodeFor(exception)}, exception {exception.GetType().Name}); details are not published.";

    /// <summary>The line written when a public line fails the policy: it names the rule, never the content.</summary>
    public static string RefusedLine(string component, string rule) =>
        $"{component}: public line refused ({rule}); details are not published.";

    /// <summary>The success line for a written frame: the size and the file name only, never the directory.</summary>
    public static string FrameWrittenLine(int width, int height, string path) =>
        $"runtime host: frame {width}x{height} written ({FileNameOf(path)})";

    /// <summary>
    /// The last path segment for either separator, so a Windows path shows only its file name on every platform. The directory is never
    /// part of the public line.
    /// </summary>
    private static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// The rule for any published text, whole or part of a line: an absolute machine path or a digest-shaped hex run. Shared by
    /// <see cref="Refusal"/> and the streaming guest sink, so both apply one policy. Returns the rule name, never the text.
    /// </summary>
    public static string? SensitiveTextRefusal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (AbsolutePath.IsMatch(WithoutGuestDevicePrefix(text)))
        {
            return "absolute machine path";
        }

        return HexRun.IsMatch(text) ? "digest-shaped hex run" : null;
    }

    /// <summary>
    /// Null when <paramref name="line"/> may be published. Otherwise the name of the rule it breaks. The policy:
    /// no absolute machine path, no run of 32 or more hex digits, and, for a JSON object line, only keys in
    /// <paramref name="allowedKeys"/>. The offending text is never returned.
    /// </summary>
    public static string? Refusal(string line, IReadOnlyCollection<string> allowedKeys)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(allowedKeys);
        if (AbsolutePath.IsMatch(WithoutGuestDevicePrefix(line)))
        {
            return "absolute machine path";
        }

        if (HexRun.IsMatch(line))
        {
            return "digest-shaped hex run";
        }

        if (line.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return "JSON line is not an object";
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!allowedKeys.Contains(property.Name))
                    {
                        return "key outside the allowlist";
                    }
                }
            }
            catch (JsonException)
            {
                return "malformed JSON line";
            }
        }

        return null;
    }
}

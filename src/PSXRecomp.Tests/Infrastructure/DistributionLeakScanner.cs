using System.Text;
using System.Text.RegularExpressions;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review MEDIUM-1, MEDIUM-2): the leak checks over a packaged distribution. Pure functions over named
/// byte arrays, so the same checks run in CI on synthetic inputs and on a real BIOS when the user supplies one.
/// </summary>
/// <remarks>
/// The BIOS check has zero tolerance. Every 8-byte window of the BIOS (at every byte offset) is a leak when it occurs in a
/// candidate file. The one excuse is content: a window whose identical bytes also occur in a <em>baseline</em> (material
/// that is not the BIOS: a synthetic run's output, the game disc) is excused, whatever its shape. That includes windows of
/// mostly 0x00 or 0xFF bytes, which the baseline holds too; the report counts those occurrences apart
/// (<see cref="BiosScanReport.LowEntropyOccurrences"/>) so the excuse is visible. An 8-byte window catches any BIOS
/// fragment of 8 bytes or more; a shorter fragment cannot be matched by a window of this length.
/// </remarks>
[Test]
internal static class DistributionLeakScanner
{
    /// <summary>The BIOS window length: 8 bytes, at every byte offset. A fragment of 8 bytes or more contains one.</summary>
    public const int WindowLength = 8;

    /// <summary>
    /// A developer or user absolute path, in Windows (<c>C:\Prog\winlibs</c>, <c>D:/src/</c>), MSYS (<c>/c/Users/</c>) or Unix
    /// (<c>/home/</c>, <c>/mnt/</c>, <c>/Users/</c>) form. A drive or MSYS letter may not follow a letter or digit, so URLs such
    /// as <c>https://x</c> do not match.
    /// </summary>
    private static readonly Regex AbsolutePath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/](?:[A-Za-z0-9_.\- ]+[\\/])+" +
        @"|(?<![A-Za-z0-9_.\-])/[A-Za-z]/(?:[A-Za-z0-9_.\- ]+/)+" +
        @"|(?<![A-Za-z0-9_.\-])/(?:home|mnt|Users|users)/[A-Za-z0-9_.\-]",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The single exemption (owner ruling, Issue #B2). The .NET SDK's apphost template, copied verbatim into
    /// <c>runtime/PSXRecomp.RuntimeHost.exe</c>, names the Microsoft CI build root <c>D:\a\_work\1\s\</c> where it was produced.
    /// That is not a developer's path. The exemption is the exact template path string below, matched case-insensitively and
    /// only in that one file; any other string under that root, and the same string in any other file, is flagged.
    /// Residual: removing it needs <c>UseAppHost=false</c>, which is not done.
    /// </summary>
    internal const string ApphostRelativePath = "runtime/PSXRecomp.RuntimeHost.exe";
    internal const string ApphostTemplatePath = @"D:\a\_work\1\s\src\runtime\artifacts\obj\win-x64.Release\corehost\apphost\standalone\";

    /// <summary>
    /// The text a file may hold under each encoding a path can appear in: Latin-1 (byte-per-character, every offset), and
    /// UTF-16LE (a wide string at an even and at an odd offset). A Windows toolchain writes its paths in UTF-16 in some places.
    /// </summary>
    private static IEnumerable<string> TextViews(byte[] bytes)
    {
        yield return Encoding.Latin1.GetString(bytes);
        yield return Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2));
        if (bytes.Length > 1)
        {
            yield return Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1 - ((bytes.Length - 1) % 2));
        }
    }

    /// <summary>True when <paramref name="match"/> is the exempt apphost template path, in the one file that carries it.</summary>
    private static bool IsExemptApphostPath(string name, string match)
    {
        var normalized = name.Replace('\\', '/');
        return string.Equals(normalized, ApphostRelativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(match, ApphostTemplatePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first file that names an absolute developer path under any text view, or null.</summary>
    public static string? FindAbsolutePath(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var (name, bytes) in files)
        {
            foreach (var text in TextViews(bytes))
            {
                foreach (Match match in AbsolutePath.Matches(text))
                {
                    if (!IsExemptApphostPath(name, match.Value))
                    {
                        return $"{name} names an absolute developer path: '{match.Value}'";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Every BIOS window, keyed by its contents, mapped to the first offset it occurs at.</summary>
    public static Dictionary<ulong, int> BiosWindows(byte[] bios)
    {
        ArgumentNullException.ThrowIfNull(bios);
        var windows = new Dictionary<ulong, int>();
        for (var i = 0; i + WindowLength <= bios.Length; i++)
        {
            windows.TryAdd(KeyAt(bios, i), i);
        }

        return windows;
    }

    /// <summary>
    /// The outcome of a BIOS window scan. <see cref="ExcusedByBaseline"/> counts the distinct BIOS windows that a non-BIOS
    /// baseline also holds: the coincidental matches the baseline absorbs. <see cref="Leaks"/> counts candidate occurrences of
    /// a BIOS window that the baseline does not hold; any of them fails the check.
    /// </summary>
    public sealed record BiosScanReport(
        int BiosWindows, int ExcusedByBaseline, int Leaks, string? FirstLeak, int LowEntropyOccurrences, int LowEntropyExcused);

    /// <summary>A window of mostly 0x00 or 0xFF bytes: such a window is common in any binary, so the report counts it apart.</summary>
    public static bool IsLowEntropy(ulong key)
    {
        var bytes = BitConverter.GetBytes(key);
        return bytes.Count(static b => b is 0x00 or 0xFF) >= 6;
    }

    /// <summary>Scans the candidates for BIOS windows that the baseline does not also hold.</summary>
    public static BiosScanReport Scan(
        IEnumerable<(string Name, byte[] Bytes)> candidates,
        IReadOnlyDictionary<ulong, int> biosWindows,
        IEnumerable<(string Name, byte[] Bytes)> baseline)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(biosWindows);
        ArgumentNullException.ThrowIfNull(baseline);

        var excused = new HashSet<ulong>();
        foreach (var (_, bytes) in baseline)
        {
            for (var i = 0; i + WindowLength <= bytes.Length; i++)
            {
                var key = KeyAt(bytes, i);
                if (biosWindows.ContainsKey(key)) excused.Add(key);
            }
        }

        var leaks = 0;
        var lowEntropy = 0;
        var lowEntropyExcused = 0;
        string? first = null;
        foreach (var (name, bytes) in candidates)
        {
            for (var i = 0; i + WindowLength <= bytes.Length; i++)
            {
                var key = KeyAt(bytes, i);
                if (!biosWindows.TryGetValue(key, out var biosOffset))
                {
                    continue;
                }

                var isExcused = excused.Contains(key);
                if (IsLowEntropy(key))
                {
                    lowEntropy++;
                    if (isExcused) lowEntropyExcused++;
                }

                if (!isExcused)
                {
                    leaks++;
                    first ??= $"{name} contains an {WindowLength}-byte window of the BIOS: file offset 0x{i:X}, " +
                              $"BIOS offset 0x{biosOffset:X}, bytes {Convert.ToHexString(bytes, i, WindowLength)}";
                }
            }
        }

        return new BiosScanReport(biosWindows.Count, excused.Count, leaks, first, lowEntropy, lowEntropyExcused);
    }

    /// <summary>The first leak, or null: the check that fails a distribution.</summary>
    public static string? FindBiosLeak(
        IEnumerable<(string Name, byte[] Bytes)> candidates,
        IReadOnlyDictionary<ulong, int> biosWindows,
        IEnumerable<(string Name, byte[] Bytes)> baseline) =>
        Scan(candidates, biosWindows, baseline).FirstLeak;

    private static ulong KeyAt(byte[] bytes, int offset) => BitConverter.ToUInt64(bytes, offset);
}

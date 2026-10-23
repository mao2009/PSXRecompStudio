using System.Text;
using System.Text.RegularExpressions;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review MEDIUM-1, MEDIUM-2): the leak checks over a packaged distribution. Pure functions over named
/// byte arrays, so the same checks run in CI on synthetic inputs and on a real BIOS when the user supplies one.
/// </summary>
/// <remarks>
/// The BIOS check has zero tolerance. A BIOS 16-byte window is a leak when it occurs in a candidate file, unless the
/// identical window also occurs in a <em>baseline</em>: material that is not the BIOS (a synthetic run's output, the
/// game disc). No window is excluded for looking like text or padding, because either kind also appears in ordinary
/// binaries and proves nothing either way; only a baseline may excuse one.
/// </remarks>
[Test]
internal static class DistributionLeakScanner
{
    /// <summary>The window length the BIOS is matched by (a 16-byte window at every byte offset).</summary>
    public const int WindowLength = 16;

    /// <summary>
    /// A developer or user absolute path: a drive-letter path with at least one directory (<c>C:\Prog\winlibs</c>,
    /// <c>D:/src/</c>), or a Unix home, mount or users path. The drive letter may not follow a letter or digit, so
    /// URLs such as <c>https://x</c> do not match.
    /// </summary>
    private static readonly Regex AbsolutePath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/](?:[A-Za-z0-9_.\- ]+[\\/])+" +
        @"|(?<![A-Za-z0-9_.\-])/(?:home|mnt|Users|users)/[A-Za-z0-9_.\-]",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The build root the .NET SDK's own apphost template was produced under (Microsoft's CI). The template is copied verbatim
    /// into every framework-dependent host, and it is not a developer's path. It is the single exemption, and it is reported
    /// as a residual (the host would need <c>UseAppHost=false</c> to drop it).
    /// </summary>
    private const string SdkApphostBuildRoot = @"D:\a\_work\1\s\";

    /// <summary>The first file that names an absolute developer path, or null.</summary>
    public static string? FindAbsolutePath(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var (name, bytes) in files)
        {
            foreach (Match match in AbsolutePath.Matches(Encoding.Latin1.GetString(bytes)))
            {
                if (!match.Value.StartsWith(SdkApphostBuildRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{name} names an absolute developer path: '{match.Value}'";
                }
            }
        }

        return null;
    }

    /// <summary>Every BIOS window, keyed by its contents, mapped to the first offset it occurs at.</summary>
    public static Dictionary<(ulong, ulong), int> BiosWindows(byte[] bios)
    {
        ArgumentNullException.ThrowIfNull(bios);
        var windows = new Dictionary<(ulong, ulong), int>();
        for (var i = 0; i + WindowLength <= bios.Length; i++)
        {
            windows.TryAdd(KeyAt(bios, i), i);
        }

        return windows;
    }

    /// <summary>
    /// The first candidate file holding a BIOS window that the baseline does not also hold, or null. The message names the
    /// file, its offset, the BIOS offset and the window's bytes.
    /// </summary>
    public static string? FindBiosLeak(
        IEnumerable<(string Name, byte[] Bytes)> candidates,
        IReadOnlyDictionary<(ulong, ulong), int> biosWindows,
        IEnumerable<(string Name, byte[] Bytes)> baseline)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(biosWindows);
        ArgumentNullException.ThrowIfNull(baseline);

        var excused = new HashSet<(ulong, ulong)>();
        foreach (var (_, bytes) in baseline)
        {
            for (var i = 0; i + WindowLength <= bytes.Length; i++)
            {
                var key = KeyAt(bytes, i);
                if (biosWindows.ContainsKey(key)) excused.Add(key);
            }
        }

        foreach (var (name, bytes) in candidates)
        {
            for (var i = 0; i + WindowLength <= bytes.Length; i++)
            {
                var key = KeyAt(bytes, i);
                if (biosWindows.TryGetValue(key, out var biosOffset) && !excused.Contains(key))
                {
                    return $"{name} contains a 16-byte window of the BIOS: file offset 0x{i:X}, BIOS offset 0x{biosOffset:X}, " +
                           $"bytes {Convert.ToHexString(bytes, i, WindowLength)}";
                }
            }
        }

        return null;
    }

    private static (ulong, ulong) KeyAt(byte[] bytes, int offset) =>
        (BitConverter.ToUInt64(bytes, offset), BitConverter.ToUInt64(bytes, offset + 8));
}

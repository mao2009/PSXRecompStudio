namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Discovers user-provided disc images under the repository <c>rom/</c> directory.
///
/// Two layouts are accepted, so a single ROM or a per-title folder both work:
/// <code>
///   rom/&lt;fixture&gt;.chd                 → fixture "&lt;fixture&gt;"
///   rom/&lt;fixture&gt;/&lt;anything&gt;.chd      → fixture "&lt;fixture&gt;"
/// </code>
///
/// Nothing here is title-specific: fixtures are whatever the user has placed in
/// <c>rom/</c>, which <c>.gitignore</c> and the artifact-policy CI gate keep out of Git.
/// An absent or empty <c>rom/</c> directory yields an empty list, which callers treat
/// as an explicit SKIP rather than a failure.
/// </summary>
[Test]
public static class RomFixtureLocator
{
    /// <summary>Disc image extensions recognized as analysis inputs, in precedence order.</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = [".chd", ".iso"];

    /// <summary>
    /// Resolves the repository root from the test assembly location
    /// (<c>src/&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;</c> → repository root).
    /// </summary>
    public static string RepositoryRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Default fixture directory: <c>&lt;repo&gt;/rom</c>.</summary>
    public static string DefaultRomDirectory => Path.Combine(RepositoryRoot, "rom");

    /// <summary>
    /// Returns every fixture found under <paramref name="romDirectory"/>, ordered by
    /// name so repeated discovery is deterministic.
    /// </summary>
    public static IReadOnlyList<RomFixture> Discover(string romDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romDirectory);

        var fixtures = new List<RomFixture>();

#pragma warning disable PSXR005
        if (!Directory.Exists(romDirectory))
        {
            return fixtures;
        }

        foreach (var file in Directory.EnumerateFiles(romDirectory))
        {
            if (TryCreateFixture(Path.GetFileNameWithoutExtension(file), file, out var fixture))
            {
                fixtures.Add(fixture);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(romDirectory))
        {
            var candidate = Directory.EnumerateFiles(directory)
                .Where(f => IsSupported(f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();

            if (candidate is not null &&
                TryCreateFixture(new DirectoryInfo(directory).Name, candidate, out var fixture))
            {
                fixtures.Add(fixture);
            }
        }
#pragma warning restore PSXR005

        return fixtures
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Discovers fixtures under the repository's default <c>rom/</c> directory.</summary>
    public static IReadOnlyList<RomFixture> Discover() => Discover(DefaultRomDirectory);

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool TryCreateFixture(string name, string path, out RomFixture fixture)
    {
        fixture = null!;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            return false;
        }

        fixture = new RomFixture
        {
            Name = name.ToLowerInvariant(),
            ImagePath = path,
            Format = extension == ".chd" ? RomFixtureFormat.Chd : RomFixtureFormat.Iso,
        };
        return true;
    }
}

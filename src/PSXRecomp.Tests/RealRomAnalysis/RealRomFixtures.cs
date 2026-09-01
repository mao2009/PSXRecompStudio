using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// One locally available disc image to analyze. The fixture id is derived
/// mechanically from the file name and is only a directory alias; the formal
/// identity of an analysis is the disc image SHA-256 recorded inside the artifacts.
/// </summary>
[Test]
public sealed record RealRomFixture
{
    public required string FixtureId { get; init; }

    /// <summary>Absolute local path to the disc image. Never persisted into any artifact.</summary>
    public required string DiscImagePath { get; init; }
}

/// <summary>
/// Discovers the real-ROM fixtures present on the local machine.
///
/// Fixtures are user-supplied disc images under <c>rom/</c>, which is git-ignored: no
/// ROM, ISO, EXE or CHD content is ever committed. Any number of titles may be present
/// and none is named in code — a fixture is whatever <c>rom/*.chd</c> finds, keyed by a
/// normalized form of its file name. On a machine (or CI runner) with no fixtures,
/// <see cref="Discover"/> returns an empty list and the real-ROM tests skip explicitly.
/// </summary>
[Test]
public static class RealRomFixtures
{
    /// <summary>Reason reported by skipped real-ROM tests when no local fixture exists.</summary>
    public const string NoFixtureSkipReason =
        "skipped: no real-ROM fixture found under rom/*.chd (disc images are never committed)";

    /// <summary>
    /// Repository root, resolved from the test assembly's output directory
    /// (<c>src/PSXRecomp.Tests/bin/&lt;config&gt;/&lt;tfm&gt;</c> is five levels below the root).
    /// Used only to locate local input and output directories; never written into artifacts.
    /// </summary>
    public static string RepositoryRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Local, git-ignored directory holding user-supplied disc images.</summary>
    public static string RomDirectory => Path.Combine(RepositoryRoot, "rom");

    /// <summary>Local, git-ignored root for deterministic artifacts: <c>reports/real-rom/&lt;fixture&gt;/</c>.</summary>
    public static string ReportRoot => Path.Combine(RepositoryRoot, "reports", "real-rom");

    /// <summary>Local, git-ignored root for execution logs: <c>logs/real-rom/&lt;fixture&gt;/</c>.</summary>
    public static string LogRoot => Path.Combine(RepositoryRoot, "logs", "real-rom");

    /// <summary>
    /// Returns every locally available fixture, ordered by fixture id (ordinal ascending)
    /// so a multi-fixture run processes them in a reproducible order. Returns an empty
    /// list when <c>rom/</c> does not exist or holds no disc image.
    /// </summary>
    public static IReadOnlyList<RealRomFixture> Discover()
    {
#pragma warning disable PSXR005
        if (!Directory.Exists(RomDirectory))
        {
            return Array.Empty<RealRomFixture>();
        }

        var discImages = Directory.GetFiles(RomDirectory, "*.chd", SearchOption.TopDirectoryOnly);
#pragma warning restore PSXR005

        var labels = discImages
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .ToList();
        var fixtureIds = AnalysisArtifactSchema.DisambiguateFixtureIds(labels);

        var fixtures = discImages
            .Select((path, index) => new RealRomFixture
            {
                FixtureId = fixtureIds[index],
                DiscImagePath = path,
            })
            .OrderBy(static fixture => fixture.FixtureId, StringComparer.Ordinal)
            .ThenBy(static fixture => fixture.DiscImagePath, StringComparer.Ordinal)
            .ToList();

        return fixtures;
    }
}

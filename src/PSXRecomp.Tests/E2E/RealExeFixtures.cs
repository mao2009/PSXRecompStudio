using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// One locally available PS-X EXE to run through the production runnable-artifact
/// pipeline. The fixture id is derived mechanically from the file name and is only
/// a directory/index alias; the formal identity of an input is its SHA-256, recorded
/// by the tests as the deterministic build-input identity (never written to disk).
/// </summary>
[Test]
public sealed record RealExeFixture
{
    public required string FixtureId { get; init; }

    /// <summary>Absolute local path to the executable. Never persisted into any artifact.</summary>
    public required string ExePath { get; init; }
}

/// <summary>
/// Discovers the real PS-X EXE fixtures present on the local machine (Issue #461).
///
/// Fixtures are user-supplied executables under <c>rom/</c>, which is git-ignored:
/// no ROM, ISO, EXE or CHD content is ever committed. Any number of executables may
/// be present and none is named in code — a fixture is whatever <c>rom/*.exe</c>
/// finds, keyed by a normalized form of its file name (case variants <c>*.exe</c> and
/// <c>*.EXE</c> are both accepted, mirroring the repository's gitignore contract).
/// On a machine (or CI runner) with no fixtures, <see cref="Discover"/> returns an
/// empty list and the real-EXE tests skip explicitly.
/// </summary>
[Test]
public static class RealExeFixtures
{
    /// <summary>Reason reported by skipped real-EXE tests when no local fixture exists.</summary>
    public const string NoFixtureSkipReason =
        "skipped: no real PS-X EXE fixture found under rom/*.exe (user-supplied executables are never committed)";

    /// <summary>
    /// Returns every locally available fixture, ordered by fixture id (ordinal ascending)
    /// so a multi-fixture run processes them in a reproducible order. Returns an empty
    /// list when <c>rom/</c> does not exist or holds no executable.
    /// </summary>
    public static IReadOnlyList<RealExeFixture> Discover()
    {
#pragma warning disable AARC003
        if (!Directory.Exists(RealRomFixtures.RomDirectory))
        {
            return Array.Empty<RealExeFixture>();
        }

        var executables = Directory
            .GetFiles(RealRomFixtures.RomDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(RealRomFixtures.RomDirectory, "*.EXE", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.Ordinal)
            .ToList();
#pragma warning restore AARC003

        var labels = executables
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .ToList();
        var fixtureIds = AnalysisArtifactSchema.DisambiguateFixtureIds(labels);

        return executables
            .Select((path, index) => new RealExeFixture
            {
                FixtureId = fixtureIds[index],
                ExePath = path,
            })
            .OrderBy(static fixture => fixture.FixtureId, StringComparer.Ordinal)
            .ThenBy(static fixture => fixture.ExePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reads a repository file by path relative to the repository root, for the
    /// fixture-exclusion policy assertions in <see cref="RealExeE2ETests"/>.
    /// </summary>
#pragma warning disable AARC003
    internal static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RealRomFixtures.RepositoryRoot, relativePath));
#pragma warning restore AARC003
}
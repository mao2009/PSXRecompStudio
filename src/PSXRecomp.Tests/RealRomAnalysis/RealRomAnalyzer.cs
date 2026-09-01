using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.Artifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Runs the Issue #212 analysis pipeline against a local disc image and hands the
/// result to the deterministic serialization layer, producing two clearly separated
/// outputs:
///
/// <list type="bullet">
///   <item><b>Deterministic artifacts</b> (<see cref="RealRomAnalysisArtifacts"/>) —
///   identical byte-for-byte for identical input. Safe to persist and diff.</item>
///   <item><b>Execution log</b> (<see cref="ExecutionLogEntry"/>) — stage/progress
///   records that intentionally carry elapsed time and are therefore local-only.</item>
/// </list>
///
/// The two must never mix: nothing from the log reaches an artifact, and the artifact
/// builder cannot read the clock even if asked to (Domain-layer PSXR005).
///
/// No analysis is re-implemented here; <see cref="DiscImageAnalyzer"/> remains the
/// single producer of analysis results.
/// </summary>
[Test]
public static class RealRomAnalyzer
{
    private const string DiscImageFormat = "CHD";

    /// <summary>
    /// Reads the disc image at <paramref name="discImagePath"/>, runs the full pipeline,
    /// and returns the deterministic artifact set together with the execution log.
    /// </summary>
    /// <param name="fixtureId">
    /// Canonical fixture alias (see <see cref="AnalysisArtifactSchema.NormalizeFixtureId"/>).
    /// Used for the artifact directory name only; the formal identity is the disc SHA-256.
    /// </param>
    /// <param name="instructionCount">
    /// Optional bound on the linear decode, forwarded to <see cref="DiscImageAnalyzer"/>.
    /// The same bound must be used for two runs to be comparable.
    /// </param>
    public static (RealRomAnalysisArtifacts Artifacts, List<ExecutionLogEntry> Log) Analyze(
        string discImagePath,
        string fixtureId,
        int? instructionCount = null)
    {
        var log = new List<ExecutionLogEntry>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        void Record(string stage, string status, string message)
        {
            log.Add(new ExecutionLogEntry
            {
                Stage = stage,
                Status = status,
                Message = message,
                ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            });
        }

        Record("CHD_OPEN", "START", $"Opening disc image '{fixtureId}'");
        byte[] chdBytes;
        string chdSha256;
        try
        {
#pragma warning disable PSXR005
            chdBytes = File.ReadAllBytes(discImagePath);
#pragma warning restore PSXR005
            chdSha256 = ComputeSha256(chdBytes);
            Record("CHD_OPEN", "PASS", $"Read {chdBytes.Length} bytes; SHA-256 {chdSha256}");
        }
        catch (Exception ex)
        {
            Record("CHD_OPEN", "FAIL", ex.Message);
            throw;
        }

        var report = DiscImageAnalyzer.Analyze(chdBytes, chdSha256, instructionCount);
        Record("ANALYZE", "PASS", $"DiscImageAnalyzer produced report ({report.DecodedInstructionCount} instructions)");

        ChdMapStatistics chdStats;
        using (var chd = ChdReader.Open(new MemoryStream(chdBytes, writable: false)))
        {
            chdStats = chd.ComputeMapStatistics();
            Record("CHD_META", "PASS",
                $"V{chdStats.Version} hunks={chdStats.TotalHunks} cdlz={chdStats.CdlzCount} cdzl={chdStats.CdzlCount}");
        }

        var isoStats = CaptureIso(chdBytes, Record);

        Record("PSX_EXE", "PASS",
            $"Boot executable '{report.ExecutableFileName}' entry=0x{report.EntryPoint:X8}");

        var artifacts = DeterministicArtifactBuilder.Build(new DeterministicArtifactInput
        {
            FixtureId = fixtureId,
            DiscImageFormat = DiscImageFormat,
            DiscImageSha256 = chdSha256,
            DiscImageSizeBytes = chdBytes.Length,
            Chd = chdStats,
            Iso = isoStats,
            Report = report,
        });

        Record("ARTIFACTS", "PASS",
            $"Built {artifacts.Files.Count} deterministic artifacts (manifest schema v{artifacts.Manifest.SchemaVersion})");

        return (artifacts, log);
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a byte buffer. Shared so every caller in
    /// the test layer uses a single implementation.
    /// </summary>
    public static string ComputeSha256ForTest(byte[] data) => ComputeSha256(data);

    private static IsoVolumeStatistics CaptureIso(byte[] chdBytes, Action<string, string, string> record)
    {
        record("FILESYSTEM", "START", "Reading ISO9660 filesystem");

        using var chd = ChdReader.Open(new MemoryStream(chdBytes, writable: false));
        var iso = DiscImageAnalyzer.CreateIsoReader(chd);
        iso.Initialize();

        var statistics = iso.ComputeVolumeStatistics();
        record("FILESYSTEM", "PASS",
            $"ISO9660 loaded; volume='{statistics.VolumeIdentifier}' files={statistics.FileCount} " +
            $"dirs={statistics.DirectoryCount} systemCnf={statistics.SystemCnfPresent}");

        return statistics;
    }

    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}

using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Tests for the reusable, deterministic real-ROM analysis snapshot pipeline.
/// The determinism test is the core requirement: the same CHD analyzed twice must
/// yield byte-identical snapshots, ignoring the (non-deterministic) execution log.
/// </summary>
[Test]
public class RealRomAnalysisTests
{
    private const string FixtureName = "persona";

    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string ChdPath = Path.Combine(RepoRoot, "rom", "PERSONA.chd");

    private static readonly string ReportDirectory = Path.Combine(RepoRoot, "reports", "real-rom");
    private static readonly string LogDirectory = Path.Combine(RepoRoot, "logs", "real-rom");

#pragma warning disable PSXR005
    private static bool FixtureExists() => File.Exists(ChdPath);
#pragma warning restore PSXR005

    /// <summary>
    /// Core requirement: the same CHD analyzed twice produces identical deterministic
    /// snapshots. Timestamps and the execution log are deliberately excluded.
    /// </summary>
    [SkippableFact]
    public void AnalysisSnapshotDeterministic()
    {
        Skip.IfNot(FixtureExists(), "skipped: real-ROM fixture rom/PERSONA.chd not present");

        var (snapshot1, _) = RealRomAnalyzer.Analyze(ChdPath, FixtureName);
        var (snapshot2, _) = RealRomAnalyzer.Analyze(ChdPath, FixtureName);

        snapshot1.ToJson().Should().Be(snapshot2.ToJson(),
            "analyzing the same ROM twice must yield identical deterministic snapshots");
    }

    /// <summary>
    /// Confirms that the deterministic snapshot itself is self-consistent and stable:
    /// repeated serialization of a single snapshot is byte-identical (stable ordering).
    /// </summary>
    [SkippableFact]
    public void Snapshot_SerializationIsStable()
    {
        Skip.IfNot(FixtureExists(), "skipped: real-ROM fixture rom/PERSONA.chd not present");

        var (snapshot, _) = RealRomAnalyzer.Analyze(ChdPath, FixtureName);

        snapshot.ToJson().Should().Be(snapshot.ToJson());
        snapshot.ToJson().Should().NotContain("\"ElapsedMs\"");
    }

    /// <summary>
    /// Generates the Persona snapshot + report + execution log into the local
    /// artifacts layout and verifies the full pipeline produced all expected stages:
    /// CHD, ISO9660, SYSTEM.CNF, PS-X EXE, entry point, and MIPS decode.
    /// </summary>
    [SkippableFact]
    public void Analyze_Persona_WritesReusableArtifacts()
    {
        Skip.IfNot(FixtureExists(), "skipped: real-ROM fixture rom/PERSONA.chd not present");

        var snapshot = AnalysisSnapshotWriter.AnalyzeAndWrite(
            ChdPath, FixtureName, ReportDirectory, LogDirectory);

        // Input identity is SHA-256 backed, and the snapshot is fully deterministic.
        snapshot.Input.Format.Should().Be("CHD");
        snapshot.Input.Size.Should().BeGreaterThan(0);
        snapshot.Input.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");

        // CHD metadata (no Persona-specific values hard-coded).
        snapshot.Chd.TotalHunks.Should().BeGreaterThan(0);
        snapshot.Chd.CdlzCount.Should().BeGreaterThan(0);
        snapshot.Chd.DataRegionSize.Should().BeGreaterThan(0);
        snapshot.Chd.LogicalBytes.Should().BeGreaterThan(0);

        // ISO metadata.
        snapshot.Iso.SystemCnfExists.Should().BeTrue();
        snapshot.Iso.FileCount.Should().BeGreaterThan(0);

        // SYSTEM.CNF.
        snapshot.SystemCnf.BootPath.Should().NotBeNullOrEmpty();
        snapshot.SystemCnf.BootPath.Should().Contain("SLPS");
        snapshot.SystemCnf.BootExecutable.Should().NotBeNullOrEmpty();

        // PS-X EXE.
        snapshot.PsxExe.EntryPoint.Should().BeGreaterThan(0x80000000u);
        snapshot.PsxExe.TextStart.Should().BeGreaterThan(0x80000000u);
        snapshot.PsxExe.TextSize.Should().BeGreaterThan(0u);
        snapshot.PsxExe.FileHash.Should().MatchRegex("^[0-9a-f]{64}$");

        // MIPS analysis.
        snapshot.Analysis.DecodedInstructionCount.Should().BeGreaterThan(0);
        snapshot.Analysis.DecodeFailureCount.Should().Be(0);
        snapshot.Analysis.BranchCount.Should().BeGreaterThanOrEqualTo(0);
        snapshot.Instructions.Should().NotBeEmpty();

        // Artifacts physically exist at the documented layout.
        var manifest = Path.Combine(ReportDirectory, FixtureName, "manifest.json");
        var report = Path.Combine(ReportDirectory, FixtureName, "report.json");
        var log = Path.Combine(LogDirectory, FixtureName, "analysis.log.jsonl");
#pragma warning disable PSXR005
        File.Exists(manifest).Should().BeTrue();
        File.Exists(report).Should().BeTrue();
        File.Exists(log).Should().BeTrue();
#pragma warning restore PSXR005
    }

    /// <summary>
    /// The documented-analysis artifact manifest plus a separately produced report
    /// must decode to a valid DiscImageAnalysisReport (schema stays usable).
    /// </summary>
    [SkippableFact]
    public void Analyze_Persona_ReportDecodes()
    {
        Skip.IfNot(FixtureExists(), "skipped: real-ROM fixture rom/PERSONA.chd not present");

#pragma warning disable PSXR005
        var bytes = File.ReadAllBytes(ChdPath);
#pragma warning restore PSXR005
        var sha256 = ComputeSha256(bytes);
        var report = DiscImageAnalyzer.Analyze(bytes, sha256);
        report.DiscImageSha256.Should().Be(sha256);
        report.DecodedInstructions.Should().NotBeEmpty();
        report.EntryPoint.Should().BeGreaterThan(0x80000000u);
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

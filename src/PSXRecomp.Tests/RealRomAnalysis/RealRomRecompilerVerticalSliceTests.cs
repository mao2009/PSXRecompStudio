using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Issue #225: recompiles and differentially validates a real-ROM function.
///
/// This is the real-fixture-gated proof; it never introduces title-specific logic —
/// <see cref="RealRomCandidateSelector"/> is generic, and this test simply runs it
/// against whatever the operator has legally placed under <c>rom/</c> (see
/// <see cref="RealRomFixtures"/>). With no fixture present (the normal CI case) it
/// skips explicitly, exactly like <see cref="RealRomAnalysisSkillTests"/>; the
/// synthetic, always-running proof of the same bridge lives in
/// <c>PSXRecomp.Tests.Recompiler.RealRomRecompilerBridgeTests</c>.
/// </summary>
[Test]
[Collection("RealRom")]
public class RealRomRecompilerVerticalSliceTests
{
    /// <summary>
    /// Instructions decoded per fixture when searching for a candidate. Larger than
    /// <see cref="RealRomAnalysisSkillTests"/>'s window: a compiler-emitted subroutine
    /// worth recompiling is rarely within the first 128 words of the entry point, and
    /// this test's own runtime (not CI's, since it never runs without a local fixture)
    /// is the only cost of raising it.
    /// </summary>
    private const int InstructionCount = 8192;

    /// <summary>
    /// The smallest candidate worth recompiling and diffing. Below this, a match would
    /// not demonstrate much beyond what the existing synthetic fixtures already cover.
    /// </summary>
    private const int MinimumQualifyingInstructionCount = 8;

    [SkippableFact]
    public void EveryLocalFixtureWithAQualifyingCandidate_InterpreterMatchesRecompiled()
    {
        var fixtures = RealRomFixtures.Discover();
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var evaluated = 0;
        foreach (var fixture in fixtures)
        {
            var staged = RealRomAnalyzer.AnalyzeStaged(fixture.DiscImagePath, fixture.FixtureId, InstructionCount);
            var report = staged.Outcome.Report;
            if (report is null)
            {
                continue; // Analysis itself failed; RealRomAnalysisSkillTests already covers that classification.
            }

            var candidate = RealRomCandidateSelector.SelectBest(report);
            if (candidate is null || candidate.InstructionCount < MinimumQualifyingInstructionCount)
            {
                continue; // No candidate big enough to be worth recompiling for this fixture; not a failure.
            }

            evaluated++;
            RunAndAssertMatch(fixture.FixtureId, fixture.DiscImagePath, report, candidate);
        }

        // Every discovered fixture in this environment has produced a qualifying real
        // candidate so far; if that ever stops being true for a newly added fixture,
        // this assertion is the signal that a candidate-selection gap needs attention
        // rather than the run silently validating nothing.
        evaluated.Should().BeGreaterThan(0,
            "at least one local real-ROM fixture is expected to yield a qualifying candidate");
    }

    private static void RunAndAssertMatch(
        string fixtureId, string discImagePath, DiscImageAnalysisReport report, RealRomFunctionCandidate candidate)
    {
#pragma warning disable AARC003
        var discImageSizeBytes = new FileInfo(discImagePath).Length;
#pragma warning restore AARC003
        var executable = new ArtifactFixtureIdentity
        {
            FixtureId = fixtureId,
            DiscImageFormat = "CHD",
            DiscImageSha256 = report.DiscImageSha256,
            DiscImageSizeBytes = discImageSizeBytes,
            ExecutableFileName = report.ExecutableFileName,
            ExecutableSerial = AnalysisArtifactSchema.DeriveExecutableSerial(report.ExecutableFileName),
            ExecutableSizeBytes = report.ExecutableFileSize,
            ExecutableSha256 = report.ExecutableFileHash,
        };
        var provenance = RealRomFixtureAdapter.BuildProvenance(
            candidate, executable,
            "RealRomCandidateSelector.SelectBest: greedy prefix over the executable's decoded instructions, " +
            "bounded by MipsToIrLowerer acceptance and excluding indirect jumps (Issue #225 policy).");

        var fixture = candidate.ToDifferentialFixture($"real-rom-{fixtureId}-0x{candidate.StartAddress:X8}");
        var reference = new RecompilerInterpreterExecutor();
        var actual = new global::PSXRecomp.Tests.Recompiler.RecompilerHostExecutor();
        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        var evidence =
            $"fixture={fixtureId} entry=0x{candidate.StartAddress:X8} instructions={candidate.InstructionCount} " +
            $"requiredSubset=[{string.Join(",", candidate.RequiredInstructionSubset)}] " +
            $"selectionSha256={provenance.SelectionIdentitySha256}";

        result.Actual.Status.Should().Be(RecompilerExecutionStatus.Completed,
            $"{evidence}\nrecompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        result.BothCompleted.Should().BeTrue(evidence);
        result.IsMatch.Should().BeTrue($"{evidence}\n{result.Diff?.Describe()}");
        provenance.HasUnresolvedDependencies.Should().BeFalse(evidence);
    }
}

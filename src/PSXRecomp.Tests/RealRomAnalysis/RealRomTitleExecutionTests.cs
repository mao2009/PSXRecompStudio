using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Execution;
using PSXRecomp.Tests.Runtime;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Issue #366: drives a real-ROM function through the full-title execution
/// orchestrator over the generated host (gcc) for several bounded segments.
///
/// Like <see cref="RealRomRecompilerVerticalSliceTests"/> this is the
/// real-fixture-gated proof and uses no title-specific logic: whatever the
/// operator has placed under <c>rom/</c> is run, and with no fixture the test
/// skips explicitly (the normal CI case).
/// </summary>
[Test]
[Collection("RealRom")]
public class RealRomTitleExecutionTests
{
    private const int InstructionCount = 8192;

    [SkippableFact]
    public void EveryLocalQualifyingCandidate_DrivesTheOrchestratorToAClassifiedEnd()
    {
        var fixtures = RealRomFixtures.Discover();
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var evaluated = 0;
        foreach (var fixture in fixtures)
        {
            var report = RealRomAnalyzer.AnalyzeStaged(fixture.DiscImagePath, fixture.FixtureId, InstructionCount)
                .Outcome.Report;
            if (report is null)
            {
                continue;
            }

            var candidate = RealRomCandidateSelector.SelectBest(report);
            if (candidate is null || candidate.InstructionCount < 8)
            {
                continue; // Nothing worth orchestrating in this fixture; not a failure.
            }

            evaluated++;
            RunAndAssertClassifiedEnd(fixture.FixtureId, fixture.DiscImagePath, report, candidate);
        }

        evaluated.Should().BeGreaterThan(0,
            "at least one local real-ROM fixture is expected to yield a qualifying candidate");
    }

    private static void RunAndAssertClassifiedEnd(
        string fixtureId, string discImagePath, DiscImageAnalysisReport report, RealRomFunctionCandidate candidate)
    {
        var fixture = candidate.ToDifferentialFixture($"real-rom-title-{fixtureId}-0x{candidate.StartAddress:X8}");
        var sink = new CapturedOutputSink();

        using var engine = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        // Issue #378: the run is observed through ObservedTitleExecution so the
        // classified-end assertions below are backed by an actual correctness
        // oracle rather than standing alone. The observer is a pass-through, so
        // this is still the one real run; see its documentation for exactly what
        // the invariants do and do not prove.
        var observed = new ObservedTitleExecution(engine, new CachedExitHandoff());
        var request = new TitleExecutionRequest(
            entryPc: fixture.EntryPc,
            initialGpr: fixture.InitialGpr,
            initialHi: fixture.InitialHi,
            initialLo: fixture.InitialLo,
            initialMemory: fixture.InitialMemory,
            outerBudget: 64,
            segmentBudget: 4096);

        var evidence =
            $"fixture={fixtureId} entry=0x{candidate.StartAddress:X8} instructions={candidate.InstructionCount}";

        var result = new ExecutionOrchestrator().Execute(observed, observed, request);

        // Real-ROM control flow is unknown until it is run; the orchestrator must
        // end in one of the classified guest outcomes with a real snapshot, never
        // InvalidState (a caller/handoff contract failure) and never an engine
        // mechanism failure with no snapshot.
        result.FinalSnapshot.Should().NotBeNull(
            $"{evidence}\n{result.DiagnosticCode} {result.DiagnosticMessage}");
        result.State.Should().NotBe(
            TitleExecutionState.InvalidState,
            $"{evidence}\n{result.DiagnosticCode} {result.DiagnosticMessage}");
        result.SegmentsRetired.Should().BeGreaterThan(0, evidence);

        // Which classified state a real ROM lands in stays unconstrained (#373):
        // the oracle asserts the reported state is the *correct* classification of
        // what this run actually did, and that architectural state survived every
        // segment boundary intact — not that the run reached a particular outcome.
        observed.AssertInvariantsHold(request, result, evidence);
    }

    private sealed class CachedExitHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            TitleExecutionHandoffResult.Exit();
    }
}
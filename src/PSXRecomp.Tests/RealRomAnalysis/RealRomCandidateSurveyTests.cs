using FluentAssertions;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Candidate-pool survey for the Issue #225 selector: quantifies why real-ROM candidates
/// are accepted and why they are rejected, using only the existing selector/lowering
/// contract. The real-fixture-gated proof runs against whatever the operator put under
/// <c>rom/</c> and writes a git-ignored local diagnostic report; the aggregation logic
/// itself is pinned by a synthetic, always-run test.
/// </summary>
[Test]
[Collection("RealRom")]
public class RealRomCandidateSurveyTests
{
    /// <summary>
    /// Instructions decoded per fixture when surveying. Larger than the skill test's
    /// window so the candidate pool spans many basic blocks; the cost only ever runs
    /// locally (no fixture in CI means the gated test skips explicitly).
    /// </summary>
    private const int InstructionCount = 16384;

    [SkippableFact]
    public void LocalFixtures_ProduceACandidatePoolSurvey()
    {
        var fixtures = RealRomFixtures.Discover();
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        foreach (var fixture in fixtures)
        {
            var staged = RealRomAnalyzer.AnalyzeStaged(fixture.DiscImagePath, fixture.FixtureId, InstructionCount);
            var report = staged.Outcome.Report;
            if (report is null)
            {
                continue; // Analysis failed; RealRomAnalysisSkillTests already covers that classification.
            }

            var snapshot = RealRomCandidateSurvey.Run(report);

            snapshot.TotalCandidates.Should().BeGreaterThan(0,
                $"fixture '{fixture.FixtureId}' decoded {report.DecodedInstructionCount} instructions into " +
                $"{report.BasicBlocks.Count} basic blocks, so a non-empty candidate pool is expected");
            (snapshot.AcceptedCandidates + snapshot.RejectedCandidates).Should().Be(snapshot.TotalCandidates,
                "every candidate is exactly one of accepted or rejected");

            var json = RealRomCandidateSurvey.ToJson(snapshot);
            WriteSurveyReport(fixture.FixtureId, json);

            // Reproducibility: a second pass over the identical report must be identical.
            var again = RealRomCandidateSurvey.ToJson(RealRomCandidateSurvey.Run(report));
            again.Should().Be(json, "the survey is deterministic for an identical report");
        }
    }

    [Fact]
    public void Run_AggregatesCorrectly_OnSyntheticReport()
    {
        var instructions = MakeInstructions(
            Base,
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),      // ADDIU $t0,$zero,1
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),      // ADDIU $t1,$zero,2
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 3),     // ADDIU $t2,$zero,3
            MipsEncoding.JumpRegister(rs: 31),                     // JR $ra — excluded
            MipsEncoding.Nop);                                     // delay slot (never reached)

        var report = new DiscImageAnalysisReport
        {
            DiscImageSha256 = new string('a', 64),
            SystemCnfBootPath = "cdrom:\\TEST.01;1",
            ExecutableFileName = "TEST.01",
            EntryPoint = Base,
            TextStart = Base,
            TextSize = 0x200,
            SpInitial = 0,
            GpInitial = 0,
            ExecutableFileSize = 1,
            ExecutableFileHash = new string('b', 64),
            DecodeStartAddress = Base,
            DecodedInstructionCount = instructions.Length,
            DecodedInstructions = instructions,
            DecodeFailures = Array.Empty<DecodeFailure>(),
            BasicBlocks = new[]
            {
                new BasicBlock { StartAddress = Base, EndAddress = Base, InstructionCount = 1 },
                new BasicBlock { StartAddress = Base + 12, EndAddress = Base + 12, InstructionCount = 1 },
            },
            CfgEdges = Array.Empty<CfgEdge>(),
            CallCandidateCount = 0,
            ReturnCandidateCount = 1,
        };

        var snapshot = RealRomCandidateSurvey.Run(report);

        snapshot.TotalCandidates.Should().Be(2, "entry point + the JR-start basic block form the candidate pool");
        snapshot.AcceptedCandidates.Should().Be(1, "only the entry-point window is non-empty; the JR start yields an empty window");
        snapshot.RejectedCandidates.Should().Be(1, "the basic-block start on the JR itself produces no window");
        snapshot.QualifyingCandidates.Should().Be(0, "the accepted window has 3 instructions, below the 8-instruction qualifying threshold");
        snapshot.StopBuckets.Should().Contain(b => b.Reason == nameof(RealRomCandidateStopReason.IndirectJumpExcluded));
        snapshot.StopDetails.Should().Contain(d => d.Reason == nameof(RealRomCandidateStopReason.IndirectJumpExcluded) && d.Opcode == "jr");
        snapshot.ExercisedOpcodes.Select(o => o.Opcode).Should().BeEquivalentTo(new[] { "addiu" });
        snapshot.ControlFlowOpcodes.Select(o => o.Opcode).Should().Contain("jr");
        snapshot.SupportProbes.Should().Contain(p => p.Opcode == "jr" && p.Status == "excluded-by-policy");
        snapshot.SupportProbes.Should().Contain(p => p.Opcode == "addiu" && p.Status == "lowered");
        snapshot.Candidates.Should().Contain(c => c.InstructionCount == 0 && c.StopOpcode == "jr");
    }

    [Fact]
    public void Run_DirectControlTransferWithDelaySlot_ProbesAsLowered()
    {
        // Regression: MipsToIrLowerer.LowerProgram consumes the entry right after a
        // control-transfer instruction as its delay slot. The probe must place the
        // branch/jump before its NOP delay slot in program order; putting the NOP
        // first left the branch with no following entry and misclassified it as
        // "unsupported".
        var instructions = MakeInstructions(
            Base,
            MipsEncoding.Branch(0x04, rs: 0, rt: 0, pc: Base, target: Base + 12), // BEQ $zero,$zero,+12
            MipsEncoding.Nop,                                                     // delay slot
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1));                    // branch target

        var report = new DiscImageAnalysisReport
        {
            DiscImageSha256 = new string('a', 64),
            SystemCnfBootPath = "cdrom:\\TEST.01;1",
            ExecutableFileName = "TEST.01",
            EntryPoint = Base,
            TextStart = Base,
            TextSize = 0x200,
            SpInitial = 0,
            GpInitial = 0,
            ExecutableFileSize = 1,
            ExecutableFileHash = new string('b', 64),
            DecodeStartAddress = Base,
            DecodedInstructionCount = instructions.Length,
            DecodedInstructions = instructions,
            DecodeFailures = Array.Empty<DecodeFailure>(),
            BasicBlocks = new[]
            {
                new BasicBlock { StartAddress = Base, EndAddress = Base, InstructionCount = 1 },
            },
            CfgEdges = Array.Empty<CfgEdge>(),
            CallCandidateCount = 0,
            ReturnCandidateCount = 0,
        };

        var snapshot = RealRomCandidateSurvey.Run(report);

        snapshot.SupportProbes.Should().Contain(p => p.Opcode == "beq" && p.Status == "lowered",
            "a direct branch with a valid static target and its delay slot must lower, not report unsupported");
    }

    private const uint Base = 0x80100000u;

    private static DecodedInstruction[] MakeInstructions(uint start, params uint[] words)
    {
        var result = new DecodedInstruction[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            result[i] = new DecodedInstruction
            {
                Address = unchecked(start + (uint)(i * 4)),
                RawWord = words[i],
                Mnemonic = string.Empty,
                Operands = string.Empty,
                Format = string.Empty,
                ControlFlow = string.Empty,
            };
        }

        return result;
    }

    /// <summary>
    /// Writes the local-only survey diagnostic to the git-ignored reports tree so it can
    /// be inspected after a run. The snapshot carries only metadata (hashes, addresses,
    /// mnemonic names, stop reasons), never copyrighted code bytes.
    /// </summary>
    private static void WriteSurveyReport(string fixtureId, string json)
    {
#pragma warning disable AARC003
        var fixtureReportDirectory = Path.Combine(RealRomFixtures.ReportRoot, fixtureId);
        Directory.CreateDirectory(fixtureReportDirectory);
        File.WriteAllText(Path.Combine(fixtureReportDirectory, "candidate-survey.json"), json);
#pragma warning restore AARC003
    }
}
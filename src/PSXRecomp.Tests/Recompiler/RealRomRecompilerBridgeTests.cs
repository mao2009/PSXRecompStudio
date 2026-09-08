using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

/// <summary>
/// Issue #225: proves the real-ROM candidate selector and fixture adapter using only
/// synthetic instruction words — no copyrighted ROM/EXE required, so this suite runs
/// everywhere in CI. The real-fixture-gated proof lives in
/// <c>PSXRecomp.Tests.RealRomAnalysis.RealRomRecompilerVerticalSliceTests</c>.
/// </summary>
[Test]
public sealed class RealRomRecompilerBridgeTests
{
    private const uint Base = 0x80100000u;

    // A real SLTU encoding captured from an actual PS1 executable during Issue #225
    // candidate exploration (SLTU $at, $v0, $v1) — MipsToIrLowerer does not lower it,
    // so it is a stable "unsupported instruction" probe without hand-rolling an encoding.
    private const uint UnsupportedSltuWord = 0x0043082Bu;

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

    [Fact]
    public void TryExtend_StopsBeforeIndirectJump_ExcludingIt()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),  // ADDIU $t0, $zero, 5
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 7),  // ADDIU $t1, $zero, 7
            MipsEncoding.R(0x21, rd: 11, rs: 8, rt: 9, shamt: 0), // ADDU $t3, $t0, $t1
            MipsEncoding.JumpRegister(rs: 31),                 // JR $ra
            MipsEncoding.Nop,                                   // delay slot (never reached)
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(3, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.IndirectJumpExcluded, candidate.StopReason);
        Assert.Equal(Base + 12, candidate.StopAddress);
        Assert.Equal(new[] { "Addiu", "Addu" }, candidate.RequiredInstructionSubset);
    }

    [Fact]
    public void TryExtend_StopsAtUnsupportedInstruction_WithoutIncludingIt()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5), // ADDIU $t0, $zero, 5
            UnsupportedSltuWord,
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 7), // never reached
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(1, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.UnsupportedInstruction, candidate.StopReason);
        Assert.Equal(Base + 4, candidate.StopAddress);
    }

    /// <summary>The window this and the two tests below share: shaped like a tiny real function body.</summary>
    private static uint[] FunctionShapedWords() => new[]
    {
        MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),                        // 0x00 ADDIU $t0, $zero, 5
        MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 7),                        // 0x04 ADDIU $t1, $zero, 7
        MipsEncoding.Branch(0x04, rs: 8, rt: 8, pc: Base + 8, target: Base + 0x14), // 0x08 BEQ $t0,$t0 (always taken)
        MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 1),                       // 0x0C delay slot: ADDIU $t3, $zero, 1
        MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 0xBAD),                   // 0x10 dead code (branch skips it)
        MipsEncoding.JumpRegister(rs: 31),                                       // 0x14 JR $ra — excluded
        MipsEncoding.Nop,                                                        // 0x18 delay slot (never reached)
    };

    [Fact]
    public void TryExtend_IncludesFusedControlTransfer_ThenStopsBeforeIndirectJump()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(5, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.IndirectJumpExcluded, candidate.StopReason);
        Assert.Equal(Base + 0x14, candidate.StopAddress);
        Assert.Contains("Beq", candidate.RequiredInstructionSubset);
    }

    [Fact]
    public void RealRomFixtureAdapter_CandidateMatchesInterpreter()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());
        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base)!;

        var fixture = candidate.ToDifferentialFixture("synthetic-function-shaped-candidate");
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.True(result.IsMatch, result.Diff?.Describe());

        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(Base + 0x14, result.Reference.Snapshot.PC);
        Assert.Equal(5u, result.Reference.Snapshot.Gpr[8]);   // $t0
        Assert.Equal(7u, result.Reference.Snapshot.Gpr[9]);   // $t1
        Assert.Equal(1u, result.Reference.Snapshot.Gpr[11]);  // $t3 (delay slot)
        Assert.Equal(0u, result.Reference.Snapshot.Gpr[12]);  // $t4: dead code, never executed
    }

    [Fact]
    public void RealRomFixtureAdapter_MismatchIsDetected()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());
        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base)!;
        var fixture = candidate.ToDifferentialFixture("synthetic-mismatch-regression");

        var reference = new RecompilerInterpreterExecutor().Execute(fixture).Snapshot!;
        var corruptedGpr = reference.Gpr.ToArray();
        corruptedGpr[11] = 0xDEADBEEFu; // flip $t3 to simulate a diverging recompiled result
        var corrupted = new RecompilerStateSnapshot(
            corruptedGpr, reference.HI, reference.LO, reference.PC, reference.LoadDelay,
            reference.Exception, reference.Termination, reference.Memory, reference.PcTrace);

        var diff = RecompilerStateDiff.Compare(reference, corrupted);

        Assert.False(diff.IsMatch);
        Assert.Contains(diff.Differences, d => d.FieldPath == "gpr[11]");
    }

    [Fact]
    public void BuildProvenance_IsDeterministic_AndChangesWithTheWindow()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());
        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base)!;
        var executable = new ArtifactFixtureIdentity
        {
            FixtureId = "synthetic",
            DiscImageFormat = "CHD",
            DiscImageSha256 = new string('a', 64),
            DiscImageSizeBytes = 1,
            ExecutableFileName = "TEST_000.01",
            ExecutableSerial = "TEST-00001",
            ExecutableSizeBytes = 1,
            ExecutableSha256 = new string('b', 64),
        };

        var first = RealRomFixtureAdapter.BuildProvenance(candidate, executable, "unit test");
        var second = RealRomFixtureAdapter.BuildProvenance(candidate, executable, "unit test");

        Assert.Equal(first.SelectionIdentitySha256, second.SelectionIdentitySha256);
        Assert.False(first.HasUnresolvedDependencies);
        Assert.Equal(candidate.InstructionCount, first.InstructionCount);
        Assert.Equal(candidate.RequiredInstructionSubset, first.RequiredInstructionSubset);

        var otherStartCandidate = RealRomCandidateSelector.TryExtend(instructions, Base)! with { StartAddress = Base + 4 };
        var third = RealRomFixtureAdapter.BuildProvenance(otherStartCandidate, executable, "unit test");
        Assert.NotEqual(first.SelectionIdentitySha256, third.SelectionIdentitySha256);
    }

    [Fact]
    public void SelectBest_PicksTheLongestCandidate_Deterministically()
    {
        // Two disjoint straight-line runs at different addresses; the second is longer.
        var shortRunStart = Base;
        var longRunStart = Base + 0x100;

        var words = new List<uint>();
        var addressToIndex = new Dictionary<uint, int>();
        void Add(uint address, uint word)
        {
            addressToIndex[address] = words.Count;
            words.Add(word);
        }

        // Pad the gap with a JR so the two runs never accidentally merge into one scan.
        Add(shortRunStart, MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1));
        Add(shortRunStart + 4, MipsEncoding.JumpRegister(rs: 31));
        Add(shortRunStart + 8, MipsEncoding.Nop);

        Add(longRunStart, MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1));
        Add(longRunStart + 4, MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2));
        Add(longRunStart + 8, MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 3));
        Add(longRunStart + 12, MipsEncoding.JumpRegister(rs: 31));
        Add(longRunStart + 16, MipsEncoding.Nop);

        var ordered = addressToIndex.OrderBy(kv => kv.Key)
            .Select(kv => new DecodedInstruction
            {
                Address = kv.Key,
                RawWord = words[kv.Value],
                Mnemonic = string.Empty,
                Operands = string.Empty,
                Format = string.Empty,
                ControlFlow = string.Empty,
            })
            .ToArray();

        var report = new DiscImageAnalysisReport
        {
            DiscImageSha256 = new string('c', 64),
            SystemCnfBootPath = "cdrom:\\TEST.01;1",
            ExecutableFileName = "TEST.01",
            EntryPoint = shortRunStart,
            TextStart = shortRunStart,
            TextSize = 0x200,
            SpInitial = 0,
            GpInitial = 0,
            ExecutableFileSize = 1,
            ExecutableFileHash = new string('d', 64),
            DecodeStartAddress = shortRunStart,
            DecodedInstructionCount = ordered.Length,
            DecodedInstructions = ordered,
            DecodeFailures = Array.Empty<DecodeFailure>(),
            BasicBlocks = new[]
            {
                new BasicBlock { StartAddress = shortRunStart, EndAddress = shortRunStart, InstructionCount = 1 },
                new BasicBlock { StartAddress = longRunStart, EndAddress = longRunStart, InstructionCount = 1 },
            },
            CfgEdges = Array.Empty<CfgEdge>(),
            CallCandidateCount = 0,
            ReturnCandidateCount = 0,
        };

        var best = RealRomCandidateSelector.SelectBest(report);

        Assert.NotNull(best);
        Assert.Equal(longRunStart, best!.StartAddress);
        Assert.Equal(3, best.InstructionCount);
    }
}
#pragma warning restore AARC003

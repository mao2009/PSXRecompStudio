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

    /// <summary>
    /// The window this and the tests below share: shaped like a tiny real function
    /// body, with its BEQ target landing on real code inside the eventual window
    /// (0x14) rather than on the excluded JR — a self-contained window, per #225's
    /// requirement that a static control-flow target may not silently exit the
    /// candidate (ADR-013).
    /// </summary>
    private static uint[] FunctionShapedWords() => new[]
    {
        MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),                        // 0x00 ADDIU $t0, $zero, 5
        MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 7),                        // 0x04 ADDIU $t1, $zero, 7
        MipsEncoding.Branch(0x04, rs: 8, rt: 8, pc: Base + 8, target: Base + 0x14), // 0x08 BEQ $t0,$t0 (always taken)
        MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 1),                       // 0x0C delay slot: ADDIU $t3, $zero, 1
        MipsEncoding.I(0x09, rt: 12, rs: 0, immediate: 0xBAD),                   // 0x10 dead code (branch skips it)
        MipsEncoding.I(0x09, rt: 13, rs: 0, immediate: 9),                       // 0x14 BEQ target: ADDIU $t5, $zero, 9
        MipsEncoding.JumpRegister(rs: 31),                                       // 0x18 JR $ra — excluded
        MipsEncoding.Nop,                                                        // 0x1C delay slot (never reached)
    };

    [Fact]
    public void TryExtend_IncludesFusedControlTransfer_ThenStopsBeforeIndirectJump()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(6, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.IndirectJumpExcluded, candidate.StopReason);
        Assert.Equal(Base + 0x18, candidate.StopAddress);
        Assert.Contains("Beq", candidate.RequiredInstructionSubset);
        Assert.False(RealRomCandidateSelector.HasExternalStaticControlFlowTarget(
            candidate.StartAddress, candidate.EncodedInstructions));
    }

    [Fact]
    public void TryExtend_AcceptsBeqTarget_WhenInsideTheWindow()
    {
        // Same shape as FunctionShapedWords but isolated: proves a BEQ whose target
        // lands on the window's own last instruction (not past it) is accepted.
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                        // 0x00 ADDIU $t0, $zero, 1
            MipsEncoding.Branch(0x04, rs: 8, rt: 8, pc: Base + 4, target: Base + 0x10), // 0x04 BEQ (taken)
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                         // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),                    // 0x0C dead code
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),                        // 0x10 BEQ target
            MipsEncoding.JumpRegister(rs: 31),                                        // 0x14 JR $ra — excluded
            MipsEncoding.Nop,
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(5, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.IndirectJumpExcluded, candidate.StopReason);
    }

    [Theory]
    [InlineData(0x04u)] // BEQ opcode field
    [InlineData(0x05u)] // BNE opcode field
    public void TryExtend_RejectsBranch_WhenTargetLandsOutsideTheWindow(uint branchOpcodeField)
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),                                       // 0x00 ADDIU $t0, $zero, 1
            MipsEncoding.Branch((byte)branchOpcodeField, rs: 8, rt: 0, pc: Base + 4, target: Base + 0x10), // 0x04 branch to the (excluded) JR
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                                        // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),                                    // 0x0C dead code
            MipsEncoding.JumpRegister(rs: 31),                                                        // 0x10 JR $ra — the branch's own target
            MipsEncoding.Nop,
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(1, candidate!.InstructionCount); // only the leading ADDIU survives the trim
        Assert.Equal(RealRomCandidateStopReason.StaticTargetOutsideCandidate, candidate.StopReason);
        Assert.Equal(Base + 4, candidate.StopAddress);
    }

    [Fact]
    public void TryExtend_RejectsJump_WhenTargetLandsOutsideTheWindow()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1), // 0x00 ADDIU $t0, $zero, 1
            MipsEncoding.Jump(Base + 0x0C),                   // 0x04 J targets the (excluded) JR
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2), // 0x08 delay slot
            MipsEncoding.JumpRegister(rs: 31),                // 0x0C JR $ra — the jump's own target
            MipsEncoding.Nop,
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(1, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.StaticTargetOutsideCandidate, candidate.StopReason);
        Assert.Equal(Base + 4, candidate.StopAddress);
    }

    [Fact]
    public void TryExtend_AcceptsJal_WhenCalleeIsInsideTheWindow()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),  // 0x00 ADDIU $t0, $zero, 1
            MipsEncoding.JumpAndLink(Base + 0x10),              // 0x04 JAL callee@0x10
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),   // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD), // 0x0C never reached
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),  // 0x10 callee, inside the window
            MipsEncoding.JumpRegister(rs: 31),                  // 0x14 JR $ra — excluded
            MipsEncoding.Nop,
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(5, candidate!.InstructionCount);
        Assert.Equal(RealRomCandidateStopReason.IndirectJumpExcluded, candidate.StopReason);
        Assert.Contains("Jal", candidate.RequiredInstructionSubset);
        Assert.False(RealRomCandidateSelector.HasExternalStaticControlFlowTarget(
            candidate.StartAddress, candidate.EncodedInstructions));
    }

    [Fact]
    public void TryExtend_RejectsJal_WhenCalleeIsOutsideTheWindow()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),  // 0x00 ADDIU $t0, $zero, 1
            MipsEncoding.JumpAndLink(Base + 0x14),              // 0x04 JAL callee@0x14 — the (excluded) JR
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),   // 0x08 delay slot
            MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD), // 0x0C never reached
            MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 3),  // 0x10 never reached either
            MipsEncoding.JumpRegister(rs: 31),                  // 0x14 JR $ra — the call's own target
            MipsEncoding.Nop,
        };
        var instructions = MakeInstructions(Base, words);

        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base);

        Assert.NotNull(candidate);
        Assert.Equal(1, candidate!.InstructionCount); // only the leading ADDIU survives the trim
        Assert.Equal(RealRomCandidateStopReason.ExternalCallExcluded, candidate.StopReason);
        Assert.Equal(Base + 4, candidate.StopAddress);
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
        Assert.Equal(Base + 0x18, result.Reference.Snapshot.PC);
        Assert.Equal(5u, result.Reference.Snapshot.Gpr[8]);   // $t0
        Assert.Equal(7u, result.Reference.Snapshot.Gpr[9]);   // $t1
        Assert.Equal(1u, result.Reference.Snapshot.Gpr[11]);  // $t3 (delay slot)
        Assert.Equal(0u, result.Reference.Snapshot.Gpr[12]);  // $t4: dead code, never executed
        Assert.Equal(9u, result.Reference.Snapshot.Gpr[13]);  // $t5: the branch's (in-window) target
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

    private static ArtifactFixtureIdentity MakeExecutableIdentity() => new()
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

    [Fact]
    public void BuildProvenance_IsDeterministic_AndChangesWithTheWindow()
    {
        var instructions = MakeInstructions(Base, FunctionShapedWords());
        var candidate = RealRomCandidateSelector.TryExtend(instructions, Base)!;
        var executable = MakeExecutableIdentity();

        var first = RealRomFixtureAdapter.BuildProvenance(candidate, executable, "unit test");
        var second = RealRomFixtureAdapter.BuildProvenance(candidate, executable, "unit test");

        Assert.Equal(first.SelectionIdentitySha256, second.SelectionIdentitySha256);
        Assert.False(first.HasUnresolvedDependencies); // this candidate is genuinely self-contained
        Assert.Equal(candidate.InstructionCount, first.InstructionCount);
        Assert.Equal(candidate.RequiredInstructionSubset, first.RequiredInstructionSubset);

        var otherStartCandidate = RealRomCandidateSelector.TryExtend(instructions, Base)! with { StartAddress = Base + 4 };
        var third = RealRomFixtureAdapter.BuildProvenance(otherStartCandidate, executable, "unit test");
        Assert.NotEqual(first.SelectionIdentitySha256, third.SelectionIdentitySha256);
    }

    /// <summary>
    /// Proves HasUnresolvedDependencies is actually computed, not a hardcoded constant:
    /// a hand-crafted candidate that bypasses the selector's own trimming (as if a
    /// future caller constructed one directly) is still correctly classified as having
    /// an unresolved static control-flow dependency.
    /// </summary>
    [Fact]
    public void BuildProvenance_DetectsExternalStaticControlFlowTarget_OnAHandCraftedCandidate()
    {
        var words = new[]
        {
            MipsEncoding.JumpAndLink(Base + 0x100), // JAL far outside this 2-word window
            MipsEncoding.Nop,                       // delay slot
        };
        var handCrafted = new RealRomFunctionCandidate
        {
            StartAddress = Base,
            EncodedInstructions = words,
            StopReason = RealRomCandidateStopReason.EndOfDecodedInstructions,
            StopAddress = null,
            StopDetail = "hand-crafted for this test; does not come from TryExtend's own trimming",
            RequiredInstructionSubset = new[] { "Jal", "Sll" },
        };

        var provenance = RealRomFixtureAdapter.BuildProvenance(handCrafted, MakeExecutableIdentity(), "unit test");

        Assert.True(provenance.HasUnresolvedDependencies);
    }

    /// <summary>
    /// TryExtend never returns a candidate containing JR/JALR, but RealRomFunctionCandidate
    /// and BuildProvenance are both public — a hand-crafted candidate (as if built some
    /// other way) that still contains one must not be reported as dependency-free.
    /// </summary>
    [Fact]
    public void BuildProvenance_DetectsIndirectJump_OnAHandCraftedCandidate()
    {
        var words = new[]
        {
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1), // ADDIU $t0, $zero, 1
            MipsEncoding.JumpRegister(rs: 31),                // JR $ra
            MipsEncoding.Nop,                                 // delay slot
        };
        var handCrafted = new RealRomFunctionCandidate
        {
            StartAddress = Base,
            EncodedInstructions = words,
            StopReason = RealRomCandidateStopReason.EndOfDecodedInstructions,
            StopAddress = null,
            StopDetail = "hand-crafted for this test; does not come from TryExtend's own exclusion",
            RequiredInstructionSubset = new[] { "Addiu", "Jr" },
        };

        var provenance = RealRomFixtureAdapter.BuildProvenance(handCrafted, MakeExecutableIdentity(), "unit test");

        Assert.True(provenance.HasUnresolvedDependencies);
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

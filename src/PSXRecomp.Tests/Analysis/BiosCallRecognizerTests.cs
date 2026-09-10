using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Analysis;

/// <summary>
/// Recognition, false-positive control, provenance, aggregation and determinism for
/// <see cref="BiosCallRecognizer"/> (Issue #11 / #279).
///
/// Every fixture is a hand-encoded MIPS word sequence, so a test states the machine code
/// it asserts on rather than a mock of it. Blocks come from the real
/// <see cref="BasicBlockBuilder"/>, so the recognizer is exercised against the same block
/// partition the analysis pipeline produces.
/// </summary>
[Test]
public class BiosCallRecognizerTests
{
    private const uint EntryPoint = 0x80010000;

    // --- Encoded MIPS words used by the fixtures -------------------------------------
    // Vector materialization: li $t2, 0xA0 / 0xB0 / 0xC0  (addiu $t2, $zero, imm)
    private const uint LiT2VectorA0 = 0x240A00A0;
    private const uint LiT2VectorB0 = 0x240A00B0;
    private const uint LiT2VectorC0 = 0x240A00C0;

    // A constant that is close to, but is not, a BIOS vector.
    private const uint LiT2NearVector = 0x240A00A4;

    // Function-number materialization into R9 ($t1).
    private const uint LiT1PutChar = 0x2409003C;   // addiu $t1, $zero, 0x3C
    private const uint OriT1Puts = 0x3409003E;     // ori   $t1, $zero, 0x3E
    private const uint LiT1Func17 = 0x24090017;    // addiu $t1, $zero, 0x17
    private const uint LiT1TooWide = 0x24090100;   // addiu $t1, $zero, 0x100  (not a byte)
    private const uint LiT3PutChar = 0x240B003C;   // addiu $t3, $zero, 0x3C   (wrong register)

    private const uint JrT2 = 0x01400008;          // jr    $t2
    private const uint JalrRaT2 = 0x0140F809;      // jalr  $ra, $t2
    private const uint JrRa = 0x03E00008;          // jr    $ra
    private const uint JalLocal = 0x0C00048D;      // jal   0x80012340
    private const uint Nop = 0x00000000;
    private const uint LwT1FromS0 = 0x8E090000;    // lw    $t1, 0($s0)
    private const uint MultT1T2 = 0x012A0018;      // mult  $t1, $t2   (writes no GPR)

    // Coprocessor moves: mfc0/mfc2 write GPR rt; a GTE command writes no GPR, yet mfc2
    // and the GTE command share the Cop2Command opcode.
    private const uint Mfc0T1 = 0x40096000;        // mfc0  $t1, $12
    private const uint Mfc2T1 = 0x48096000;        // mfc2  $t1, $12
    private const uint GteRtps = 0x4A180001;       // rtps  (COP2 command)

    // lui $t2, 0x8000 ; ori $t2, $t2, 0xA0  -> KSEG0 alias of the A0 vector.
    private const uint LuiT2Kseg0 = 0x3C0A8000;
    private const uint OriT2VectorA0 = 0x354A00A0;

    // j 0x800000A0 — a direct jump that lands on the KSEG0-aliased A0 vector.
    private const uint JDirectKseg0A0 = 0x08000028;

    // === Recognition ================================================================

    [Fact]
    public void Recognize_A0FamilyCall_IsRecognizedWithFunctionNumberAndIdentity()
    {
        var evidence = Recognize(LiT2VectorA0, JrT2, LiT1PutChar, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.A0, site.Family);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
        Assert.Equal("putchar", site.ServiceName);
        Assert.Equal(BiosCallResolution.DelaySlotConstant, site.Resolution);
    }

    [Fact]
    public void Recognize_B0FamilyCall_IsRecognized()
    {
        var evidence = Recognize(LiT2VectorB0, JrT2, LiT1Func17, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.B0, site.Family);
        Assert.Equal((byte)0x17, site.FunctionNumber);

        // 0x17 is not among the identities docs/REFERENCES.md verifies, so the site is
        // recorded without a name rather than with a guessed one.
        Assert.Null(site.ServiceName);
    }

    [Fact]
    public void Recognize_C0FamilyCall_IsRecognized()
    {
        var evidence = Recognize(LiT2VectorC0, JrT2, LiT1Func17, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.C0, site.Family);
        Assert.Equal((byte)0x17, site.FunctionNumber);
    }

    [Fact]
    public void Recognize_FunctionNumberFromOri_IsExtracted()
    {
        var evidence = Recognize(LiT2VectorA0, JrT2, OriT1Puts, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal((byte)0x3E, site.FunctionNumber);
        Assert.Equal("puts", site.ServiceName);
    }

    [Fact]
    public void Recognize_FunctionNumberSetBeforeTheJump_IsResolvedFromTheBlock()
    {
        // li $t1 before the transfer, an unrelated nop in the delay slot.
        var evidence = Recognize(LiT1PutChar, LiT2VectorA0, JrT2, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
        Assert.Equal(BiosCallResolution.BlockConstant, site.Resolution);
    }

    [Fact]
    public void Recognize_JalrDispatch_IsRecognized()
    {
        // Some code keeps a return address, dispatching with jalr instead of jr.
        var evidence = Recognize(LiT2VectorA0, JalrRaT2, LiT1PutChar, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.A0, site.Family);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
    }

    [Fact]
    public void Recognize_Kseg0AliasedVector_ResolvesToTheSameFamily()
    {
        // lui/ori materializes 0x800000A0, the KSEG0 alias of the A0 vector.
        var evidence = Recognize(LuiT2Kseg0, OriT2VectorA0, JrT2, LiT1PutChar, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.A0, site.Family);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
    }

    [Fact]
    public void Recognize_DirectJumpToAVector_IsRecognized()
    {
        var evidence = Recognize(LiT1PutChar, JDirectKseg0A0, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.A0, site.Family);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
    }

    // === False-positive control =====================================================

    [Fact]
    public void Recognize_OrdinaryJalAndJr_AreNotClassifiedAsBiosCalls()
    {
        var evidence = Recognize(JalLocal, Nop, JrRa, Nop);

        Assert.Empty(evidence.Sites);
        Assert.Empty(evidence.Summary);
    }

    [Fact]
    public void Recognize_JumpToAnAddressNearAVector_IsNotABiosCall()
    {
        // 0xA4 is four bytes past the A0 vector: adjacency is not identity.
        var evidence = Recognize(LiT2NearVector, JrT2, LiT1PutChar, JrRa, Nop);

        Assert.Empty(evidence.Sites);
    }

    [Fact]
    public void Recognize_IndirectJumpWithUnknownRegister_IsNotABiosCall()
    {
        // $t2 is loaded from memory, so the jump target is unknown. It must not be
        // attributed to a BIOS family on the strength of the nearby function number.
        var evidence = Recognize(LwT1FromS0, JrT2, LiT1PutChar, JrRa, Nop);

        Assert.Empty(evidence.Sites);
    }

    [Fact]
    public void Recognize_VectorWithUnresolvableFunctionNumber_IsRecordedAsUnresolved()
    {
        // The vector is certain, but R9 is loaded from memory: the number is not known.
        var evidence = Recognize(LiT2VectorA0, JrT2, LwT1FromS0, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(BiosCallFamily.A0, site.Family);
        Assert.Null(site.FunctionNumber);
        Assert.Null(site.ServiceName);
        Assert.Equal(BiosCallResolution.Unresolved, site.Resolution);
    }

    [Fact]
    public void Recognize_FunctionNumberClobberedByALoadDelay_IsNotTrusted()
    {
        // li $t1 sets the number, but a load into $t1 sits in the delay slot. The
        // architectural write is delayed, so the value at the jump-table entry is not
        // statically knowable — the site stays unresolved rather than reporting 0x3C.
        var evidence = Recognize(LiT1PutChar, LiT2VectorA0, JrT2, LwT1FromS0, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Null(site.FunctionNumber);
        Assert.Equal(BiosCallResolution.Unresolved, site.Resolution);
    }

    [Fact]
    public void Recognize_FunctionNumberWiderThanAByte_IsNotTrusted()
    {
        var evidence = Recognize(LiT2VectorA0, JrT2, LiT1TooWide, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Null(site.FunctionNumber);
        Assert.Equal(BiosCallResolution.Unresolved, site.Resolution);
    }

    [Fact]
    public void Recognize_ConstantInADifferentRegister_DoesNotSupplyTheFunctionNumber()
    {
        // 0x3C is materialized into $t3, not R9/$t1.
        var evidence = Recognize(LiT2VectorA0, JrT2, LiT3PutChar, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Null(site.FunctionNumber);
        Assert.Equal(BiosCallResolution.Unresolved, site.Resolution);
    }

    [Fact]
    public void Recognize_InstructionWithNoGprWrite_DoesNotInvalidateTheFunctionNumber()
    {
        // mult writes HI/LO only, so a known R9 survives it.
        var evidence = Recognize(LiT1PutChar, MultT1T2, LiT2VectorA0, JrT2, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
    }

    [Fact]
    public void Recognize_MoveFromCoprocessorTwo_InvalidatesTheFunctionNumber()
    {
        // mfc2 writes a GPR but shares the Cop2Command opcode with the GTE commands and
        // the moves *to* COP2, which write none. Treating the whole opcode as
        // GPR-preserving would let `mfc2 $t1, $12` leave a stale 0x3C in R9 and report a
        // BIOS identity the code never asked for.
        var evidence = Recognize(LiT1PutChar, Mfc2T1, LiT2VectorA0, JrT2, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Null(site.FunctionNumber);
        Assert.Equal(BiosCallResolution.Unresolved, site.Resolution);
    }

    [Fact]
    public void Recognize_GteCommand_DoesNotInvalidateTheFunctionNumber()
    {
        // The counterpart: a GTE command writes no GPR, so it must not force a resolved
        // call site to become unresolved. Real PS1 code is full of these.
        var evidence = Recognize(LiT1PutChar, GteRtps, LiT2VectorA0, JrT2, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal((byte)0x3C, site.FunctionNumber);
    }

    [Fact]
    public void Recognize_MoveFromCoprocessorZero_InvalidatesTheFunctionNumber()
    {
        var evidence = Recognize(LiT1PutChar, Mfc0T1, LiT2VectorA0, JrT2, Nop, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Null(site.FunctionNumber);
    }

    [Fact]
    public void Recognize_ReturnJump_IsNotABiosCall()
    {
        var evidence = Recognize(LiT1PutChar, JrRa, Nop);

        Assert.Empty(evidence.Sites);
    }

    // === Provenance =================================================================

    [Fact]
    public void Recognize_RecordsTheGuestPcOfTheTransferringInstruction()
    {
        var evidence = Recognize(LiT2VectorA0, JrT2, LiT1PutChar, JrRa, Nop);

        var site = Assert.Single(evidence.Sites);
        Assert.Equal(EntryPoint + 0x04, site.GuestPc);
    }

    [Fact]
    public void Recognize_RecordsTheContainingBlockAndFunction()
    {
        var words = new[] { LiT2VectorA0, JrT2, LiT1PutChar, JrRa, Nop };
        var instructions = Decode(words);
        var (blocks, edges) = BasicBlockBuilder.Build(instructions, EntryPoint, instructions.Count);
        var functions = FunctionDiscovery.Build(
            EntryPoint, EntryPoint, (uint)(words.Length * 4), instructions, blocks, edges);

        var site = Assert.Single(BiosCallRecognizer.Recognize(instructions, blocks, functions).Sites);

        var owningBlock = Assert.Single(blocks, block =>
            site.GuestPc >= block.StartAddress && site.GuestPc <= block.EndAddress);
        Assert.Equal(owningBlock.StartAddress, site.BasicBlockStartAddress);
        Assert.Equal(EntryPoint, site.ContainingFunctionAddress);
    }

    [Fact]
    public void Recognize_WithoutFunctionDiscovery_LeavesTheContainingFunctionUnset()
    {
        var site = Assert.Single(Recognize(LiT2VectorA0, JrT2, LiT1PutChar, JrRa, Nop).Sites);

        Assert.Null(site.ContainingFunctionAddress);
    }

    // === Aggregation ================================================================

    [Fact]
    public void Aggregate_CountsRepeatedCallsToTheSameService()
    {
        var evidence = Recognize(
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorA0, JrT2, LiT1PutChar,
            JrRa, Nop);

        var entry = Assert.Single(evidence.Summary);
        Assert.Equal(BiosCallFamily.A0, entry.Family);
        Assert.Equal((byte)0x3C, entry.FunctionNumber);
        Assert.Equal("putchar", entry.ServiceName);
        Assert.Equal(3, entry.CallSiteCount);
        Assert.Equal(3, evidence.Sites.Count);
    }

    [Fact]
    public void Aggregate_KeepsFamiliesDistinct()
    {
        var evidence = Recognize(
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorB0, JrT2, LiT1Func17,
            LiT2VectorC0, JrT2, LiT1Func17,
            JrRa, Nop);

        Assert.Equal(
            new[] { BiosCallFamily.A0, BiosCallFamily.B0, BiosCallFamily.C0 },
            evidence.Summary.Select(static entry => entry.Family));
        Assert.All(evidence.Summary, static entry => Assert.Equal(1, entry.CallSiteCount));
    }

    [Fact]
    public void Aggregate_DoesNotConflateTheSameFunctionNumberInDifferentFamilies()
    {
        var evidence = Recognize(
            LiT2VectorA0, JrT2, LiT1Func17,
            LiT2VectorB0, JrT2, LiT1Func17,
            JrRa, Nop);

        Assert.Equal(2, evidence.Summary.Count);
        Assert.All(evidence.Summary, entry =>
        {
            Assert.Equal((byte)0x17, entry.FunctionNumber);
            Assert.Equal(1, entry.CallSiteCount);
        });
        Assert.Equal(BiosCallFamily.A0, evidence.Summary[0].Family);
        Assert.Equal(BiosCallFamily.B0, evidence.Summary[1].Family);
    }

    [Fact]
    public void Aggregate_KeepsUnresolvedSitesInTheSummary()
    {
        var evidence = Recognize(
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorA0, JrT2, LwT1FromS0,
            JrRa, Nop);

        Assert.Equal(2, evidence.Summary.Count);

        // The resolved identity sorts before its family's unresolved bucket.
        Assert.Equal((byte)0x3C, evidence.Summary[0].FunctionNumber);
        Assert.Null(evidence.Summary[1].FunctionNumber);
        Assert.Equal(1, evidence.Summary[1].CallSiteCount);
    }

    // === Determinism ================================================================

    [Fact]
    public void Recognize_IsDeterministicAcrossRuns()
    {
        var words = new[]
        {
            LiT2VectorC0, JrT2, LiT1Func17,
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorB0, JrT2, LwT1FromS0,
            LiT2VectorA0, JrT2, OriT1Puts,
            JrRa, Nop,
        };

        var first = RecognizeWords(words);
        var second = RecognizeWords(words);

        // Element-wise: the evidence record holds lists, whose record equality is by
        // reference. The site and summary records themselves compare structurally.
        Assert.Equal(first.Sites, second.Sites);
        Assert.Equal(first.Summary, second.Summary);
    }

    [Fact]
    public void Recognize_SiteOrderIsAscendingByGuestPcRegardlessOfInputOrder()
    {
        var words = new[]
        {
            LiT2VectorC0, JrT2, LiT1Func17,
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorB0, JrT2, LiT1Func17,
            JrRa, Nop,
        };

        var instructions = Decode(words);
        var (blocks, _) = BasicBlockBuilder.Build(instructions, EntryPoint, instructions.Count);

        var inOrder = BiosCallRecognizer.Recognize(instructions, blocks);
        var shuffled = BiosCallRecognizer.Recognize(
            instructions.Reverse().ToList(),
            blocks.Reverse().ToList());

        Assert.Equal(inOrder.Sites, shuffled.Sites);
        Assert.Equal(inOrder.Summary, shuffled.Summary);
        Assert.Equal(
            inOrder.Sites.Select(static site => site.GuestPc).Order(),
            inOrder.Sites.Select(static site => site.GuestPc));
    }

    [Fact]
    public void Recognize_SummaryOrderIsStableRegardlessOfEncounterOrder()
    {
        // The same three identities, met in two different orders, aggregate identically.
        var first = RecognizeWords(new[]
        {
            LiT2VectorC0, JrT2, LiT1Func17,
            LiT2VectorA0, JrT2, LiT1PutChar,
            LiT2VectorB0, JrT2, LiT1Func17,
            JrRa, Nop,
        }).Summary;

        var second = RecognizeWords(new[]
        {
            LiT2VectorB0, JrT2, LiT1Func17,
            LiT2VectorC0, JrT2, LiT1Func17,
            LiT2VectorA0, JrT2, LiT1PutChar,
            JrRa, Nop,
        }).Summary;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Recognize_EmptyInput_ProducesEmptyEvidence()
    {
        var evidence = BiosCallRecognizer.Recognize(
            Array.Empty<DecodedInstruction>(), Array.Empty<BasicBlock>());

        Assert.Empty(evidence.Sites);
        Assert.Empty(evidence.Summary);
    }

    // === Identity contract ==========================================================

    [Theory]
    [InlineData(BiosCallFamily.A0, (byte)0x3C, "putchar")]
    [InlineData(BiosCallFamily.B0, (byte)0x3D, "putchar")]
    [InlineData(BiosCallFamily.A0, (byte)0x3E, "puts")]
    [InlineData(BiosCallFamily.B0, (byte)0x3F, "puts")]
    public void BiosCallNames_ResolvesTheVerifiedIdentities(
        BiosCallFamily family, byte functionNumber, string expected)
    {
        Assert.True(BiosCallNames.TryResolve(family, functionNumber, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    // Unverified identities carry a "verify before use" marker in the evidence doc and
    // must not be named; a family that does not host the alias must not inherit it.
    [InlineData(BiosCallFamily.A0, (byte)0x3B)]
    [InlineData(BiosCallFamily.A0, (byte)0x3D)]
    [InlineData(BiosCallFamily.B0, (byte)0x3C)]
    [InlineData(BiosCallFamily.B0, (byte)0x3E)]
    [InlineData(BiosCallFamily.C0, (byte)0x3C)]
    [InlineData(BiosCallFamily.C0, (byte)0x3E)]
    [InlineData(BiosCallFamily.A0, (byte)0x00)]
    public void BiosCallNames_DoesNotGuessUnverifiedIdentities(BiosCallFamily family, byte functionNumber)
    {
        Assert.False(BiosCallNames.TryResolve(family, functionNumber, out var name));
        Assert.Empty(name);
    }

    // === Helpers ====================================================================

    private static BiosCallEvidence Recognize(params uint[] words) => RecognizeWords(words);

    private static BiosCallEvidence RecognizeWords(uint[] words)
    {
        var instructions = Decode(words);
        var (blocks, _) = BasicBlockBuilder.Build(instructions, EntryPoint, instructions.Count);
        return BiosCallRecognizer.Recognize(instructions, blocks);
    }

    /// <summary>
    /// Turns raw MIPS words into the same <see cref="DecodedInstruction"/> shape the
    /// analysis pipeline records, starting at <see cref="EntryPoint"/>.
    /// </summary>
    private static IReadOnlyList<DecodedInstruction> Decode(uint[] words)
    {
        var instructions = new List<DecodedInstruction>(words.Length);
        for (var index = 0; index < words.Length; index++)
        {
            var raw = R3000aDecoder.Decode(words[index]);
            instructions.Add(new DecodedInstruction
            {
                Address = unchecked(EntryPoint + (uint)(index * 4)),
                RawWord = words[index],
                Mnemonic = MipsInstructionFormatter.FormatMnemonic(raw.Opcode),
                Operands = MipsInstructionFormatter.FormatOperands(raw),
                Format = raw.Format.ToString(),
                ControlFlow = raw.ControlFlow.ToString(),
            });
        }

        return instructions;
    }
}

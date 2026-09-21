using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Issue #411: the IR-level memory-effect contract that distinguishes ordinary
/// guest-RAM access from MMIO/device-visible access, and the surrounding
/// control-flow / exception effects that must stay explicit rather than being
/// silently folded into an ordinary access or a silent fallback.
/// </summary>
[Test]
public class RecompilerIrMemoryEffectTests
{
    private const uint EntryPc = 0x80001000u;

    // --- 1/2: ordinary RAM read/write classifies as Ordinary --------------

    [Theory]
    [InlineData(0x00000010u)] // KUSEG
    [InlineData(0x80000010u)] // KSEG0
    [InlineData(0xA0000010u)] // KSEG1
    public void Classify_OrdinaryRamAddress_IsOrdinary(uint address)
    {
        RecompilerIrMemoryEffectClassifier.Classify(address).Should().Be(RecompilerIrMemoryEffectKind.Ordinary);
    }

    [Fact]
    public void Classify_BiosRomAddress_IsOrdinary()
    {
        RecompilerIrMemoryEffectClassifier.Classify(Ps1MemoryMap.BiosBase).Should().Be(RecompilerIrMemoryEffectKind.Ordinary);
    }

    [Fact]
    public void OrdinaryRamLoad_CarriesTheOrdinaryEffect()
    {
        var effect = RecompilerIrMemoryEffectClassifier.Classify(0x00000010u);
        var block = BuildLoadBlock(0x00000010u, effect);

        block.Operations.Single(op => op.Kind == RecompilerIrOperationKind.Load32).MemoryEffect.Should().Be(RecompilerIrMemoryEffectKind.Ordinary);
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    [Fact]
    public void OrdinaryRamStore_CarriesTheOrdinaryEffect()
    {
        var effect = RecompilerIrMemoryEffectClassifier.Classify(0x00000010u);
        var block = BuildStoreBlock(0x00000010u, effect);

        block.Operations.Single(op => op.Kind == RecompilerIrOperationKind.Store32).MemoryEffect.Should().Be(RecompilerIrMemoryEffectKind.Ordinary);
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    // --- 3/4: statically-known MMIO read/write classifies as Device -------

    [Theory]
    [InlineData(Ps1MemoryMap.IStat)]
    [InlineData(Ps1MemoryMap.Dpcr)]
    public void Classify_HardwareRegisterAddress_IsDevice(uint address)
    {
        RecompilerIrMemoryEffectClassifier.Classify(address).Should().Be(RecompilerIrMemoryEffectKind.Device);
        // A hardware register must never be classified as ordinary RAM.
        RecompilerIrMemoryEffectClassifier.Classify(address).Should().NotBe(RecompilerIrMemoryEffectKind.Ordinary);
    }

    [Fact]
    public void Classify_HardwareRegisterAddress_ThroughAKseg1Alias_IsStillDevice()
    {
        // KSEG1 (uncached) alias of the same physical IStat register.
        var kseg1Alias = 0xA0000000u | Ps1MemoryMap.IStat;
        RecompilerIrMemoryEffectClassifier.Classify(kseg1Alias).Should().Be(RecompilerIrMemoryEffectKind.Device);
    }

    [Fact]
    public void DeviceRead_CarriesTheDeviceEffect()
    {
        var effect = RecompilerIrMemoryEffectClassifier.Classify(Ps1MemoryMap.IStat);
        var block = BuildLoadBlock(Ps1MemoryMap.IStat, effect);

        block.Operations.Single(op => op.Kind == RecompilerIrOperationKind.Load32).MemoryEffect.Should().Be(RecompilerIrMemoryEffectKind.Device);
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    [Fact]
    public void DeviceWrite_CarriesTheDeviceEffect()
    {
        var effect = RecompilerIrMemoryEffectClassifier.Classify(Ps1MemoryMap.Dpcr);
        var block = BuildStoreBlock(Ps1MemoryMap.Dpcr, effect);

        block.Operations.Single(op => op.Kind == RecompilerIrOperationKind.Store32).MemoryEffect.Should().Be(RecompilerIrMemoryEffectKind.Device);
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    // --- 5: device read/write sequence order is preserved deterministically

    [Fact]
    public void DeviceReadThenWrite_SerializesDeterministicallyAndPreservesOrder()
    {
        var first = BuildDeviceSequenceBlock(readFirst: true);
        var second = BuildDeviceSequenceBlock(readFirst: true);

        RecompilerIrSerializer.Serialize(new RecompilerIrProgram(new[] { first }))
            .Should().Be(RecompilerIrSerializer.Serialize(new RecompilerIrProgram(new[] { second })));

        first.Operations.Select(op => op.Kind).Should().Equal(
            RecompilerIrOperationKind.Constant,
            RecompilerIrOperationKind.Load32,
            RecompilerIrOperationKind.Constant,
            RecompilerIrOperationKind.Constant,
            RecompilerIrOperationKind.Store32);
    }

    // Negative: reordering the observable device operations must not be
    // indistinguishable from the original sequence — the IR carries order,
    // it does not canonicalize it away.
    [Fact]
    public void ReorderingDeviceOperations_ProducesADifferentProgram()
    {
        var readFirst = new RecompilerIrProgram(new[] { BuildDeviceSequenceBlock(readFirst: true) });
        var writeFirst = new RecompilerIrProgram(new[] { BuildDeviceSequenceBlock(readFirst: false) });

        RecompilerIrSerializer.Serialize(readFirst).Should().NotBe(RecompilerIrSerializer.Serialize(writeFirst));

        var readFirstGen = RecompilerHostCodeGen.Generate(readFirst);
        var writeFirstGen = RecompilerHostCodeGen.Generate(writeFirst);
        readFirstGen.Success.Should().BeTrue();
        writeFirstGen.Success.Should().BeTrue();
        readFirstGen.Source.Should().NotBe(writeFirstGen.Source);
    }

    private static RecompilerIrBlock BuildDeviceSequenceBlock(bool readFirst)
    {
        var deviceEffect = RecompilerIrMemoryEffectClassifier.Classify(Ps1MemoryMap.IStat);
        var address = new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: Ps1MemoryMap.IStat);
        var operations = readFirst
            ? new[]
            {
                address,
                new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0, memoryEffect: deviceEffect),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2, immediate: Ps1MemoryMap.Dpcr),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 3, immediate: 0xFFFFFFFF),
                new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 2, inputValueB: 3, memoryEffect: deviceEffect),
            }
            : new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: Ps1MemoryMap.Dpcr),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0xFFFFFFFF),
                new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1, memoryEffect: deviceEffect),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2, immediate: Ps1MemoryMap.IStat),
                new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 3, inputValueA: 2, memoryEffect: deviceEffect),
            };

        return new RecompilerIrBlock(EntryPc, operations, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
    }

    // --- 6: an unclassifiable / unknown address is never silently ordinary

    [Theory]
    [InlineData(0xC0000000u)] // KSEG2, untranslatable
    public void Classify_UntranslatableAddress_IsUnknownNotOrdinary(uint address)
    {
        var effect = RecompilerIrMemoryEffectClassifier.Classify(address);
        effect.Should().Be(RecompilerIrMemoryEffectKind.Unknown);
        effect.Should().NotBe(RecompilerIrMemoryEffectKind.Ordinary);
    }

    [Fact]
    public void Classify_UnmappedTranslatableAddress_IsUnknownNotOrdinary()
    {
        // Between the BIOS ROM and the HW register window: translatable, but
        // not RAM, BIOS, or a hardware register.
        var effect = RecompilerIrMemoryEffectClassifier.Classify(0x1F000000u);
        effect.Should().Be(RecompilerIrMemoryEffectKind.Unknown);
        effect.Should().NotBe(RecompilerIrMemoryEffectKind.Ordinary);
    }

    [Fact]
    public void RealMipsLowering_LeavesLoadStoreEffectUnknown_BecauseTheAddressIsRegisterRelative()
    {
        // A real base+offset LW/SW has a runtime-only effective address, so the
        // lowering stage must not guess: it must leave MemoryEffect at Unknown
        // rather than defaulting it to Ordinary.
        var instructions = new[]
        {
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Lw, rt: 9, baseRegister: 8, offset: 0)), EntryPc),
            (R3000aDecoder.Decode(MipsEncoding.Load(R3000aOpcode.Sw, rt: 9, baseRegister: 8, offset: 4)), EntryPc + 4),
        };

        var program = MipsToIrLowerer.LowerProgram(instructions);
        var memoryOps = program.Blocks.SelectMany(block => block.Operations)
            .Where(op => op.Kind is RecompilerIrOperationKind.Load32 or RecompilerIrOperationKind.Store32);

        memoryOps.Should().NotBeEmpty();
        memoryOps.Should().OnlyContain(op => op.MemoryEffect == RecompilerIrMemoryEffectKind.Unknown);
    }

    // --- Negative: dropping the effect metadata changes the operation ------

    [Fact]
    public void DroppingTheMemoryEffect_ProducesADifferentOperation()
    {
        var withEffect = new RecompilerIrOperation(
            RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0, memoryEffect: RecompilerIrMemoryEffectKind.Device);
        var withoutEffect = new RecompilerIrOperation(
            RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0);

        withEffect.Should().NotBe(withoutEffect);

        var address = new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: Ps1MemoryMap.IStat);
        var withEffectBlock = new RecompilerIrBlock(EntryPc, new[] { address, withEffect, WriteResult(1) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
        var withoutEffectBlock = new RecompilerIrBlock(EntryPc, new[] { address, withoutEffect, WriteResult(1) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));

        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { withEffectBlock })).IsValid.Should().BeTrue();
        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { withoutEffectBlock })).IsValid.Should().BeTrue();
        RecompilerIrSerializer.Serialize(new RecompilerIrProgram(new[] { withEffectBlock }))
            .Should().NotBe(RecompilerIrSerializer.Serialize(new RecompilerIrProgram(new[] { withoutEffectBlock })));
    }

    // --- Validator: fail closed on malformed / misplaced effect metadata --

    [Fact]
    public void Validator_RejectsUndefinedMemoryEffect()
    {
        var block = new RecompilerIrBlock(EntryPc, new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 0, inputValueA: -1, memoryEffect: (RecompilerIrMemoryEffectKind)255),
        }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidMemoryAccess);
    }

    [Fact]
    public void Validator_RejectsAMemoryEffectOnANonMemoryOperation()
    {
        var block = new RecompilerIrBlock(EntryPc, new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1, memoryEffect: RecompilerIrMemoryEffectKind.Device),
        }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));

        var result = RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block }));

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(RecompilerIrDiagnosticCode.InvalidMemoryAccess);
    }

    // --- 7/8: BIOS/runtime transfer and indirect flow stay explicit --------

    [Fact]
    public void Jalr_ToARegisterHeldTarget_TerminatesAsExplicitUnresolvedIndirectFlow_NeverAJumpOrCall()
    {
        // The BIOS A0/B0/C0 call convention loads the vector address into a
        // register, then JALR through it: at lowering time the target is a
        // runtime register value, indistinguishable from any other indirect
        // call. The lowering stage must never invent a resolved Jump/Call flow
        // for it — the BIOS/runtime boundary is resolved later, by the host
        // (RecompilerInterpreterExecutor / the generated host_transfer hook),
        // never guessed here.
        var block = LowerControlTransferSupported(
            MipsEncoding.JumpAndLinkRegister(rd: 31, rs: 8), MipsEncoding.Nop);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        block.Exit.Flow.Should().BeNull();
        block.Exit.NextPc.Should().BeNull();
    }

    [Fact]
    public void Jr_ToARegisterHeldTarget_TerminatesAsExplicitUnresolvedIndirectFlow()
    {
        var block = LowerControlTransferSupported(MipsEncoding.JumpRegister(rs: 8), MipsEncoding.Nop);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        block.Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void ResolvedDirectCall_IsNeverConfusedWithAnUnresolvedTransfer()
    {
        // J/JAL to a static target lowers to an explicit Jump/Call flow, kept
        // distinct in shape from the indirect/unresolved case above.
        var block = LowerControlTransferSupported(MipsEncoding.JumpAndLink(0x80002000u), MipsEncoding.Nop);

        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        block.Exit.Flow.Should().NotBeNull();
        block.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Call);
        block.Exit.Flow.Target.Should().Be(0x80002000u);
    }

    // --- 9: an exception-producing (trapping) operation fails explicitly ---

    [Fact]
    public void Add_TheOverflowTrappingOpcode_IsUnsupportedAndFailsExplicitly()
    {
        // ADD (unlike ADDU) traps to an Overflow exception on signed overflow.
        // This lowering stage does not model that exception effect, so it must
        // fail closed rather than silently lower it as if it were ADDU.
        var instruction = R3000aDecoder.Decode(MipsEncoding.R(0x20, rd: 8, rs: 9, rt: 10, shamt: 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Add);

        var result = MipsToIrLowerer.Lower(instruction, EntryPc);

        result.IsSupported.Should().BeFalse();
        result.Block.Should().BeNull();
        result.UnsupportedOpcode.Should().Be(R3000aOpcode.Add);
    }

    [Fact]
    public void Syscall_IsUnsupportedAndFailsExplicitly()
    {
        var instruction = R3000aDecoder.Decode(MipsEncoding.R(0x0C, rd: 0, rs: 0, rt: 0, shamt: 0));
        instruction.Opcode.Should().Be(R3000aOpcode.Syscall);

        var result = MipsToIrLowerer.Lower(instruction, EntryPc);

        result.IsSupported.Should().BeFalse();
        result.Block.Should().BeNull();
    }

    // Issue #481: a reachable BREAK raises a synchronous Bp exception (Excode
    // 0x09). Unlike ADD/ADDI overflow it does not depend on runtime data, so the
    // exception is fully resolvable at lowering time and is carried on the exit
    // rather than being silently folded into a NOP, a bare Success, or an
    // unsupported failure.

    [Fact]
    public void Break_LowersToAnExceptionExit_CarryingTheBpResolution()
    {
        var instruction = R3000aDecoder.Decode(MipsEncoding.Break());
        instruction.Opcode.Should().Be(R3000aOpcode.Break);

        var result = MipsToIrLowerer.Lower(instruction, EntryPc);

        result.IsSupported.Should().BeTrue($"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        var block = result.Block!;
        block.EntryPc.Should().Be(EntryPc);
        block.Operations.Should().BeEmpty();
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Exception);
        block.Exit.Flow.Should().BeNull();
        block.Exit.NextPc.Should().BeNull();

        block.Exit.Exception.Should().NotBeNull();
        block.Exit.Exception!.IsRaised.Should().BeTrue();
        block.Exit.Exception.Code.Should().Be(MipsToIrLowerer.BreakExcode);
        block.Exit.Exception.FaultPc.Should().Be(EntryPc);
        block.Exit.Exception.InDelaySlot.Should().BeFalse();

        // A standalone BREAK block is valid IR on its own: an exception exit
        // carrying its resolution validates (no implicit fallback is invented).
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    [Fact]
    public void Break_AsADelaySlot_LowersTheOwningTransferToAnExceptionExit()
    {
        // JAL links PC+8 into $ra before its delay slot runs; the delay-slot
        // BREAK then raises with EPC/BD pointing at the JAL, so the link write
        // must still be emitted and the transfer's call flow must be suppressed.
        var control = R3000aDecoder.Decode(MipsEncoding.JumpAndLink(0x80002000u));
        var delaySlot = R3000aDecoder.Decode(MipsEncoding.Break());
        var result = MipsToIrLowerer.LowerControlTransfer(control, EntryPc, delaySlot);

        result.IsSupported.Should().BeTrue($"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        var block = result.Block!;
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Exception);
        block.Exit.Flow.Should().BeNull();
        block.Exit.NextPc.Should().BeNull();

        block.Exit.Exception.IsRaised.Should().BeTrue();
        block.Exit.Exception.Code.Should().Be(MipsToIrLowerer.BreakExcode);
        block.Exit.Exception.FaultPc.Should().Be(EntryPc); // EPC = the owning JAL's PC
        block.Exit.Exception.InDelaySlot.Should().BeTrue();

        // The JAL's link still retires before the fault (the IR carries the
        // write; the call flow it would otherwise start is dropped).
        block.Operations.Should().Contain(op =>
            op.Kind == RecompilerIrOperationKind.Constant &&
            op.Immediate == EntryPc + 8); // $ra = PC + 8
        block.Operations.Should().Contain(op =>
            op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == 31);
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    [Fact]
    public void Break_AsADelaySlot_SuppressesTheBranchTransferFlow()
    {
        // A delay-slot BREAK behind a branch suppresses the pending taken/fall
        // transfer: the block stops at the exception instead of flowing.
        var control = R3000aDecoder.Decode(MipsEncoding.Branch(0x04, rs: 8, rt: 9, pc: EntryPc, target: 0x80002000u));
        var delaySlot = R3000aDecoder.Decode(MipsEncoding.Break());
        var result = MipsToIrLowerer.LowerControlTransfer(control, EntryPc, delaySlot);

        result.IsSupported.Should().BeTrue($"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        var block = result.Block!;
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Exception);
        block.Exit.Flow.Should().BeNull();
        block.Exit.Exception.IsRaised.Should().BeTrue();
        block.Exit.Exception.FaultPc.Should().Be(EntryPc);
        block.Exit.Exception.InDelaySlot.Should().BeTrue();
        ValidatesAsSingleBlockProgram(block).Should().BeTrue();
    }

    // --- 10/11: deterministic IR serialization and generated C -------------

    [Fact]
    public void DeviceProgram_SerializesIdenticallyAcrossRepeatedRuns()
    {
        var program = new RecompilerIrProgram(new[] { BuildDeviceSequenceBlock(readFirst: true) });

        var runs = Enumerable.Range(0, 3).Select(_ => RecompilerIrSerializer.Serialize(program)).ToArray();

        runs.Should().OnlyContain(text => text == runs[0]);
    }

    [Fact]
    public void DeviceProgram_GeneratesIdenticalHostCSourceAcrossRepeatedRuns()
    {
        var program = new RecompilerIrProgram(new[] { BuildDeviceSequenceBlock(readFirst: true) });

        var runs = Enumerable.Range(0, 3).Select(_ => RecompilerHostCodeGen.Generate(program)).ToArray();

        runs.Should().OnlyContain(r => r.Success);
        runs.Should().OnlyContain(r => r.Source == runs[0].Source);
    }

    // --- helpers -------------------------------------------------------

    private static RecompilerIrOperation WriteResult(int valueId) =>
        new(RecompilerIrOperationKind.WriteGpr, inputValueA: valueId, register: 8);

    /// <summary>Builds a valid single-block Load32 program: address constant, load, commit.</summary>
    private static RecompilerIrBlock BuildLoadBlock(uint address, RecompilerIrMemoryEffectKind effect)
    {
        var addressOp = new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: address);
        var load = new RecompilerIrOperation(RecompilerIrOperationKind.Load32, resultValueId: 1, inputValueA: 0, memoryEffect: effect);
        return new RecompilerIrBlock(EntryPc, new[] { addressOp, load, WriteResult(1) }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
    }

    /// <summary>Builds a valid single-block Store32 program: address constant, value constant, store.</summary>
    private static RecompilerIrBlock BuildStoreBlock(uint address, RecompilerIrMemoryEffectKind effect)
    {
        var addressOp = new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: address);
        var valueOp = new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0xFFFFFFFF);
        var store = new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1, memoryEffect: effect);
        return new RecompilerIrBlock(EntryPc, new[] { addressOp, valueOp, store }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
    }

    private static bool ValidatesAsSingleBlockProgram(RecompilerIrBlock block) =>
        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block })).IsValid;

    private static RecompilerIrBlock LowerControlTransferSupported(uint controlWord, uint delaySlotWord)
    {
        var control = R3000aDecoder.Decode(controlWord);
        var delaySlot = R3000aDecoder.Decode(delaySlotWord);
        var result = MipsToIrLowerer.LowerControlTransfer(control, EntryPc, delaySlot);
        result.IsSupported.Should().BeTrue($"lowering failed: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
        return result.Block!;
    }
}

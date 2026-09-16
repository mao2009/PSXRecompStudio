using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Runtime;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Issue #362, recompiled half: the generated host must dispatch a guest transfer
/// to a BIOS A0/B0/C0 trampoline vector through the very same
/// <see cref="IBiosRuntime"/> the interpreter path uses, with the same
/// Supported / PatchedTarget / Unsupported semantics.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture here is a synthetic MIPS program compiled all the way through
/// the real pipeline (decode → lower → validate → host codegen → gcc → run), so
/// what is asserted is the behavior of generated C, not of a simulation of it.
/// </para>
/// <para>
/// The generated host can only enter a PC it compiled a block for, so every
/// program here returns from a patched routine with a static jump rather than
/// <c>jr $ra</c> (which lowers to
/// <see cref="RecompilerIrTerminationReason.UnresolvedIndirectFlow"/>). The
/// interpreter runs the identical fixture, which is what makes the parity
/// assertions meaningful.
/// </para>
/// </remarks>
[Test]
public sealed class RecompiledBiosVectorDispatchTests
{
    private const uint Kseg0EntryPc = 0x80001000u;
    private const uint KusegEntryPc = 0x00001000u;

    /// <summary>Value the patched routine leaves in <c>$s0</c>, and stores to <see cref="ScratchAddress"/>.</summary>
    private const uint PatchedRoutineMarker = 0x5Au;

    /// <summary>Value the post-return tail leaves in <c>$s1</c>, proving control came back.</summary>
    private const uint ReturnedMarker = 0x77u;

    /// <summary>Guest RAM byte the patched routine writes, well clear of the jump tables and the program.</summary>
    private const uint ScratchAddress = 0x00000C00u;

    /// <summary>An unregistered A0 slot, so its entry starts at zero rather than at an HLE sentinel.</summary>
    private const byte UnregisteredA0Function = 0x10;

    private const byte LuiOpcodeField = 0x0F;
    private const byte OriOpcodeField = 0x0D;

    // --- Supported: the generated path reaches a registered HLE service -------

    [Theory]
    // B0:56 GetC0Table and B0:57 GetB0Table take no arguments at all, so the
    // generated path must read the Runtime's arity SSOT rather than assume four:
    // the deliberately garbage $a0-$a3 below would otherwise fail the service's
    // own argument-count check.
    [InlineData(BiosHleRuntime.GetC0TableFunction, BiosJumpTables.C0TableAddress)]
    [InlineData(BiosHleRuntime.GetB0TableFunction, BiosJumpTables.B0TableAddress)]
    public void GeneratedPath_ZeroArgumentService_IsSupported_DespiteGarbageInEveryAbiRegister(
        byte function, uint expectedReturnValue)
    {
        var result = RunGenerated(CallProgram(
            Kseg0EntryPc, BiosJumpTables.B0VectorAddress, function,
            a0: 0xDEADBEEFu, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        var snapshot = result.Snapshot!;
        snapshot.Gpr[(int)R3000aRegister.V0].Should().Be(expectedReturnValue);

        // Control returned to the $ra the call site linked, and the tail ran.
        snapshot.Gpr[(int)R3000aRegister.Ra].Should().Be(Kseg0EntryPc + (CallTailIndex * 4u));
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.Success);
    }

    [Fact]
    public void GeneratedPath_OneArgumentService_UsesOnlyA0_AndProducesTheServicesOutput()
    {
        var sink = new CapturedOutputSink();
        var result = RunGenerated(
            CallProgram(
                Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosHleRuntime.PutCharFunction,
                a0: (uint)'Z', a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu),
            sink);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be((uint)'Z');
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        sink.Bytes.Should().BeEquivalentTo(new byte[] { (byte)'Z' }, static o => o.WithStrictOrdering());
    }

    [Theory]
    // A0:3E puts, its registered B0:3F alias, and the C0 high-range mirror of that
    // same physical slot (C0:BF -> B0:3F) must all reach one implementation with
    // one arity through the generated path, exactly as they do in the interpreter.
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutsFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction)]
    [InlineData(BiosCallFamily.C0, (byte)0xBF)]
    public void GeneratedPath_Puts_ReachesOneImplementation_ThroughEveryRegisteredIdentity(
        BiosCallFamily family, byte function)
    {
        const uint stringAddress = 0x00000400u;
        var sink = new CapturedOutputSink();
        var result = RunGenerated(
            CallProgram(
                Kseg0EntryPc, VectorAddressOf(family), function,
                a0: stringAddress, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu,
                initialMemory: StringBytes(stringAddress, "hi")),
            sink);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(stringAddress);
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), static o => o.WithStrictOrdering());
    }

    // --- PatchedTarget: control actually transfers to the guest routine -------

    [Fact]
    public void GeneratedPath_PatchedEntry_TransfersControlToTheGeneratedBlockAtTheGuestTarget()
    {
        var result = RunGenerated(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        var snapshot = result.Snapshot!;

        // The patched guest routine ran: it is the only thing that writes $s0 or
        // the scratch byte, and it is unreachable on the straight-line path.
        snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);
        snapshot.Memory.Single(observation => observation.Address == ScratchAddress)
            .Value.Should().Be(PatchedRoutineMarker);

        // ...and control came back to the call site's continuation.
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        snapshot.Gpr[(int)R3000aRegister.Ra].Should().Be(Kseg0EntryPc + (ReturnIndex * 4u));
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.Success);

        // The dispatch is visible in the generated block trace at the vector
        // itself, between the call block and the patched routine's own block.
        snapshot.PcTrace.Should().ContainInOrder(
            Kseg0EntryPc + (CallIndex * 4u),
            VectorPcFor(Kseg0EntryPc, BiosJumpTables.A0VectorAddress),
            Kseg0EntryPc + (PatchedRoutineIndex * 4u));
    }

    [Theory]
    // The vectors live in the first page of RAM, so a call through any KUSEG/KSEG
    // alias of 0xA0/0xB0/0xC0 names the same trampoline and must dispatch alike.
    [InlineData(Kseg0EntryPc)] // reaches the vector as 0x800000A0 (KSEG0)
    [InlineData(KusegEntryPc)] // reaches the vector as 0x000000A0 (KUSEG)
    public void GeneratedPath_PatchedEntry_Dispatches_ThroughEveryAliasOfTheTrampolineVector(uint entryPc)
    {
        var result = RunGenerated(PatchProgram(
            entryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
    }

    [Fact]
    public void GeneratedPath_PatchedEntry_OverridesARegisteredServicesOwnSlot()
    {
        var sink = new CapturedOutputSink();
        var result = RunGenerated(
            PatchProgram(
                Kseg0EntryPc, BiosJumpTables.A0VectorAddress,
                BiosCallFamily.A0, BiosHleRuntime.PutCharFunction),
            sink);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);

        // A patched slot never reaches its registered HLE service, so no service
        // side effect may appear: the patch fully overrides the host handler.
        sink.Bytes.Should().BeEmpty();
    }

    // --- Explicit failures: never a silent success (Issue #279) ---------------

    [Fact]
    public void GeneratedPath_PatchedTarget_WithNoGeneratedBlock_StopsAtTheTarget_WithAnAdvisory()
    {
        // A translatable address the program has no block for. The interpreter
        // would fetch whatever MIPS lives there; the generated host has nothing to
        // enter, so control transfers to the target and the run ends there — the
        // same segment-level outcome the interpreter reaches when the target lies
        // outside its program image, which is what lets both backends hand the
        // identical PC to a full-title handoff (Issue #379). The reason the
        // recompiled path could go no further is still reported (Issue #279).
        var unreachableTarget = Kseg0EntryPc + 0x800u;
        var result = RunGenerated(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: unreachableTarget));

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.DiagnosticCode.Should().Be(
            RecompilerHostExecutor.BiosPatchedTargetHasNoGeneratedBlockDiagnosticCode);
        result.DiagnosticMessage.Should().Contain($"0x{unreachableTarget:X8}");
        result.DiagnosticMessage.Should().Contain("#249");

        var snapshot = result.Snapshot!;
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        snapshot.PC.Should().Be(unreachableTarget, "control stops at the address it could not enter");

        // Nothing past the dispatch ran: neither the patched routine nor the tail.
        snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(0u);
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void GeneratedPath_PatchedTarget_OutsideEveryTranslatableRegion_StopsTheRun_WithADiagnostic()
    {
        // KSEG2 translates nowhere, so it is rejected by the shared dispatch
        // before the generated path's own block-table question is even asked.
        const uint untranslatable = 0xC0000000u;
        var result = RunGenerated(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: untranslatable));

        result.DiagnosticCode.Should().Be(BiosVectorDispatch.UntranslatableTargetDiagnosticCode);
        result.DiagnosticMessage.Should().Contain("0xC0000000");
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
    }

    [Fact]
    public void GeneratedPath_UnregisteredService_StopsTheRun_WithTheRuntimesOwnDiagnostic()
    {
        // No patch and no registered service: the Runtime can neither name a guest
        // target nor perform the call, so execution stops rather than continuing
        // past a BIOS call whose effects never happened.
        var result = RunGenerated(CallProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, UnregisteredA0Function));

        result.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.DiagnosticMessage.Should().Contain($"A0:{UnregisteredA0Function:X2}");

        // The trampoline vector is not a guest call-site PC this path tracks, so
        // it is never misreported as one.
        result.DiagnosticMessage.Should().Contain("pc=unknown");

        var snapshot = result.Snapshot!;
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void GeneratedPath_PatchedTarget_LoopingBackIntoTheCall_IsBoundedByTheExecutionBudget()
    {
        // The patch points back at the call block itself, so the guest re-enters
        // the BIOS dispatch forever. A claimed transfer spends a step from the
        // same budget that bounds retired blocks, so the run is cut short exactly
        // as any other unterminated loop is - it does not spin.
        var result = RunGenerated(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: Kseg0EntryPc + (CallIndex * 4u)));

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void WithoutABiosRuntime_ACallToAVector_LeavesTheGeneratedProgramExactlyAsBefore()
    {
        // The hook is opt-in: a generated program run without a Runtime keeps the
        // pre-existing unknown-PC behavior, which is what keeps every existing
        // differential fixture comparing like for like.
        var result = new RecompilerHostExecutor().Execute(CallProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosHleRuntime.PutCharFunction));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    // --- Interpreter / recompiled parity --------------------------------------

    [Fact]
    public void PatchedTargetDispatch_Matches_BetweenTheInterpreterAndTheRecompiledPath()
    {
        AssertParity(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));
    }

    [Fact]
    public void SupportedServiceDispatch_Matches_BetweenTheInterpreterAndTheRecompiledPath()
    {
        const uint stringAddress = 0x00000400u;
        var (interpreterSink, hostSink) = AssertParity(CallProgram(
            Kseg0EntryPc, BiosJumpTables.B0VectorAddress, BiosHleRuntime.PutsAliasFunction,
            a0: stringAddress, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu,
            initialMemory: StringBytes(stringAddress, "hi")));

        // The service's host-visible effect is part of the comparison, not just
        // the architectural state it leaves behind.
        hostSink.Bytes.Should().BeEquivalentTo(interpreterSink.Bytes, static o => o.WithStrictOrdering());
        hostSink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), static o => o.WithStrictOrdering());
    }

    [Fact]
    public void UnresolvedDispatch_Matches_BetweenTheInterpreterAndTheRecompiledPath()
    {
        AssertParity(CallProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, UnregisteredA0Function));
    }

    /// <summary>
    /// Runs one fixture through both executors over the same Runtime configuration
    /// and asserts that GPRs, HI/LO, PC, memory, termination reason and the
    /// diagnostic all agree.
    /// </summary>
    private static (CapturedOutputSink Interpreter, CapturedOutputSink Host) AssertParity(
        RecompilerDifferentialFixture fixture)
    {
        var interpreterSink = new CapturedOutputSink();
        var hostSink = new CapturedOutputSink();

        var result = RecompilerDifferentialRunner.Run(
            fixture,
            new RecompilerInterpreterExecutor(
                (reader, writer) => new BiosHleRuntime(interpreterSink, reader, writer)),
            new RecompilerHostExecutor(
                (reader, writer) => new BiosHleRuntime(hostSink, reader, writer)));

        result.Reference.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Actual.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Diff!.Classification.Should().Be(
            RecompilerComparisonClassification.Match, result.Diff.Describe());

        result.Actual.DiagnosticCode.Should().Be(result.Reference.DiagnosticCode);
        result.Actual.DiagnosticMessage.Should().Be(result.Reference.DiagnosticMessage);

        return (interpreterSink, hostSink);
    }

    // --- Fixtures -------------------------------------------------------------

    // Patch-and-call program layout. The patched routine returns with a static
    // jump so the whole program is representable as generated blocks:
    //
    //   0  LUI  $t0, hi(target)
    //   1  ORI  $t0, $t0, lo(target)
    //   2  ORI  $t2, $zero, slot          slot addresses are all below 0x10000
    //   3  SW   $t0, 0($t2)               the guest patches the jump-table entry
    //   4  ORI  $t1, $zero, function      PS1 ABI: $t1 selects the BIOS function
    //   5  JAL  vector                    links $ra to index 7
    //   6  NOP                            branch delay slot
    //   7  J    tail                      reached only on return from the patch
    //   8  NOP
    //   9  ORI  $s0, $zero, marker        the patched routine, reachable only
    //  10  ORI  $t2, $zero, scratch       through the BIOS dispatch under test
    //  11  J    return                    static return to index 7
    //  12  SB   $s0, 0($t2)               delay slot: observable memory effect
    //  13  ORI  $s1, $zero, returned      the tail

    private const uint CallIndex = 5;
    private const uint ReturnIndex = 7;
    private const uint PatchedRoutineIndex = 9;
    private const uint TailIndex = 13;
    private const uint ProgramLength = 14;

    private static RecompilerDifferentialFixture PatchProgram(
        uint entryPc,
        uint vectorAddress,
        BiosCallFamily family,
        byte function,
        uint? patchedTargetOverride = null)
    {
        var target = patchedTargetOverride ?? (entryPc + (PatchedRoutineIndex * 4u));
        var slot = BiosJumpTables.EntryAddress(family, function);

        var words = new uint[ProgramLength];
        words[0] = MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T0, rs: 0, immediate: (ushort)(target >> 16));
        words[1] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T0, rs: (byte)R3000aRegister.T0, immediate: (ushort)target);
        words[2] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: (ushort)slot);
        words[3] = MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T0, baseRegister: (byte)R3000aRegister.T2, offset: 0);
        words[4] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: function);
        words[CallIndex] = MipsEncoding.JumpAndLink(vectorAddress);
        words[6] = MipsEncoding.Nop;
        words[ReturnIndex] = MipsEncoding.Jump(entryPc + (TailIndex * 4u));
        words[8] = MipsEncoding.Nop;
        words[PatchedRoutineIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: (ushort)PatchedRoutineMarker);
        words[10] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: (ushort)ScratchAddress);
        words[11] = MipsEncoding.Jump(entryPc + (ReturnIndex * 4u));
        words[12] = MipsEncoding.Load(R3000aOpcode.Sb, rt: (byte)R3000aRegister.S0, baseRegister: (byte)R3000aRegister.T2, offset: 0);
        words[TailIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);

        return new RecompilerDifferentialFixture(
            name: "recompiled-bios-patched-target",
            encodedInstructions: words,
            entryPc: entryPc,
            stepBudget: 64,
            memoryWindow: [ScratchAddress],
            referenceStepBudget: 64);
    }

    // Call-only program layout: no patch store, so the slot keeps whatever the
    // Runtime seeded there.
    //
    //   0  JAL  vector                    links $ra to index 2
    //   1  NOP                            branch delay slot
    //   2  ORI  $s1, $zero, returned      the tail

    private const uint CallTailIndex = 2;

    private static RecompilerDifferentialFixture CallProgram(
        uint entryPc,
        uint vectorAddress,
        byte function,
        uint a0 = 0,
        uint a1 = 0,
        uint a2 = 0,
        uint a3 = 0,
        IEnumerable<RecompilerInitialMemoryItem>? initialMemory = null)
    {
        var words = new uint[CallTailIndex + 1];
        words[0] = MipsEncoding.JumpAndLink(vectorAddress);
        words[1] = MipsEncoding.Nop;
        words[CallTailIndex] = MipsEncoding.I(
            OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);

        var initialGpr = new uint[RecompilerDifferentialFixture.GprCount];
        initialGpr[(int)R3000aRegister.T1] = function;
        initialGpr[(int)R3000aRegister.A0] = a0;
        initialGpr[(int)R3000aRegister.A1] = a1;
        initialGpr[(int)R3000aRegister.A2] = a2;
        initialGpr[(int)R3000aRegister.A3] = a3;

        return new RecompilerDifferentialFixture(
            name: "recompiled-bios-call",
            encodedInstructions: words,
            entryPc: entryPc,
            stepBudget: 16,
            initialGpr: initialGpr,
            initialMemory: initialMemory,
            referenceStepBudget: 16);
    }

    private static IEnumerable<RecompilerInitialMemoryItem> StringBytes(uint address, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            yield return new RecompilerInitialMemoryItem(address + (uint)i, (byte)value[i]);
        }

        yield return new RecompilerInitialMemoryItem(address + (uint)value.Length, 0);
    }

    /// <summary>
    /// The PC a <c>jal vector</c> in a program entered at <paramref name="entryPc"/>
    /// actually reaches. A J-format transfer keeps only the top nibble of the
    /// delay slot's PC, so a KSEG0 program calls the KSEG0 alias of the vector and
    /// a KUSEG program calls the raw one.
    /// </summary>
    private static uint VectorPcFor(uint entryPc, uint vectorAddress) =>
        (entryPc & 0xF0000000u) | vectorAddress;

    private static uint VectorAddressOf(BiosCallFamily family) => family switch
    {
        BiosCallFamily.A0 => BiosJumpTables.A0VectorAddress,
        BiosCallFamily.B0 => BiosJumpTables.B0VectorAddress,
        _ => BiosJumpTables.C0VectorAddress,
    };

    private static RecompilerExecutionResult RunGenerated(
        RecompilerDifferentialFixture fixture, CapturedOutputSink? sink = null)
    {
        var outputSink = sink ?? new CapturedOutputSink();
        var executor = new RecompilerHostExecutor(
            (reader, writer) => new BiosHleRuntime(outputSink, reader, writer));
        return executor.Execute(fixture);
    }
}

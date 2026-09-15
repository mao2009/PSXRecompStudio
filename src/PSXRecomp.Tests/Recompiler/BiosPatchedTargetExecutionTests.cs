using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Runtime;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Issue #362: a guest that patches a BIOS A0/B0/C0 jump-table entry must have
/// control actually transferred to the raw guest address it wrote there, not
/// merely observe a <see cref="BiosServiceStatus.PatchedTarget"/> report.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture here is one synthetic MIPS program that patches a slot with its
/// own store instruction — the patch is guest-visible work, never host-side
/// scaffolding — then calls the corresponding trampoline vector and proves the
/// patched routine ran by the architectural state it leaves behind.
/// </para>
/// <para>
/// The program is laid out so the patched routine sits past an unconditional
/// jump the straight-line path takes: the routine is reachable only through the
/// BIOS dispatch under test, so an assertion on its effects cannot pass by the
/// program simply falling through into it.
/// </para>
/// </remarks>
[Test]
public sealed class BiosPatchedTargetExecutionTests
{
    private const uint Kseg0EntryPc = 0x80001000u;
    private const uint KusegEntryPc = 0x00001000u;

    /// <summary>Value the patched routine leaves in <c>$s0</c>, and stores to <see cref="ScratchAddress"/>.</summary>
    private const uint PatchedRoutineMarker = 0x5Au;

    /// <summary>Value the post-return tail leaves in <c>$s1</c>, proving the routine returned through <c>$ra</c>.</summary>
    private const uint ReturnedMarker = 0x77u;

    /// <summary>Guest RAM byte the patched routine writes, well clear of the jump tables and the program.</summary>
    private const uint ScratchAddress = 0x00000C00u;

    /// <summary>An unregistered A0 slot, so its entry starts at zero rather than at an HLE sentinel.</summary>
    private const byte UnregisteredA0Function = 0x10;

    [Fact]
    public void PatchedEntry_TransfersControlToTheGuestTarget_WhichRunsAndReturns()
    {
        var result = Run(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));

        var snapshot = result.Snapshot!;
        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);

        // The patched guest routine ran: it is the only thing that writes $s0 or
        // the scratch byte, and it is unreachable on the straight-line path.
        snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);
        snapshot.Memory.Single(observation => observation.Address == ScratchAddress)
            .Value.Should().Be(PatchedRoutineMarker);

        // ...and it returned through the $ra the original call site linked, so the
        // trampoline was transparent to the return path exactly as on hardware.
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        snapshot.Gpr[(int)R3000aRegister.Ra].Should().Be(Kseg0EntryPc + (ReturnIndex * 4u));
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.Success);

        // The dispatch is visible in the trace at the vector itself, between the
        // call site's delay slot and the first instruction of the patched routine.
        snapshot.PcTrace.Should().ContainInOrder(
            Kseg0EntryPc + (DelaySlotIndex * 4u),
            VectorPcFor(Kseg0EntryPc, BiosJumpTables.A0VectorAddress),
            Kseg0EntryPc + (PatchedRoutineIndex * 4u));
    }

    [Theory]
    // A registered A0 slot: the guest patch overrides the HLE sentinel this
    // Runtime seeded there, and dispatch follows the patch rather than the service.
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutCharFunction)]
    // A registered B0 slot, overridden the same way through the B0 vector.
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction)]
    // The C0 high-range mirror of that same physical B0 slot (C0:BF -> B0:3F):
    // the alias identity reaches the very same patched entry.
    [InlineData(BiosCallFamily.C0, (byte)0xBF)]
    // An unregistered slot, which starts at zero instead of at a sentinel.
    [InlineData(BiosCallFamily.A0, UnregisteredA0Function)]
    public void PatchedEntry_TransfersControl_ForRegisteredAndUnregisteredAndMirroredSlots(
        BiosCallFamily family, byte function)
    {
        var sink = new CapturedOutputSink();
        var result = Run(
            PatchProgram(Kseg0EntryPc, VectorAddressOf(family), family, function),
            sink);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);

        // A patched slot never reaches its registered HLE service, so no service
        // side effect may appear: the patch fully overrides the host handler.
        sink.Bytes.Should().BeEmpty();
    }

    [Theory]
    // The vectors live in the first page of RAM, so a call through any KUSEG/KSEG
    // alias of 0xA0/0xB0/0xC0 names the same trampoline and must dispatch alike.
    [InlineData(Kseg0EntryPc)] // reaches the vector as 0x800000A0 (KSEG0)
    [InlineData(KusegEntryPc)] // reaches the vector as 0x000000A0 (KUSEG)
    public void PatchedEntry_Dispatches_ThroughEveryAliasOfTheTrampolineVector(uint entryPc)
    {
        var result = Run(PatchProgram(
            entryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.S0].Should().Be(PatchedRoutineMarker);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
    }

    [Fact]
    public void PatchedTarget_IsJumpedTo_Verbatim_WithoutSegmentNormalization()
    {
        // The patched entry names the KUSEG alias of the routine this KSEG0
        // program holds. The raw address is what control must move to: the
        // Runtime reports what the guest wrote and this executor jumps there
        // unchanged, so the resulting PC is the alias, not a rewritten KSEG0 form.
        // The differential harness bounds a run to its own entry segment, so the
        // run then ends there rather than continuing - which is exactly what makes
        // the final PC a clean witness of the address control was transferred to.
        var aliasTarget = (Kseg0EntryPc + (PatchedRoutineIndex * 4u)) & 0x1FFFFFFFu;
        var result = Run(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: aliasTarget));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.PC.Should().Be(aliasTarget);
    }

    [Fact]
    public void PatchedTarget_OutsideEveryTranslatableRegion_StopsTheRun_WithADiagnostic()
    {
        // KSEG2 translates nowhere, so there is no guest memory to fetch the
        // target's instructions from. Control must not move to it.
        const uint untranslatable = 0xC0000000u;
        var result = Run(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: untranslatable));

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.DiagnosticCode.Should().Be(
            RecompilerInterpreterExecutor.BiosUntranslatableTargetDiagnosticCode);
        result.DiagnosticMessage.Should().Contain("0xC0000000");

        var snapshot = result.Snapshot!;
        snapshot.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);

        // The PC stayed on the trampoline: nothing was executed at the rejected
        // address, and the patched routine never ran.
        snapshot.PC.Should().Be(VectorPcFor(Kseg0EntryPc, BiosJumpTables.A0VectorAddress));
        snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(0u);
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void UnpatchedUnregisteredEntry_StopsTheRun_WithTheRuntimesOwnDiagnostic()
    {
        // No patch and no registered service: the Runtime can neither name a guest
        // target nor perform the call, so execution stops rather than continuing
        // past a BIOS call whose effects never happened.
        var result = Run(CallOnlyProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, UnregisteredA0Function));

        result.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.DiagnosticMessage.Should().Contain($"A0:{UnregisteredA0Function:X2}");
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);

        // CodeRabbit fresh review Finding B (#364): core.Pc at this point IS the
        // BIOS trampoline vector (0x000000A0), not a guest call-site PC this
        // executor tracks separately, so it must never be reported as one.
        result.DiagnosticMessage.Should().Contain("pc=unknown");
        result.DiagnosticMessage.Should().NotContain("pc=0x000000A0",
            "the trampoline vector address must never be misreported as the guest call-site PC");
    }

    [Fact]
    public void PatchedTarget_LoopingBackIntoTheCall_IsBoundedByTheExecutionBudget()
    {
        // The patch points back at the call site itself, so the guest re-enters the
        // BIOS dispatch forever. Dispatching a vector spends a step from the same
        // budget that bounds ordinary instructions, so the run is cut short exactly
        // as any other unterminated loop is - it does not spin.
        var result = Run(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function,
            patchedTargetOverride: Kseg0EntryPc + (CallIndex * 4u)));

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
    }

    [Fact]
    public void WithoutABiosRuntime_ACallToAVector_LeavesTheProgramExactlyAsBefore()
    {
        // The trap is opt-in: an executor built without a Runtime keeps the
        // pre-existing behavior, which is what keeps every existing differential
        // fixture comparing like for like against the generated host.
        var result = new RecompilerInterpreterExecutor().Execute(PatchProgram(
            Kseg0EntryPc, BiosJumpTables.A0VectorAddress, BiosCallFamily.A0, UnregisteredA0Function));

        result.DiagnosticCode.Should().BeNull();
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Snapshot.PC.Should().Be(VectorPcFor(Kseg0EntryPc, BiosJumpTables.A0VectorAddress));
        result.Snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(0u);
    }

    [Fact]
    public void RecompiledPath_CannotYetDispatchABiosVector_AndSaysSoInTheLowering()
    {
        // Parity marker, deliberately asserting the gap rather than hiding it: the
        // generated-host path has no equivalent of the trap above. A call to a
        // trampoline vector lowers to an ordinary Call flow naming an address the
        // program has no block for, and the host dispatch stops at an unknown PC
        // instead of dispatching - so the recompiled side cannot reach a patched
        // target at all. Closing that needs a host-side Runtime dispatch hook,
        // which is a separate change from this executor's trap.
        var lowered = MipsToIrLowerer.LowerControlTransfer(
            R3000aDecoder.Decode(MipsEncoding.JumpAndLink(BiosJumpTables.A0VectorAddress)),
            Kseg0EntryPc + (CallIndex * 4u),
            R3000aDecoder.Decode(MipsEncoding.Nop));

        lowered.IsSupported.Should().BeTrue();
        lowered.Block!.Exit.Flow!.Kind.Should().Be(RecompilerIrFlowKind.Call);
        lowered.Block.Exit.Flow.Target.Should().Be(VectorPcFor(Kseg0EntryPc, BiosJumpTables.A0VectorAddress));

        // Nothing in the IR marks that target as a BIOS vector, which is precisely
        // why the generated host cannot act on it.
        lowered.Block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
    }

    // --- Live-trap arity (CodeRabbit fresh review on #364, Finding A) -------
    //
    // The live trap must pass a registered service only the argument count it
    // actually expects, read from the Runtime's own arity registry
    // (IBiosRuntime.TryGetServiceArgumentCount) rather than always all four
    // ABI argument registers — otherwise a live call to a registered
    // 0/1-argument service fails BIOS_HLE_INVALID_ARGUMENTS.

    private const uint ArityCallIndex = 0;
    private const uint ArityDelaySlotIndex = 1;
    private const uint ArityTailIndex = 2;
    private const uint ArityProgramLength = 3;

    [Fact]
    public void LiveTrap_GetC0Table_ZeroArgumentService_IsSupported_DespiteNonZeroAbiRegisters()
    {
        var result = RunArityProgram(
            BiosJumpTables.B0VectorAddress, BiosHleRuntime.GetC0TableFunction,
            a0: 0xDEADBEEFu, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(BiosJumpTables.C0TableAddress);
        result.Snapshot.Gpr[(int)R3000aRegister.Ra].Should().Be(Kseg0EntryPc + (ArityTailIndex * 4u));
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        result.Snapshot.Termination.Should().Be(RecompilerIrTerminationReason.Success);
    }

    [Fact]
    public void LiveTrap_GetB0Table_ZeroArgumentService_IsSupported_DespiteNonZeroAbiRegisters()
    {
        var result = RunArityProgram(
            BiosJumpTables.B0VectorAddress, BiosHleRuntime.GetB0TableFunction,
            a0: 0xDEADBEEFu, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(BiosJumpTables.B0TableAddress);
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
    }

    [Fact]
    public void LiveTrap_PutChar_OneArgumentService_UsesOnlyA0_DespiteNonZeroExtraAbiRegisters()
    {
        var sink = new CapturedOutputSink();
        var result = RunArityProgram(
            BiosJumpTables.A0VectorAddress, BiosHleRuntime.PutCharFunction,
            a0: (uint)'Z', a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu, sink: sink);

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be((uint)'Z');
        result.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        sink.Bytes.Should().BeEquivalentTo(new byte[] { (byte)'Z' }, static o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, BiosHleRuntime.PutsFunction)]
    [InlineData(BiosCallFamily.B0, BiosHleRuntime.PutsAliasFunction)]
    public void LiveTrap_Puts_OneArgumentService_UsesOnlyA0_DespiteNonZeroExtraAbiRegisters(
        BiosCallFamily family, byte function)
    {
        const uint stringAddress = 0x00000100u;
        var sink = new CapturedOutputSink();
        var result = RunArityProgram(
            VectorAddressOf(family), function,
            a0: stringAddress, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu,
            sink: sink,
            initialMemory: StringBytes(stringAddress, "hi"));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(stringAddress);
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), static o => o.WithStrictOrdering());
    }

    [Fact]
    public void LiveTrap_C0HighRangeAlias_UsesTheCanonicalArity_AndDispatchesNormally()
    {
        // C0:BF canonicalizes to B0:3F puts (one argument) — the arity lookup
        // must resolve through the same canonical mapping dispatch uses.
        const uint stringAddress = 0x00000400u;
        var sink = new CapturedOutputSink();
        var result = RunArityProgram(
            BiosJumpTables.C0VectorAddress, 0xBF,
            a0: stringAddress, a1: 0xDEADBEEFu, a2: 0xDEADBEEFu, a3: 0xDEADBEEFu,
            sink: sink,
            initialMemory: StringBytes(stringAddress, "hi"));

        result.DiagnosticCode.Should().BeNull(result.DiagnosticMessage);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(stringAddress);
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), static o => o.WithStrictOrdering());
    }

    private static RecompilerExecutionResult RunArityProgram(
        uint vectorAddress,
        byte function,
        uint a0 = 0,
        uint a1 = 0,
        uint a2 = 0,
        uint a3 = 0,
        CapturedOutputSink? sink = null,
        IEnumerable<RecompilerInitialMemoryItem>? initialMemory = null)
    {
        var words = new uint[ArityProgramLength];
        words[ArityCallIndex] = MipsEncoding.JumpAndLink(vectorAddress);
        words[ArityDelaySlotIndex] = MipsEncoding.Nop;
        words[ArityTailIndex] = MipsEncoding.I(
            OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);

        var initialGpr = new uint[RecompilerDifferentialFixture.GprCount];
        initialGpr[(int)R3000aRegister.T1] = function;
        initialGpr[(int)R3000aRegister.A0] = a0;
        initialGpr[(int)R3000aRegister.A1] = a1;
        initialGpr[(int)R3000aRegister.A2] = a2;
        initialGpr[(int)R3000aRegister.A3] = a3;

        var fixture = new RecompilerDifferentialFixture(
            name: "bios-live-trap-arity",
            encodedInstructions: words,
            entryPc: Kseg0EntryPc,
            stepBudget: 16,
            initialGpr: initialGpr,
            initialMemory: initialMemory,
            referenceStepBudget: 16);

        return Run(fixture, sink);
    }

    private static IEnumerable<RecompilerInitialMemoryItem> StringBytes(uint address, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            yield return new RecompilerInitialMemoryItem(address + (uint)i, (byte)value[i]);
        }

        yield return new RecompilerInitialMemoryItem(address + (uint)value.Length, 0);
    }

    // --- Program layout -----------------------------------------------------
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
    //  11  JR   $ra
    //  12  SB   $s0, 0($t2)               delay slot: observable memory effect
    //  13  ORI  $s1, $zero, returned      the tail

    private const uint CallIndex = 5;
    private const uint DelaySlotIndex = 6;
    private const uint ReturnIndex = 7;
    private const uint PatchedRoutineIndex = 9;
    private const uint TailIndex = 13;
    private const uint ProgramLength = 14;

    private const byte LuiOpcodeField = 0x0F;
    private const byte OriOpcodeField = 0x0D;

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
        words[DelaySlotIndex] = MipsEncoding.Nop;
        words[ReturnIndex] = MipsEncoding.Jump(entryPc + (TailIndex * 4u));
        words[8] = MipsEncoding.Nop;
        words[PatchedRoutineIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: (ushort)PatchedRoutineMarker);
        words[10] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: (ushort)ScratchAddress);
        words[11] = MipsEncoding.JumpRegister((byte)R3000aRegister.Ra);
        words[12] = MipsEncoding.Load(R3000aOpcode.Sb, rt: (byte)R3000aRegister.S0, baseRegister: (byte)R3000aRegister.T2, offset: 0);
        words[TailIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);

        return Fixture(words, entryPc);
    }

    /// <summary>The same call site with no patch store, so the slot keeps whatever the Runtime seeded.</summary>
    private static RecompilerDifferentialFixture CallOnlyProgram(uint entryPc, uint vectorAddress, byte function)
    {
        var words = new uint[ProgramLength];
        Array.Fill(words, MipsEncoding.Nop);
        words[4] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: function);
        words[CallIndex] = MipsEncoding.JumpAndLink(vectorAddress);
        words[TailIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);
        return Fixture(words, entryPc);
    }

    private static RecompilerDifferentialFixture Fixture(uint[] words, uint entryPc) =>
        new(
            name: "bios-patched-target",
            encodedInstructions: words,
            entryPc: entryPc,
            stepBudget: 64,
            memoryWindow: [ScratchAddress],
            referenceStepBudget: 64);

    private static RecompilerExecutionResult Run(
        RecompilerDifferentialFixture fixture, CapturedOutputSink? sink = null)
    {
        var outputSink = sink ?? new CapturedOutputSink();
        var executor = new RecompilerInterpreterExecutor(
            (reader, writer) => new BiosHleRuntime(outputSink, reader, writer));
        return executor.Execute(fixture);
    }
}

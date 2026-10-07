using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:17 ReturnFromException, reached the way a title reaches it: a call to the B0 trampoline.
// It is the kernel's exception-return operation, so it restores the interrupted context from the
// current TCB, RFEs, and continues at the saved EPC instead of returning to the call's $ra.
// psx-spx kernelbios ReturnFromException, control-blocks. Issue #664.
[Test]
public sealed class BiosReturnFromExceptionTests
{
    private const uint EntryPc = 0x80001000u;

    // The TCB layout is the guest RAM the kernel itself writes (psx-spx control-blocks), mirrored from
    // BiosExceptionCompletionTests so both sides of the boundary agree on where the context lives.
    private const uint Pcb = 0x0000E100u;
    private const uint Tcb = 0x0000E200u;
    private const uint SavedEpc = EntryPc + (EpcIndex * 4u);

    private const uint SavedHi = 0x11111111u;
    private const uint SavedLo = 0x22222222u;

    /// <summary>An SR with one level pushed, so the RFE pop below it has a visible effect.</summary>
    private const uint SavedSr = 0x40000404u;
    private const uint SavedRa = 0xDEAD0001u;

    /// <summary>A saved <c>$sp</c> distinct from every other slot, so the restore is visible in a snapshot.</summary>
    private const uint SavedSp = 0x0000FFFFu;

    private const uint SavedS0 = 0x00C0FFEEu;

    /// <summary>The live <c>$k0</c> the kernel left behind: a restore must not overwrite it.</summary>
    private const uint LiveK0 = 0x0000C0DEu;

    private const uint TailMarker = 0x77u;

    /// <summary><c>$ra</c> marks a mistake: this call returns to the EPC, never to the call site.</summary>
    private const uint RaMarker = 0xAAu;

    // Program layout. The call site and the restored EPC are distinguishable by their markers, so a
    // return to $ra cannot pass for a return to the EPC:
    //
    //   0  JAL  B0 vector            links $ra to index 2
    //   1  NOP                      branch delay slot
    //   2  ORI  $s2, $zero, $aa      runs only if control wrongly returned to $ra
    //   3  J    epc                 makes the EPC's block statically reachable
    //   4  NOP                      branch delay slot
    //   5  ORI  $s1, $zero, $77      the restored EPC's own first instruction
    //   6  J    out                 leave the image
    //   7  NOP                      branch delay slot
    private const uint EpcIndex = 5;
    private const uint ProgramLength = 8;

    /// <summary>The address both executors end at: the <c>J 0</c> at the image's end.</summary>
    private const uint ExitPc = 0x80000000u;

    // --- The Runtime service ---------------------------------------------------

    [Fact]
    public void ReturnFromException_IsRegisteredWithNoArguments()
    {
        var runtime = new TestRuntime();

        runtime.Service.TryGetServiceArgumentCount(
            BiosCallFamily.B0, BiosHleRuntime.ReturnFromExceptionFunction, out var argumentCount)
            .Should().BeTrue();

        // It takes nothing: the context it restores is the TCB's, not the ABI registers'.
        argumentCount.Should().Be(0);
    }

    [Fact]
    public void ReturnFromException_ReplacesTheCpuState_AndContinuesAtTheSavedEpc()
    {
        var runtime = new TestRuntime();
        runtime.CurrentTcb(SavedEpc);
        var gpr = CallRegisters();

        var outcome = BiosVectorDispatch.Dispatch(runtime.Service, BiosCallFamily.B0, gpr);

        outcome.ContinueExecution.Should().BeTrue(outcome.DiagnosticMessage);
        outcome.DiagnosticCode.Should().BeNull();
        outcome.NextPc.Should().Be(SavedEpc, "the saved EPC resumes the interrupted code, not the call's $ra");
        outcome.IsPatchedTarget.Should().BeFalse();

        // $v0 comes from the restored register file; there is no separate return value to write.
        outcome.ReturnValue.Should().BeNull();

        outcome.CpuState.Should().NotBeNull();
        var cpuState = outcome.CpuState!;
        cpuState.NextPc.Should().Be(SavedEpc);
        cpuState.Hi.Should().Be(SavedHi);
        cpuState.Lo.Should().Be(SavedLo);
        cpuState.RestoredSr.Should().Be(SavedSr, "a non-null SR means the restore is followed by the RFE pop");
        cpuState.Gpr[(int)R3000aRegister.Zero].Should().Be(0u, "$v0 keeps the restored file's value");
        cpuState.Gpr[(int)R3000aRegister.V0].Should().Be(SavedSlot((int)R3000aRegister.V0));
        cpuState.Gpr[(int)R3000aRegister.S0].Should().Be(SavedS0);
        cpuState.Gpr[(int)R3000aRegister.Sp].Should().Be(SavedSp);
        cpuState.Gpr[(int)R3000aRegister.Ra].Should().Be(SavedRa);
        cpuState.Gpr[(int)R3000aRegister.K0].Should().Be(LiveK0, "the kernel's own $k0 survives the restore");
    }

    [Fact]
    public void ReturnFromException_LeavesTheCallersRegistersUntouched_AndDispatchNeverAsksForAReturnValue()
    {
        var runtime = new TestRuntime();
        runtime.CurrentTcb(SavedEpc);
        var gpr = CallRegisters();
        var before = (uint[])gpr.Clone();

        BiosVectorDispatch.Dispatch(runtime.Service, BiosCallFamily.B0, gpr).CpuState.Should().NotBeNull();

        // The service builds its replacement from a clone: dispatch's own register file is never
        // written, so a rejected or completed call provably mutates nothing in place.
        gpr.Should().Equal(before);
    }

    // --- Fail-closed ----------------------------------------------------------

    [Fact]
    public void ReturnFromException_WithoutAKernelInitialisedPcb_FailsClosed()
    {
        var runtime = new TestRuntime();
        var gpr = CallRegisters();
        var before = (uint[])gpr.Clone();

        var outcome = BiosVectorDispatch.Dispatch(runtime.Service, BiosCallFamily.B0, gpr);

        outcome.ContinueExecution.Should().BeFalse("a BIOS-less run has no context to restore");
        outcome.CpuState.Should().BeNull();
        outcome.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        gpr.Should().Equal(before, "an all-or-nothing restore never applies a partial one");
    }

    [Theory]
    [InlineData(0x00800000u - 0x40u)]  // TCB straddles the RAM window end
    [InlineData(0xFFFFFFFCu)]          // TCB + 08h would wrap
    public void ReturnFromException_WithAnUnreadableTcb_FailsClosedWithoutPartialRestore(uint tcb)
    {
        var runtime = new TestRuntime();
        runtime.Write(BiosExceptionCompletion.ProcessControlBlockPointerAddress, Pcb, 4);
        runtime.Write(Pcb, tcb);
        var gpr = CallRegisters();
        var before = (uint[])gpr.Clone();

        var outcome = BiosVectorDispatch.Dispatch(runtime.Service, BiosCallFamily.B0, gpr);

        outcome.ContinueExecution.Should().BeFalse();
        outcome.CpuState.Should().BeNull();
        outcome.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        gpr.Should().Equal(before);
    }

    // --- Applying the replacement ---------------------------------------------

    [Fact]
    public void TheCpuStateReplacement_WritesRegistersHiLoSrAndTheContinuationPc_AndRfePopsTheInterruptStack()
    {
        using var core = new PSXCoreWrapper();
        for (var r = 0; r < PSXCoreWrapper.GprCount; r++)
        {
            core.SetGpr(r, 0x2000u + ((uint)r * 4u));
        }

        core.Hi = 0xAAAAAAABu;
        core.Lo = 0xBBBBAAABu;
        core.SetCop0(12, 0u);
        var pushed = core.GetCop0(12);
        core.SetCop0(12, pushed | 0x00010000u);

        var restored = SavedRegisters();
        restored[(int)R3000aRegister.K0] = LiveK0;
        new BiosCpuStateMutation(restored, SavedHi, SavedLo, SavedSr, SavedEpc).ApplyTo(core);

        for (var r = 1; r < PSXCoreWrapper.GprCount; r++)
        {
            core.GetGpr(r).Should().Be(restored[r], $"r{r}");
        }

        core.Hi.Should().Be(SavedHi);
        core.Lo.Should().Be(SavedLo);
        core.Pc.Should().Be(SavedEpc);

        // What the CPU's own RFE makes of "write this SR, then pop" — the exact bit semantics belong to
        // the native CPU, so the expectation is read off it rather than restated here. What matters is
        // that the result is not simply the SR the restore wrote: that is the pop, not a plain SR store.
        using var oracle = new PSXCoreWrapper();
        oracle.SetCop0(12, 0u);
        oracle.SetCop0(12, oracle.GetCop0(12) | 0x00010000u);
        oracle.SetCop0(12, SavedSr);
        oracle.PopExceptionSrStack();

        core.GetCop0(12).Should().Be(oracle.GetCop0(12));
        core.GetCop0(12).Should().NotBe(SavedSr, "the pop is what makes this an RFE rather than a plain SR write");
    }

    [Fact]
    public void TheCpuStateReplacement_WithoutARestoredSr_LeavesTheInterruptStackAlone()
    {
        using var core = new PSXCoreWrapper();
        core.SetCop0(12, 0x00400004u);

        new BiosCpuStateMutation(new uint[PSXCoreWrapper.GprCount], 0, 0, RestoredSr: null, NextPc: SavedEpc)
            .ApplyTo(core);

        core.GetCop0(12).Should().Be(0x00400004u, "only a service that restored SR pops the stack");
    }

    // --- Both execution forms -------------------------------------------------

    [Fact]
    public void BothExecutionForms_RestoreTheContextAndResumeAtTheEpc_WithTheSameState()
    {
        var fixture = ReturnFromExceptionProgram();

        var host = new RecompilerHostExecutor(
            (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer))
            .Execute(fixture);

        host.DiagnosticCode.Should().BeNull(host.DiagnosticMessage);
        host.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Success);

        var gpr = host.Snapshot.Gpr;
        gpr[(int)R3000aRegister.S1].Should().Be(TailMarker, "the restored EPC's own instruction ran");
        gpr[(int)R3000aRegister.S2].Should().Be(0u, "control never returned to the call site's $ra");
        gpr[(int)R3000aRegister.S0].Should().Be(SavedS0);
        gpr[(int)R3000aRegister.Sp].Should().Be(SavedSp);
        gpr[(int)R3000aRegister.Ra].Should().Be(SavedRa);
        gpr[(int)R3000aRegister.V0].Should().Be(SavedSlot((int)R3000aRegister.V0));
        gpr[(int)R3000aRegister.K0].Should().Be(LiveK0);
        host.Snapshot.HI.Should().Be(SavedHi);
        host.Snapshot.LO.Should().Be(SavedLo);
        host.Snapshot.PC.Should().Be(ExitPc);

        var differential = RecompilerDifferentialRunner.Run(
            fixture,
            new RecompilerInterpreterExecutor(
                (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer)),
            new RecompilerHostExecutor(
                (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer)));

        differential.Reference.Status.Should().Be(RecompilerExecutionStatus.Completed);
        differential.Actual.Status.Should().Be(RecompilerExecutionStatus.Completed);
        differential.Diff!.Classification.Should().Be(RecompilerComparisonClassification.Match, differential.Diff.Describe());
        differential.Actual.DiagnosticCode.Should().Be(differential.Reference.DiagnosticCode);
        differential.Actual.DiagnosticMessage.Should().Be(differential.Reference.DiagnosticMessage);
    }

    [Fact]
    public void BothExecutionForms_FailClosedOnAnAbsentTcb_AndStopWithTheSameDiagnostic()
    {
        var fixture = ReturnFromExceptionProgram(seedTcb: false);

        var host = new RecompilerHostExecutor(
            (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer))
            .Execute(fixture);

        host.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_STATE", host.DiagnosticMessage);
        host.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        host.Snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u, "nothing was restored and nothing continued");
        host.Snapshot.Gpr[(int)R3000aRegister.S2].Should().Be(0u);

        var differential = RecompilerDifferentialRunner.Run(
            fixture,
            new RecompilerInterpreterExecutor(
                (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer)),
            new RecompilerHostExecutor(
                (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer)));

        differential.Reference.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        differential.Actual.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        differential.Actual.DiagnosticMessage.Should().Be(differential.Reference.DiagnosticMessage);
        differential.Diff!.Classification.Should().Be(RecompilerComparisonClassification.Match, differential.Diff.Describe());
    }

    // --- Fixtures -------------------------------------------------------------

    /// <summary>What <see cref="SavedRegisters"/> writes into slot <c>r</c>, before the per-register rules.</summary>
    private static uint SavedSlot(int register) => 0x1000u + ((uint)register * 0x11u);

    /// <summary>
    /// The current TCB's saved context. Every register gets a distinguishable value so a partial
    /// restore cannot pass, except the two that carry a rule: <c>$k0</c> must lose to the live
    /// register, and <c>$s2</c> is zero so the snapshot can tell "returned to $ra" from
    /// "resumed at the EPC".
    /// </summary>
    private static uint[] SavedRegisters()
    {
        var saved = new uint[RecompilerDifferentialFixture.GprCount];
        for (var r = 0; r < saved.Length; r++)
        {
            saved[r] = 0x1000u + ((uint)r * 0x11u);
        }

        saved[(int)R3000aRegister.K0] = 0xBAD0000u;
        saved[(int)R3000aRegister.S2] = 0u;
        saved[(int)R3000aRegister.Ra] = SavedRa;
        saved[(int)R3000aRegister.S0] = SavedS0;
        saved[(int)R3000aRegister.Sp] = 0x0000FFFFu;
        return saved;
    }

    /// <summary>The register file at the transfer: <c>$t1</c> selects the function, nothing else matters.</summary>
    private static uint[] CallRegisters()
    {
        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.ReturnFromExceptionFunction;
        gpr[(int)R3000aRegister.K0] = LiveK0;
        gpr[(int)R3000aRegister.Ra] = 0x80001234u;
        return gpr;
    }

    private static RecompilerDifferentialFixture ReturnFromExceptionProgram(bool seedTcb = true)
    {
        var words = new uint[ProgramLength];
        words[0] = MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress);
        words[1] = MipsEncoding.Nop;
        words[2] = MipsEncoding.I(0x0D, rt: (byte)R3000aRegister.S2, rs: 0, immediate: (ushort)RaMarker);

        // The EPC's block has to be one the generated host actually compiled. Nothing reaches it
        // through the call's own $ra, so the $ra path jumps there explicitly: that keeps it inside the
        // statically reachable set while leaving the two continuations distinguishable by $s2.
        words[3] = MipsEncoding.Jump(EntryPc + (EpcIndex * 4u));
        words[4] = MipsEncoding.Nop;
        words[EpcIndex] = MipsEncoding.I(0x0D, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)TailMarker);

        // Out of the image, so the run ends the same way on both executors.
        words[ProgramLength - 2] = MipsEncoding.Jump(0u);
        words[ProgramLength - 1] = MipsEncoding.Nop;

        var initialGpr = new uint[RecompilerDifferentialFixture.GprCount];
        initialGpr[(int)R3000aRegister.T1] = BiosHleRuntime.ReturnFromExceptionFunction;
        initialGpr[(int)R3000aRegister.K0] = LiveK0;

        return new RecompilerDifferentialFixture(
            name: "recompiled-bios-return-from-exception",
            encodedInstructions: words,
            entryPc: EntryPc,
            stepBudget: 16,
            initialGpr: initialGpr,
            initialMemory: seedTcb ? CurrentTcbMemory() : null,
            referenceStepBudget: 16);
    }

    private static List<RecompilerInitialMemoryItem> CurrentTcbMemory()
    {
        var items = new List<RecompilerInitialMemoryItem>();
        Add(items, BiosExceptionCompletion.ProcessControlBlockPointerAddress, Pcb);
        Add(items, Pcb, Tcb);
        Add(items, Tcb + 0x00, 0x00004000u);
        Add(items, Tcb + 0x04, 0x00001000u);

        var saved = SavedRegisters();
        for (var r = 0; r < saved.Length; r++)
        {
            Add(items, Tcb + 0x08 + ((uint)r * 4u), saved[r]);
        }

        Add(items, Tcb + 0x88, SavedEpc);
        Add(items, Tcb + 0x8C, SavedHi);
        Add(items, Tcb + 0x90, SavedLo);
        Add(items, Tcb + 0x94, SavedSr);
        return items;
    }

    private static void Add(List<RecompilerInitialMemoryItem> items, uint address, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i < bytes.Length; i++)
        {
            items.Add(new RecompilerInitialMemoryItem(address + (uint)i, bytes[i]));
        }
    }

    /// <summary>A Runtime over guest RAM the test seeds, exposing the little RAM writes the boundary needs.</summary>
    private sealed class TestRuntime
    {
        private readonly RecompilerGuestMemory _ram = new();

        public BiosHleRuntime Service { get; }

        public TestRuntime()
        {
            Service = new BiosHleRuntime(
                new CapturedOutputSink(),
                new GuestMemoryReader(_ram.Read8),
                new GuestMemoryWriter(_ram.Write8));
        }

        public void Write(uint address, params uint[] words) =>
            new GuestMemoryWriter(_ram.Write8).TryWrite(address, words.SelectMany(BitConverter.GetBytes).ToArray())
                .Should().BeTrue();

        /// <summary>Makes this TCB the current one, exactly as the kernel's own save would.</summary>
        public void CurrentTcb(uint savedEpc)
        {
            Write(BiosExceptionCompletion.ProcessControlBlockPointerAddress, Pcb, 4);
            Write(Pcb, Tcb);
            Write(Tcb, 0x00004000u, 0x00001000u);

            var saved = SavedRegisters();
            Write(Tcb + 0x08, saved);
            Write(Tcb + 0x88, savedEpc, SavedHi, SavedLo, SavedSr, 0xCA05E000u);
        }

    }

}

using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #680: a pending + enabled device IRQ becomes a hardware INT exception in the
/// generated-host CPU state, at a dispatch boundary, with the R3000A COP0 entry the
/// interpreter's CPU performs — and nothing else: no guest handler is called, the run
/// stops at the exception vector where the kernel exception path would start.
/// </summary>
[Test]
public sealed class RecompiledArtifactInterruptTests
{
    private const uint Entry = 0x80001000u;
    private const uint VectorBev0 = 0x80000080u;
    private const uint VectorBev1 = 0xBFC00180u;
    private const uint InterruptMask = 0x1F801074u;
    private const uint Timer2Mode = 0x1F801124u;
    private const uint Timer2Target = 0x1F801128u;
    private const uint Timer2Irq = 1u << (DeviceScheduler.Timer0Irq + 2);

    private const uint SrIec = 0x1u;      // SR bit 0 (R3000A IEc; docs/cpu/cop0.md, ADR-014)
    private const uint SrKuc = 0x2u;      // SR bit 1 (KUc): never an interrupt enable (Issue #684)
    private const uint SrIm2 = 0x400u;
    private const uint SrBev = 0x400000u;
    private const uint CauseIp2 = 0x400u;

    private const R3000aRegister T0 = R3000aRegister.T0;
    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    private static uint Nop => MipsEncoding.Nop;

    /// <summary>$a0 = SYS(02h) ExitCriticalSection; SYSCALL — the Runtime then leaves SR.IEc | SR.IM2 set.</summary>
    private static uint[] ExitCriticalSection() =>
        [Ori(R3000aRegister.A0, Zero, (ushort)BiosKernelSyscall.ExitCriticalSection), MipsEncoding.Syscall()];

    private static uint[] BranchToSelf(uint address) =>
        [MipsEncoding.Branch(0x04, (byte)Zero, (byte)Zero, address, address), Nop];

    /// <summary>
    /// The exception-frame SR whose RFE pop (the artifact's own, after a serviced SYSCALL) leaves
    /// <paramref name="sr"/>: what the Runtime's kernel contract returns for SYS 02h is a frame value.
    /// </summary>
    private static uint Frame(uint sr) => (sr & ~0xFu) | ((sr & 0x3u) << 2);

    private static string AlwaysAsserted(ulong _) => RecompiledArtifactCodeGen.ProtocolRetiredAckInterruptReply;

    // ---- the entry itself, on a scripted parent (exact COP0 values) -------------------

    [Theory]
    [InlineData(SrIec | SrIm2, VectorBev0, 0x00000404u)]                     // SR.BEV = 0: the RAM vector
    [InlineData(SrIec | SrIm2 | SrBev, VectorBev1, 0x00400404u)]             // SR.BEV = 1: the BIOS-ROM vector
    [InlineData(SrIec | SrIm2 | 0x0000FF00u, VectorBev0, 0x0000FF04u | SrIm2)] // other IM bits and the rest of SR survive the push
    public void AssertedLine_WithIecAndIm2_EntersTheGeneralExceptionVector_WithR3000aCop0State(uint sr, uint vector, uint pushedSr)
    {
        // syscall, then the INT boundary: EPC is the instruction after the SYSCALL (the one about to be fetched).
        var words = Program(ExitCriticalSection(), [Ori(T1, Zero, 5)]);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 0", AlwaysAsserted, syscallSr: Frame(sr));

        run.HasSnapshot.Should().BeTrue();
        var epc = Entry + 8u; // ori a0 (0), syscall (4) -> resumes at 8
        run.Snapshot!["pc"].Should().Be(vector, "control transferred to the exception vector");
        run.Snapshot["cop0.epc"].Should().Be(epc, "EPC is the interrupted instruction, outside a delay slot");
        run.Snapshot["cop0.cause"].Should().Be(CauseIp2, "Excode INT (0), BD 0, IP2 asserted");
        run.Snapshot["cop0.sr"].Should().Be(pushedSr, "the KU/IE stack was pushed and IEc cleared");
        G(run, T1).Should().Be(0u, "the interrupted instruction did not execute; no instruction retired for the INT");
    }

    [Theory]
    [InlineData(SrIec | SrIm2)]                 // SR = 0x401: SYS(02h)'s value
    [InlineData(SrIec | SrIm2 | SrBev)]         // BIOS-ROM vector
    [InlineData(SrIec | SrKuc | SrIm2 | 0xFF00u)] // KUc and the other IM bits ride along in the push
    public void Entry_MatchesTheInterpretersNativeCpu_ForTheSameSrAndLine(uint sr)
    {
        // Issue #684: both CPUs read IEc from SR bit 0, so the same SR gives the same INT entry:
        // vector pc, EPC, CAUSE (Excode, BD, IP2) and the pushed SR stack.
        using var core = new PSXCoreWrapper();
        core.WriteInterruptControllerRegister(InterruptMask, 1u << 6);
        core.RaiseInterrupt(6);
        core.SetCop0(12, sr);
        core.Pc = Entry + 8u;
        core.Step().Should().Be(0);
        core.ExceptionRaised.Should().BeTrue();
        core.ExceptionCode.Should().Be(0u);

        using var dir = new TempDirectory();
        Run(Program(ExitCriticalSection(), [Ori(T1, Zero, 5)]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", AlwaysAsserted, syscallSr: Frame(sr));

        run.Snapshot!["pc"].Should().Be(core.Pc);
        run.Snapshot["cop0.epc"].Should().Be(core.GetCop0(14));
        run.Snapshot["cop0.cause"].Should().Be(core.GetCop0(13));
        run.Snapshot["cop0.sr"].Should().Be(core.GetCop0(12));
    }

    [Theory]
    [InlineData(SrIec | SrIm2, true, true)]   // SR = 0x401 + asserted line: accepted
    [InlineData(SrKuc | SrIm2, true, false)]  // KUc (bit 1) without IEc (bit 0): rejected
    [InlineData(SrIec, true, false)]          // IM2 clear: rejected
    [InlineData(SrIec | SrIm2, false, false)] // line not asserted: rejected
    public void InterpreterNativeCpu_AcceptsInt_OnlyWithIecBit0_Im2_AndAnAssertedLine(uint sr, bool asserted, bool accepted)
    {
        using var core = new PSXCoreWrapper();
        core.WriteInterruptControllerRegister(InterruptMask, 1u << 6);
        if (asserted) core.RaiseInterrupt(6);
        core.SetCop0(12, sr);
        core.Pc = Entry;

        core.Step().Should().Be(0);

        core.ExceptionRaised.Should().Be(accepted);
        core.Pc.Should().Be(accepted ? VectorBev0 : Entry + 4u);
    }

    [Fact]
    public void Entry_AtABranch_IsAtTheBranch_NeverBetweenItAndItsDelaySlot()
    {
        // The SYSCALL resumes at a BEQ + delay slot. The INT is taken before the pair: EPC is the BEQ,
        // CAUSE.BD stays 0, and the delay slot has not executed (it would be re-run after the return).
        var words = Program(
            ExitCriticalSection(),
            [MipsEncoding.Branch(0x04, (byte)Zero, (byte)Zero, Entry + 16u, Entry + 16u)],
            [Ori(T1, Zero, 9)], // delay slot
            [Nop]);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 0", AlwaysAsserted, syscallSr: Frame(SrIec | SrIm2));

        run.Snapshot!["cop0.epc"].Should().Be(Entry + 8u, "EPC is the branch itself");
        (run.Snapshot["cop0.cause"] & 0x80000000u).Should().Be(0u, "not taken from a delay slot, so BD is clear");
        G(run, T1).Should().Be(0u, "the delay slot did not execute");
    }

    // ---- acceptance conditions -----------------------------------------------------------

    [Theory]
    [InlineData(SrIm2, "I")]                 // IEc clear: interrupts disabled
    [InlineData(SrKuc | SrIm2, "I")]         // KUc (bit 1) is not IEc
    [InlineData(SrIec, "I")]                 // IM2 clear: the line is masked
    [InlineData(SrIec | SrIm2, "A")]         // nothing pending
    [InlineData(SrIec | 0x0000FB00u, "I")]   // every IM bit but IM2
    public void AssertedLine_WithoutEveryEnable_IsNotAccepted(uint sr, string ack)
    {
        var words = Program(ExitCriticalSection(), [Ori(T1, Zero, 5)]);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);

        var run = RunScripted(dir, _ => "V 0", _ => ack, syscallSr: Frame(sr));

        run.HasSnapshot.Should().BeTrue();
        G(run, T1).Should().Be(5u, "the guest ran straight on");
        run.Snapshot!["cop0.epc"].Should().Be(0u);
        (run.Snapshot["cop0.cause"] & 0x7Cu).Should().Be(0u);
        run.Snapshot["pc"].Should().NotBe(VectorBev0);
    }

    [Fact]
    public void AssertedLine_WithNoBudgetLeft_TakesNoException()
    {
        // The two units (ORI, SYSCALL) spend the whole budget: the boundary hook is gated by the same
        // budget guard as every block, so no exception entry mutates the state past the strict bound.
        var words = Program(ExitCriticalSection(), [Ori(T1, Zero, 5)]);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false, segmentBudget: 2);

        var run = RunScripted(dir, _ => "V 0", AlwaysAsserted, syscallSr: Frame(SrIec | SrIm2));

        run.Snapshot!["pc"].Should().Be(Entry + 8u, "stopped at the budget, before the INT boundary");
        run.Snapshot["cop0.epc"].Should().Be(0u);
        (run.Snapshot["cop0.sr"] & SrIec).Should().Be(SrIec, "SR still holds the serviced SYSCALL's value, no INT push");
    }

    // ---- device event -> pending IRQ -> INT entry, on the real Runtime device graph ----------

    /// <summary>
    /// Enables interrupts through the kernel contract (SYS 02h), arms Timer 2 to raise IRQ6 at counter
    /// 100 and unmasks it in I_MASK (when <paramref name="unmask"/>), then spins.
    /// </summary>
    private static uint[] TimerIrqProgram(bool enableCpu, bool unmask)
    {
        var armed = Program(
            enableCpu ? ExitCriticalSection() : [Nop, Nop],
            [Lui(T0, 0x1F80)],
            unmask ? [Ori(T1, Zero, 1 << 6), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(InterruptMask & 0xFFFF))] : [Nop, Nop],
            [Ori(T1, Zero, 100), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Target & 0xFFFF))],
            [Ori(T1, Zero, 0x10), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Mode & 0xFFFF))]);
        var spin = Entry + (uint)(armed.Length - 1) * 4u; // overwrite the appended nop with the loop
        return [.. armed[..^1], .. BranchToSelf(spin)];
    }

    [Fact]
    public void Timer2Irq_GuestCyclesToSchedulerToIStat_EntersTheExceptionVector_AndStopsThereClassified()
    {
        using var dir = new TempDirectory();

        var result = Run(TimerIrqProgram(enableCpu: true, unmask: true), dir, withRuntime: true, segmentBudget: 100_000);

        result.FinalSnapshot.Should().NotBeNull(result.DiagnosticMessage);
        result.FinalSnapshot!.PC.Should().Be(VectorBev0, "the INT entered the general exception vector");
        // Issue #662: the unpopulated vector is the Runtime's kernel exception handler, not a bare dead end. It
        // saved the context and walked the chain up to the first element the Runtime does not model.
        result.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        result.DiagnosticMessage.Should().Contain("EPC=0x8000").And.Contain("CAUSE=0x00000400")
            .And.Contain("I_STAT=0x0040").And.Contain("pendingEnabled=0x0040");
        result.State.Should().Be(TitleExecutionState.RuntimeFailure, "an unmodelled kernel chain element fails closed");
    }

    // ---- the kernel exception handler (Issue #662) -------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void TimerIrqInsideReportInterval_RetiresOnlyTheInterpretersDelaySlots(ushort target)
    {
        var words = TimerIrqProgram(enableCpu: true, unmask: true);
        words[5] = Ori(T1, Zero, target);
        words[^1] = MipsEncoding.I(0x09, 10, 10, 1); // ADDIU t2,t2,1 in the spin's delay slot
        using var dir = new TempDirectory();
        ulong? retired = null;
        var result = Run(words, dir, withRuntime: true, segmentBudget: 100_000,
            observe: engine => retired = engine.NativeRetiredInstructions);
        result.FinalSnapshot.Should().NotBeNull();
        result.FinalSnapshot!.PC.Should().Be(VectorBev0);
        using var core = new PSXCoreWrapper();
        var spin = Entry + 36u;
        core.WriteMemory32(spin & 0x1FFFFFFFu, words[^2]);
        core.WriteMemory32((spin + 4) & 0x1FFFFFFFu, words[^1]);
        core.Pc = spin;
        core.SetCop0(12, SrIec | SrIm2);
        core.WriteInterruptControllerRegister(InterruptMask, Timer2Irq);
        core.WriteTimerRegister(Timer2Target, target);
        core.WriteTimerRegister(Timer2Mode, 0x10);
        using var interrupts = new PSXRecomp.Core.Dma.InterruptControllerMmioAdapter(core);
        var scheduler = new DeviceScheduler(core, interrupts);
        scheduler.Advance(1); // retirement of the arming SW
        for (var i = 0; i < target + 2; i++)
        {
            core.Step().Should().Be(0);
            if (core.ExceptionRaised) break;
            scheduler.Advance(1);
        }
        core.ExceptionRaised.Should().BeTrue();
        retired.Should().Be(8 + scheduler.ElapsedCycles, "the arming sequence and native spin must have exactly the reference retirement count");
        result.FinalSnapshot.Gpr[10].Should().Be(core.GetGpr(10),
            "both CPUs accept IRQ at the same fetch boundary after any owed branch delay slot");
        result.DiagnosticMessage.Should().Contain($"EPC=0x{core.GetCop0(14):X8}");
    }

    [Fact]
    public void IrqAfterFusedLoad_CommitsPendingLoadBeforeObserverAndKeepsEpcAtObserver()
    {
        var words = Program(ExitCriticalSection(),
            [Lui(T0, 0x8000), Ori(T0, T0, 0x1000), Mem(R3000aOpcode.Lw, T1, T0, 0),
             MipsEncoding.I(0x09, (byte)T1, (byte)T1, 1), Ori(R3000aRegister.T2, Zero, 99)]);
        using var dir = new TempDirectory();
        Run(words, dir, withRuntime: false);
        ulong retired = 0;
        var run = RunScripted(dir, _ => "V 0", count =>
        {
            retired += count;
            return retired >= 5 ? "I" : "A";
        }, syscallSr: Frame(SrIec | SrIm2), eventCredit: 1, requireExactTime: true);
        run.Snapshot!["pc"].Should().Be(VectorBev0);
        run.Snapshot["cop0.epc"].Should().Be(Entry + 20u);
        run.Snapshot["cop0.cause"].Should().Be(CauseIp2);
        G(run, T1).Should().Be(words[0], "exception entry flushes the pending load even though the observer would cancel it");
        G(run, R3000aRegister.T2).Should().Be(0);
        retired.Should().Be(5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1u)]
    public void ExactTimeProtocol_MissingInitialOrReportCredit_FailsClosed(uint? initialCredit)
    {
        using var dir = new TempDirectory();
        Run(Program([Nop, Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", eventCredit: initialCredit,
            requireExactTime: true, sendCreditOnReports: false);
        run.ExitCode.Should().Be(RecompiledArtifactCodeGen.RetiredProtocolExitCode);
        run.HasSnapshot.Should().BeFalse();
    }

    [Theory]
    [InlineData("T 0\nA")]
    [InlineData("T 4294967296\nA")]
    [InlineData("T malformed\nA")]
    public void ExactTimeProtocol_MalformedCredit_FailsClosed(string reply)
    {
        using var dir = new TempDirectory();
        Run(Program([Nop, Nop]), dir, withRuntime: false);
        var run = RunScripted(dir, _ => "V 0", _ => reply, eventCredit: 1,
            requireExactTime: true, sendCreditOnReports: false);
        run.ExitCode.Should().Be(RecompiledArtifactCodeGen.RetiredProtocolExitCode);
        run.HasSnapshot.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingTimerIrq_MaskChangeAcceptsAtTheNextInstructionUnlessAcknowledged(bool acknowledge)
    {
        var before = Program(ExitCriticalSection(), [Lui(T0, 0x1F80), Ori(T1, Zero, 2),
            Mem(R3000aOpcode.Sw, T1, T0, 0x1128), Ori(T1, Zero, 0x10),
            Mem(R3000aOpcode.Sw, T1, T0, 0x1124), Nop, Nop]);
        var words = Program(before[..^1],
            acknowledge ? [Mem(R3000aOpcode.Sw, Zero, T0, 0x1070)] : [Nop],
            [Ori(T1, Zero, 1 << 6), Mem(R3000aOpcode.Sw, T1, T0, 0x1074),
             Ori(R3000aRegister.T2, Zero, 0x77)]);
        using var dir = new TempDirectory();
        var run = Run(words, dir, withRuntime: true);
        run.FinalSnapshot.Should().NotBeNull();
        run.FinalSnapshot!.Gpr[10].Should().Be(acknowledge ? 0x77u : 0u);
        if (!acknowledge)
        {
            run.FinalSnapshot.PC.Should().Be(VectorBev0);
            run.DiagnosticMessage.Should().Contain($"EPC=0x{Entry + (uint)(words.Length - 2) * 4:X8}");
        }
    }

    [Fact]
    public void AlreadyPendingIrq_IsTransportedAtInitializationBeforeCpuEnablesIt()
    {
        var words = Program([Ori(T1, Zero, 0x401), 0x40896000u, Ori(R3000aRegister.T2, Zero, 0x77)]);
        using var dir = new TempDirectory();
        var run = Run(words, dir, withRuntime: true, configureDevices: devices =>
        {
            devices.Core.WriteInterruptControllerRegister(InterruptMask, Timer2Irq);
            devices.Core.RaiseInterrupt(DeviceScheduler.Timer0Irq + 2);
        });
        run.FinalSnapshot.Should().NotBeNull();
        run.FinalSnapshot!.PC.Should().Be(VectorBev0);
        run.FinalSnapshot.Gpr[10].Should().Be(0);
        run.DiagnosticMessage.Should().Contain($"EPC=0x{Entry + 8:X8}");
    }

    private const ushort Marker = 0x77;
    private const R3000aRegister T5 = R3000aRegister.T5;
    private const R3000aRegister S2 = R3000aRegister.S2;
    private const uint HookBuffer = 0x80002000u;

    /// <summary>SYS(02h), then I_MASK = IRQ6 and Timer 2 raising IRQ6 at counter 100.</summary>
    private static uint[] ArmTimer2Irq() =>
    [
        .. ExitCriticalSection(),
        Lui(T0, 0x1F80),
        Ori(T1, Zero, 1 << 6), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(InterruptMask & 0xFFFF)),
        Ori(T1, Zero, 100), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Target & 0xFFFF)),
        Ori(T1, Zero, 0x10), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(Timer2Mode & 0xFFFF)),
    ];

    private static uint[] StoreWord(uint address, uint value) =>
        [.. Li(T0, address & ~0xFFFFu), .. Li(T1, value), Mem(R3000aOpcode.Sw, T1, T0, (ushort)(address & 0xFFFF))];

    [Fact]
    public void Int_WithAGuestInstalledVector_KeepsTheGuestOwnedBoundary_NotTheKernelHandler()
    {
        // The guest wrote its own code at the RAM vector: the kernel handler must not claim it. The artifact has
        // no generated block there, so the run still ends at the classified vector boundary.
        var code = new List<uint>([.. StoreWord(VectorBev0, 0x42000010u), .. ArmTimer2Irq()]);
        code.AddRange(BranchToSelf(Entry + (uint)code.Count * 4u));
        using var dir = new TempDirectory();

        var result = Run([.. code], dir, withRuntime: true, segmentBudget: 100_000);

        result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.ExceptionVectorUnhandledDiagnosticCode);
        result.FinalSnapshot!.PC.Should().Be(VectorBev0);
    }

    [Fact]
    public void OrdinaryTransferToAZeroFilledVector_WithoutAnIntEntry_IsNotTheKernelHandler()
    {
        // No interrupt is ever enabled: the guest merely jumps to the unpopulated 0x80000080. CAUSE.ExcCode is 0 and
        // the vector is zero, but no INT entry happened, so the kernel handler must not run (it would save state to a
        // seeded TCB and RFE). The generic fail-closed vector diagnostic is what remains.
        var code = new List<uint>([.. Li(T0, VectorBev0), MipsEncoding.JumpRegister((byte)T0), Nop]);
        var chainCalls = 0;
        using var dir = new TempDirectory();

        var result = Run([.. code], dir, withRuntime: true, segmentBudget: 1_000, exceptionChain: ctx =>
        {
            chainCalls++;
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        chainCalls.Should().Be(0, "an ordinary transfer is not a kernel exception entry");
        result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.ExceptionVectorUnhandledDiagnosticCode);
        result.FinalSnapshot!.PC.Should().Be(VectorBev0);
    }

    /// <summary>Handshakes, transfers to the unpopulated general vector, answers the host's E query with <c>RHOST_COP0 ... intEntry</c>.</summary>
    private sealed class Cop0ReplyChildBuildService(string intEntry) : IGeneratedHostBuildService
    {
        public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
        {
            var gprs = string.Concat(Enumerable.Repeat(" 0", 32));
            var source =
                "#include <stdio.h>\n" +
                "int main(void) {\n" +
                "  char b[64]; unsigned long a, v;\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolInitLine}\\n\"); fflush(stdout);\n" +
                "  for (;;) {\n" +
                "    if (scanf(\"%63s\", b) != 1) return 1;\n" +
                "    if (b[0] == 'N') break;\n" +
                "    if (b[0] == 'R' && scanf(\"%lu\", &a) == 1) { printf(\"RHOST_DATA 0\\n\"); fflush(stdout); }\n" +
                "    else if (b[0] == 'W' && scanf(\"%lu %lu\", &a, &v) == 2) { printf(\"RHOST_OK\\n\"); fflush(stdout); }\n" +
                "    else if (b[0] == 'T' && scanf(\"%lu\", &v) == 1) {}\n" +
                "    else if (b[0] == 'L' && scanf(\"%lu\", &v) == 1) {}\n" +
                "    else return 1;\n" +
                "  }\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolTransferPrefix}{VectorBev0}{gprs}\\n\"); fflush(stdout);\n" +
                // The host first reads the vector's RAM (zero-filled: unpopulated), then asks for COP0.
                "  for (;;) {\n" +
                "    if (scanf(\"%63s\", b) != 1) return 1;\n" +
                $"    if (b[0] == '{RecompiledArtifactCodeGen.ProtocolCop0QueryCommand}') break;\n" +
                "    if (b[0] == 'R' && scanf(\"%lu\", &a) == 1) { printf(\"RHOST_DATA 0\\n\"); fflush(stdout); }\n" +
                "    else return 1;\n" +
                "  }\n" +
                $"  printf(\"{RecompiledArtifactCodeGen.ProtocolCop0ReplyPrefix}0 0 0 0 0 {intEntry}\\n\"); fflush(stdout);\n" +
                "  for (;;) {\n" +
                "    if (scanf(\"%63s\", b) != 1) return 0;\n" +
                "    if (b[0] == 'D') return 0;\n" +
                "    if (b[0] == 'T' && scanf(\"%lu\", &v) == 1) {}\n" +
                "    else if (b[0] == 'R' && scanf(\"%lu\", &a) == 1) { printf(\"RHOST_DATA 0\\n\"); fflush(stdout); }\n" +
                "    else if (b[0] == 'W' && scanf(\"%lu %lu\", &a, &v) == 2) { printf(\"RHOST_OK\\n\"); fflush(stdout); }\n" +
                "    else return 0;\n" +
                "  }\n" +
                "  return 0;\n" +
                "}\n";
            return new GeneratedHostBuildService().Build(request with { Source = source });
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void QueryCop0_IntEntryZeroOrOne_IsAccepted(string intEntry)
    {
        using var dir = new TempDirectory();

        var result = Run(Program([Nop]), dir, withRuntime: true, new Cop0ReplyChildBuildService(intEntry));

        result.DiagnosticCode.Should().NotBe("ARTIFACT_HOST_PROTOCOL_FAILED", result.DiagnosticMessage);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("123")]
    [InlineData("4294967295")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("true")]
    [InlineData("x")]
    public void QueryCop0_IntEntryOtherThanZeroOrOne_IsAProtocolFailure(string intEntry)
    {
        using var dir = new TempDirectory();

        var result = Run(Program([Nop]), dir, withRuntime: true, new Cop0ReplyChildBuildService(intEntry));

        result.State.Should().NotBe(TitleExecutionState.Completed);
        result.DiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
        result.FinalSnapshot.Should().BeNull();
    }

    [Fact]
    public void ChainCompletion_ReturnsToEpc_ThroughTheArtifactsOwnRfe_AndTakesAnUnacknowledgedIrqAgain()
    {
        // The first pass completes without acknowledging IRQ6: it is taken again only if the return resumed the
        // interrupted countdown with its registers intact and the artifact's RFE re-enabled interrupts.
        var code = new List<uint>([.. ArmTimer2Irq(), Ori(T5, Zero, 1000)]);
        var loop = Entry + (uint)code.Count * 4u;
        code.AddRange(
        [
            MipsEncoding.I(0x09, (byte)T5, (byte)T5, 0xFFFF),                     // addiu t5, t5, -1
            MipsEncoding.Branch(0x05, (byte)T5, (byte)Zero, loop + 4, loop),      // bne t5, zero, loop
            Nop,
            Ori(S2, Zero, Marker),
            Nop,
        ]);
        var calls = 0;
        using var dir = new TempDirectory();

        var result = Run([.. code], dir, withRuntime: true, segmentBudget: 100_000, exceptionChain: ctx =>
        {
            if (++calls == 2)
            {
                ctx.Interrupts.Acknowledge(~Timer2Irq);
            }

            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        calls.Should().Be(2);
        result.FinalSnapshot!.Gpr[(int)T5].Should().Be(0u, "the countdown finished: its register survived the save/restore");
        result.FinalSnapshot.Gpr[(int)S2].Should().Be(Marker);
    }

    [Fact]
    public void ChainCompletion_WithARegisteredHook_AppliesTheHooksRegistersAndPc_ToTheArtifact()
    {
        // The guest writes a hook buffer (ra, sp, fp, s0..s7, gp), registers it through B0:19, and spins. The
        // completed chain fires the hook: the artifact's registers change over the protocol's G commands and
        // control lands on the hook's saved ra — a block reachable only that way, so it is an explicit root.
        const uint s0 = 0x1234;
        List<uint> Build(uint landing)
        {
            uint[] saved = [landing, 0x801FFF00, 0x801FFF10, s0, 0, 0, 0, 0, 0, 0, 0, 0x80008000];
            var code = new List<uint>();
            for (var i = 0; i < saved.Length; i++)
            {
                code.AddRange(StoreWord(HookBuffer + (uint)i * 4u, saved[i]));
            }

            code.Add(Ori(T1, Zero, BiosHleRuntime.HookEntryIntFunction));
            code.AddRange(Li(R3000aRegister.A0, HookBuffer));
            code.AddRange([MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress), Nop]);
            code.AddRange(ArmTimer2Irq());
            code.AddRange(BranchToSelf(Entry + (uint)code.Count * 4u));
            return code;
        }

        var landing = Entry + (uint)Build(0).Count * 4u;
        var words = Build(landing);
        words.AddRange([Ori(S2, Zero, Marker), Nop]);
        var calls = 0;
        using var dir = new TempDirectory();

        var result = Run(
            [.. words], dir, withRuntime: true, segmentBudget: 100_000, additionalRoots: [landing],
            exceptionChain: ctx =>
            {
                calls++;
                ctx.Interrupts.Acknowledge(~Timer2Irq);
                return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
            });

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        calls.Should().Be(1);
        result.FinalSnapshot!.Gpr[(int)S2].Should().Be(Marker, "control continued at the hook's saved ra");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.V0].Should().Be(1u, "the hook is entered with $v0 = 1");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S0].Should().Be(s0);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.Sp].Should().Be(0x801FFF00u);
    }

    [Theory]
    [InlineData(false, true)]  // CPU interrupts never enabled: the IRQ stays pending in I_STAT only
    [InlineData(true, false)]  // CPU enabled but I_MASK does not enable IRQ6: not pending at the controller line
    public void Timer2Irq_WithoutEveryEnable_NeverReachesTheCpu(bool enableCpu, bool unmask)
    {
        using var dir = new TempDirectory();

        var result = Run(TimerIrqProgram(enableCpu, unmask), dir, withRuntime: true, segmentBudget: 20_000);

        result.DiagnosticCode.Should().NotBe(RecompiledHostExecutionEngine.ExceptionVectorUnhandledDiagnosticCode);
        result.FinalSnapshot?.PC.Should().NotBe(VectorBev0);
    }
}

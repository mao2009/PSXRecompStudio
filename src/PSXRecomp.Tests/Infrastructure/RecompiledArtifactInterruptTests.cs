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

    private const uint SrIec = 0x1u;      // SR bit 0 (R3000A IEc; BiosKernelSyscallDispatch / ADR-014)
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

    [Fact]
    public void Entry_MatchesTheInterpretersNativeCpu_ForTheSameSrAndLine()
    {
        // SR bit 1 is the interpreter's IEc (docs/cpu/cop0.md) and bit 0 the generated host's (R3000A):
        // SR = IEc(0) | bit 1 | IM2 enables INT on both, so every other COP0 value must agree.
        const uint sr = SrIec | 0x2u | SrIm2;
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
        result.DiagnosticCode.Should().Be(RecompiledHostExecutionEngine.ExceptionVectorUnhandledDiagnosticCode);
        result.DiagnosticMessage.Should().Contain("EPC=0x8000").And.Contain("CAUSE=0x00000400").And.Contain("#662");
        result.State.Should().Be(TitleExecutionState.RuntimeFailure, "an unmodelled kernel exception path fails closed");
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

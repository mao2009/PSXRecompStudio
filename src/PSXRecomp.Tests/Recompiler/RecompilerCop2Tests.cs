using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Recompiler.RecompilerCop0Tests;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Issue #447: MFC2/CFC2/MTC2/CTC2, GTE commands and LWC2/SWC2 carried through the lowering, the
// validator, the IR evaluator and the generated host. Each program runs on the native interpreter
// (the oracle), the IR evaluator and the gcc-built host; all three execute against the same managed
// GteRegisterBank implementation (the host relays to it), and all three must agree.
public sealed class RecompilerCop2Tests
{
    private const byte T0 = 8, T1 = 9, T2 = 10, T3 = 11, T4 = 12, T5 = 13;
    private const uint Rtps = 0x0018_0001u | (1u << 19);

    private static uint Mfc2(byte rt, byte rd) => 0x4800_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Cfc2(byte rt, byte rd) => 0x4840_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Mtc2(byte rt, byte rd) => 0x4880_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Ctc2(byte rt, byte rd) => 0x48C0_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Cop2(uint command) => 0x4A00_0000u | command;
    private static uint Lwc2(byte rt, byte baseRegister, ushort offset) => 0xC800_0000u | ((uint)baseRegister << 21) | ((uint)rt << 16) | offset;
    private static uint Swc2(byte rt, byte baseRegister, ushort offset) => 0xE800_0000u | ((uint)baseRegister << 21) | ((uint)rt << 16) | offset;

    /// <summary>SR.CU2 on, so COP2 is usable on every backend.</summary>
    private static readonly uint[] EnableCop2 = [Lui(T0, 0x4000), Mtc0(T0, RecompilerCop0.Status)];

    [Fact]
    public void Ctc2ThenCfc2_SignExtendsZsf3_AndCfc2IsLoadDelayed()
    {
        var run = RunThreeWay("cop2-ctc2-cfc2",
        [
            .. EnableCop2,
            Lui(T1, 0x1234), Ori(T1, T1, 0x8001),
            Ctc2(T1, 29),
            Ori(T2, 0, 0x55),
            Cfc2(T2, 29),
            MipsEncoding.R(0x21, rd: T3, rs: T2, rt: 0, shamt: 0),   // load-delay slot: old $t2
            MipsEncoding.R(0x21, rd: T4, rs: T2, rt: 0, shamt: 0),   // the GTE value
        ]);

        run.Gpr[T3].Should().Be(0x55u);
        run.Gpr[T4].Should().Be(0xFFFF_8001u);
    }

    [Fact]
    public void Mtc2ToSxyp_ThenNclip_ProducesMac0()
    {
        var run = RunThreeWay("cop2-nclip",
        [
            .. EnableCop2,
            Mtc2(0, 15),                       // SXY: (0,0)
            Ori(T1, 0, 10), Mtc2(T1, 15),      // (10,0)
            Lui(T1, 10), Mtc2(T1, 15),         // (0,10)
            Cop2(0x0140_0006),                 // NCLIP
            Mfc2(T2, 24),                      // MAC0
            Cfc2(T3, 31),                      // FLAG
            Mfc2(T4, 12),                      // SXY0
            MipsEncoding.Nop,
        ]);

        run.Gpr[T2].Should().Be(100u);
        run.Gpr[T3].Should().Be(0u);
        run.Gpr[T4].Should().Be(0u);
    }

    [Fact]
    public void Rtps_ThroughControlAndDataMoves_MatchesOnEveryBackend()
    {
        var run = RunThreeWay("cop2-rtps",
        [
            .. EnableCop2,
            Ori(T1, 0, 0x1000), Ctc2(T1, 0), Ctc2(T1, 2), Ctc2(T1, 4),   // RT = identity
            Ori(T1, 0, 0x200), Ctc2(T1, 7),                             // TRZ
            Lui(T1, 160), Ctc2(T1, 24), Lui(T1, 120), Ctc2(T1, 25),     // OFX, OFY
            Ori(T1, 0, 0x100), Ctc2(T1, 26),                            // H
            Lui(T1, 0xFFCE), Ori(T1, T1, 100), Mtc2(T1, 0),             // VX0 = 100, VY0 = -50
            Ori(T1, 0, 0x300), Mtc2(T1, 1),                             // VZ0
            Cop2(Rtps),
            Mfc2(T2, 14), Mfc2(T3, 19), Cfc2(T4, 31), Mfc2(T5, 25),
            MipsEncoding.Nop,
        ]);

        run.Gpr[T3].Should().Be(0x500u, "SZ3 = VZ + TRZ");
        ((short)run.Gpr[T2]).Should().BeInRange((short)179, (short)180);
        run.Gpr[T5].Should().Be(100u, "MAC1 = VX with sf=1");
    }

    [Fact]
    public void Lwc2AndSwc2_MoveWordsThroughTheGte()
    {
        var run = RunThreeWay("cop2-lwc2-swc2",
        [
            .. EnableCop2,
            Lui(T0, 0x8000), Ori(T0, T0, 0x1000),
            Lui(T1, 0x0000), Ori(T1, T1, 0x00FF),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: T1, baseRegister: T0, offset: 0),
            Lwc2(30, T0, 0),                   // LZCS <- 0x000000FF
            Swc2(31, T0, 4),                   // LZCR = 24
            Swc2(30, T0, 8),
            MipsEncoding.Nop,
        ], windowBytes: 12);

        run.Memory.Should().Equal(0xFF, 0, 0, 0, 24, 0, 0, 0, 0xFF, 0, 0, 0);
    }

    [Theory]
    [InlineData(false)] // SR.CU2 clear
    [InlineData(true)]  // an unimplemented command (RTPT)
    public void AFaultingCop2Program_StopsAsAnException_OnEveryBackend(bool cop2Enabled)
    {
        uint[] words = cop2Enabled
            ? [.. EnableCop2, Cop2(0x0028_0030), MipsEncoding.Nop]
            : [Ctc2(T0, 29), MipsEncoding.Nop];
        var fixture = new RecompilerDifferentialFixture(
            $"cop2-fault-{cop2Enabled}", words, EntryPc, stepBudget: (uint)words.Length, referenceStepBudget: (uint)words.Length);

        var reference = new RecompilerInterpreterExecutor().Execute(fixture);
        var host = new RecompilerHostExecutor().Execute(fixture);
        var ir = RecompilerIrEvaluator.Run(Lower(words), EntryPc, new uint[32], new RecompilerGuestMemory(), blockBudget: (uint)words.Length);

        reference.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        Assert.True(host.Status == RecompilerExecutionStatus.Completed, $"[{host.DiagnosticCode}] {host.DiagnosticMessage}");
        host.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        ir.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
    }

    [Fact]
    public void Validator_AcceptsTheCop2Shapes_AndRejectsAnOutOfRangeBankOrCommand()
    {
        static bool Valid(params RecompilerIrOperation[] operations) =>
            RecompilerIrValidator.Validate(new RecompilerIrProgram(
            [
                new RecompilerIrBlock(EntryPc, operations, new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4)),
            ])).IsValid;

        Valid(
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadCop2, resultValueId: 0, register: 31, immediate: RecompilerCop2.ControlBank),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteCop2, inputValueA: 0, register: 15, immediate: RecompilerCop2.DataBank),
            new RecompilerIrOperation(RecompilerIrOperationKind.Cop2Command, immediate: 0x01FF_FFFF)).Should().BeTrue();

        Valid(new RecompilerIrOperation(RecompilerIrOperationKind.ReadCop2, resultValueId: 0, register: 1, immediate: 2)).Should().BeFalse();
        Valid(new RecompilerIrOperation(RecompilerIrOperationKind.Cop2Command, immediate: 0x0200_0000)).Should().BeFalse();
    }
}

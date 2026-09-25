using PSXRecomp.Core;

namespace PSXRecomp.Tests;

/// <summary>
/// End-to-end regression coverage for the CodeRabbit finding on PR #548:
/// the guest-visible SIO0 register model (Issue #542) must be reachable from
/// the production guest CPU memory-access path — <c>LW/SW/LH/SH/LB/LBU/SB</c>
/// executed by the real native R3000A interpreter through
/// <c>PSXCore_StepWithoutInterrupts</c> -&gt; native <c>PSXCpu</c> -&gt; native
/// <c>PSXMemory</c> -&gt; SIO0 (<c>crate::sio0</c>) — not only through the
/// managed <see cref="PSXRecomp.Core.Dma.MemoryBus"/> test/BIOS-HLE seam.
/// Follows the same "assemble raw MIPS words, Step() the real core" pattern
/// as <see cref="PSXCoreMinimalProgramTests"/> (Issue #157): no C ABI
/// addition was needed.
/// </summary>
[Test]
public class Sio0CpuEndToEndTests : IDisposable
{
    private const uint Sio0Base = 0x1F801040;
    private const uint DataOffset = 0x00;
    private const uint StatusOffset = 0x04;
    private const uint ModeOffset = 0x08;
    private const uint ControlOffset = 0x0A;
    private const uint BaudOffset = 0x0E;
    private const uint IdleStatus = 0x00000005; // TX ready 1 + TX ready 2

    private const int Zero = 0;
    private const int T0 = 8; // holds the SIO0 base address
    private const int T1 = 9; // holds the value under test
    private const int T2 = 10; // receives loaded values

    private readonly PSXCoreWrapper _core = new();

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private static uint Lui(int rt, ushort imm) => (0x0Fu << 26) | ((uint)rt << 16) | imm;
    private static uint Ori(int rt, int rs, ushort imm) => (0x0Du << 26) | ((uint)rs << 21) | ((uint)rt << 16) | imm;
    private static uint Sb(int rt, int rs, ushort offset) => (0x28u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Sh(int rt, int rs, ushort offset) => (0x29u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Sw(int rt, int rs, ushort offset) => (0x2Bu << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lb(int rt, int rs, ushort offset) => (0x20u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lbu(int rt, int rs, ushort offset) => (0x24u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lh(int rt, int rs, ushort offset) => (0x21u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lhu(int rt, int rs, ushort offset) => (0x25u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lw(int rt, int rs, ushort offset) => (0x23u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private const uint Nop = 0x00000000u; // sll $zero, $zero, 0

    /// <summary>
    /// Runs a short program starting at PC 0: LUI/ORI load the SIO0 base
    /// address into <c>$t0</c>, then every word in <paramref name="body"/>
    /// executes in order, followed by one trailing NOP. Each word is
    /// Step()'d individually through the real native core so a wrong opcode
    /// encoding fails loudly (a bad decode raises a guest exception, and
    /// <see cref="PSXCoreWrapper.Step"/> would return non-zero or leave
    /// <c>ExceptionRaised</c> set) rather than silently mis-executing. The
    /// trailing NOP lets a load in the last body instruction cross this R3000A
    /// interpreter's one-instruction load delay slot (<c>WriteRegDelayed</c>
    /// in <c>psx_cpu_memory_access.cpp</c>) before a caller inspects GPRs.
    /// </summary>
    private void Run(params uint[] body)
    {
        var program = new List<uint>
        {
            Lui(T0, 0x1F80),
            Ori(T0, T0, 0x1040),
        };
        program.AddRange(body);
        program.Add(Nop);

        for (var i = 0; i < program.Count; i++)
        {
            _core.WriteMemory32((uint)(i * 4), program[i]);
        }
        _core.Pc = 0;

        for (var i = 0; i < program.Count; i++)
        {
            _core.Step().Should().Be(0, $"instruction {i} (0x{program[i]:X8}) should execute without a native-call failure");
            _core.ExceptionRaised.Should().BeFalse($"instruction {i} (0x{program[i]:X8}) should not raise a guest exception");
        }
    }

    [Fact]
    public void BaseAddressBuild_ProducesDocumentedSio0Base()
    {
        // Guards the LUI/ORI helper program itself: if this fails, every
        // other test's failure would be a false positive from a wrong base
        // address rather than a real SIO0 routing regression.
        Run();
        _core.GetGpr(T0).Should().Be(Sio0Base);
    }

    [Fact]
    public void Reset_ReadsDocumentedIdleValuesThroughCpuLoad()
    {
        Run(
            Lw(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0u, "SIO_DATA is 0 at reset");

        Run(
            Lw(T2, T0, (ushort)StatusOffset));
        _core.GetGpr(T2).Should().Be(IdleStatus, "SIO_STAT is idle (TX ready 1+2) at reset");

        Run(
            Lw(T2, T0, (ushort)ModeOffset));
        _core.GetGpr(T2).Should().Be(0u, "SIO_MODE is 0 at reset");

        Run(
            Lhu(T2, T0, (ushort)ControlOffset));
        _core.GetGpr(T2).Should().Be(0u, "SIO_CTRL is 0 at reset");

        Run(
            Lhu(T2, T0, (ushort)BaudOffset));
        _core.GetGpr(T2).Should().Be(0u, "SIO_BAUD is 0 at reset");
    }

    [Fact]
    public void Mode_SwThenLw_RoundTripsThroughRealCpuAndMemory()
    {
        Run(
            Lui(T1, 0xFFFF),
            Ori(T1, T1, 0xFFFF), // $t1 = 0xFFFFFFFF
            Sw(T1, T0, (ushort)ModeOffset),
            Lw(T2, T0, (ushort)ModeOffset));

        _core.GetGpr(T2).Should().Be(0x01FFu, "SIO_MODE only stores bits 0-8");
    }

    [Fact]
    public void Ctrl_ShThenLh_RoundTripsAndDropsWriteOnlyBits()
    {
        Run(
            Ori(T1, Zero, 0xFFBF), // everything but the reset bit
            Sh(T1, T0, (ushort)ControlOffset),
            Lhu(T2, T0, (ushort)ControlOffset));

        _core.GetGpr(T2).Should().Be(0x3FAFu, "bit4 (ack)/bit6 (reset)/bits14-15 are never stored");
    }

    [Fact]
    public void Ctrl_ResetBit_ZeroesEveryRegister_ObservedThroughCpuLoads()
    {
        Run(
            Ori(T1, Zero, 0x000D),
            Sh(T1, T0, (ushort)ModeOffset),
            Ori(T1, Zero, 0x0088),
            Sh(T1, T0, (ushort)BaudOffset),
            Ori(T1, Zero, 0x1003),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0042),
            Sb(T1, T0, (ushort)DataOffset),
            Ori(T1, Zero, 0x1043), // reset bit set, wins over the rest of this same write
            Sh(T1, T0, (ushort)ControlOffset),
            Lw(T2, T0, (ushort)ModeOffset));
        _core.GetGpr(T2).Should().Be(0u);

        // SIO_CTRL/SIO_BAUD are 16-bit registers at a 2-byte-aligned (not
        // 4-byte-aligned) offset: LW there is a real MIPS misaligned access,
        // so guest code (and this test) must use LHU/LH.
        Run(Lhu(T2, T0, (ushort)ControlOffset));
        _core.GetGpr(T2).Should().Be(0u);

        Run(Lhu(T2, T0, (ushort)BaudOffset));
        _core.GetGpr(T2).Should().Be(0u);

        Run(Lw(T2, T0, (ushort)StatusOffset));
        _core.GetGpr(T2).Should().Be(IdleStatus);

        Run(Lw(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0u);
    }

    [Fact]
    public void Baud_LbuAndLhu_ReadTheSameByteAndHalfwordTheSbAndShWrote()
    {
        Run(
            Ori(T1, Zero, 0x1234),
            Sh(T1, T0, (ushort)BaudOffset),
            Lhu(T2, T0, (ushort)BaudOffset));
        _core.GetGpr(T2).Should().Be(0x1234u);
    }

    [Fact]
    public void Data_SbThenLb_DoesNotObserveTheJustWrittenTxByte()
    {
        // SIO_CTRL is still 0 (deselected) here, so Issue #543's protocol
        // never engages: SIO_DATA reads RX, and an unselected TX write never
        // fills RX, so the byte just written is never read back and
        // SIO_STAT stays idle. See the SelectedTransaction_* tests below for
        // the selected/protocol-engaged behavior.
        Run(
            Ori(T1, Zero, 0x0042),
            Sb(T1, T0, (ushort)DataOffset),
            Lb(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0u);

        Run(Lw(T2, T0, (ushort)StatusOffset));
        _core.GetGpr(T2).Should().Be(IdleStatus);
    }

    [Fact]
    public void Status_SwIgnoredByCpuStore_StaysIdleOnLw()
    {
        Run(
            Lui(T1, 0xFFFF),
            Ori(T1, T1, 0xFFFF),
            Sw(T1, T0, (ushort)StatusOffset),
            Lw(T2, T0, (ushort)StatusOffset));
        _core.GetGpr(T2).Should().Be(IdleStatus);
    }

    [Theory]
    [InlineData((ushort)0x01)]
    [InlineData((ushort)0x06)]
    [InlineData((ushort)0x0C)]
    [InlineData((ushort)0x10)]
    [InlineData((ushort)0x1E)]
    [InlineData((ushort)0x1F)]
    public void ReservedOffset_SbThenLb_ReadsZeroAndLeavesNamedRegistersAlone(ushort offset)
    {
        // Byte-width (not SW/LW): several offsets under test (0x01, 0x06,
        // 0x1E, 0x1F) are not 4-byte aligned, so a word access there would be
        // a genuine MIPS misaligned-address exception unrelated to what this
        // test is probing.
        Run(
            Ori(T1, Zero, 0x000D),
            Sh(T1, T0, (ushort)ModeOffset),
            Ori(T1, Zero, 0x00FF),
            Sb(T1, T0, offset),
            Lbu(T2, T0, offset));
        _core.GetGpr(T2).Should().Be(0u, $"offset 0x{offset:X2} is Reserved");

        Run(Lw(T2, T0, (ushort)ModeOffset));
        _core.GetGpr(T2).Should().Be(0x000Du, "the reserved-offset write must not corrupt SIO_MODE");
    }

    [Fact]
    public void LowerBoundary_JustBeforeWindow_IsNotSio0()
    {
        // 0x1F80103F: one byte before SIO0's base. A store here, then a
        // fresh reset-value read of SIO_DATA, proves the two addresses are
        // not aliased onto the same storage.
        Run(
            Lui(T1, 0xFFFF),
            Ori(T1, T1, 0xFFFF),
            Sb(T1, T0, unchecked((ushort)(0 - 1))), // sb $t1, -1($t0) => 0x1F80103F
            Lw(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0u, "0x1F80103F is outside the SIO0 window and must not alias SIO_DATA");
    }

    [Fact]
    public void UpperBoundary_LastMappedByte_IsReservedAndNextByteIsNot()
    {
        // 0x1F80105F (Sio0Base + 0x1F) is the last mapped byte (Reserved).
        // 0x1F801060 (Sio0Base + 0x20) is one past the window.
        Run(
            Lui(T1, 0xFFFF),
            Ori(T1, T1, 0xFFFF),
            Sb(T1, T0, 0x1F),
            Lbu(T2, T0, 0x1F));
        _core.GetGpr(T2).Should().Be(0u, "0x1F80105F is the last mapped (Reserved) byte");

        Run(
            Ori(T1, Zero, 0x000D),
            Sh(T1, T0, (ushort)ModeOffset),
            Lui(T1, 0xFFFF),
            Ori(T1, T1, 0xFFFF),
            Sb(T1, T0, 0x20),
            Lw(T2, T0, (ushort)ModeOffset));
        _core.GetGpr(T2).Should().Be(0x000Du, "a write one byte past the SIO0 window must not reach SIO_MODE");
    }

    [Fact]
    public void CoreReset_ZeroesSio0StateObservedThroughCpuLoads()
    {
        Run(
            Ori(T1, Zero, 0x000D),
            Sh(T1, T0, (ushort)ModeOffset),
            Ori(T1, Zero, 0x1003),
            Sh(T1, T0, (ushort)ControlOffset));

        _core.Reset();

        Run(Lw(T2, T0, (ushort)ModeOffset));
        _core.GetGpr(T2).Should().Be(0u, "PSXCore_Reset must reset SIO0 state along with the rest of guest memory");

        Run(Lhu(T2, T0, (ushort)ControlOffset));
        _core.GetGpr(T2).Should().Be(0u);

        Run(Lw(T2, T0, (ushort)StatusOffset));
        _core.GetGpr(T2).Should().Be(IdleStatus);
    }

    // Issue #543: minimal controller serial protocol (disconnected-pad response),
    // driven through the same real CPU Step()/PSXMemory production path as above.

    private const ushort CtrlSelect = 0x0003; // TXEN | SIO_CTRL.1 (select)
    private const ushort CtrlDeselect = 0x0001; // TXEN only, deselected
    private const ushort CtrlResetBit = 0x0040;

    [Fact]
    public void SelectedTransaction_DisconnectedResponse_ReadsFFForEveryByte_ThroughCpuLoadsAndStores()
    {
        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001), // address byte
            Sb(T1, T0, (ushort)DataOffset),
            Lw(T2, T0, (ushort)StatusOffset));
        (_core.GetGpr(T2) & 0x2).Should().NotBe(0u, "the disconnected-slot response byte must land in the RX FIFO");

        Run(Lbu(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0xFFu, "a disconnected port reads back 0xFF (Issue #543)");

        Run(
            Ori(T1, Zero, 0x0042), // command byte (recognized: read pad)
            Sb(T1, T0, (ushort)DataOffset),
            Lbu(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0xFFu, "the command byte's response is the same fixed disconnected value, known command or not");
    }

    [Fact]
    public void SelectedTransaction_SignalsSio0InterruptPending_UntilClearedThroughPSXCoreWrapper()
    {
        _core.GetSio0InterruptPending().Should().BeFalse();

        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset));
        _core.GetSio0InterruptPending().Should().BeFalse("selecting alone must not signal a byte-received IRQ");

        Run(
            Ori(T1, Zero, 0x0001),
            Sb(T1, T0, (ushort)DataOffset));
        _core.GetSio0InterruptPending().Should().BeTrue("a transaction byte was transferred while selected");

        _core.ClearSio0Interrupt();
        _core.GetSio0InterruptPending().Should().BeFalse();
    }

    [Fact]
    public void UnselectedDataWrite_NeverSignalsSio0InterruptPending()
    {
        Run(
            Ori(T1, Zero, 0x0042),
            Sb(T1, T0, (ushort)DataOffset));
        _core.GetSio0InterruptPending().Should().BeFalse("SIO_CTRL is still 0 (deselected)");
    }

    [Fact]
    public void RepeatedTransactionAfterCtrlReset_SignalsIrqAgain_ThroughCpuLoadsAndStores()
    {
        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001),
            Sb(T1, T0, (ushort)DataOffset));
        _core.GetSio0InterruptPending().Should().BeTrue();
        _core.ClearSio0Interrupt();

        Run(
            Ori(T1, Zero, CtrlResetBit),
            Sh(T1, T0, (ushort)ControlOffset)); // full register reset between polls
        _core.GetSio0InterruptPending().Should().BeFalse();

        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001),
            Sb(T1, T0, (ushort)DataOffset),
            Lbu(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0xFFu);
        _core.GetSio0InterruptPending().Should().BeTrue("the second transaction must signal its own byte-received IRQ");
    }

    [Fact]
    public void Deselecting_ThenReselecting_SignalsAFreshIrqForTheNewTransaction()
    {
        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001),
            Sb(T1, T0, (ushort)DataOffset));
        _core.ClearSio0Interrupt();

        Run(
            Ori(T1, Zero, CtrlDeselect),
            Sh(T1, T0, (ushort)ControlOffset)); // deselect: abandon the transaction
        _core.GetSio0InterruptPending().Should().BeFalse("deselecting alone must not itself signal an IRQ");

        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001),
            Sb(T1, T0, (ushort)DataOffset),
            Lbu(T2, T0, (ushort)DataOffset));
        _core.GetGpr(T2).Should().Be(0xFFu);
        _core.GetSio0InterruptPending().Should().BeTrue("the reselected transaction signals its own IRQ");
    }

    // Issue #543 review finding: the command-recognized/unrecognized
    // classification sio0.rs computes internally must be production-visible,
    // not only observable from Rust's own #[cfg(test)] unit tests. These
    // drive the same real CPU Step()/PSXMemory production path as above and
    // read the classification back through PSXCoreWrapper.

    private const uint Sio0CommandStatusNone = 0;
    private const uint Sio0CommandStatusRecognized = 1;
    private const uint Sio0CommandStatusUnsupported = 2;

    [Fact]
    public void RecognizedCommand_ReportsRecognizedStatus_ThroughPSXCoreWrapper()
    {
        _core.GetSio0CommandStatus().Should().Be(Sio0CommandStatusNone, "no command byte has been sent yet");

        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001), // address byte
            Sb(T1, T0, (ushort)DataOffset));
        _core.GetSio0CommandStatus().Should().Be(Sio0CommandStatusNone, "the address byte alone must not flip the classification");

        Run(
            Ori(T1, Zero, 0x0042), // command byte: recognized (read pad)
            Sb(T1, T0, (ushort)DataOffset),
            Lbu(T2, T0, (ushort)DataOffset));
        _core.GetSio0CommandStatus().Should().Be(Sio0CommandStatusRecognized);
        _core.GetGpr(T2).Should().Be(0xFFu, "the disconnected response is unchanged by recognition");
        _core.GetSio0InterruptPending().Should().BeTrue("the transaction still completed and signaled IRQ7");
    }

    [Fact]
    public void UnknownCommand_ReportsUnsupportedStatusAndTheActualByte_ThroughPSXCoreWrapper()
    {
        Run(
            Ori(T1, Zero, CtrlSelect),
            Sh(T1, T0, (ushort)ControlOffset),
            Ori(T1, Zero, 0x0001), // address byte
            Sb(T1, T0, (ushort)DataOffset),
            Ori(T1, Zero, 0x0099), // command byte: unrecognized
            Sb(T1, T0, (ushort)DataOffset),
            Lbu(T2, T0, (ushort)DataOffset));

        _core.GetSio0CommandStatus().Should().Be(Sio0CommandStatusUnsupported, "0x99 is not the recognized command byte");
        _core.GetSio0LastCommandByte().Should().Be(0x99, "the actual unrecognized byte is retained, not just a flag");

        // The classification must not change any previously verified
        // behavior: response, RX-ready, or IRQ7.
        _core.GetGpr(T2).Should().Be(0xFFu, "the transaction still completed deterministically, not hung");
        _core.GetSio0InterruptPending().Should().BeTrue("IRQ7 still latches for an unrecognized command, same as a recognized one");
    }
}

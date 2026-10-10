using PSXRecomp.Core;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests;

/// <summary>
/// Issue #447: the native CPU executes COP2 against the one GTE state of its device graph
/// (<see cref="PsxDeviceGraph.Gte"/>): SR.CU2 gating, MFC2/CFC2 load delay, MTC2/CTC2, LWC2/SWC2
/// addressing and faults, command dispatch, and fail-closed unimplemented commands.
/// </summary>
[Test]
public sealed class GteCop2ExecutionTests : IDisposable
{
    private const uint Base = 0x1000;
    private const uint SrCu2 = 1u << 30;
    private const int Sr = 12, Cause = 13, Epc = 14, BadVAddr = 8;
    private const byte T0 = 8, T1 = 9, T2 = 10;

    private readonly PsxDeviceGraph _graph = new();

    public void Dispose() => _graph.Dispose();

    private static uint Mfc2(byte rt, byte rd) => 0x4800_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Cfc2(byte rt, byte rd) => 0x4840_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Mtc2(byte rt, byte rd) => 0x4880_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Ctc2(byte rt, byte rd) => 0x48C0_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Command(uint command) => 0x4A00_0000u | command;
    private static uint Lwc2(byte rt, byte baseRegister, ushort offset) => 0xC800_0000u | ((uint)baseRegister << 21) | ((uint)rt << 16) | offset;
    private static uint Swc2(byte rt, byte baseRegister, ushort offset) => 0xE800_0000u | ((uint)baseRegister << 21) | ((uint)rt << 16) | offset;
    private static uint Ori(byte rt, ushort immediate) => 0x3400_0000u | ((uint)rt << 16) | immediate;

    private PSXCoreWrapper Load(uint sr, params uint[] words)
    {
        var core = _graph.Core;
        for (var i = 0; i < words.Length; i++)
        {
            core.WriteMemory32(Base + (uint)i * 4, words[i]);
        }

        core.SetCop0(Sr, sr);
        core.Pc = Base;
        return core;
    }

    private static void Steps(PSXCoreWrapper core, int count)
    {
        for (var i = 0; i < count; i++)
        {
            core.Step().Should().Be(0);
            core.ExceptionRaised.Should().BeFalse($"step {i} must not fault");
        }
    }

    [Theory]
    [InlineData(0x48C8_E800u)] // CTC2 $t0, $29 -- Persona's first COP2 word
    [InlineData(0x4800_0000u)] // MFC2
    [InlineData(0x4A18_0001u)] // RTPS
    [InlineData(0xC801_0000u)] // LWC2
    [InlineData(0xE801_0000u)] // SWC2
    public void WithSrCu2Clear_EveryCop2Form_RaisesCpuWithCe2(uint word)
    {
        var core = Load(0, word);
        core.SetGpr(T0, 0x1234);

        core.Step().Should().Be(0);

        core.ExceptionRaised.Should().BeTrue();
        core.ExceptionCode.Should().Be(0x0Bu);
        ((core.GetCop0(Cause) >> 28) & 3).Should().Be(2u);
        core.GetCop0(Epc).Should().Be(Base);
        _graph.Gte.ReadControlRegister(29).Should().Be(0u, "a refused CTC2 writes nothing");
    }

    [Fact]
    public void Ctc2ThenCfc2_RoundTripsZsf3_ThroughTheLoadDelay()
    {
        const byte T3 = 11;
        var core = Load(SrCu2,
            Ctc2(T0, 29),                    // ZSF3 <- $t0
            Cfc2(T1, 29),                    // $t1 <- ZSF3, through the load delay
            Ori(T2, 0) | ((uint)T1 << 21),   // delay slot: ori $t2, $t1, 0 sees the old $t1
            Ori(T3, 0) | ((uint)T1 << 21));  // ori $t3, $t1, 0 sees the transferred value
        core.SetGpr(T0, 0x1234_8001);
        core.SetGpr(T1, 0x5555);

        Steps(core, 4);

        _graph.Gte.ReadControlRegister(29).Should().Be(0xFFFF_8001u, "ZSF3 reads back sign-extended");
        core.GetGpr(T2).Should().Be(0x5555u);
        core.GetGpr(T3).Should().Be(0xFFFF_8001u);
    }

    [Fact]
    public void Mfc2_InTheLoadDelaySlot_IsNotVisible_ToTheNextInstruction()
    {
        _graph.Gte.WriteDataRegister(24, 0xCAFE); // MAC0
        var core = Load(SrCu2, Mfc2(T1, 24), Ori(T2, 0) | ((uint)T1 << 21), Ori(T0, 0) | ((uint)T1 << 21));
        core.SetGpr(T1, 7);

        Steps(core, 3);

        core.GetGpr(T2).Should().Be(7u, "the delay slot reads the old $t1");
        core.GetGpr(T0).Should().Be(0xCAFEu);
    }

    [Fact]
    public void Mtc2ToSxyp_PushesTheFifo_AndMfc2OfIrgbReadsOrgb()
    {
        _graph.Gte.WriteDataRegister(13, 0x11);
        _graph.Gte.WriteDataRegister(14, 0x22);
        var core = Load(SrCu2, Mtc2(T0, 15), Mtc2(T1, 28), Mfc2(T2, 28), 0);
        core.SetGpr(T0, 0x33);
        core.SetGpr(T1, (0x1Fu << 10) | (0x02u << 5) | 0x01u);

        Steps(core, 4);

        _graph.Gte.ReadDataRegister(12).Should().Be(0x11u);
        _graph.Gte.ReadDataRegister(13).Should().Be(0x22u);
        _graph.Gte.ReadDataRegister(14).Should().Be(0x33u);
        core.GetGpr(T2).Should().Be((0x1Fu << 10) | (0x02u << 5) | 0x01u);
    }

    [Fact]
    public void Lwc2AndSwc2_TransferAWord_BetweenRamAndTheGte()
    {
        var core = Load(SrCu2, Lwc2(16, T0, 0), Swc2(16, T0, 4), 0);
        core.SetGpr(T0, 0x8000_2000);
        core.WriteMemory32(0x2000, 0xABCD_1234);

        Steps(core, 3);

        _graph.Gte.ReadDataRegister(16).Should().Be(0x1234u, "SZ0 holds 16 bits");
        core.ReadMemory32(0x2004).Should().Be(0x1234u, "SWC2 stores the register as it reads");
    }

    [Theory]
    [InlineData(true, 0x04u)]  // LWC2 -> AdEL
    [InlineData(false, 0x05u)] // SWC2 -> AdES
    public void MisalignedLwc2OrSwc2_RaisesAnAddressError_WithBadVAddr(bool load, uint excode)
    {
        var core = Load(SrCu2, load ? Lwc2(1, T0, 2) : Swc2(1, T0, 2));
        core.SetGpr(T0, 0x8000_2000);

        core.Step().Should().Be(0);

        core.ExceptionRaised.Should().BeTrue();
        core.ExceptionCode.Should().Be(excode);
        core.GetCop0(BadVAddr).Should().Be(0x8000_2002u);
    }

    [Fact]
    public void ANclipCommand_RunsAgainstTheGraphsGte()
    {
        _graph.Gte.WriteDataRegister(12, 0x0000_0000);
        _graph.Gte.WriteDataRegister(13, 0x0000_000A);
        _graph.Gte.WriteDataRegister(14, 0x000A_0000);
        var core = Load(SrCu2, Command(0x0140_0006), Mfc2(T1, 24), 0);

        Steps(core, 3);

        core.GetGpr(T1).Should().Be(100u);
        _graph.Gte.CommandsExecuted.Should().Be(1UL);
    }

    [Fact]
    public void AnUnimplementedCommand_StopsTheCpu_WithADistinctStatus_AndRetiresNothing()
    {
        var core = Load(SrCu2, Command(0x0028_0030)); // RTPT, not implemented yet

        core.Step().Should().Be(PSXCoreWrapper.StepGteCommandUnsupported);

        core.ExceptionRaised.Should().BeFalse("it is not a guest exception, the emulator refuses");
        core.Pc.Should().Be(Base, "nothing retired");
        _graph.Gte.LastUnsupportedCommand.Should().Be(0x0028_0030u);
    }

    [Fact]
    public void WithoutAnAttachedGte_Cop2StaysUnusable_EvenWithSrCu2Set()
    {
        using var bare = new PSXCoreWrapper();
        bare.WriteMemory32(Base, Ctc2(T0, 29));
        bare.SetCop0(Sr, SrCu2);
        bare.Pc = Base;

        bare.Step().Should().Be(0);

        bare.ExceptionRaised.Should().BeTrue();
        bare.ExceptionCode.Should().Be(0x0Bu);
    }
}

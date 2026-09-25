using PSXRecomp.Core;

namespace PSXRecomp.Tests;

/// <summary>
/// Production-path regression coverage for Issue #445: real R3000A load/store
/// instructions must reach the Rust-owned SPU register store through
/// PSXCore -> PSXMemory, not only through the managed MemoryBus seam.
/// </summary>
[Test]
public class SpuCpuEndToEndTests : IDisposable
{
    private const uint SpuBase = 0x1F801C00;
    private const int Zero = 0;
    private const int T0 = 8;
    private const int T1 = 9;
    private const int T2 = 10;
    private const uint Nop = 0;

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
    private static uint Lbu(int rt, int rs, ushort offset) => (0x24u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lhu(int rt, int rs, ushort offset) => (0x25u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;
    private static uint Lw(int rt, int rs, ushort offset) => (0x23u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | offset;

    private void Run(params uint[] body)
    {
        var program = new List<uint>
        {
            Lui(T0, 0x1F80),
            Ori(T0, T0, 0x1C00),
        };
        program.AddRange(body);
        program.Add(Nop);

        for (var i = 0; i < program.Count; i++)
            _core.WriteMemory32((uint)(i * 4), program[i]);

        _core.Pc = 0;
        for (var i = 0; i < program.Count; i++)
        {
            _core.Step().Should().Be(0, $"instruction {i} (0x{program[i]:X8}) should execute");
            _core.ExceptionRaised.Should().BeFalse($"instruction {i} (0x{program[i]:X8}) should not raise a guest exception");
        }
    }

    [Fact]
    public void BaseAddressBuild_ProducesDocumentedSpuBase()
    {
        Run();
        _core.GetGpr(T0).Should().Be(SpuBase);
    }

    [Fact]
    public void ShThenLhu_RoundTripsControlRegisterThroughRealCpu()
    {
        Run(
            Ori(T1, Zero, 0xBEEF),
            Sh(T1, T0, 0x0180),
            Lhu(T2, T0, 0x0180));

        _core.GetGpr(T2).Should().Be(0xBEEFu);
    }

    [Fact]
    public void SwThenLw_RoundTripsTwoAdjacentRegistersLittleEndian()
    {
        Run(
            Lui(T1, 0xAABB),
            Ori(T1, T1, 0xCCDD),
            Sw(T1, T0, 0x0180),
            Lw(T2, T0, 0x0180));

        _core.GetGpr(T2).Should().Be(0xAABBCCDDu);
        _core.ReadMemory16(SpuBase + 0x0180).Should().Be(0xCCDD);
        _core.ReadMemory16(SpuBase + 0x0182).Should().Be(0xAABB);
    }

    [Fact]
    public void SbOnHighByte_UpdatesOnlyThatLane()
    {
        Run(
            Ori(T1, Zero, 0x1234),
            Sh(T1, T0, 0x0180),
            Ori(T1, Zero, 0x00AB),
            Sb(T1, T0, 0x0181),
            Lhu(T2, T0, 0x0180));

        _core.GetGpr(T2).Should().Be(0xAB34u);

        Run(Lbu(T2, T0, 0x0181));
        _core.GetGpr(T2).Should().Be(0xABu);
    }

    [Fact]
    public void LastRegisterInWindow_RoundTripsThroughCpuHalfwordAccess()
    {
        Run(
            Ori(T1, Zero, 0x55AA),
            Sh(T1, T0, 0x01FE),
            Lhu(T2, T0, 0x01FE));

        _core.GetGpr(T2).Should().Be(0x55AAu);
    }

    [Fact]
    public void CoreReset_ClearsSpuStateObservedThroughCpuLoad()
    {
        Run(
            Ori(T1, Zero, 0xCAFE),
            Sh(T1, T0, 0x0180));

        _core.Reset();

        Run(Lhu(T2, T0, 0x0180));
        _core.GetGpr(T2).Should().Be(0u);
    }
}

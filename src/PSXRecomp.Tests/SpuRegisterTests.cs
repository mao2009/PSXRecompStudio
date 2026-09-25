using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

/// <summary>
/// Contract coverage for Issue #445's register-only SPU model. The register
/// state lives in native Rust inside PSXMemory; MemoryBus routes to the same
/// production memory path rather than maintaining a second managed model.
/// </summary>
[Test]
public class SpuRegisterTests : IDisposable
{
    private const uint Base = 0x1F801C00;
    private const uint End = 0x1F801E00;

    private readonly PSXCoreWrapper _core = new();
    private readonly MemoryBus _bus;

    public SpuRegisterTests()
    {
        _bus = new MemoryBus(_core);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(0x1F801C00u, SpuRegisterType.Voice, 0x000u)]
    [InlineData(0x1F801D7Fu, SpuRegisterType.Voice, 0x17Fu)]
    [InlineData(0x1F801D80u, SpuRegisterType.Control, 0x180u)]
    [InlineData(0x1F801DBFu, SpuRegisterType.Control, 0x1BFu)]
    [InlineData(0x1F801DC0u, SpuRegisterType.Reverb, 0x1C0u)]
    [InlineData(0x1F801DFFu, SpuRegisterType.Reverb, 0x1FFu)]
    public void Resolve_SpuWindow_RoutesEveryCategoryToSpu(uint address, SpuRegisterType type, uint offset)
    {
        var route = MmioRoute.Resolve(address);
        route.Target.Should().Be(MmioTarget.Spu);
        route.SpuRegisterType.Should().Be(type);
        route.Offset.Should().Be(offset);
    }

    [Theory]
    [InlineData(0x1F801BFFu)]
    [InlineData(0x1F801E00u)]
    public void Resolve_OutsideSpuWindow_IsNotSpu(uint address)
    {
        MmioRoute.Resolve(address).Target.Should().NotBe(MmioTarget.Spu);
        Ps1MemoryMap.GetSpuRegisterType(address).Should().Be(SpuRegisterType.None);
    }

    [Fact]
    public void Reset_AllRepresentativeRegistersReadZero()
    {
        _bus.Read16(Base).Should().Be(0);
        _bus.Read16(Base + 0x180).Should().Be(0);
        _bus.Read16(Base + 0x1C0).Should().Be(0);
        _bus.Read16(End - 2).Should().Be(0);
    }

    [Fact]
    public void HalfwordWrites_RoundTripAcrossVoiceControlAndReverb()
    {
        _bus.Write16(Base + 0x000, 0x1111);
        _bus.Write16(Base + 0x180, 0x2222);
        _bus.Write16(Base + 0x1C0, 0x3333);
        _bus.Write16(End - 2, 0x4444);

        _bus.Read16(Base + 0x000).Should().Be(0x1111);
        _bus.Read16(Base + 0x180).Should().Be(0x2222);
        _bus.Read16(Base + 0x1C0).Should().Be(0x3333);
        _bus.Read16(End - 2).Should().Be(0x4444);
    }

    [Fact]
    public void ByteWrites_UpdateOnlyTheSelectedRegisterLane()
    {
        var address = Base + 0x180;
        _bus.Write16(address, 0x1234);

        _bus.Write8(address, 0xAA);
        _bus.Read16(address).Should().Be(0x12AA);

        _bus.Write8(address + 1, 0xBB);
        _bus.Read16(address).Should().Be(0xBBAA);
        _bus.Read8(address).Should().Be(0xAA);
        _bus.Read8(address + 1).Should().Be(0xBB);
    }

    [Fact]
    public void WordWrite_RoundTripsTwoAdjacentRegistersLittleEndian()
    {
        var address = Base + 0x180;
        _bus.Write(address, 0xAABBCCDD);

        _bus.Read16(address).Should().Be(0xCCDD);
        _bus.Read16(address + 2).Should().Be(0xAABB);
        _bus.Read(address).Should().Be(0xAABBCCDD);
    }

    [Fact]
    public void HalfwordWrite_DoesNotClobberAdjacentRegister()
    {
        var address = Base + 0x180;
        _bus.Write16(address, 0x1111);
        _bus.Write16(address + 2, 0x2222);

        _bus.Write16(address, 0xABCD);

        _bus.Read16(address).Should().Be(0xABCD);
        _bus.Read16(address + 2).Should().Be(0x2222);
    }

    [Fact]
    public void CoreReset_ClearsSpuRegisterState()
    {
        _bus.Write16(Base + 0x000, 0x1111);
        _bus.Write16(Base + 0x180, 0x2222);
        _bus.Write16(End - 2, 0x3333);

        _core.Reset();

        _bus.Read16(Base + 0x000).Should().Be(0);
        _bus.Read16(Base + 0x180).Should().Be(0);
        _bus.Read16(End - 2).Should().Be(0);
    }

    [Fact]
    public void EntireHalfwordWindow_IsDeterministicStorage()
    {
        for (uint address = Base; address < End; address += 2)
            _bus.Write16(address, (ushort)((address - Base) ^ 0x5A5A));

        for (uint address = Base; address < End; address += 2)
            _bus.Read16(address).Should().Be((ushort)((address - Base) ^ 0x5A5A));
    }
}

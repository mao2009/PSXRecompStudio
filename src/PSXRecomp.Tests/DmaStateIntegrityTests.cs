using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class DmaStateIntegrityTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _adapter;

    public DmaStateIntegrityTests()
    {
        _adapter = new DmaMmioAdapter(_core);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteAndRead_Madr_StateConsistent()
    {
        for (int ch = 0; ch < 7; ch++)
        {
            var address = Ps1MemoryMap.GetChannelMadr(ch);
            _adapter.WriteRegister(address, 0x1000 + (uint)ch);
            var nativeValue = _core.ReadDmaRegister(address);
            nativeValue.Should().Be((uint)(0x1000 + ch),
                $"native state should match MMIO adapter for channel {ch} MADR");
        }
    }

    [Fact]
    public void WriteAndRead_Bcr_StateConsistent()
    {
        for (int ch = 0; ch < 7; ch++)
        {
            var address = Ps1MemoryMap.GetChannelBcr(ch);
            _adapter.WriteRegister(address, 0x2000 + (uint)ch);
            var nativeValue = _core.ReadDmaRegister(address);
            nativeValue.Should().Be((uint)(0x2000 + ch),
                $"native state should match MMIO adapter for channel {ch} BCR");
        }
    }

    [Fact]
    public void WriteAndRead_Chcr_StateConsistent()
    {
        for (int ch = 0; ch < 7; ch++)
        {
            var address = Ps1MemoryMap.GetChannelChcr(ch);
            _adapter.WriteRegister(address, 0x3000 + (uint)ch);
            var nativeValue = _core.ReadDmaRegister(address);
            nativeValue.Should().Be((uint)(0x3000 + ch),
                $"native state should match MMIO adapter for channel {ch} CHCR");
        }
    }

    [Fact]
    public void WriteAndRead_Dpcr_StateConsistent()
    {
        _adapter.WriteRegister(Ps1MemoryMap.Dpcr, 0xDEADBEEF);
        var nativeValue = _core.ReadDmaRegister(Ps1MemoryMap.Dpcr);
        nativeValue.Should().Be(0xDEADBEEFu);
    }

    [Fact]
    public void WriteAndRead_Dicr_StateConsistent()
    {
        uint masterEnable = 1u << 15;
        uint ch0Enable = 1u << 16;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable | ch0Enable);
        var readValue = _adapter.ReadRegister(Ps1MemoryMap.Dicr);
        readValue.Should().Be(masterEnable | ch0Enable,
            "writable control bits should round-trip, bit 31 derived as 0 with no active flags");
        readValue.Should().Be(_core.ReadDmaRegister(Ps1MemoryMap.Dicr),
            "adapter and native should agree on derived DICR value");
    }

    [Fact]
    public void Reset_ClearsDmaState()
    {
        _adapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(0), 0xFFFFFFFF);
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, 0xFFFFFFFF);
        _core.Reset();
        _adapter.ReadRegister(Ps1MemoryMap.GetChannelMadr(0)).Should().Be(0u);
        _adapter.ReadRegister(Ps1MemoryMap.Dicr).Should().Be(0u);
    }

    [Fact]
    public void InterruptPending_Bit31ReadOnly()
    {
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, 1u << 31);
        _adapter.GetInterruptPending().Should().BeFalse("bit 31 is read-only status, not settable by CPU write");
        _adapter.ReadRegister(Ps1MemoryMap.Dicr).Should().Be(0u, "no writable bits were set in the write value");
    }

    [Fact]
    public void InterruptPending_MasterEnableAndFlagSet()
    {
        uint masterEnable = 1u << 15;
        uint ch0Enable = 1u << 16;
        uint forceIrq = 1u << 23;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable | ch0Enable | forceIrq);
        _adapter.GetInterruptPending().Should().BeTrue("master enable + force IRQ should trigger interrupt");
    }

    [Fact]
    public void InterruptPending_FlagWriteOneToClear()
    {
        uint masterEnable = 1u << 15;
        uint forceIrq = 1u << 23;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable | forceIrq);
        _adapter.GetInterruptPending().Should().BeTrue();
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable);
        _adapter.GetInterruptPending().Should().BeFalse("clearing force IRQ should stop interrupt");
    }

    [Fact]
    public void InterruptPending_ForceIrq()
    {
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, 1u << 23);
        _adapter.GetInterruptPending().Should().BeTrue("force IRQ bit should trigger interrupt regardless of flags");
    }

    [Fact]
    public void ChannelStride_IsCorrect()
    {
        Ps1MemoryMap.ChannelStride.Should().Be(0x10u);
    }
}

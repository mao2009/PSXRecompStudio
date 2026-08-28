using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class MemoryBusTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _dmaAdapter;
    private readonly TimerMmioAdapter _timerAdapter;
    private readonly MemoryBus _memoryBus;

    public MemoryBusTests()
    {
        _dmaAdapter = new DmaMmioAdapter(_core);
        _timerAdapter = new TimerMmioAdapter(_core);
        _memoryBus = new MemoryBus(_core);
        _memoryBus.AttachDmaAdapter(_dmaAdapter);
        _memoryBus.AttachTimerAdapter(_timerAdapter);
    }

    public void Dispose()
    {
        _memoryBus.Dispose();
        _timerAdapter.Dispose();
        _dmaAdapter.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Read_Ram_ReturnsValue()
    {
        unsafe
        {
            var ptr = (uint*)_core.RamPointer;
            ptr[0] = 0xDEADBEEF;
        }
        _memoryBus.Read(0x00000000).Should().Be(0xDEADBEEFu);
    }

    [Fact]
    public void Write_Ram_ThenRead_ReturnsWrittenValue()
    {
        _memoryBus.Write(0x00000100, 0xCAFEBABE);
        _memoryBus.Read(0x00000100).Should().Be(0xCAFEBABEu);
    }

    [Fact]
    public void Read_Ram_AlignsToWordBoundary()
    {
        _memoryBus.Write(0x00000004, 0x12345678);
        _memoryBus.Read(0x00000004).Should().Be(0x12345678u);
    }

    [Fact]
    public void Read_DmaRegister_RoutesToAdapter()
    {
        _dmaAdapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(0), 0xAABBCCDD);
        _memoryBus.Read(Ps1MemoryMap.GetChannelMadr(0)).Should().Be(0xAABBCCDDu);
    }

    [Fact]
    public void Write_DmaRegister_RoutesToAdapter()
    {
        _memoryBus.Write(Ps1MemoryMap.GetChannelChcr(1), 0x11223344);
        _dmaAdapter.ReadRegister(Ps1MemoryMap.GetChannelChcr(1)).Should().Be(0x11223344u);
    }

    [Fact]
    public void Write_DmaRegister_TriggersInterruptCallback()
    {
        var callbackInvoked = false;
        _dmaAdapter.SetInterruptCallback(_ => { callbackInvoked = true; });

        uint masterEnable = 1u << 23;
        uint forceIrq = 1u << 15;
        _memoryBus.Write(Ps1MemoryMap.Dicr, masterEnable | forceIrq);

        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public void Read_UnmappedAddress_ReturnsZero()
    {
        _memoryBus.Read(0x20000000).Should().Be(0u);
    }

    [Fact]
    public void Write_UnmappedAddress_DoesNotThrow()
    {
        var act = () => _memoryBus.Write(0x20000000, 0x12345678);
        act.Should().NotThrow();
    }

    [Fact]
    public void ImplementsIMemoryBus()
    {
        IMemoryBus bus = _memoryBus;
        bus.Should().NotBeNull();
    }

    [Fact]
    public void FullRoute_PhysicalAddress_ToDmaRegister()
    {
        var dmaAddress = Ps1MemoryMap.GetChannelMadr(3);
        _memoryBus.Write(dmaAddress, 0xFEEDFACE);
        var result = _memoryBus.Read(dmaAddress);
        result.Should().Be(0xFEEDFACEu);
    }

    [Fact]
    public void MultipleDmaChannels_ThroughMemoryBus()
    {
        for (int ch = 0; ch < 7; ch++)
        {
            var madr = Ps1MemoryMap.GetChannelMadr(ch);
            var value = (uint)(ch + 1) * 0x10000;
            _memoryBus.Write(madr, value);
            _memoryBus.Read(madr).Should().Be(value);
        }
    }

    [Fact]
    public void Dpcr_ThroughMemoryBus()
    {
        _memoryBus.Write(Ps1MemoryMap.Dpcr, 0x12345678);
        _memoryBus.Read(Ps1MemoryMap.Dpcr).Should().Be(0x12345678u);
    }

    [Fact]
    public void Dicr_ThroughMemoryBus()
    {
        uint masterEnable = 1u << 23;
        uint ch0Enable = 1u << 24;
        uint forceIrq = 1u << 15;
        uint writableBits = masterEnable | ch0Enable | forceIrq;
        _memoryBus.Write(Ps1MemoryMap.Dicr, writableBits);
        uint expected = writableBits | (1u << 31);
        _memoryBus.Read(Ps1MemoryMap.Dicr).Should().Be(expected);
    }

    [Fact]
    public void Dispose_PreventsFurtherAccess()
    {
        var bus = new MemoryBus(_core);
        bus.AttachDmaAdapter(_dmaAdapter);
        bus.Dispose();
        var act = () => bus.Read(0x00000000);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Read_TimerCount_RoutesToAdapter()
    {
        uint addr = Ps1MemoryMap.GetTimerBase(0) + Ps1MemoryMap.TimerCountOffset;
        _timerAdapter.WriteRegister(addr, 0x1234);
        _memoryBus.Read(addr).Should().Be(0x1234u);
    }

    [Fact]
    public void Write_TimerMode_RoutesToAdapter_AndSetsIrqRequestBit()
    {
        uint addr = Ps1MemoryMap.GetTimerBase(1) + Ps1MemoryMap.TimerModeOffset;
        _memoryBus.Write(addr, 0x0030);
        _timerAdapter.GetMode((Core.Runtime.ITimer.TimerId)1).Should().Be(0x0030 | 0x0400);
    }

    [Fact]
    public void Write_TimerTarget_RoutesToAdapter()
    {
        uint addr = Ps1MemoryMap.GetTimerBase(2) + Ps1MemoryMap.TimerTargetOffset;
        _memoryBus.Write(addr, 0x7FFF);
        _timerAdapter.GetTarget((Core.Runtime.ITimer.TimerId)2).Should().Be(0x7FFFu);
    }

    [Fact]
    public void Timer_TickThroughAdapter_ReflectedInCountRead()
    {
        uint countAddr = Ps1MemoryMap.GetTimerBase(0) + Ps1MemoryMap.TimerCountOffset;
        _memoryBus.Write(countAddr, 0);
        _timerAdapter.Tick(3);
        _memoryBus.Read(countAddr).Should().Be(3u);
    }
}

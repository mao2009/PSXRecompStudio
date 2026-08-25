using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class DmaMmioAdapterTests : IDisposable
{
    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _adapter;

    public DmaMmioAdapterTests()
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
    public void ReadRegister_ReturnsZero_ForUninitializedDma()
    {
        var value = _adapter.ReadRegister(Ps1MemoryMap.GetChannelMadr(0));
        value.Should().Be(0u);
    }

    [Fact]
    public void WriteRegister_ThenReadRegister_ReturnsWrittenValue()
    {
        var address = Ps1MemoryMap.GetChannelMadr(0);
        _adapter.WriteRegister(address, 0x12345678);
        var value = _adapter.ReadRegister(address);
        value.Should().Be(0x12345678u);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void WriteRegister_Madr_ThenRead_ReturnsCorrectValue(int channel)
    {
        var address = Ps1MemoryMap.GetChannelMadr(channel);
        var testValue = (uint)(channel + 1) * 0x1000;
        _adapter.WriteRegister(address, testValue);
        _adapter.ReadRegister(address).Should().Be(testValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void WriteRegister_Bcr_ThenRead_ReturnsCorrectValue(int channel)
    {
        var address = Ps1MemoryMap.GetChannelBcr(channel);
        var testValue = (uint)(channel + 1) * 0x2000;
        _adapter.WriteRegister(address, testValue);
        _adapter.ReadRegister(address).Should().Be(testValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void WriteRegister_Chcr_ThenRead_ReturnsCorrectValue(int channel)
    {
        var address = Ps1MemoryMap.GetChannelChcr(channel);
        var testValue = (uint)(channel + 1) * 0x3000;
        _adapter.WriteRegister(address, testValue);
        _adapter.ReadRegister(address).Should().Be(testValue);
    }

    [Fact]
    public void WriteRegister_Dpcr_ThenRead_ReturnsWrittenValue()
    {
        _adapter.WriteRegister(Ps1MemoryMap.Dpcr, 0xDEADBEEF);
        _adapter.ReadRegister(Ps1MemoryMap.Dpcr).Should().Be(0xDEADBEEFu);
    }

    [Fact]
    public void WriteRegister_Dicr_ThenRead_ReturnsDerivedValue()
    {
        uint masterEnable = 1u << 23;
        uint ch0Enable = 1u << 24;
        uint forceIrq = 1u << 15;
        uint writableBits = masterEnable | ch0Enable | forceIrq;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, writableBits);
        uint expected = writableBits | (1u << 31);
        _adapter.ReadRegister(Ps1MemoryMap.Dicr).Should().Be(expected);
    }

    [Fact]
    public void Dpcr_HasCorrectDefaultValue()
    {
        _adapter.ReadRegister(Ps1MemoryMap.Dpcr).Should().Be(0x07654321u);
    }

    [Fact]
    public void Dicr_HasZeroDefaultValue()
    {
        _adapter.ReadRegister(Ps1MemoryMap.Dicr).Should().Be(0u);
    }

    [Fact]
    public void GetInterruptPending_False_WhenNoInterrupt()
    {
        _adapter.GetInterruptPending().Should().BeFalse();
    }

    [Fact]
    public void GetInterruptPending_True_WhenForceIrqSet()
    {
        uint masterEnable = 1u << 23;
        uint forceIrq = 1u << 15;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable | forceIrq);
        _adapter.GetInterruptPending().Should().BeTrue();
    }

    [Fact]
    public void SetInterruptCallback_IsCalledOnInterrupt()
    {
        var callbackInvoked = false;
        uint callbackAddress = 0;
        _adapter.SetInterruptCallback(addr =>
        {
            callbackInvoked = true;
            callbackAddress = addr;
        });

        uint masterEnable = 1u << 23;
        uint forceIrq = 1u << 15;
        _adapter.WriteRegister(Ps1MemoryMap.Dicr, masterEnable | forceIrq);

        callbackInvoked.Should().BeTrue();
        callbackAddress.Should().Be(Ps1MemoryMap.Dicr);
    }

    [Fact]
    public void SetInterruptCallback_NotCalledWhenNoInterrupt()
    {
        var callbackInvoked = false;
        _adapter.SetInterruptCallback(_ => { callbackInvoked = true; });

        _adapter.WriteRegister(Ps1MemoryMap.Dicr, 0);

        callbackInvoked.Should().BeFalse();
    }

    [Fact]
    public void ImplementsIMemoryBus()
    {
        IMemoryBus bus = _adapter;
        bus.Should().NotBeNull();
    }

    [Fact]
    public void ImplementsIDmaController()
    {
        IDmaController controller = _adapter;
        controller.Should().NotBeNull();
    }

    [Fact]
    public void IMemoryBus_Read_DelegatesToDmaRegister()
    {
        IMemoryBus bus = _adapter;
        _adapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0xAABBCCDD);
        bus.Read(Ps1MemoryMap.GetChannelMadr(2)).Should().Be(0xAABBCCDDu);
    }

    [Fact]
    public void IMemoryBus_Write_DelegatesToDmaRegister()
    {
        IMemoryBus bus = _adapter;
        bus.Write(Ps1MemoryMap.GetChannelChcr(3), 0x11223344);
        _adapter.ReadRegister(Ps1MemoryMap.GetChannelChcr(3)).Should().Be(0x11223344u);
    }

    [Fact]
    public void Dispose_PreventsFurtherAccess()
    {
        var adapter = new DmaMmioAdapter(_core);
        adapter.Dispose();
        var act = () => adapter.ReadRegister(Ps1MemoryMap.Dicr);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void MultipleChannels_IndependentState()
    {
        _adapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(0), 0x1111);
        _adapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(1), 0x2222);
        _adapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0x3333);

        _adapter.ReadRegister(Ps1MemoryMap.GetChannelMadr(0)).Should().Be(0x1111u);
        _adapter.ReadRegister(Ps1MemoryMap.GetChannelMadr(1)).Should().Be(0x2222u);
        _adapter.ReadRegister(Ps1MemoryMap.GetChannelMadr(2)).Should().Be(0x3333u);
    }
}

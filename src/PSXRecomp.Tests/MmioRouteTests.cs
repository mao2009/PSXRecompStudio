using PSXRecomp.Core;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class MmioRouteTests
{
    [Fact]
    public void Resolve_DmaMadr_ReturnsDmaControllerTarget()
    {
        var route = MmioRoute.Resolve(0x1F801080u);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.RegisterType.Should().Be(DmaRegisterType.Madr);
        route.ChannelIndex.Should().Be(0);
    }

    [Fact]
    public void Resolve_DmaBcr_ReturnsCorrectRegisterType()
    {
        var route = MmioRoute.Resolve(0x1F801084u);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.RegisterType.Should().Be(DmaRegisterType.Bcr);
        route.ChannelIndex.Should().Be(0);
    }

    [Fact]
    public void Resolve_DmaChcr_ReturnsCorrectRegisterType()
    {
        var route = MmioRoute.Resolve(0x1F801088u);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.RegisterType.Should().Be(DmaRegisterType.Chcr);
        route.ChannelIndex.Should().Be(0);
    }

    [Fact]
    public void Resolve_Dpcr_ReturnsDpcrType()
    {
        var route = MmioRoute.Resolve(0x1F8010F0u);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.RegisterType.Should().Be(DmaRegisterType.Dpcr);
        route.ChannelIndex.Should().Be(-1);
    }

    [Fact]
    public void Resolve_Dicr_ReturnsDicrType()
    {
        var route = MmioRoute.Resolve(0x1F8010F4u);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.RegisterType.Should().Be(DmaRegisterType.Dicr);
        route.ChannelIndex.Should().Be(-1);
    }

    [Theory]
    [InlineData(0x1F801090u, 1)]
    [InlineData(0x1F8010A0u, 2)]
    [InlineData(0x1F8010B0u, 3)]
    [InlineData(0x1F8010C0u, 4)]
    [InlineData(0x1F8010D0u, 5)]
    [InlineData(0x1F8010E0u, 6)]
    public void Resolve_ChannelAddresses_ReturnsCorrectChannelIndex(uint address, int expectedChannel)
    {
        var route = MmioRoute.Resolve(address);
        route.ChannelIndex.Should().Be(expectedChannel);
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x1F801070u)]
    [InlineData(0x1F802000u)]
    public void Resolve_NonDmaAddress_ReturnsUnmapped(uint address)
    {
        var route = MmioRoute.Resolve(address);
        route.Target.Should().Be(MmioTarget.None);
    }

    [Fact]
    public void Unmapped_HasNoneTarget()
    {
        MmioRoute.Unmapped.Target.Should().Be(MmioTarget.None);
    }

    [Fact]
    public void ForDma_CreatesCorrectRoute()
    {
        var route = MmioRoute.ForDma(3, DmaRegisterType.Madr, 0);
        route.Target.Should().Be(MmioTarget.DmaController);
        route.ChannelIndex.Should().Be(3);
        route.RegisterType.Should().Be(DmaRegisterType.Madr);
    }
}

using PSXRecomp.Core.Dma;

namespace PSXRecomp.Tests;

[Test]
public class GpuMmioRouteTests
{
    [Fact]
    public void Resolve_Gp0_ReturnsGpuDataRegister()
    {
        var route = MmioRoute.Resolve(0x1F801810u);
        route.Target.Should().Be(MmioTarget.Gpu);
        route.GpuRegisterType.Should().Be(GpuRegisterType.Data);
        route.Offset.Should().Be(0u);
    }

    [Fact]
    public void Resolve_Gp1_ReturnsGpuStatusRegister()
    {
        var route = MmioRoute.Resolve(0x1F801814u);
        route.Target.Should().Be(MmioTarget.Gpu);
        route.GpuRegisterType.Should().Be(GpuRegisterType.Status);
        route.Offset.Should().Be(4u);
    }

    [Theory]
    [InlineData(0x1F801818u, GpuRegisterType.Data, 8u)]
    [InlineData(0x1F80181Cu, GpuRegisterType.Status, 12u)]
    public void Resolve_MirrorPorts_MapToSameHandlers(uint address, GpuRegisterType type, uint offset)
    {
        var route = MmioRoute.Resolve(address);
        route.Target.Should().Be(MmioTarget.Gpu);
        route.GpuRegisterType.Should().Be(type);
        route.Offset.Should().Be(offset);
    }

    [Fact]
    public void Resolve_GpuPorts_DistinguishDataFromStatus()
    {
        MmioRoute.Resolve(0x1F801810u).GpuRegisterType.Should().NotBe(MmioRoute.Resolve(0x1F801814u).GpuRegisterType);
    }

    [Theory]
    [InlineData(0x1F801808u)]
    [InlineData(0x1F80180Cu)]
    [InlineData(0x1F801820u)]
    public void Resolve_NonGpuNeighbor_ReturnsUnmapped(uint address)
    {
        MmioRoute.Resolve(address).Target.Should().Be(MmioTarget.None);
    }

    [Fact]
    public void ForGpu_CreatesCorrectRoute()
    {
        var route = MmioRoute.ForGpu(GpuRegisterType.Status, 4);
        route.Target.Should().Be(MmioTarget.Gpu);
        route.GpuRegisterType.Should().Be(GpuRegisterType.Status);
        route.Offset.Should().Be(4u);
    }
}
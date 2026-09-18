using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

[Test]
public class GpuMemoryBusTests : IDisposable
{
    private const uint Gp0 = 0x1F801810;
    private const uint Gp1 = 0x1F801814;

    private readonly PSXCoreWrapper _core = new();
    private readonly GpuDevice _device = new();
    private readonly GpuMmioAdapter _adapter;
    private readonly MemoryBus _memoryBus;

    public GpuMemoryBusTests()
    {
        _adapter = new GpuMmioAdapter(_device);
        _memoryBus = new MemoryBus(_core);
        _memoryBus.AttachGpuAdapter(_adapter);
    }

    public void Dispose()
    {
        _memoryBus.Dispose();
        _adapter.Dispose();
        _device.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Read_Gpustat_RoutesToAdapter()
    {
        _memoryBus.Read(Gp1).Should().Be(_adapter.ReadGpustat());
    }

    [Fact]
    public void Write_Gp1_DisplayDisable_RoutesToDevice()
    {
        _memoryBus.Write(Gp1, 0x03000001);
        ((_memoryBus.Read(Gp1) >> 23) & 1).Should().Be(1u);
    }

    [Fact]
    public void Write_Gp0_RoutesToDevice()
    {
        _memoryBus.Write(Gp0, 0x1F000000); // IRQ1
        ((_memoryBus.Read(Gp1) >> 24) & 1).Should().Be(1u);

        _memoryBus.Write(Gp1, 0x02000000); // acknowledge
        ((_memoryBus.Read(Gp1) >> 24) & 1).Should().Be(0u);
    }

    [Fact]
    public void Read_Gpuread_RoutesToDataPort()
    {
        _memoryBus.Write(Gp1, 0x10000007); // query GPU version
        _memoryBus.Read(Gp0).Should().Be(2u);
    }

    [Fact]
    public void Write_MirrorPorts_RouteToSameHandlers()
    {
        _memoryBus.Write(0x1F80181C, 0x03000001); // Gp1 mirror => display disable
        ((_memoryBus.Read(0x1F801814) >> 23) & 1).Should().Be(1u);

        _memoryBus.Write(0x1F801818, 0x1F000000); // Gp0 mirror => IRQ1
        ((_memoryBus.Read(0x1F801814) >> 24) & 1).Should().Be(1u);
    }

    [Fact]
    public void GpuWrites_DoNotAffectDmaRegisters()
    {
        _memoryBus.Write(Gp0, 0x1F000000);
        _memoryBus.Read(0x1F801080).Should().Be(0u); // DMA0 MADR untouched
    }
}
using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// A0:49 GPU_cw(cmd) — OpenBIOS openbios/gpu/gpu.c: GPU_sync, then GP0 write. Issue #701.
[Test]
public sealed class BiosGpuCommandServiceTests
{
    private sealed class FakeDevices : IGuestDeviceAccess
    {
        public Dictionary<uint, uint> Registers { get; } = new();
        public List<(uint Address, uint Value)> Writes { get; } = [];

        public bool TryRead32(uint physicalAddress, out uint value) => Registers.TryGetValue(physicalAddress, out value);

        public bool TryWrite32(uint physicalAddress, uint value)
        {
            Writes.Add((physicalAddress, value));
            return true;
        }
    }

    private static BiosHleRuntime Runtime(FakeDevices? devices)
    {
        var ram = new RecompilerGuestMemory();
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), new GuestMemoryReader(ram.Read8), new GuestMemoryWriter(ram.Write8));
        if (devices is not null)
        {
            runtime.AttachDevices(devices);
        }

        return runtime;
    }

    private static BiosServiceResult GpuCw(BiosHleRuntime runtime, params uint[] args) =>
        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.A0, BiosHleRuntime.GpuCommandWordFunction, arguments: args));

    [Fact]
    public void Foreground_Sync_Ready_Writes_The_Word_To_Gp0_And_Returns_Zero()
    {
        var devices = new FakeDevices();
        devices.Registers[BiosGpuCommandService.GpuStatusAddress] = 0x14802000; // bit 28 set, direction 0

        var result = GpuCw(Runtime(devices), 0xE1000000);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0u);
        devices.Writes.Should().Equal([(BiosGpuCommandService.Gp0Address, 0xE1000000u)]);
    }

    [Fact]
    public void Foreground_Sync_Not_Ready_Fails_Closed_And_Writes_Nothing()
    {
        var devices = new FakeDevices();
        devices.Registers[BiosGpuCommandService.GpuStatusAddress] = 0x04802000; // bit 28 clear

        var result = GpuCw(Runtime(devices), 1);

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        devices.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Background_Sync_Waits_For_Dma2_Then_Switches_Dma_Direction_Off_Before_The_Write()
    {
        var devices = new FakeDevices();
        devices.Registers[BiosGpuCommandService.GpuStatusAddress] = 0x24000000 | 0x04000000; // direction 1, bit 26
        devices.Registers[BiosGpuCommandService.Dma2ChcrAddress] = 0;

        var result = GpuCw(Runtime(devices), 0x02000000);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        devices.Writes.Should().Equal(
            (BiosGpuCommandService.GpuStatusAddress, 0x04000000u),
            (BiosGpuCommandService.Gp0Address, 0x02000000u));
    }

    [Fact]
    public void Background_Sync_With_Busy_Dma2_Fails_Closed()
    {
        var devices = new FakeDevices();
        devices.Registers[BiosGpuCommandService.GpuStatusAddress] = 0x24000000;
        devices.Registers[BiosGpuCommandService.Dma2ChcrAddress] = 0x01000000;

        GpuCw(Runtime(devices), 1).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        devices.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Without_Attached_Devices_The_Call_Fails_Closed()
    {
        GpuCw(Runtime(null), 1).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    [Fact]
    public void An_Unmodelled_Gpustat_Fails_Closed()
    {
        var devices = new FakeDevices();

        GpuCw(Runtime(devices), 1).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        devices.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Wrong_Argument_Count_Is_Invalid(int count)
    {
        var devices = new FakeDevices();
        devices.Registers[BiosGpuCommandService.GpuStatusAddress] = 0x14802000;

        GpuCw(Runtime(devices), new uint[count]).Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        devices.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Arity_Is_One()
    {
        Runtime(null).TryGetServiceArgumentCount(BiosCallFamily.A0, BiosHleRuntime.GpuCommandWordFunction, out var count).Should().BeTrue();
        count.Should().Be(1);
    }

    [Fact]
    public void Against_The_Real_Device_Graph_A_Reset_Gpu_Accepts_The_Word_Through_Gp0()
    {
        using var graph = new PsxDeviceGraph();
        var runtime = Runtime(null);
        runtime.AttachDevices(graph);
        graph.TryWrite(BiosGpuCommandService.GpuStatusAddress, 4, 0).Should().Be(PsxDeviceAccessStatus.Completed); // GP1(00h) reset

        var result = GpuCw(runtime, 0xE1000000);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        graph.GpuDevice.LastResultOpcode.Should().Be(0xE1);
    }
}

using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.Sio;

namespace PSXRecomp.Tests;

/// <summary>SIO0 guest-visible register model (Issue #542).</summary>
[Test]
public class Sio0RegisterTests : IDisposable
{
    private const uint Data = 0x1F801040;
    private const uint Stat = 0x1F801044;
    private const uint Mode = 0x1F801048;
    private const uint Ctrl = 0x1F80104A;
    private const uint Baud = 0x1F80104E;
    private const uint IdleStatus = 0x00000005; // TX ready 1 + TX ready 2

    private readonly PSXCoreWrapper _core = new();
    private readonly Sio0Device _device = new();
    private readonly MemoryBus _bus;

    public Sio0RegisterTests()
    {
        _bus = new MemoryBus(_core);
        _bus.AttachSio0Adapter(new Sio0MmioAdapter(_device));
    }

    public void Dispose()
    {
        _bus.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(Data, Sio0RegisterType.Data, 0x0u)]
    [InlineData(Stat, Sio0RegisterType.Status, 0x4u)]
    [InlineData(Mode, Sio0RegisterType.Mode, 0x8u)]
    [InlineData(Ctrl, Sio0RegisterType.Control, 0xAu)]
    [InlineData(Baud, Sio0RegisterType.Baud, 0xEu)]
    [InlineData(0x1F801041u, Sio0RegisterType.Reserved, 0x1u)]
    [InlineData(0x1F801046u, Sio0RegisterType.Reserved, 0x6u)]
    [InlineData(0x1F80104Cu, Sio0RegisterType.Reserved, 0xCu)]
    [InlineData(0x1F801050u, Sio0RegisterType.Reserved, 0x10u)]
    [InlineData(0x1F80105Eu, Sio0RegisterType.Reserved, 0x1Eu)]
    [InlineData(0x1F80105Fu, Sio0RegisterType.Reserved, 0x1Fu)]
    public void Resolve_Sio0Window_RoutesEveryAddressToSio0(uint address, Sio0RegisterType type, uint offset)
    {
        var route = MmioRoute.Resolve(address);
        route.Target.Should().Be(MmioTarget.Sio0);
        route.Sio0RegisterType.Should().Be(type);
        route.Offset.Should().Be(offset);
    }

    [Theory]
    [InlineData(0x1F80103Cu)]
    [InlineData(0x1F801060u)]
    public void Resolve_OutsideSio0Window_IsNotSio0(uint address)
    {
        MmioRoute.Resolve(address).Target.Should().NotBe(MmioTarget.Sio0);
        Ps1MemoryMap.GetSio0RegisterType(address).Should().Be(Sio0RegisterType.None);
    }

    [Fact]
    public void Reset_ReadsDocumentedIdleValues()
    {
        _bus.Read(Data).Should().Be(0u);
        _bus.Read(Stat).Should().Be(IdleStatus);
        _bus.Read(Mode).Should().Be(0u);
        _bus.Read(Ctrl).Should().Be(0u);
        _bus.Read(Baud).Should().Be(0u);
    }

    [Fact]
    public void Mode_RoundTrip_MasksToBits0To8()
    {
        _bus.Write16(Mode, 0x000D);
        _bus.Read16(Mode).Should().Be(0x000D);

        _bus.Write(Mode, 0xFFFFFFFF);
        _bus.Read(Mode).Should().Be(0x01FFu);
    }

    [Fact]
    public void Baud_RoundTrip_Keeps16Bits()
    {
        _bus.Write16(Baud, 0x0088);
        _bus.Read16(Baud).Should().Be(0x0088);

        _bus.Write(Baud, 0x1234ABCD);
        _bus.Read(Baud).Should().Be(0xABCDu);
    }

    [Fact]
    public void Control_RoundTrip_DropsWriteOnlyAndUnusedBits()
    {
        _bus.Write16(Ctrl, 0x1003); // TX enable, DTR, JOY2 select
        _bus.Read16(Ctrl).Should().Be(0x1003);

        _bus.Write(Ctrl, 0xFFBF); // everything but reset
        _bus.Read(Ctrl).Should().Be(0x3FAFu); // bit4 (ack), bit6, bits 14-15 not stored
    }

    [Fact]
    public void Control_ResetBit_ZeroesAllRegisters()
    {
        _bus.Write16(Mode, 0x000D);
        _bus.Write16(Baud, 0x0088);
        _bus.Write16(Ctrl, 0x1003);
        _bus.Write8(Data, 0x42);
        _device.EnqueueReceivedByte(0x99);

        _bus.Write16(Ctrl, 0x1043); // reset wins over the other bits

        _bus.Read(Mode).Should().Be(0u);
        _bus.Read(Baud).Should().Be(0u);
        _bus.Read(Ctrl).Should().Be(0u);
        _bus.Read(Stat).Should().Be(IdleStatus);
        _bus.Read(Data).Should().Be(0u);
        _device.TxData.Should().Be(0);
    }

    [Fact]
    public void Data_Write_LatchesTxByteWithoutFillingRx()
    {
        _bus.Write8(Data, 0x01);
        _device.TxData.Should().Be(0x01);

        _bus.Write(Data, 0xFFFFFF42);
        _device.TxData.Should().Be(0x42);

        _bus.Read(Stat).Should().Be(IdleStatus); // no transfer modeled, RX stays empty
        _bus.Read(Data).Should().Be(0u);
    }

    [Fact]
    public void Data_Read_PopsRxFifoInOrderAndRepeatsLastByteWhenEmpty()
    {
        _device.EnqueueReceivedByte(0xFF);
        _device.EnqueueReceivedByte(0x41);

        (_bus.Read(Stat) & Sio0Device.StatusRxNotEmpty).Should().Be(Sio0Device.StatusRxNotEmpty);
        _bus.Read8(Data).Should().Be(0xFF);
        _bus.Read8(Data).Should().Be(0x41);
        _bus.Read(Stat).Should().Be(IdleStatus);
        _bus.Read8(Data).Should().Be(0x41);
    }

    [Fact]
    public void RxFifo_DropsBytesBeyondEight()
    {
        for (byte i = 1; i <= 9; i++)
            _device.EnqueueReceivedByte(i);

        for (byte i = 1; i <= 8; i++)
            _bus.Read8(Data).Should().Be(i);
        _bus.Read(Stat).Should().Be(IdleStatus);
    }

    [Fact]
    public void Status_IsReadOnly()
    {
        _bus.Write(Stat, 0xFFFFFFFF);
        _bus.Read(Stat).Should().Be(IdleStatus);
    }

    [Theory]
    [InlineData(0x1F801041u)]
    [InlineData(0x1F801046u)]
    [InlineData(0x1F80104Cu)]
    [InlineData(0x1F801050u)]
    [InlineData(0x1F801054u)]
    [InlineData(0x1F80105Au)]
    [InlineData(0x1F80105Eu)]
    public void ReservedAddress_ReadsZeroAndIgnoresWrites(uint address)
    {
        _bus.Write16(Mode, 0x000D);
        _bus.Write16(Ctrl, 0x1003);
        _bus.Write16(Baud, 0x0088);

        _bus.Write(address, 0xFFFFFFFF);
        _bus.Write16(address, 0xFFFF);
        _bus.Write8(address, 0xFF);

        _bus.Read(address).Should().Be(0u);
        _bus.Read16(address).Should().Be(0);
        _bus.Read8(address).Should().Be(0);
        _bus.Read(Mode).Should().Be(0x000Du);
        _bus.Read(Ctrl).Should().Be(0x1003u);
        _bus.Read(Baud).Should().Be(0x0088u);
        _bus.Read(Stat).Should().Be(IdleStatus);
        _device.TxData.Should().Be(0);
    }

    [Fact]
    public void EveryAddressInWindow_IsDeterministicAcrossIdenticalSequences()
    {
        static List<uint> Run()
        {
            var device = new Sio0Device();
            var adapter = new Sio0MmioAdapter(device);
            var reads = new List<uint>();
            for (uint a = 0x1F801040; a < 0x1F801060; a++)
            {
                adapter.Write(a, a * 0x9E3779B1u);
                reads.Add(adapter.Read(a));
            }
            return reads;
        }

        Run().Should().Equal(Run());
    }
}

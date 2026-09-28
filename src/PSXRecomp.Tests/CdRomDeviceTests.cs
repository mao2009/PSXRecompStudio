using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Tests;

[Test]
public class CdRomDeviceTests
{
    private const byte StatusIdle = 0x18; // PRMEMPT | PRMWRDY, index 0

    private static CdRomDevice WithIndex(int index)
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(0, (byte)index);
        return cd;
    }

    [Fact]
    public void NewDevice_StatusIsIdleWithEmptyFifos()
    {
        var cd = new CdRomDevice();
        cd.ReadStatus().Should().Be(StatusIdle);
        cd.ReadRegister(0).Should().Be(StatusIdle);
        cd.GetInterruptFlag().Should().Be(0xE0);
        cd.HasInterrupt.Should().BeFalse();
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x02, 2)]
    [InlineData(0x03, 3)]
    [InlineData(0xFE, 2)]
    public void IndexWrite_SelectsLowTwoBitsAndIsReportedInStatus(byte value, int expected)
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(0, value);
        cd.Index.Should().Be(expected);
        cd.ReadStatus().Should().Be((byte)(StatusIdle | expected));
    }

    [Fact]
    public void ParameterWrite_OnlyAtIndex0()
    {
        for (var index = 1; index < 4; index++)
        {
            var cd = WithIndex(index);
            cd.WriteRegister(2, 0xAA);
            cd.Parameters.Should().BeEmpty($"index {index} port 2 is not the parameter FIFO");
        }

        var idx0 = WithIndex(0);
        idx0.WriteRegister(2, 0xAA);
        idx0.Parameters.Should().Equal(0xAA);
    }

    [Fact]
    public void ParameterFifo_KeepsOrderAndReportsStatus()
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(2, 0x11);
        cd.ReadStatus().Should().Be(0x10, "not empty, still writable");
        for (var i = 1; i < CdRomDevice.FifoCapacity; i++) cd.WriteRegister(2, (byte)(0x11 + i));

        cd.Parameters.Should().Equal(Enumerable.Range(0x11, 16).Select(v => (byte)v));
        cd.ReadStatus().Should().Be(0x00, "full: neither empty nor writable");
    }

    [Fact]
    public void ParameterFifo_OverflowFailsExplicitlyAndKeepsContents()
    {
        var cd = new CdRomDevice();
        for (var i = 0; i < CdRomDevice.FifoCapacity; i++) cd.WriteRegister(2, (byte)i);

        var act = () => cd.WriteRegister(2, 0xFF);

        act.Should().Throw<InvalidOperationException>().WithMessage("*overflow*");
        cd.Parameters.Should().Equal(Enumerable.Range(0, 16).Select(v => (byte)v));
    }

    [Fact]
    public void UnsupportedCommand_FailsClosedWithInvalidCommandError()
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(2, 0x01);
        cd.WriteRegister(2, 0x02);
        cd.WriteRegister(1, 0x01); // GetStat: not yet implemented (#586)

        cd.LastCommand.Should().Be(0x01);
        cd.Parameters.Should().BeEmpty("command dispatch consumes the parameter FIFO");
        cd.ReadStatus().Should().Be(StatusIdle | 0x20, "response FIFO holds the error");
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        cd.HasInterrupt.Should().BeFalse("interrupt enable is 0");

        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorStat);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorInvalidCommand);
        cd.ReadStatus().Should().Be(StatusIdle, "response FIFO drained");
        cd.ReadRegister(1).Should().Be(0, "empty response FIFO reads 0");
    }

    [Fact]
    public void CommandWrite_OnlyAtIndex0()
    {
        for (var index = 1; index < 4; index++)
        {
            var cd = WithIndex(index);
            cd.WriteRegister(1, 0x01);
            cd.LastCommand.Should().BeNull();
            cd.ResponseCount.Should().Be(0);
        }
    }

    [Fact]
    public void ResponseRead_IsIndexIndependent()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x19);
        cd.WriteRegister(0, 3);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorStat);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorInvalidCommand);
    }

    [Fact]
    public void RepeatedCommands_ReplaceResponseRatherThanAccumulate()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x01);
        cd.WriteCommand(0x02);
        cd.ResponseCount.Should().Be(2);
        cd.LastCommand.Should().Be(0x02);
    }

    [Fact]
    public void InterruptEnableAndFlag_ThroughIndexedPorts()
    {
        var cd = WithIndex(1);
        cd.WriteRegister(2, 0xFF);
        cd.InterruptEnable.Should().Be(0x1F);
        cd.WriteRegister(0, 0);
        cd.ReadRegister(3).Should().Be(0xFF, "index 0 port 3 reads enable with bits 5-7 set");

        cd.WriteCommand(0x00);
        cd.HasInterrupt.Should().BeTrue();
        cd.WriteRegister(0, 1);
        cd.ReadRegister(3).Should().Be(0xE5);

        cd.WriteRegister(3, 0x01); // acknowledge bit 0 only
        cd.ReadRegister(3).Should().Be(0xE4);
        cd.WriteRegister(3, 0x1F);
        cd.ReadRegister(3).Should().Be(0xE0);
        cd.HasInterrupt.Should().BeFalse();
    }

    [Fact]
    public void InterruptFlagWrite_Bit6ClearsParameterFifo()
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(2, 0x42);
        cd.SetInterruptFlag(0x40);
        cd.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void AcknowledgeInterrupt_ClearsAllFlagBits()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x00);
        cd.AcknowledgeInterrupt();
        cd.GetInterruptFlag().Should().Be(0xE0);
    }

    [Fact]
    public void Reset_ClearsAllTransientStateDeterministically()
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(2, 0x01);
        cd.WriteRegister(0, 1);
        cd.WriteRegister(2, 0x1F);
        cd.WriteCommand(0x0A);
        cd.WriteRegister(0, 0);
        cd.WriteRegister(2, 0x99);
        cd.WriteRegister(0, 3);

        cd.Reset();

        cd.Index.Should().Be(0);
        cd.Parameters.Should().BeEmpty();
        cd.ResponseCount.Should().Be(0);
        cd.InterruptEnable.Should().Be(0);
        cd.LastCommand.Should().BeNull();
        cd.ReadStatus().Should().Be(StatusIdle);
        cd.GetInterruptFlag().Should().Be(0xE0);
        cd.HasInterrupt.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void OutOfRangePort_Throws(int port)
    {
        var cd = new CdRomDevice();
        cd.Invoking(c => c.ReadRegister(port)).Should().Throw<ArgumentOutOfRangeException>();
        cd.Invoking(c => c.WriteRegister(port, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DataRegister_ReadsZeroWithoutSectorData()
    {
        var cd = new CdRomDevice();
        cd.ReadRegister(2).Should().Be(0);
        cd.ReadData().Should().Be(0);
    }
}

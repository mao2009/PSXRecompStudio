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
    public void Parameters_ReturnsSnapshotThatCannotTrackLaterFifoMutation()
    {
        var cd = new CdRomDevice();
        cd.WriteRegister(2, 0x11);

        var snapshot = cd.Parameters;
        cd.WriteRegister(2, 0x22);

        snapshot.Should().Equal(0x11);
        cd.Parameters.Should().Equal(0x11, 0x22);
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
        cd.WriteRegister(1, 0x00); // Unused/unsupported command.

        cd.LastCommand.Should().Be(0x00);
        cd.Parameters.Should().BeEmpty("command dispatch consumes the parameter FIFO");
        cd.ReadStatus().Should().Be(StatusIdle | 0x20, "response FIFO holds the error");
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        cd.HasInterrupt.Should().BeTrue("the reset interrupt enable (0x1F) includes INT5");

        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorStat);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorInvalidCommand);
        cd.ReadStatus().Should().Be(StatusIdle, "response FIFO drained");
        cd.ReadRegister(1).Should().Be(0, "empty response FIFO reads 0");
    }

    [Fact]
    public void GetStat_ReturnsDeterministicStatusWithInt3()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x01);

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x02, "present disc reports the minimal motor-on status");
        cd.ResponseCount.Should().Be(0);
    }

    [Fact]
    public void Init_QueuesInt3ThenInt2AndClearsReadState()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(2, 0x00);
        cd.WriteRegister(2, 0x02);
        cd.WriteRegister(2, 0x00);
        cd.WriteCommand(0x02);
        cd.ReadRegister(1).Should().Be(0x02);
        cd.AcknowledgeInterrupt();

        cd.WriteCommand(0x06);
        cd.LoadData(new byte[] { 0x00 });
        cd.ReadRegister(1).Should().Be(0x22);
        cd.AcknowledgeInterrupt();
        cd.ReadRegister(1).Should().Be(0x22);
        cd.DataReady.Should().BeTrue();
        cd.AcknowledgeInterrupt();
        cd.DataReady.Should().BeTrue("acknowledging INT1 must not discard unconsumed data (Issue #587)");

        cd.WriteCommand(0x0A);

        cd.IsReading.Should().BeFalse();
        cd.DataReady.Should().BeFalse();
        cd.Location.Should().BeNull();
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x02);

        cd.AcknowledgeInterrupt();
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntComplete);
        cd.ReadRegister(1).Should().Be(0x02);
    }

    [Fact]
    public void GetId_LicensedMode2_QueuesInt3ThenLicensedIdentityInt2()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2((byte)'E'));
        cd.WriteCommand(0x1A);

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x02);

        cd.AcknowledgeInterrupt();

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntComplete);
        Enumerable.Range(0, 8).Select(_ => cd.ReadRegister(1)).Should().Equal(
            0x02, 0x00, 0x20, 0x00, (byte)'S', (byte)'C', (byte)'E', (byte)'E');
    }

    [Fact]
    public void GetId_NoDisc_QueuesInt3ThenFailClosedInt5()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x1A);

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x00);

        cd.AcknowledgeInterrupt();

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        Enumerable.Range(0, 8).Select(_ => cd.ReadRegister(1)).Should().Equal(
            0x08, CdRomDevice.ErrorInvalidCommand, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void SetLoc_StoresValidatedBcdLocationUntilReadConsumesIt()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(2, 0x12);
        cd.WriteRegister(2, 0x34);
        cd.WriteRegister(2, 0x56);

        cd.WriteCommand(0x02);

        cd.Location.Should().Be(new CdRomLocation(0x12, 0x34, 0x56));
        cd.HasPendingLocation.Should().BeTrue();
        cd.Parameters.Should().BeEmpty();
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x02);
    }

    [Theory]
    [InlineData(0x00, 0x60, 0x00)]
    [InlineData(0x00, 0x00, 0x75)]
    [InlineData(0x0A, 0x00, 0x00)]
    public void SetLoc_InvalidBcdFailsClosed(byte minute, byte second, byte frame)
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(2, minute);
        cd.WriteRegister(2, second);
        cd.WriteRegister(2, frame);

        cd.WriteCommand(0x02);

        cd.Location.Should().BeNull();
        cd.HasPendingLocation.Should().BeFalse();
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        cd.ReadRegister(1).Should().Be(0x03);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorInvalidParameter);
    }

    [Fact]
    public void WrongParameterCount_UsesDedicatedErrorCode()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(2, 0x12);
        cd.WriteCommand(0x01); // GetStat expects no parameters.

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        cd.ReadRegister(1).Should().Be(0x03);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorWrongParameterCount);
    }

    [Theory]
    [InlineData(0x06, false)]
    [InlineData(0x1B, true)]
    public void ReadCommand_QueuesOneBoundedInt1DataReadyEvent(byte command, bool raw)
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(2, 0x00);
        cd.WriteRegister(2, 0x02);
        cd.WriteRegister(2, 0x00);
        cd.WriteCommand(0x02);
        cd.ReadRegister(1).Should().Be(0x02);
        cd.AcknowledgeInterrupt();

        cd.WriteCommand(command);
        cd.LoadData(new byte[] { 0xAA });

        cd.IsReading.Should().BeTrue();
        cd.ReadSectorsRaw.Should().Be(raw);
        cd.HasPendingLocation.Should().BeFalse();
        cd.DataReady.Should().BeFalse("INT1 is not visible until the command INT3 is completed");
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x22);

        cd.AcknowledgeInterrupt();

        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntDataReady);
        cd.DataReady.Should().BeTrue();
        (cd.ReadStatus() & 0x40).Should().Be(0x40, "DRQSTS reflects the bounded data-ready token");
        cd.ReadRegister(1).Should().Be(0x22);

        cd.AcknowledgeInterrupt();

        // Issue #587: acknowledging INT1 must not discard data the guest has
        // not consumed yet. Interrupt acknowledgement and data-FIFO
        // availability are separate states.
        cd.DataReady.Should().BeTrue("unconsumed data must survive the INT1 acknowledgement");
        (cd.ReadStatus() & 0x40).Should().Be(0x40);
        cd.GetInterruptFlag().Should().Be(0xE0, "no repeating INT1 is synthesized in the #586 slice");

        cd.ReadData();
        cd.DataReady.Should().BeFalse("DataReady clears once the FIFO is actually drained, not on interrupt ack");
    }

    [Fact]
    public void ReadWithoutDisc_FailsClosedWithoutDataReady()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x06);

        cd.IsReading.Should().BeFalse();
        cd.DataReady.Should().BeFalse();
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntError);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorStat);
        cd.ReadRegister(1).Should().Be(CdRomDevice.ErrorNotReady);
    }

    [Fact]
    public void DataFifo_RequiresActiveRead_AndPreservesByteOrder()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());

        var beforeRead = () => cd.LoadData(new byte[] { 1, 2, 3, 4 });
        beforeRead.Should().Throw<InvalidOperationException>();

        cd.WriteCommand(0x06);
        cd.LoadData(new byte[] { 0x11, 0x22, 0x33, 0x44 });

        cd.DataBytesAvailable.Should().Be(4);
        cd.ReadData().Should().Be(0x11);
        cd.ReadData().Should().Be(0x22);
        cd.ReadData().Should().Be(0x33);
        cd.ReadData().Should().Be(0x44);
        cd.DataBytesAvailable.Should().Be(0);
        cd.ReadData().Should().Be(0, "empty data FIFO reads fail closed as zero");
    }

    [Fact]
    public void DataFifo_HasItsOwnCapacity_IndependentOfParameterAndResponseFifos()
    {
        CdRomDevice.DataFifoCapacity.Should().Be(2352, "one raw CD sector");
        CdRomDevice.DataFifoCapacity.Should().NotBe(CdRomDevice.FifoCapacity);
    }

    [Fact]
    public void LoadData_ExactlyFillingCapacity_IsAccepted()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x06);

        cd.LoadData(new byte[CdRomDevice.DataFifoCapacity]);

        cd.DataBytesAvailable.Should().Be(CdRomDevice.DataFifoCapacity);
    }

    [Fact]
    public void LoadData_OneByteOverCapacity_EnqueuesNothing()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x06);
        cd.LoadData(new byte[] { 0xA1, 0xA2 });

        var overflow = () => cd.LoadData(new byte[CdRomDevice.DataFifoCapacity - 1]);

        overflow.Should().Throw<InvalidOperationException>();
        cd.DataBytesAvailable.Should().Be(2, "a rejected load must not partially enqueue");
        cd.ReadData().Should().Be(0xA1);
        cd.ReadData().Should().Be(0xA2);
        cd.ReadData().Should().Be(0, "no byte of the rejected load may be visible");
    }

    [Fact]
    public void LoadData_EmptyInput_IsANoOp_EvenWhenFull()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x06);
        cd.LoadData(new byte[CdRomDevice.DataFifoCapacity]);

        cd.LoadData(ReadOnlySpan<byte>.Empty);

        cd.DataBytesAvailable.Should().Be(CdRomDevice.DataFifoCapacity);
    }

    [Fact]
    public void LoadData_AfterPartialDrain_AcceptsExactlyTheFreedSpace()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x06);
        cd.LoadData(new byte[CdRomDevice.DataFifoCapacity]);
        cd.ReadData();
        cd.ReadData();

        var tooMuch = () => cd.LoadData(new byte[] { 1, 2, 3 });
        tooMuch.Should().Throw<InvalidOperationException>();
        cd.DataBytesAvailable.Should().Be(CdRomDevice.DataFifoCapacity - 2);

        cd.LoadData(new byte[] { 1, 2 });
        cd.DataBytesAvailable.Should().Be(CdRomDevice.DataFifoCapacity);
    }

    [Fact]
    public void Reset_ClearsLoadedDataAndReadState_WithoutReusingInterruptGeneration()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteCommand(0x06);
        cd.LoadData(new byte[] { 1, 2, 3, 4 });
        var generation = cd.InterruptGeneration;
        generation.Should().BeGreaterThan(0);

        cd.Reset();

        cd.DataBytesAvailable.Should().Be(0);
        cd.DataReady.Should().BeFalse();
        cd.HasInterrupt.Should().BeFalse();
        cd.IsReading.Should().BeFalse();
        cd.InterruptGeneration.Should().Be(generation, "packet identities are monotonic across device reset");
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
    public void FreshDevice_HasAllInterruptSourcesEnabled_SoAGuestThatNeverWritesTheEnableGetsIrq()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());

        cd.InterruptEnable.Should().Be(0x1F);
        cd.WriteCommand(0x01); // GetStat -> INT3, no enable write by the guest
        cd.HasInterrupt.Should().BeTrue();
    }

    [Fact]
    public void GuestClearingTheEnable_StillMasksTheInterruptLine()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        cd.WriteRegister(0, 1);
        cd.WriteRegister(2, 0x00);
        cd.WriteRegister(0, 0);

        cd.WriteCommand(0x01);

        cd.HasInterrupt.Should().BeFalse();
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
        cd.WriteRegister(2, 0x00);
        cd.WriteCommand(0x0A);
        cd.WriteRegister(0, 0);
        cd.WriteRegister(2, 0x99);
        cd.WriteRegister(0, 3);

        cd.Reset();

        cd.Index.Should().Be(0);
        cd.Parameters.Should().BeEmpty();
        cd.ResponseCount.Should().Be(0);
        cd.InterruptEnable.Should().Be(CdRomDevice.ResetInterruptEnable);
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

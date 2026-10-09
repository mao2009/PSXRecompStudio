using System.Text;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Issue #732: the CD-ROM controller reading a disc (<see cref="ICdSectorSource"/>) with hardware timing — the
/// command/response protocol the OpenBIOS kernel and shell drive. The disc is synthetic: an in-memory ISO 9660 volume
/// with a SYSTEM.CNF and a tiny PS-X EXE, wrapped in Mode 2 Form 1 raw sectors. No commercial data.
/// </summary>
[Test]
public sealed class CdRomDiscDriveTests
{
    private const string BootPath = @"cdrom:\TEST.EXE;1";

    /// <summary>Wraps 2048-byte ISO sectors into raw 2352-byte Mode 2 Form 1 sectors (sync, BCD header, data subheader).</summary>
    internal static byte[] Mode2Form1Image(byte[] iso) => SyntheticDiscBuilder.Mode2Form1(iso);

    internal static RawCdSectorSource SyntheticDisc() =>
        new(Mode2Form1Image(new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootPath)
            .AddFile("TEST.EXE;1", SyntheticPsxExeBuilder.BuildValid())
            .Build()));

    private static CdRomDevice FreshDrive() => new(CdRomDiscIdentity.LicensedMode2(), SyntheticDisc());

    /// <summary>A drive whose power-on ShellOpen flag the guest's first GetStat has already consumed.</summary>
    private static CdRomDevice Drive()
    {
        var cd = FreshDrive();
        Command(cd, 0x01).Should().Equal(3, 0x12);
        return cd;
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    /// <summary>Advances one cycle at a time until an interrupt is raised; returns the cycles it took.</summary>
    private static uint RunUntilInterrupt(CdRomDevice cd, uint limit = 40_000_000)
    {
        for (uint cycles = 1; cycles <= limit; cycles++)
        {
            cd.Advance(1);
            if (cd.HasInterrupt)
            {
                return cycles;
            }
        }

        throw new InvalidOperationException($"No CD-ROM interrupt within {limit} cycles.");
    }

    private static byte[] Command(CdRomDevice cd, byte command, params byte[] parameters)
    {
        foreach (var p in parameters)
        {
            cd.WriteRegister(2, p);
        }

        cd.WriteCommand(command);
        return TakeResponse(cd);
    }

    /// <summary>Waits for the next interrupt, then reads its response and acknowledges it as a guest handler would.</summary>
    private static byte[] TakeResponse(CdRomDevice cd)
    {
        RunUntilInterrupt(cd);
        var bytes = new List<byte> { (byte)(cd.GetInterruptFlag() & 7) };
        while (cd.ResponseCount > 0)
        {
            bytes.Add(cd.ReadRegister(1));
        }

        cd.SetInterruptFlag(0x07);
        return bytes.ToArray();
    }

    [Fact]
    public void Reset_RearmsPowerOnShellOpen_ForTheConfiguredDisc()
    {
        var cd = Drive();
        cd.Reset();
        Command(cd, 0x01).Should().Equal(3, 0x12);
        Command(cd, 0x01).Should().Equal(3, 0x02);
    }

    [Fact]
    public void GetStat_ReportsShellOpenOnce_AfterPowerOnWithADisc()
    {
        // OpenBIOS's dev_cd_open reads the path table only when GetStat reports stat bit 4 (psx-spx "ShellOpen").
        var cd = FreshDrive();

        Command(cd, 0x01).Should().Equal(3, 0x12);
        Command(cd, 0x01).Should().Equal(3, 0x02);
    }

    [Fact]
    public void GetStat_NeverReportsShellOpen_WithoutADisc()
    {
        var cd = new CdRomDevice();
        cd.WriteCommand(0x01);

        RunUntilInterrupt(cd);
        (cd.ReadRegister(1) & 0x10).Should().Be(0);
    }

    [Fact]
    public void RawSource_ReportsAnAbsentSector_WithoutTouchingTheDestination()
    {
        var disc = SyntheticDisc();
        var destination = Enumerable.Repeat((byte)0xAA, ICdSectorSource.RawSectorSize).ToArray();

        disc.TryReadSector(disc.Toc.LeadOutLba, destination).Should().BeFalse();
        disc.TryReadSector(-1, destination).Should().BeFalse();
        destination.Should().OnlyContain(b => b == 0xAA, "an absent sector is never zero-filled");
        disc.TryReadSector(16, destination).Should().BeTrue();
    }

    [Fact]
    public void RawSource_RejectsAnImageThatIsNotWholeSectors()
    {
        var act = () => new RawCdSectorSource(new byte[ICdSectorSource.RawSectorSize + 1]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Toc_RejectsTracksThatAreNotAscendingBeforeTheLeadOut()
    {
        var act = () => new CdDiscToc([new CdTrack(1, 0, false), new CdTrack(2, 0, true)], 100);
        act.Should().Throw<ArgumentException>();
        var pastLeadOut = () => new CdDiscToc([new CdTrack(1, 100, false)], 100);
        pastLeadOut.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SyntheticDisc_UserData_IsAnIso9660VolumeWithItsSystemCnfAndExecutable()
    {
        var disc = SyntheticDisc();
        var iso = new Iso9660Reader(lba =>
        {
            var raw = new byte[ICdSectorSource.RawSectorSize];
            disc.TryReadSector(lba, raw).Should().BeTrue();
            return raw[24..(24 + Iso9660Reader.SectorSize)];
        });
        iso.Initialize();

        SystemCnfParser.Parse(iso.ReadFile("SYSTEM.CNF;1")).BootPath.Should().Contain("TEST.EXE");
        PsxExe.Load(iso.ReadFile("TEST.EXE;1"), "TEST.EXE").Header.EntryPoint.Should().Be(SyntheticPsxExeBuilder.DefaultTextStart);
    }

    [Fact]
    public void DeadlineTracksResponseSpacingAndResetWithoutDeliveringEarly()
    {
        var cd = Drive();
        cd.NextEventCycles.Should().Be(ulong.MaxValue);
        cd.WriteCommand(0x01);
        cd.NextEventCycles.Should().Be(CdRomDevice.AcknowledgeDelayCycles);
        cd.Advance(CdRomDevice.AcknowledgeDelayCycles - 1);
        cd.NextEventCycles.Should().Be(1);
        cd.HasInterrupt.Should().BeFalse();
        cd.Advance(1);
        cd.HasInterrupt.Should().BeTrue();
        cd.SetInterruptFlag(7);
        cd.WriteCommand(0x0A);
        cd.NextEventCycles.Should().Be(CdRomDevice.AcknowledgeDelayCycles);
        cd.Reset();
        cd.NextEventCycles.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void FirstResponse_ArrivesOnlyAfterTheAcknowledgeDelay()
    {
        var cd = Drive();
        cd.WriteCommand(0x01);

        RunUntilInterrupt(cd).Should().Be(CdRomDevice.AcknowledgeDelayCycles);
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.ReadRegister(1).Should().Be(0x02, "motor on");
    }

    [Fact]
    public void Init_SecondResponseFollowsTheAcknowledgedFirst_AfterItsDelay_AndSetsMode20h()
    {
        var cd = Drive();
        Command(cd, 0x0E, 0x80).Should().Equal(3, 0x02);

        cd.WriteCommand(0x0A);
        RunUntilInterrupt(cd);
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntAcknowledge);
        cd.SetInterruptFlag(0x07);

        RunUntilInterrupt(cd).Should().Be(CdRomDevice.InitCompleteCycles);
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntComplete);
        cd.Mode.Should().Be(0x20);
    }

    [Fact]
    public void GetTnAndGetTd_ReportTheTableOfContentsInBcd()
    {
        var cd = Drive();
        var leadOut = SyntheticDisc().Toc.LeadOutLba + 150;

        Command(cd, 0x13).Should().Equal(3, 0x02, 0x01, 0x01);
        Command(cd, 0x14, 0x01).Should().Equal(3, 0x02, 0x00, 0x02);
        Command(cd, 0x14, 0x00).Should().Equal(3, 0x02, Bcd(leadOut / 4500), Bcd(leadOut / 75 % 60));
        Command(cd, 0x14, 0x02).Should().Equal(5, 0x03, CdRomDevice.ErrorInvalidParameter);
    }

    [Fact]
    public void ReadN_StreamsOneInt1PerSector_AndTheRequestRegisterLoadsItsUserData()
    {
        var cd = Drive();
        Command(cd, 0x0E, 0x00);
        Command(cd, 0x02, 0x00, 0x02, 0x16).Should().Equal(3, 0x02);
        Command(cd, 0x06).Should().Equal(3, 0x22);

        TakeResponse(cd).Should().Equal(1, 0x22);
        cd.DataReady.Should().BeFalse("the sector enters the data FIFO only on request");
        cd.WriteRegister(3, 0x80); // index 0: BFRD
        cd.DataBytesAvailable.Should().Be(2048);
        cd.DataReady.Should().BeTrue();
        Enumerable.Range(0, 6).Select(_ => cd.ReadData()).Should().Equal(1, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1');
        cd.WriteRegister(3, 0x00);
        cd.DataBytesAvailable.Should().Be(0, "clearing BFRD empties the data FIFO");

        RunUntilInterrupt(cd).Should().Be(CdRomDevice.SingleSpeedSectorCycles, "the next sector follows one sector period later");
        cd.GetInterruptFlag().Should().Be(0xE0 | CdRomDevice.IntDataReady);
        cd.SetInterruptFlag(0x07);
        Command(cd, 0x10).Should().Equal(3, 0x00, 0x02, 0x17, 0x02, 0x00, 0x00, 0x08, 0x00); // GetLocL: header of LBA 17
    }

    [Fact]
    public void DoubleSpeedWholeSectorMode_HalvesThePeriod_AndDeliversTheSectorAfterItsSync()
    {
        var cd = Drive();
        Command(cd, 0x0E, 0xA0);
        Command(cd, 0x02, 0x00, 0x02, 0x16);
        Command(cd, 0x1B).Should().Equal(3, 0x22);
        TakeResponse(cd);
        cd.WriteRegister(3, 0x80);

        cd.DataBytesAvailable.Should().Be(2340);
        Enumerable.Range(0, 4).Select(_ => cd.ReadData()).Should().Equal(0x00, 0x02, 0x16, 0x02);
        cd.ReadSectorsRaw.Should().BeTrue();
        RunUntilInterrupt(cd).Should().Be(CdRomDevice.SingleSpeedSectorCycles / 2);
    }

    [Fact]
    public void Pause_EndsTheStream_WithInt3ThenInt2_AndNoFurtherSector()
    {
        var cd = Drive();
        Command(cd, 0x0E, 0x80);
        Command(cd, 0x02, 0x00, 0x02, 0x16);
        Command(cd, 0x06);
        TakeResponse(cd).Should().Equal(1, 0x22);

        Command(cd, 0x09).Should().Equal(3, 0x22);
        TakeResponse(cd).Should().Equal(2, 0x02);
        cd.IsReading.Should().BeFalse();
        var act = () => RunUntilInterrupt(cd, 2 * CdRomDevice.SingleSpeedSectorCycles);
        act.Should().Throw<InvalidOperationException>("a paused drive raises no more INT1");
    }

    [Fact]
    public void ReadingPastTheDisc_FailsClosedWithInt5_AndStops()
    {
        var cd = Drive();
        var last = SyntheticDisc().Toc.LeadOutLba - 1 + 150;
        Command(cd, 0x0E, 0x80);
        Command(cd, 0x02, Bcd(last / 4500), Bcd(last / 75 % 60), Bcd(last % 75));
        Command(cd, 0x06);
        TakeResponse(cd).Should().Equal(1, 0x22);

        TakeResponse(cd).Should().Equal(5, 0x02 | CdRomDevice.ErrorStat | CdRomDevice.ErrorSeekFailed, CdRomDevice.ErrorSeekFailed);
        cd.IsReading.Should().BeFalse();
    }

    [Fact]
    public void SeekL_MovesTheHead_ThenGetLocPReportsThePosition()
    {
        var cd = Drive();
        Command(cd, 0x02, 0x00, 0x02, 0x16);
        Command(cd, 0x15).Should().Equal(3, 0x42);
        TakeResponse(cd).Should().Equal(2, 0x02);

        Command(cd, 0x11).Should().Equal(3, 0x01, 0x01, 0x00, 0x00, 0x16, 0x00, 0x02, 0x16);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x1D)]
    public void UnmodelledCommand_AnswersInvalidCommand_NotASilentSuccess(byte command)
    {
        Command(Drive(), command).Should().Equal(5, 0x03, CdRomDevice.ErrorInvalidCommand);
    }

    [Fact]
    public void TestVersion_AnswersTheControllerDate()
    {
        Command(Drive(), 0x19, 0x20).Should().Equal(3, 0x94, 0x09, 0x19, 0xC0);
    }

    [Fact]
    public void ARequestedSector_ReachesGuestRamThroughDma3_AfterIrq2Responses()
    {
        const uint buffer = 0x00010000;
        using var graph = new PsxDeviceGraph(disc: SyntheticDisc());
        var scheduler = new DeviceScheduler(
            graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        void Write8(uint address, byte value) => graph.TryWrite(address, 1, value).Should().Be(PsxDeviceAccessStatus.Completed);
        byte Read8(uint address) => graph.TryRead(address, 1, out var v) == PsxDeviceAccessStatus.Completed ? (byte)v : throw new InvalidOperationException();
        void WaitIrq2()
        {
            for (var i = 0; i < 2_000_000 && (graph.InterruptControllerAdapter.Status & 4) == 0; i++)
            {
                scheduler.Advance(1);
            }

            (graph.InterruptControllerAdapter.Status & 4u).Should().Be(4u, "IRQ2");
            graph.InterruptControllerAdapter.Acknowledge(~4u);
            Write8(0x1F801800, 1);
            Write8(0x1F801803, 0x07);
            Write8(0x1F801800, 0);
        }

        Write8(0x1F801800, 0);
        Write8(0x1F801802, 0x00);
        Write8(0x1F801802, 0x02);
        Write8(0x1F801802, 0x16);
        Write8(0x1F801801, 0x02); // SetLoc 00:02:16
        WaitIrq2();
        Write8(0x1F801801, 0x06); // ReadN (mode 0: 2048 bytes)
        WaitIrq2();
        WaitIrq2(); // INT1
        Write8(0x1F801803, 0x80); // BFRD
        (Read8(0x1F801800) & 0x40).Should().Be(0x40, "DRQSTS: the data FIFO holds the sector");

        graph.TryWrite(0x1F8010F0, 4, 0x0000_8000).Should().Be(PsxDeviceAccessStatus.Completed); // DPCR: DMA3 enable
        graph.TryWrite(0x1F8010B0, 4, buffer).Should().Be(PsxDeviceAccessStatus.Completed);
        graph.TryWrite(0x1F8010B4, 4, 0x0001_0200).Should().Be(PsxDeviceAccessStatus.Completed);
        graph.TryWrite(0x1F8010B8, 4, 0x1100_0000).Should().Be(PsxDeviceAccessStatus.Completed);
        scheduler.Advance(1);

        Encoding.ASCII.GetString(Enumerable.Range(1, 5).Select(i => graph.Core.ReadMemory8(buffer + (uint)i)).ToArray())
            .Should().Be("CD001");
        (graph.Core.ReadDmaRegister(0x1F8010B8) & (1u << 24)).Should().Be(0u, "the burst completed channel 3");
    }
}

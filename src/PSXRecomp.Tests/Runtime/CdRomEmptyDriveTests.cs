using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Issue #736: an empty drive under a guest firmware is timed like a real one. A firmware CD driver writes the command
/// register and only then records which command is in flight; a response raised at once is taken as an interrupt
/// before that record exists and is misfiled as unsolicited, so the driver's completion event never fires.
/// </summary>
[Test]
public sealed class CdRomEmptyDriveTests
{
    private static CdRomDevice EmptyDrive() => new(CdRomDiscIdentity.NoDisc, null, timed: true);

    private static void Send(CdRomDevice cd, byte command, params byte[] parameters)
    {
        cd.WriteRegister(0, 0);
        foreach (var p in parameters)
        {
            cd.WriteRegister(2, p);
        }

        cd.WriteRegister(1, command);
    }

    private static byte[] AcknowledgeAndDrain(CdRomDevice cd)
    {
        cd.WriteRegister(0, 1);
        var interrupt = (byte)(cd.ReadRegister(3) & 0x1F);
        var bytes = new List<byte> { interrupt };
        while ((cd.ReadStatus() & 0x20) != 0)
        {
            bytes.Add(cd.ReadRegister(1));
        }

        cd.WriteRegister(3, 0x1F);
        return bytes.ToArray();
    }

    /// <summary>Advances until an interrupt is raised; returns the cycles it took.</summary>
    private static uint RunUntilInterrupt(CdRomDevice cd)
    {
        for (uint cycles = 1; cycles <= 1_000_000; cycles++)
        {
            cd.Advance(1);
            if (cd.HasInterrupt)
            {
                return cycles;
            }
        }

        throw new InvalidOperationException("No CD-ROM interrupt.");
    }

    [Fact]
    public void Command_IsNotAnsweredUntilTheAcknowledgeDelayHasPassed()
    {
        var cd = EmptyDrive();
        Send(cd, 0x02, 0x00, 0x02, 0x00); // SetLoc

        cd.HasInterrupt.Should().BeFalse("the guest must run past its command write before INT3");
        RunUntilInterrupt(cd).Should().Be(CdRomDevice.AcknowledgeDelayCycles);
        AcknowledgeAndDrain(cd).Should().Equal(CdRomDevice.IntAcknowledge, 0x00);
    }

    [Fact]
    public void GetId_ReportsNoDisc_AndReadFailsNotReady()
    {
        var cd = EmptyDrive();
        Send(cd, 0x1A); // GetID
        RunUntilInterrupt(cd);
        AcknowledgeAndDrain(cd).Should().Equal(CdRomDevice.IntAcknowledge, 0x00);
        RunUntilInterrupt(cd);
        AcknowledgeAndDrain(cd)[..3].Should().Equal(CdRomDevice.IntError, 0x08, CdRomDevice.ErrorInvalidCommand);

        Send(cd, 0x06); // ReadN
        RunUntilInterrupt(cd);
        AcknowledgeAndDrain(cd).Should().Equal(CdRomDevice.IntError, CdRomDevice.ErrorStat, CdRomDevice.ErrorNotReady);
    }

    [Fact]
    public void LegacyUntimedDrive_StillAnswersAtOnce()
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.NoDisc);
        Send(cd, 0x01); // GetStat
        cd.HasInterrupt.Should().BeTrue();
    }

    [Fact]
    public void DeviceGraph_GivesAGuestFirmwareWithoutADiscATimedEmptyDrive()
    {
        using (var firmware = new PsxDeviceGraph(guestFirmware: true))
        {
            Send(firmware.CdRomDevice, 0x01);
            firmware.CdRomDevice.HasInterrupt.Should().BeFalse();
        }

        using var biosLess = new PsxDeviceGraph();
        Send(biosLess.CdRomDevice, 0x01);
        biosLess.CdRomDevice.HasInterrupt.Should().BeTrue("BIOS-less HLE callers keep the zero-delay model");
    }
}

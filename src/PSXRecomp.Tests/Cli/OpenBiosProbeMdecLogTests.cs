using PSXRecomp.Core.Runtime.Mdec;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

// Issue #774 (B-7): the MDEC event log is diagnostic. A failing output must never reach the guest's MDEC port access.
[Test]
public sealed class OpenBiosProbeMdecLogTests
{
    private static MdecDevice.MdecEvent AnEvent(ulong cycles) =>
        new("cpu_write_control", 0x40000000u, 0, 0, 0, 0, false, false, cycles);

    [Fact]
    public void A_Failing_Writer_Does_Not_Throw_Into_The_Guest_Port_Access_And_Marks_The_Log_Incomplete()
    {
        var mdec = new MdecDevice();
        using var log = new OpenBiosProbeMdecLog(new FailingWriter(), 0, ulong.MaxValue);
        mdec.Observer = e => log.Write("host", e, null, null);

        var act = () =>
        {
            mdec.WriteRegister(4, 0x40000000u);
            mdec.WriteRegister(4, 0x40000000u);
        };

        act.Should().NotThrow();
        log.FailureMessage.Should().Contain("IOException");
    }

    [Fact]
    public void A_Failing_Writer_Is_Not_Written_To_Again_After_It_Has_Failed()
    {
        var writer = new FailingWriter();
        using var log = new OpenBiosProbeMdecLog(writer, 0, ulong.MaxValue);

        log.Write("host", AnEvent(1), null, null);
        log.Write("host", AnEvent(2), null, null);

        writer.WriteAttempts.Should().Be(1);
    }

    [Fact]
    public void A_Failing_Flush_On_Dispose_Is_Contained_And_Recorded()
    {
        var log = new OpenBiosProbeMdecLog(new FailingWriter(failOnDispose: true), 0, ulong.MaxValue);

        var act = () => log.Dispose();

        act.Should().NotThrow();
        log.FailureMessage.Should().Contain("IOException");
    }

    [Fact]
    public void A_Healthy_Writer_Records_Every_Event_In_The_Window_And_Stays_Complete()
    {
        var writer = new StringWriter();
        using var log = new OpenBiosProbeMdecLog(writer, 2, 3);

        for (ulong cycles = 1; cycles <= 4; cycles++)
            log.Write("host", AnEvent(cycles), null, null);

        log.FailureMessage.Should().BeNull();
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    /// <summary>A writer that throws the way a full disk or a closed file does.</summary>
    private sealed class FailingWriter(bool failOnDispose = false) : TextWriter
    {
        public int WriteAttempts { get; private set; }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("disk full");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && failOnDispose) throw new IOException("flush failed");
            base.Dispose(disposing);
        }
    }
}

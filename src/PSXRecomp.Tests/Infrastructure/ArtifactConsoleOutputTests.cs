using FluentAssertions;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The direct launch prints only the guest's text. Host protocol lines and the state snapshot (whose body holds GPR values) never reach
/// the console, whether or not the run ended with a snapshot. The values here are synthetic and identifiable.
/// </summary>
[Test]
public sealed class ArtifactConsoleOutputTests
{
    private const string Value = "C0FFEE11";

    // The synthetic stream goes through the function the direct launch uses; its output is what reaches the console.
    private static string Forwarded(string stdout)
    {
        using var console = new StringWriter();
        ArtifactConsoleOutput.Forward(new StringReader(stdout), console);
        return console.ToString();
    }

    [Fact]
    public void AFailedRun_PrintsNoHostProtocolLine_AndKeepsTheGuestText()
    {
        var stdout = $"HELLO\nRHOST_TRANSFER 0x80010000 0x{Value} 0x0\nRHOST_OK\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain("RHOST_");
        printed.Should().NotContain(Value, "a GPR value is never printed");
    }

    [Fact]
    public void ASuccessfulRun_PrintsNoSnapshot_AndNoGprValue()
    {
        var stdout = $"HELLO\nRSNAPSHOT_BEGIN\ntermination=0\npc=0x80010000\ngpr[2]=0x{Value}\nRSNAPSHOT_END\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain(Value);
        printed.Should().NotContain("RSNAPSHOT");
        printed.Should().NotContain("gpr[");
    }

    [Fact]
    public void AnUnclosedSnapshot_DropsEverythingAfterItsBegin_FailClosed()
    {
        var stdout = $"HELLO\nRSNAPSHOT_BEGIN\ngpr[2]=0x{Value}\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\n", "the guest's line is written with its line end");
        printed.Should().NotContain(Value);
    }

    // PR #839 MAJOR item 1: guest text with no line end before a protocol marker. The native code prints the marker right after the guest text,
    // so the marker is not at the start of the line; it must still be withheld, and the body that follows it too.
    [Fact]
    public void UnterminatedGuestText_BeforeRsnapshotBegin_KeepsTheTextAndWithholdsTheBlock()
    {
        var stdout = $"HELLORSNAPSHOT_BEGIN\ntermination=0\ngpr[2]=0x{Value}\nRSNAPSHOT_END\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void UnterminatedGuestText_BeforeRhostData_KeepsTheTextAndWithholdsTheRamWord()
    {
        var stdout = $"HELLORHOST_DATA {Value}\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("RHOST_");
    }

    [Fact]
    public void UnterminatedGuestText_BeforeRsnapshotEnd_WithholdsTheGprLineAndTheTextAfterTheMarker()
    {
        var stdout = $"HELLO\nRSNAPSHOT_BEGIN\ntermination=0\ngpr[2]=0x{Value}\nWORLDRSNAPSHOT_END\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("WORLD");
    }

    [Fact]
    public void AMarkerInTheMiddleOfALine_KeepsOnlyTheTextBeforeIt()
    {
        var stdout = $"AB RHOST_OK CD{Value}\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("AB \nGOODBYE\n");
        printed.Should().NotContain("CD").And.NotContain(Value);
    }

    [Fact]
    public void ASnapshotBeginInTheMiddleOfALine_OpensTheBlock_AndItsBodyIsWithheld()
    {
        var stdout = $"X RSNAPSHOT_BEGIN gpr[2]=0x{Value}\ngpr[3]=0x{Value}\nRSNAPSHOT_END\nGOODBYE\n";

        var printed = Forwarded(stdout);

        printed.Should().Be("X \nGOODBYE\n");
        printed.Should().NotContain(Value);
    }
}

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

    // PR #839 MAJOR (re-review of cb40910): an earlier protocol token on the line must not hide a later RSNAPSHOT_BEGIN. Each input is followed by
    // a body line (gpr[2]=0x{Value}) that must not reach the console; the guest text before the first token still prints.
    private static readonly string Body = $"gpr[2]=0x{Value}\n";

    [Fact]
    public void BeginAfterAnRsnapshotPrefix_OpensTheBlock_AndTheBodyIsWithheld()
    {
        var printed = Forwarded("RSNAPSHOT_FOORSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void BeginAfterAnRhostPrefix_OpensTheBlock_AndTheBodyIsWithheld()
    {
        var printed = Forwarded("RHOST_RSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void BeginAfterGuestTextAndAnRhostPrefix_KeepsTheGuestText_AndWithholdsTheBody()
    {
        var printed = Forwarded("AB RHOST_OK RSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("AB \nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void BeginAfterAnotherRsnapshotPrefix_OpensTheBlock_AndTheBodyIsWithheld()
    {
        var printed = Forwarded("RSNAPSHOT_RSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void AnEndAfterAPrefixInsideTheBlock_ClosesTheBlock_AndTheRestOfThatLineIsWithheld()
    {
        var printed = Forwarded($"HELLO\nRSNAPSHOT_BEGIN\ngpr[2]=0x{Value}RHOST_X RSNAPSHOT_END RHOST_Y\nGOODBYE\n");

        printed.Should().Be("HELLO\nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("RHOST_");
    }

    [Fact]
    public void AnEndAfterAPrefixOnTheBeginLine_ClosesTheBlockAtOnce()
    {
        var printed = Forwarded($"RHOST_RSNAPSHOT_BEGINgpr[2]=0x{Value} RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value);
    }

    [Fact]
    public void GuestTextBeforeTheFirstToken_StillPrints_WhenTheTokenHidesALaterBegin()
    {
        var printed = Forwarded("HELLO RSNAPSHOT_FOORSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("HELLO \nGOODBYE\n");
        printed.Should().NotContain(Value);
    }

    // PR #839 MAJOR (re-review of 78777cb): an END on the same line as a BEGIN must not close the real block. The real block's BEGIN is the last
    // token of its line (the emitter writes "RSNAPSHOT_BEGIN\n"), so guest text printed without a line end is glued in front of it, here as
    // "... RSNAPSHOT_END" followed by the real "RSNAPSHOT_BEGIN". Each case must withhold the real body line that follows.
    [Fact]
    public void GuestBeginAndEndBeforeTheRealBegin_KeepsTheGuestText_AndWithholdsTheBody()
    {
        var printed = Forwarded("HELLO RSNAPSHOT_BEGIN RSNAPSHOT_ENDRSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("HELLO \nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void AnRhostPrefixThenGuestBeginAndEnd_BeforeTheRealBegin_WithholdsTheBody()
    {
        var printed = Forwarded("RHOST_OK RSNAPSHOT_BEGIN RSNAPSHOT_ENDRSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    [Fact]
    public void GuestBeginEndBeginOnOneLine_ThenTheBody_IsWithheld()
    {
        var printed = Forwarded("RSNAPSHOT_BEGIN RSNAPSHOT_END RSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("GOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    // A guest-printed END before a BEGIN on the same line (no line end between them), then the real block: the body is withheld, and the guest
    // text before the END is kept.
    [Fact]
    public void AGuestEndBeforeTheRealBegin_KeepsTheGuestText_AndWithholdsTheBody()
    {
        var printed = Forwarded("X RSNAPSHOT_ENDRSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_END\nGOODBYE\n");

        printed.Should().Be("X \nGOODBYE\n");
        printed.Should().NotContain(Value).And.NotContain("gpr[");
    }

    // Guest text after a closed block (the END and the text on one line) still prints.
    [Fact]
    public void GuestTextAfterAClosedBlock_StillPrints()
    {
        var printed = Forwarded("RSNAPSHOT_BEGIN\n" + Body + "RSNAPSHOT_ENDGUEST TEXT\nGOODBYE\n");

        printed.Should().Be("GUEST TEXT\nGOODBYE\n");
        printed.Should().NotContain(Value);
    }
}

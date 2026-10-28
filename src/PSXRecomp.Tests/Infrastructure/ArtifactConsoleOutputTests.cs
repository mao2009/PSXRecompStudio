using FluentAssertions;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The direct launch's console forwarder copies the guest's stdout text. The state snapshot is not on stdout (PR #839 ruling: it is on stderr,
/// for managed runs only), so the forwarder needs no marker logic. These tests pin its contract; the channel itself is tested in
/// <see cref="ArtifactSnapshotChannelTests"/>.
/// </summary>
[Test]
public sealed class ArtifactConsoleOutputTests
{
    private static string Forwarded(string stdout)
    {
        using var console = new StringWriter();
        ArtifactConsoleOutput.Forward(new StringReader(stdout), console);
        return console.ToString();
    }

    [Fact]
    public void Forward_CopiesTheGuestTextLineByLine_IncludingBlankLines()
    {
        Forwarded("HELLO\n\nGOODBYE\n").Should().Be("HELLO\n\nGOODBYE\n");
    }

    [Fact]
    public void Forward_AddsALineEndToAFinalLineWithoutOne()
    {
        Forwarded("HELLO").Should().Be("HELLO\n");
    }
}

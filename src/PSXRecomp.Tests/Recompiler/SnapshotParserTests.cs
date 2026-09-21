using System.Text;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class SnapshotParserTests
{
    [Fact]
    public void Parse_ReturnsNull_WhenTerminationByteIsUndefined()
    {
        var snapshot = BuildSnapshot(termination: 255);

        Assert.Null(SnapshotParser.Parse(snapshot));
    }

    [Fact]
    public void Parse_ReturnsNull_WhenGprIndexIsDuplicated()
    {
        var builder = new StringBuilder();
        builder.AppendLine(SnapshotParser.BeginMarker);
        builder.AppendLine("termination=0");

        for (var index = 0; index < 31; index++)
        {
            builder.Append("gpr[").Append(index).AppendLine("]=0x00000000");
        }

        builder.AppendLine("gpr[0]=0x00000000");
        builder.AppendLine(SnapshotParser.EndMarker);

        Assert.Null(SnapshotParser.Parse(builder.ToString()));
    }

    [Fact]
    public void Parse_ExceptionKeys_ProduceTheExceptionResolution()
    {
        // Issue #481: the driver prints exception.raised/code/faultPc/inDelaySlot
        // after the final gpr line; the parser must fold them back into the
        // snapshot's exception state.
        var builder = new StringBuilder();
        builder.AppendLine(SnapshotParser.BeginMarker);
        builder.AppendLine("termination=6");
        builder.AppendLine("pc=0x80000000");
        builder.AppendLine("hi=0x00000000");
        builder.AppendLine("lo=0x00000000");

        for (var index = 0; index < 32; index++)
        {
            builder.Append("gpr[").Append(index).AppendLine("]=0x00000000");
        }

        builder.AppendLine("exception.raised=1");
        builder.AppendLine("exception.code=0x00000009");
        builder.AppendLine("exception.faultPc=0x80000000");
        builder.AppendLine("exception.inDelaySlot=1");
        builder.AppendLine(SnapshotParser.EndMarker);

        var snapshot = SnapshotParser.Parse(builder.ToString());
        Assert.NotNull(snapshot);
        Assert.Equal(RecompilerIrTerminationReason.Exception, snapshot!.Termination);
        Assert.True(snapshot.Exception.IsRaised);
        Assert.Equal(0x09u, snapshot.Exception.Code);
        Assert.Equal(0x80000000u, snapshot.Exception.FaultPc);
        Assert.True(snapshot.Exception.InDelaySlot);
    }

    [Fact]
    public void Parse_WithoutExceptionKeys_DefaultsToNoExceptionState()
    {
        var snapshot = SnapshotParser.Parse(BuildSnapshot(termination: 6));
        Assert.NotNull(snapshot);
        Assert.Equal(RecompilerIrTerminationReason.Exception, snapshot!.Termination);
        Assert.False(snapshot.Exception.IsRaised);
        Assert.Equal(0u, snapshot.Exception.Code);
        Assert.False(snapshot.Exception.InDelaySlot);
    }

    private static string BuildSnapshot(byte termination)
    {
        var builder = new StringBuilder();
        builder.AppendLine(SnapshotParser.BeginMarker);
        builder.Append("termination=").AppendLine(termination.ToString());

        for (var index = 0; index < 32; index++)
        {
            builder.Append("gpr[").Append(index).AppendLine("]=0x00000000");
        }

        builder.AppendLine(SnapshotParser.EndMarker);
        return builder.ToString();
    }
}

using System.Text;
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

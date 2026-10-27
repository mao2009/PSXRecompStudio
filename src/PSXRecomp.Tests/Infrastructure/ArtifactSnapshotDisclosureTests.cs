using FluentAssertions;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The internal RSNAPSHOT protocol carries GPR values, and the host must restore them. The public output (a malformed-snapshot
/// diagnostic, the run text, the run JSON) must never carry a GPR raw value. Values here are synthetic and identifiable.
/// </summary>
[Test]
public sealed class ArtifactSnapshotDisclosureTests
{
    private const uint IdentifiableGpr = 0xC0FFEE11u;

    private static string Snapshot(params string[] bodyLines) =>
        RecompiledArtifactCodeGen.SnapshotBeginMarker + "\n"
        + string.Concat(bodyLines.Select(static line => line + "\n"))
        + RecompiledArtifactCodeGen.SnapshotEndMarker + "\n";

    private static string[] ValidBody(uint gpr9)
    {
        var lines = new List<string>
        {
            "termination=0",
            "pc=0x80010000",
            "hi=0x00000000",
            "lo=0x00000000",
        };
        for (var i = 0; i < 32; i++)
        {
            lines.Add($"gpr[{i}]=0x{(i == 9 ? gpr9 : 0u):X8}");
        }

        return lines.ToArray();
    }

    [Fact]
    public void Parse_RestoresTheGprValuesFromTheInternalSnapshot()
    {
        // The internal protocol is unchanged: the parser must still hand the GPR values back to the host (CPU state restore).
        var snapshot = ArtifactSnapshotParser.Parse(Snapshot(ValidBody(IdentifiableGpr)));

        snapshot.Should().NotBeNull();
        snapshot!.Gpr.ToArray()[9].Should().Be(IdentifiableGpr);
    }

    [Fact]
    public void MalformedSnapshotMessage_NamesOnlyTheShape_NotTheGprValueOrTheLine()
    {
        var stdout = Snapshot(ValidBody(IdentifiableGpr).Append("not a snapshot line").ToArray());

        ArtifactSnapshotParser.Parse(stdout).Should().BeNull("the snapshot has a line the parser does not accept");
        var message = RecompiledHostExecutionEngine.MalformedSnapshotMessage(stdout);

        message.Should().Be("The runnable artifact's state snapshot is malformed (37 body lines).");
        message.Should().NotContain("C0FFEE11", "a GPR value is never in a public diagnostic");
        message.Should().NotContain("not a snapshot line", "no raw protocol line is in a public diagnostic");
        message.Should().NotContain("gpr", "no raw protocol key is in a public diagnostic");
    }

    [Fact]
    public void MalformedSnapshotMessage_WithoutAnEndMarker_IsIncompleteAndEchoesNothing()
    {
        var stdout = RecompiledArtifactCodeGen.SnapshotBeginMarker + "\ngpr[9]=0xC0FFEE11\n";

        var message = RecompiledHostExecutionEngine.MalformedSnapshotMessage(stdout);

        message.Should().Be("The runnable artifact's state snapshot is incomplete.");
        message.Should().NotContain("C0FFEE11");
    }
}

using System.Text.Json.Nodes;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #773 provenance: every registered loaded-code version knows which image(s) carried it, per (PC, version). It is
// diagnostics only; the selection by LoadedCodeTable.Match must not change.
public sealed class LoadedCodeProvenanceTests
{
    private const uint Pc = 0x80084C88u;

    private static GuardedBlock Version(uint pc, params uint[] words) =>
        new(new RecompilerIrBlock(pc, [], new RecompilerIrExit(RecompilerIrTerminationReason.Success, pc + 4)), words);

    private static GuardedImageProgram Image(LoadedImageOrigin? origin, params GuardedBlock[] blocks) =>
        new(blocks, []) { Origin = origin };

    private static LoadedImageOrigin Origin(string name, uint load = 0x80030000u, string kind = "disc-exe") =>
        new(name, kind, load, 0x10000);

    [Fact]
    public void SingleImage_RecordsItsOriginForTheVersion()
    {
        var table = new LoadedCodeTable([Image(Origin("disc:SLUS/GAME.EXE"), Version(Pc, 0x340201E1u))]);

        table.OriginsAt(Pc).Should().HaveCount(1);
        table.OriginsAt(Pc)[0].Should().Equal(Origin("disc:SLUS/GAME.EXE"));
    }

    [Fact]
    public void TwoImagesAtOneAddress_WithDifferentWords_KeepEachVersionsOwnOrigin()
    {
        var table = new LoadedCodeTable([
            Image(Origin("shell"), Version(Pc, 1u)),
            Image(Origin("disc:OVERLAY.BIN", 0x80080000u), Version(Pc, 2u)),
        ]);

        table.VersionsAt(Pc).Select(static v => v.Words[0]).Should().Equal(1u, 2u);
        table.OriginsAt(Pc)[0].Select(static o => o.Name).Should().Equal("shell");
        table.OriginsAt(Pc)[1].Select(static o => o.Name).Should().Equal("disc:OVERLAY.BIN");
    }

    [Fact]
    public void IdenticalVersionInTwoImages_IsOneVersionWithBothOrigins()
    {
        var table = new LoadedCodeTable([
            Image(Origin("first"), Version(Pc, 7u)),
            Image(Origin("second", kind: "exe-file"), Version(Pc, 7u)),
        ]);

        table.VersionsAt(Pc).Should().HaveCount(1);
        table.OriginsAt(Pc)[0].Select(static o => o.Name).Should().Equal("first", "second");
    }

    [Fact]
    public void ImageWithoutOrigin_IsReportedAsNoKnownOrigin_AndMatchIsUnchanged()
    {
        var table = new LoadedCodeTable([Image(null, Version(Pc, 3u))]);

        table.OriginsAt(Pc)[0].Should().BeEmpty();
        table.Match(Pc, _ => 3u)!.Words.Should().Equal(3u);
        table.Match(Pc, _ => 4u).Should().BeNull();
    }

    [Fact]
    public void Diagnostics_WriteTheOriginWithTheEntrysWordOffset_AndUnknownWhenAbsent()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var table = new LoadedCodeTable([Image(Origin("disc:SLUS/GAME.EXE", 0x80030000u), Version(Pc, 0x340201E1u))]);

        diagnostics.Observe(Pc, table.VersionsAt(Pc), _ => 0x3C03800Au, table.OriginsAt(Pc));
        diagnostics.Observe(Pc + 4, [Version(Pc + 4, 1u)], _ => 2u);

        var records = JsonNode.Parse(diagnostics.ToJson())!["records"]!.AsArray();
        var origin = records[0]!["origins"]!.AsArray()[0]!;
        origin["name"]!.GetValue<string>().Should().Be("disc:SLUS/GAME.EXE");
        origin["kind"]!.GetValue<string>().Should().Be("disc-exe");
        origin["loadAddress"]!.GetValue<string>().Should().Be("0x80030000");
        origin["wordOffset"]!.GetValue<long>().Should().Be((0x80084C88L - 0x80030000L) / 4);
        records[1]!["origins"]!.AsArray()[0]!["kind"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public void Diagnostics_KeepTheExistingFields_WhenOriginsAreAdded()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var table = new LoadedCodeTable([Image(Origin("shell"), Version(Pc, 0x340201E1u))]);

        diagnostics.Observe(Pc, table.VersionsAt(Pc), _ => 0x3C03800Au, table.OriginsAt(Pc));

        var record = JsonNode.Parse(diagnostics.ToJson())!["records"]!.AsArray()[0]!;
        record["rejections"]!.GetValue<ulong>().Should().Be(1);
        record["fullMatches"]!.GetValue<ulong>().Should().Be(0);
        record["selectedVersion"].Should().BeNull();
        record["aotSha256"].Should().NotBeNull();
    }
}

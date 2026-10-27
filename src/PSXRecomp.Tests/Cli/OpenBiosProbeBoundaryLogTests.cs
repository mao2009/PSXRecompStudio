using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

// Issue #774 (W03): the VBlank boundary log is the comparison harness's record. Synthetic only: no ROM is involved.
[Test]
public sealed class OpenBiosProbeBoundaryLogTests
{
    private static readonly string[] Keys =
        ["engine", "vblank", "cycles", "iStat", "iMask", "dicr", "gpuStat", "displayMode", "displayVramStart", "vramSha256"];

    [Fact]
    public void A_Record_Has_The_Fixed_Keys_And_Hashes_VRAM_Without_Raw_Bytes()
    {
        using var graph = new PsxDeviceGraph();

        var record = OpenBiosProbeBoundaryLog.Describe("interpreter", graph, 1, 564480);

        record.Select(static p => p.Key).Should().Equal(Keys);
        record["vramSha256"]!.GetValue<string>().Should().HaveLength(64);
        record.ToJsonString().Should().NotContain("vram\"", "only the hash is written, never VRAM bytes");
    }

    [Fact]
    public void Each_VBlank_Writes_One_Record_At_The_Boundary()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var writer = new StringWriter();
        using var log = new OpenBiosProbeBoundaryLog(writer);
        scheduler.VblankCallback = vblank => log.Write("interpreter", graph, vblank, scheduler.ElapsedCycles);

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[1].Should().Contain("\"vblank\":2");
        log.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void FirstMismatch_Ignores_The_Engine_Label_And_Names_The_First_Differing_Field()
    {
        var left = new[] { Line("interpreter", vram: "aa"), Line("interpreter", vram: "bb") };
        var same = new[] { Line("host", vram: "aa"), Line("host", vram: "bb") };
        var diverged = new[] { Line("host", vram: "aa"), Line("host", vram: "cc") };

        OpenBiosProbeBoundaryLog.FirstMismatch(left, same).Should().BeNull();
        OpenBiosProbeBoundaryLog.FirstMismatch(left, diverged).Should().Be((1, "vramSha256"));
    }

    [Fact]
    public void FirstMismatch_Reports_A_Boundary_That_Exists_On_One_Side_Only()
    {
        var left = new[] { Line("interpreter", vram: "aa") };
        var right = new[] { Line("host", vram: "aa"), Line("host", vram: "bb") };

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().Be((1, "<missing boundary>"));
    }

    [Fact]
    public void A_Failing_Writer_Is_Contained_And_Recorded()
    {
        using var graph = new PsxDeviceGraph();
        using var log = new OpenBiosProbeBoundaryLog(new FailingWriter());

        var act = () => log.Write("interpreter", graph, 1, 0);

        act.Should().NotThrow();
        log.FailureMessage.Should().Contain("IOException");
    }

    private static string Line(string engine, string vram) =>
        "{\"engine\":\"" + engine + "\",\"vblank\":1,\"cycles\":0,\"iStat\":\"0x00000000\",\"iMask\":\"0x00000000\"," +
        "\"dicr\":\"0x00000000\",\"gpuStat\":\"0x00000000\",\"displayMode\":\"0x00000000\",\"displayVramStart\":\"0x00000000\"," +
        "\"vramSha256\":\"" + vram + "\"}";

    private sealed class FailingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("disk full");
    }
}

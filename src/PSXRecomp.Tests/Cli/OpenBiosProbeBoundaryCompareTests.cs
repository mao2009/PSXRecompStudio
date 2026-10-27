using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

// Issue #774 (W03): `openbios-probe --compare-boundary-logs <a.jsonl> <b.jsonl>` names the first boundary where two logs
// differ. Synthetic logs only. Boundaries present on one side alone are not a mismatch: a run may simply have stopped earlier.
[Test]
public sealed class OpenBiosProbeBoundaryCompareTests
{
    [Fact]
    public void Identical_Prefixes_Report_No_Mismatch_And_Count_Each_Side()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa", "bb", "cc"));
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa", "bb"));

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(0, error);
        var document = JsonNode.Parse(output)!.AsObject();
        document["firstMismatch"].Should().BeNull();
        document["commonBoundaries"]!.GetValue<int>().Should().Be(2);
        document["leftBoundaries"]!.GetValue<int>().Should().Be(3);
        document["rightBoundaries"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void A_Differing_Boundary_Is_Named_By_Index_VBlank_And_Field()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa", "bb"));
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa", "cc"));

        var exit = Compare(left, right, out var output, out _);

        exit.Should().Be(0);
        var mismatch = JsonNode.Parse(output)!["firstMismatch"]!.AsObject();
        mismatch["index"]!.GetValue<int>().Should().Be(1);
        mismatch["vblank"]!.GetValue<int>().Should().Be(2);
        mismatch["field"]!.GetValue<string>().Should().Be("vramSha256");
    }

    [Fact]
    public void Zero_Common_Boundaries_Is_A_Failure_Not_A_Clean_Match()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa", "bb"));
        var right = dir.WriteFile("right.jsonl", []);

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(1, "an empty side compares nothing; reporting it as a match would pass a run that was never compared");
        error.Should().Contain("commonBoundaries is 0");
        JsonNode.Parse(output)!["commonBoundaries"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void A_Missing_File_Is_Refused()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa"));

        var exit = Compare(left, dir.Combine("absent.jsonl"), out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("absent.jsonl");
    }

    private static int Compare(string left, string right, out string output, out string error)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = OpenBiosProbeCommand.Run(["--compare-boundary-logs", left, right], o, e);
        output = o.ToString();
        error = e.ToString();
        return exit;
    }

    private static byte[] Lines(string engine, params string[] vramHashes)
    {
        var text = string.Concat(vramHashes.Select((vram, i) =>
            "{\"engine\":\"" + engine + "\",\"vblank\":" + (i + 1) + ",\"cycles\":0,\"iStat\":\"0x00000000\",\"iMask\":\"0x00000000\"," +
            "\"dicr\":\"0x00000000\",\"gpuStat\":\"0x00000000\",\"displayMode\":\"0x00000000\",\"displayVramStart\":\"0x00000000\"," +
            "\"vramSha256\":\"" + vram + "\"}\n"));
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}

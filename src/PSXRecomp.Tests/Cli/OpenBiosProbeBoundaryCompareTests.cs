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
    public void Identical_Invalid_Records_Are_Refused_With_A_Diagnostic_Not_A_Match()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", System.Text.Encoding.UTF8.GetBytes("{\"foo\":1}\n"));
        var right = dir.WriteFile("right.jsonl", System.Text.Encoding.UTF8.GetBytes("{\"foo\":1}\n"));

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("left log line 1: missing required field");
    }

    [Fact]
    public void A_JSON_Array_Line_Is_Refused_Without_An_Unhandled_Exception()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa"));
        var right = dir.WriteFile("right.jsonl", System.Text.Encoding.UTF8.GetBytes("[1,2,3]\n"));

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("right log line 1: expected a JSON object");
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

    [Fact]
    public void An_Empty_Path_Is_Refused_With_A_Diagnostic_Not_An_Unhandled_Exception()
    {
        using var dir = new TempDirectory();
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa"));

        var exit = Compare("", right, out var output, out var error);

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("--compare-boundary-logs: ArgumentException");
    }

    [Fact]
    public void A_Directory_Path_Is_Refused_With_A_Diagnostic_Not_An_Unhandled_Exception()
    {
        using var dir = new TempDirectory();
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa"));

        var exit = Compare(dir.FullPath, right, out var output, out var error);

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("--compare-boundary-logs:");
    }

    [Fact]
    public void A_Nonexistent_Path_Is_Refused_With_A_Diagnostic()
    {
        using var dir = new TempDirectory();
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa"));

        var exit = Compare(dir.Combine("no-such-dir", "absent.jsonl"), right, out var output, out var error);

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("--compare-boundary-logs: DirectoryNotFoundException");
    }

    [Fact]
    public void Both_Empty_Files_Are_Refused_Because_No_Boundary_Is_Compared()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", []);
        var right = dir.WriteFile("right.jsonl", []);

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(1);
        error.Should().Contain("commonBoundaries is 0");
        output.Should().Contain("\"commonBoundaries\":0");
    }

    [Fact]
    public void A_Single_Line_With_No_Trailing_Newline_Is_A_Valid_Boundary()
    {
        using var dir = new TempDirectory();
        var left = dir.WriteFile("left.jsonl", Lines("interpreter", "aa")[..^1]);
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa"));

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(0, error);
        var document = JsonNode.Parse(output)!.AsObject();
        document["leftBoundaries"]!.GetValue<int>().Should().Be(1);
        document["firstMismatch"].Should().BeNull();
    }

    [Fact]
    public void A_Non_UTF8_Engine_Label_Is_Read_Without_An_Unhandled_Exception()
    {
        // The engine label is not compared, so a label with invalid UTF-8 bytes must not make the record unreadable.
        using var dir = new TempDirectory();
        var line = System.Text.Encoding.ASCII.GetBytes("{\"engine\":\"").Concat(new byte[] { 0xFF, 0xFE })
            .Concat(System.Text.Encoding.ASCII.GetBytes(RecordTail("aa")))
            .Concat(new byte[] { (byte)'\n' }).ToArray();
        var left = dir.WriteFile("left.jsonl", line);
        var right = dir.WriteFile("right.jsonl", Lines("standalone", "aa"));

        var exit = Compare(left, right, out var output, out var error);

        exit.Should().Be(0, error);
        error.Should().NotContain("Exception");
        output.Should().Contain("\"commonBoundaries\":1");
    }

    private static string RecordTail(string vram) =>
        "\",\"vblank\":1,\"cycles\":0,\"iStat\":\"0x00000000\",\"iMask\":\"0x00000000\"," +
        "\"dicr\":\"0x00000000\",\"gpuStat\":\"0x00000000\",\"displayMode\":\"0x00000000\",\"displayVramStart\":\"0x00000000\"," +
        "\"vramSha256\":\"" + vram.PadLeft(64, '0') + "\"}";

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
            "\"vramSha256\":\"" + vram.PadLeft(64, '0') + "\"}\n"));
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}

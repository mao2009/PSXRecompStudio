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
        record["vramSha256"]!.GetValue<string>().Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(graph.GpuDevice.GetVramBytes())).ToLowerInvariant());
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
        var left = Parse(Line("interpreter", vram: "aa"), Line("interpreter", vram: "bb", vblank: 2));
        var same = Parse(Line("host", vram: "aa"), Line("host", vram: "bb", vblank: 2));
        var diverged = Parse(Line("host", vram: "aa"), Line("host", vram: "cc", vblank: 2));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, same).Should().BeNull();
        OpenBiosProbeBoundaryLog.FirstMismatch(left, diverged).Should().Be((1, "vramSha256"));
    }

    [Fact]
    public void FirstMismatch_Compares_Only_The_Boundaries_Both_Logs_Hold()
    {
        var left = Parse(Line("interpreter", vram: "aa"));
        var right = Parse(Line("host", vram: "aa"), Line("host", vram: "bb", vblank: 2));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().BeNull();
    }

    [Fact]
    public void Identical_Invalid_Json_On_Both_Sides_Is_Rejected_Not_Matched()
    {
        Action parse = () => OpenBiosProbeBoundaryLog.ParseLog(["{\"foo\":1}"], "left");
        Action both = () =>
        {
            var left = OpenBiosProbeBoundaryLog.ParseLog(["{\"foo\":1}"], "left");
            var right = OpenBiosProbeBoundaryLog.ParseLog(["{\"foo\":1}"], "right");
            OpenBiosProbeBoundaryLog.FirstMismatch(left, right);
        };

        parse.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: missing required field *");
        both.Should().Throw<BoundaryLogFormatException>();
    }

    [Fact]
    public void JSON_Array_Input_Is_Rejected_With_A_Diagnostic_Not_An_Unhandled_Exception()
    {
        Action act = () => OpenBiosProbeBoundaryLog.ParseLog(["[1,2,3]"], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: expected a JSON object, got a JSON array");
    }

    [Fact]
    public void Non_Object_And_Malformed_Lines_Are_Rejected()
    {
        Action scalar = () => OpenBiosProbeBoundaryLog.ParseLog(["42"], "left");
        Action malformed = () => OpenBiosProbeBoundaryLog.ParseLog(["{\"engine\":"], "left");

        scalar.Should().Throw<BoundaryLogFormatException>().WithMessage("*expected a JSON object*");
        malformed.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: not valid JSON*");
    }

    [Fact]
    public void A_Missing_Required_Field_Is_Rejected()
    {
        var withoutVram = Line("interpreter", vram: "aa").Replace(",\"vramSha256\":\"" + Sha("aa") + "\"", "", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([withoutVram], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: missing required field \"vramSha256\"");
    }

    [Fact]
    public void An_Unexpected_Extra_Field_Is_Rejected()
    {
        var extra = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"extra\":1,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([extra], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: unexpected field \"extra\"");
    }

    [Fact]
    public void A_Field_Of_The_Wrong_Type_Is_Rejected()
    {
        // vblank as a string, not a number.
        var stringVBlank = Line("interpreter", vram: "aa").Replace("\"vblank\":1", "\"vblank\":\"1\"", StringComparison.Ordinal);
        // cycles as a fraction, and a hex field without the 0x prefix.
        var fractionCycles = Line("interpreter", vram: "aa").Replace("\"cycles\":0", "\"cycles\":1.5", StringComparison.Ordinal);
        var badHex = Line("interpreter", vram: "aa").Replace("\"iStat\":\"0x00000000\"", "\"iStat\":\"00000000\"", StringComparison.Ordinal);

        Action a = () => OpenBiosProbeBoundaryLog.ParseLog([stringVBlank], "left");
        Action b = () => OpenBiosProbeBoundaryLog.ParseLog([fractionCycles], "left");
        Action c = () => OpenBiosProbeBoundaryLog.ParseLog([badHex], "left");

        a.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"vblank\" must be a non-negative integer");
        b.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"cycles\" must be a non-negative integer");
        c.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"iStat\" must be *");
    }

    [Fact]
    public void A_Hex_Field_With_A_Trailing_Newline_Is_Refused()
    {
        // The JSON escape \n is a real newline in the value. A "$" anchor would accept it; the pattern must end at \z.
        var line = Line("interpreter", vram: "aa").Replace("\"iStat\":\"0x00000000\"", "\"iStat\":\"0x00000000\\n\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"iStat\" must be *");
    }

    [Fact]
    public void A_Digest_With_A_Trailing_Newline_Is_Refused()
    {
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\":\"" + Sha("aa") + "\"", "\"vramSha256\":\"" + Sha("aa") + "\\n\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"vramSha256\" must be 64 lowercase hex digits*");
    }

    [Fact]
    public void A_Duplicate_Required_Key_Is_Refused_Not_Silently_Read_Once()
    {
        // JsonNode would keep one of two equal property names; the record must refuse the duplicate instead.
        var line = Line("interpreter", vram: "aa").Replace("\"cycles\":0", "\"cycles\":0,\"cycles\":0", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: duplicate field \"cycles\"");
    }

    [Fact]
    public void A_Duplicate_Unexpected_Key_Is_Refused()
    {
        // The record has no optional keys: every key is required, so an unexpected key is refused whether it repeats or not.
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"extra\":1,\"extra\":2,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: duplicate field \"extra\"");
    }

    [Fact]
    public void A_Duplicate_Key_Written_With_A_Unicode_Escape_Is_Refused()
    {
        // "\u0063ycles" is the same property name as "cycles" once unescaped. JsonProperty.Name returns the unescaped name,
        // so the duplicate check must see the two as equal.
        var line = Line("interpreter", vram: "aa").Replace("\"cycles\":0", "\"cycles\":0,\"\\u0063ycles\":0", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: duplicate field \"cycles\"");
    }

    [Fact]
    public void A_Control_Character_In_An_Echoed_Key_Is_Written_As_Escape_Text()
    {
        // The key holds a real BEL character (written in the file as the escape \u0007). The diagnostic must not carry the raw character.
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"ex\\u0007tra\":1,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: unexpected field \"ex\\u0007tra\"");
    }

    [Fact]
    public void A_Long_Echoed_Key_Is_Truncated_In_The_Diagnostic()
    {
        var longKey = new string('k', 60);
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"" + longKey + "\":1,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: unexpected field \"" + new string('k', 48) + "...\"");
    }

    [Fact]
    public void A_Surrogate_Pair_Straddling_The_Cut_Is_Kept_Whole_Not_Split()
    {
        // 47 characters, then an emoji (two UTF-16 code units, the 48th code point), then one more character. A cut counted
        // in code units would fall between the surrogates. The cut is after 48 code points, so the emoji is kept whole.
        var key = new string('k', 47) + "\U0001F600" + "z";
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"" + key + "\":1,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: unexpected field \"" + new string('k', 47) + "\U0001F600...\"");
    }

    [Fact]
    public void Format_And_Separator_Characters_In_An_Echoed_Key_Are_Written_As_Escape_Text()
    {
        // U+202E (bidi override, category Cf), U+2028 (line separator): written in the file as JSON escapes, echoed as escape text.
        var line = Line("interpreter", vram: "aa").Replace("\"vramSha256\"", "\"ab\\u202Ecd\\u2028\":1,\"vramSha256\"", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: unexpected field \"ab\\u202Ecd\\u2028\"");
    }

    [Fact]
    public void A_Huge_Integer_Beyond_UInt64_Is_Refused_With_A_Diagnostic()
    {
        var line = Line("interpreter", vram: "aa").Replace("\"vblank\":1", "\"vblank\":18446744073709551616", StringComparison.Ordinal);

        Action act = () => OpenBiosProbeBoundaryLog.ParseLog([line], "left");

        act.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 1: field \"vblank\" must be a non-negative integer");
    }

    [Fact]
    public void VBlank_Above_Int_Range_Is_A_Valid_Boundary_And_Compares()
    {
        var left = Parse(Line("i", "aa", 3_000_000_000), Line("i", "bb", 3_000_000_001));
        var right = Parse(Line("h", "aa", 3_000_000_000), Line("h", "cc", 3_000_000_001));

        left[1].VBlank.Should().Be(3_000_000_001UL);
        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().Be((1, "vramSha256"));
    }

    [Fact]
    public void A_Duplicated_Or_Backwards_VBlank_Number_Is_Rejected()
    {
        Action duplicated = () => OpenBiosProbeBoundaryLog.ParseLog([Line("i", "aa", vblank: 2), Line("i", "aa", vblank: 2)], "left");
        Action backwards = () => OpenBiosProbeBoundaryLog.ParseLog([Line("i", "aa", vblank: 3), Line("i", "aa", vblank: 2)], "right");

        duplicated.Should().Throw<BoundaryLogFormatException>().WithMessage("left log line 2: vblank 2 does not follow vblank 2*");
        backwards.Should().Throw<BoundaryLogFormatException>().WithMessage("right log line 2: vblank 2 does not follow vblank 3*");
    }

    [Fact]
    public void A_Mismatched_Corresponding_VBlank_Number_Is_Detected()
    {
        var left = Parse(Line("interpreter", vram: "aa", vblank: 1), Line("interpreter", vram: "aa", vblank: 2));
        var right = Parse(Line("host", vram: "aa", vblank: 1), Line("host", vram: "aa", vblank: 3));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().Be((1, "vblank"));
    }

    [Fact]
    public void An_Exact_Match_Passes()
    {
        var left = Parse(Line("interpreter", vram: "aa"), Line("interpreter", vram: "bb", vblank: 2));
        var right = Parse(Line("host", vram: "aa"), Line("host", vram: "bb", vblank: 2));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().BeNull();
    }

    [Fact]
    public void The_First_Real_Difference_Is_Reported_As_The_First_Mismatch()
    {
        var left = Parse(Line("i", "aa"), Line("i", "bb", vblank: 2), Line("i", "cc", vblank: 3));
        var right = Parse(Line("h", "aa"), Line("h", "dd", vblank: 2), Line("h", "ee", vblank: 3));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().Be((1, "vramSha256"));
    }

    [Fact]
    public void A_Later_Boundary_Present_On_One_Side_Only_Is_Not_A_Mismatch()
    {
        var left = Parse(Line("i", "aa"), Line("i", "bb", vblank: 2), Line("i", "cc", vblank: 3));
        var right = Parse(Line("h", "aa"));

        OpenBiosProbeBoundaryLog.FirstMismatch(left, right).Should().BeNull();
        OpenBiosProbeBoundaryLog.FirstMismatch(right, left).Should().BeNull();
    }

    private static IReadOnlyList<OpenBiosProbeBoundaryRecord> Parse(params string[] lines) =>
        OpenBiosProbeBoundaryLog.ParseLog(lines, "test");

    [Fact]
    public void A_Failing_Writer_Is_Contained_And_Recorded()
    {
        using var graph = new PsxDeviceGraph();
        using var log = new OpenBiosProbeBoundaryLog(new FailingWriter());

        var act = () => log.Write("interpreter", graph, 1, 0);

        act.Should().NotThrow();
        log.FailureMessage.Should().Contain("IOException");
    }

    [Fact]
    public void A_Null_Device_Graph_Is_Contained_And_Recorded_As_A_NullReferenceException()
    {
        // Renamed to what it does: a null graph makes the device reads throw NullReferenceException, a type the log's
        // output path does not list. The catch-all in Write must contain it rather than let it end the guest run.
        using var log = new OpenBiosProbeBoundaryLog(new StringWriter());

        var act = () => log.Write("interpreter", null!, 1, 0);

        act.Should().NotThrow();
        log.FailureMessage.Should().Contain("NullReferenceException");
    }

    private static string Line(string engine, string vram, ulong vblank = 1) =>
        "{\"engine\":\"" + engine + "\",\"vblank\":" + vblank + ",\"cycles\":0,\"iStat\":\"0x00000000\",\"iMask\":\"0x00000000\"," +
        "\"dicr\":\"0x00000000\",\"gpuStat\":\"0x00000000\",\"displayMode\":\"0x00000000\",\"displayVramStart\":\"0x00000000\"," +
        "\"vramSha256\":\"" + Sha(vram) + "\"}";

    // A synthetic digest: the short tag left-padded to the 64 lowercase hex digits a real SHA-256 has.
    private static string Sha(string tag) => tag.PadLeft(64, '0');

    private sealed class FailingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("disk full");
    }
}

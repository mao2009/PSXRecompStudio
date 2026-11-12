using System.Text.Json.Nodes;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #773 Phase B: the opt-in record of RAM-placed code refused at a handoff. It only describes the refusal; the
// selection (LoadedCodeTable.Match) it observes must stay exactly as it was.
public sealed class LoadedCodeMismatchDiagnosticsTests
{
    private const uint Pc = 0x80030C44u;

    private static GuardedBlock Version(uint pc, params uint[] words) =>
        new(new RecompilerIrBlock(pc, [], new RecompilerIrExit(RecompilerIrTerminationReason.Success, pc + 4)), words);

    private static JsonArray Records(LoadedCodeMismatchDiagnostics diagnostics) =>
        JsonNode.Parse(diagnostics.ToJson())!["records"]!.AsArray();

    [Fact]
    public void Observe_RecordsTheFirstDifferingWord_AndTheWindowAroundIt()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var ram = new uint[] { 1, 2, 9, 4, 5 };

        diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4, 5)], address => ram[(address - Pc) / 4]);

        var record = Records(diagnostics)[0]!;
        record["pc"]!.GetValue<string>().Should().Be("0x80030C44");
        record["rejections"]!.GetValue<ulong>().Should().Be(1);
        record["comparedWords"]!.GetValue<int>().Should().Be(5);
        record["firstMismatchIndexRange"]!.AsArray().Select(static n => n!.GetValue<int>()).Should().Equal(2, 2);
        record["mismatchedWordsRange"]!.AsArray().Select(static n => n!.GetValue<int>()).Should().Equal(1, 1);
        record["windowStartPc"]!.GetValue<string>().Should().Be("0x80030C44", "the window is clamped at the start of the range");
        record["windowAot"]!.AsArray().Select(static n => n!.GetValue<string>()).Should().Equal("0x00000001", "0x00000002", "0x00000003", "0x00000004", "0x00000005");
    }

    // PR #839 MAJOR: the report must not carry a RAM word. The RAM word here (0x5EEDCAFE) differs from every image word; the report names no
    // RAM word, and the RAM range is only hashed (ramSha256).
    [Fact]
    public void Report_CarriesNoRamWordValue_OnlyTheImageWindowAndHashes()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var ram = new uint[] { 1, 2, 0x5EEDCAFE, 4, 5 };

        diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4, 5)], address => ram[(address - Pc) / 4]);

        var json = diagnostics.ToJson();
        json.Should().NotContain("windowRam");
        json.Should().NotContain("5EEDCAFE").And.NotContain("5eedcafe");
        Records(diagnostics)[0]!["ramSha256"]!.GetValue<string>().Should().HaveLength(64, "the RAM range is hashed, not written");
    }

    [Fact]
    public void Observe_SeparatesVersionsAtOnePc_AndNamesTheMatchingOne()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var versions = new[] { Version(Pc, 1, 2), Version(Pc, 7, 8) };

        // Guest RAM holds the second version, while the refused handoff was recorded for the first: the host's comparison
        // differs per version. It says nothing about which version the native guard selected.
        diagnostics.Observe(Pc, versions, address => (address - Pc) / 4 == 0 ? 7u : 8u);

        var records = Records(diagnostics);
        records.Should().HaveCount(2);
        var refused = records[0]!;
        refused["version"]!.GetValue<int>().Should().Be(0);
        refused["rejections"]!.GetValue<ulong>().Should().Be(1);
        refused["versionCount"]!.GetValue<int>().Should().Be(2);
        var matching = records[1]!;
        matching["version"]!.GetValue<int>().Should().Be(1);
        matching["rejections"]!.GetValue<ulong>().Should().Be(0);
        matching["fullMatches"]!.GetValue<ulong>().Should().Be(1, "the host's RAM comparison matched this version in full");
    }

    [Fact]
    public void Observe_AggregatesRepeatedRefusalsAtOnePc_AndCountsChangingRam()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var block = Version(Pc, 1, 2);
        for (var n = 0u; n < 1000; n++)
        {
            diagnostics.Observe(Pc, [block], address => address == Pc ? n + 10u : 2u);
        }

        diagnostics.TotalObservations.Should().Be(1000);
        diagnostics.DistinctRecords.Should().Be(1);
        var record = Records(diagnostics)[0]!;
        record["rejections"]!.GetValue<ulong>().Should().Be(1000);
        record["ramHashChanges"]!.GetValue<ulong>().Should().Be(999, "the first word differs on every refusal, so every refusal after the first changes the hash");
    }

    [Fact]
    public void Observe_CapsDistinctRecords_AndKeepsTheTotals()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxRecords: 2);

        for (var k = 0u; k < 5; k++)
        {
            var pc = Pc + k * 0x100u;
            diagnostics.Observe(pc, [Version(pc, 1)], _ => 0u);
        }

        diagnostics.TotalObservations.Should().Be(5);
        diagnostics.DistinctRecords.Should().Be(2);
        diagnostics.SuppressedObservations.Should().Be(3);
        Records(diagnostics).Select(static r => r!["pc"]!.GetValue<string>()).Should().Equal("0x80030C44", "0x80030D44");
    }

    [Fact]
    public void ToJson_ReportsIncomplete_WhenTheRecordCapDroppedRecords()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxRecords: 1);
        diagnostics.Observe(Pc, [Version(Pc, 1)], _ => 0u);
        diagnostics.Observe(Pc + 0x100u, [Version(Pc + 0x100u, 1)], _ => 0u);

        JsonNode.Parse(diagnostics.ToJson())!["incomplete"]!.GetValue<bool>().Should().BeTrue("a dropped record is a gap in the report");
    }

    // Contract pin only: the default of a report nobody marked is incomplete. It passes before and after any fix, so it is
    // not the regression guard for M-C; that is the parse-failure test in RuntimeHostLoadedCodeReportTests.
    [Fact]
    public void ToJson_DefaultsToIncomplete_UntilARunIsMarked()
    {
        var report = JsonNode.Parse(new LoadedCodeMismatchDiagnostics().ToJson())!.AsObject();

        report["executed"]!.GetValue<bool>().Should().BeFalse();
        report["incomplete"]!.GetValue<bool>().Should().BeTrue("a report without the completion marker is not complete");
    }

    [Fact]
    public void ToJson_IsDeterministic_ForTheSameObservations()
    {
        string Run()
        {
            var diagnostics = new LoadedCodeMismatchDiagnostics();
            diagnostics.Observe(Pc + 0x100u, [Version(Pc + 0x100u, 3)], _ => 4u);
            diagnostics.Observe(Pc, [Version(Pc, 1, 2), Version(Pc, 5)], _ => 9u);
            return diagnostics.ToJson();
        }

        Run().Should().Be(Run());
    }

    [Fact]
    public void Observe_LeavesTheSelectedVersionUnchanged()
    {
        // Guest RAM holds [3, 4] at Pc: that version is selected before and after the observation.
        static uint Ram(uint address) => address == Pc ? 3u : 4u;
        var table = new LoadedCodeTable([new GuardedImageProgram([Version(Pc, 1, 2), Version(Pc, 3, 4)], [])]);
        var before = table.Match(Pc, Ram)!.Words;

        new LoadedCodeMismatchDiagnostics().Observe(Pc, table.VersionsAt(Pc), Ram);

        table.Match(Pc, Ram)!.Words.Should().Equal(before).And.Equal(3u, 4u);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCap()
    {
        Action act = () => _ = new LoadedCodeMismatchDiagnostics(maxRecords: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Review (#786): the cost of observing must be bounded and reported. A suppressed (PC, version) must not read its words.
    [Fact]
    public void A_Suppressed_Record_Does_Not_Read_Its_Words_After_The_Cap_Is_Reached()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxRecords: 1);
        diagnostics.Observe(Pc, [Version(Pc, 1)], _ => 1u);
        var reads = 0;

        diagnostics.Observe(Pc + 0x100u, [Version(Pc + 0x100u, 1, 2, 3, 4, 5, 6, 7, 8)], _ => { reads++; return 0u; });

        reads.Should().Be(0);
        diagnostics.SuppressedObservations.Should().Be(1);
    }

    // Review M1 (#786): the budget bounds every version, not just the start of an observation. A version is compared whole
    // or not at all, so reads never exceed maxComparedWords and a cut-short comparison is never stored as a result.
    [Fact]
    public void A_Budget_Of_One_Word_Reads_Nothing_From_Two_Five_Word_Versions()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: 1);
        var reads = 0;

        diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4, 5), Version(Pc, 6, 7, 8, 9, 10)], _ => { reads++; return 0u; });

        reads.Should().Be(0);
        diagnostics.ComparedWords.Should().Be(0);
        diagnostics.SkippedVersions.Should().Be(2);
        diagnostics.SkippedWords.Should().Be(10);
        diagnostics.SkippedObservations.Should().Be(1);
        diagnostics.DistinctRecords.Should().Be(0);
        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["incomplete"]!.GetValue<bool>().Should().BeTrue();
        report["skippedVersions"]!.GetValue<ulong>().Should().Be(2);
        report["skippedWords"]!.GetValue<ulong>().Should().Be(10);
    }

    [Fact]
    public void A_Zero_Budget_Reads_Nothing_And_Reports_Incomplete()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: 0);
        var reads = 0;

        diagnostics.Observe(Pc, [Version(Pc, 1)], _ => { reads++; return 0u; });

        reads.Should().Be(0);
        JsonNode.Parse(diagnostics.ToJson())!["incomplete"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void A_Version_That_Does_Not_Fit_The_Remaining_Budget_Is_Skipped_Whole_And_A_Smaller_One_Still_Runs()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: 6);
        var reads = 0;
        uint Read(uint _) { reads++; return 0u; }

        // 4 words fit (remaining 2); the 4-word second version does not and is skipped; the 2-word third fits exactly.
        diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4), Version(Pc, 5, 6, 7, 8), Version(Pc, 9, 10)], Read);

        reads.Should().Be(6);
        diagnostics.ComparedWords.Should().Be(6);
        diagnostics.SkippedVersions.Should().Be(1);
        diagnostics.SkippedWords.Should().Be(4);
        Records(diagnostics).Select(static r => r!["version"]!.GetValue<int>()).Should().Equal(0, 2);
    }

    [Fact]
    public void Many_Versions_Never_Read_More_Than_The_Budget()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxRecords: 5000, maxComparedWords: 100);
        var versions = Enumerable.Range(0, 1000).Select(n => Version(Pc, (uint)n, 2, 3)).ToArray();
        var reads = 0;

        diagnostics.Observe(Pc, versions, _ => { reads++; return 0u; });

        reads.Should().Be(99); // 33 whole three-word versions
        diagnostics.ComparedWords.Should().Be(99);
        diagnostics.SkippedVersions.Should().Be(967);
        diagnostics.SkippedWords.Should().Be(967UL * 3);
        reads.Should().Be((int)diagnostics.ComparedWords);
    }

    [Fact]
    public void Repeated_Observations_Of_One_Pc_Stop_Reading_When_The_Budget_Is_Spent()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: 10);
        var reads = 0;
        for (var n = 0; n < 4; n++)
        {
            diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4)], _ => { reads++; return 9u; });
        }

        reads.Should().Be(8);
        diagnostics.ComparedWords.Should().Be(8);
        diagnostics.SkippedObservations.Should().Be(2);
        diagnostics.SkippedVersions.Should().Be(2);
        diagnostics.TotalObservations.Should().Be(4);

        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["incomplete"]!.GetValue<bool>().Should().BeTrue();
        report["skippedObservations"]!.GetValue<ulong>().Should().Be(2);
        report["maxComparedWords"]!.GetValue<ulong>().Should().Be(10);
        Records(diagnostics)[0]!["rejections"]!.GetValue<ulong>().Should().Be(2);
    }

    [Fact]
    public void The_Record_Cap_And_The_Budget_Together_Keep_Reads_Equal_To_ComparedWords()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxRecords: 2, maxComparedWords: 7);
        var reads = 0;
        for (var n = 0u; n < 6; n++)
        {
            var pc = Pc + n * 0x100u;
            diagnostics.Observe(pc, [Version(pc, 1, 2, 3)], _ => { reads++; return 0u; });
        }

        reads.Should().Be(6);
        ((ulong)reads).Should().Be(diagnostics.ComparedWords);
        diagnostics.DistinctRecords.Should().Be(2);
        diagnostics.SuppressedObservations.Should().Be(4);
    }

    [Fact]
    public void An_Unbounded_Budget_Behaves_As_Before_And_Is_Complete()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: ulong.MaxValue);
        diagnostics.Observe(Pc, [Version(Pc, 1, 2), Version(Pc, 3)], _ => 0u);
        diagnostics.Observe(Pc, [Version(Pc, 1, 2), Version(Pc, 3)], _ => 0u);
        diagnostics.MarkExecuted(true);

        diagnostics.ComparedWords.Should().Be(6);
        diagnostics.SkippedVersions.Should().Be(0);
        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["incomplete"]!.GetValue<bool>().Should().BeFalse();
        report["skippedWords"]!.GetValue<ulong>().Should().Be(0);
    }

    [Fact]
    public void The_Report_Says_It_Records_Observed_Handoffs_Not_Confirmed_Guard_Decisions()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        diagnostics.Observe(Pc, [Version(Pc, 1)], _ => 2u);

        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["guardDecisionRecorded"]!.GetValue<bool>().Should().BeFalse();
        report["attribution"]!.GetValue<string>().Should().Be("host-handoff-ram-comparison");
        report["rejectionsMeaning"]!.GetValue<string>().Should().Contain("not a confirmed native guard refusal");
    }

    // Coordination with #784, which adds an init property to LoadedImageSpec: a `with` rebuild must keep the provenance Kind.
    [Fact]
    public void Rebuilding_A_LoadedImageSpec_With_Another_Member_Keeps_Its_Kind()
    {
        var spec = new LoadedImageSpec("a", 0x80010000u, [1u], [0x80010000u], "boot-exe");

        (spec with { Roots = [0x80010004u] }).Kind.Should().Be("boot-exe");
        (spec with { Roots = [0x80010004u] }).Origin.Kind.Should().Be("boot-exe");
    }
}

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
        record["windowRam"]!.AsArray().Select(static n => n!.GetValue<string>()).Should().Equal("0x00000001", "0x00000002", "0x00000009", "0x00000004", "0x00000005");
        record.ToJsonString().Should().Contain("\"selectedVersion\":null");
    }

    [Fact]
    public void Observe_SeparatesVersionsAtOnePc_AndNamesTheMatchingOne()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var versions = new[] { Version(Pc, 1, 2), Version(Pc, 7, 8) };

        // Guest RAM holds the second version, but the artifact still refused the PC: a selection problem, not a stale copy.
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
        matching["fullMatches"]!.GetValue<ulong>().Should().Be(1, "RAM holds this version in full, so it was a selection miss");
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

    [Fact]
    public void Observing_Stops_Comparing_At_The_Word_Budget_And_Reports_The_Skipped_Observations()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics(maxComparedWords: 10);
        var reads = 0;
        for (var n = 0; n < 4; n++)
        {
            diagnostics.Observe(Pc, [Version(Pc, 1, 2, 3, 4)], _ => { reads++; return 9u; });
        }

        // Observations 1–3 compare 4 words each (the budget is checked before an observation, so the third overshoots to 12).
        reads.Should().Be(12);
        diagnostics.ComparedWords.Should().Be(12);
        diagnostics.SkippedObservations.Should().Be(1);

        var report = JsonNode.Parse(diagnostics.ToJson())!.AsObject();
        report["incomplete"]!.GetValue<bool>().Should().BeTrue();
        report["skippedObservations"]!.GetValue<ulong>().Should().Be(1);
        report["maxComparedWords"]!.GetValue<ulong>().Should().Be(10);
    }

    [Fact]
    public void The_Report_Says_It_Records_Observed_Handoffs_Not_Confirmed_Guard_Decisions()
    {
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        diagnostics.Observe(Pc, [Version(Pc, 1)], _ => 2u);

        JsonNode.Parse(diagnostics.ToJson())!["guardDecisionRecorded"]!.GetValue<bool>().Should().BeFalse();
    }
}

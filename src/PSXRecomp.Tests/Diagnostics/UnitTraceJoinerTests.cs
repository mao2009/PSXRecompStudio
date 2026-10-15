using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

// Issue #774 (B-3): the native / fallback join. Every fixture is a synthetic line set; no game data is involved.
[Test]
public class UnitTraceJoinerTests
{
    private const string Session = "S1";

    private static string NativeHeader(string session = Session) => $"{{\"v\":1,\"kind\":\"header\",\"session\":\"{session}\",\"cap\":1024}}";
    private static string FallbackHeader(string session = Session) => $"{{\"v\":1,\"kind\":\"header\",\"session\":\"{session}\",\"cap\":1024}}";

    // Issue #774 (B-5): the joiner checks that each native stretch enters where the previous one ended, so the synthetic
    // units follow one straight line of pcs by default: unit [start, end) enters at PcOf(start) and ends at PcOf(end).
    private static uint PcOf(ulong nativeTotal) => 0x80000000u + (uint)(4 * nativeTotal);

    private static string Unit(ulong seq, ulong start, ulong end, uint? entry = null, uint? next = null) =>
        $"{{\"v\":1,\"kind\":\"unit\",\"seq\":{seq},\"entry\":\"0x{entry ?? PcOf(start):X8}\",\"next\":\"0x{next ?? PcOf(end):X8}\",\"start\":{start},\"end\":{end},\"retired\":{end - start},\"interrupted\":0,\"exception\":0,\"termination\":0}}";

    private static string Handoff(ulong seq, ulong handoff, ulong nativeTotal) =>
        $"{{\"v\":1,\"kind\":\"handoff\",\"seq\":{seq},\"handoff\":{handoff},\"native_total\":{nativeTotal}}}";

    /// <summary>A fallback segment. A returned one resumes the native trace at <paramref name="endPc"/>.</summary>
    private static string Fallback(ulong seq, ulong handoff, ulong retired, string status = "returned", uint endPc = 0x80010010u) =>
        $"{{\"v\":1,\"kind\":\"fallback\",\"seq\":{seq},\"handoff\":{handoff},\"retired\":{retired},\"status\":\"{status}\",\"start_pc\":\"0x80010000\",\"end_pc\":\"0x{endPc:X8}\"}}";

    private static string End(bool complete) => $"{{\"v\":1,\"kind\":\"end\",\"seq\":0,\"complete\":{(complete ? "true" : "false")}}}";

    private static string Truncated(ulong seq) => $"{{\"v\":1,\"kind\":\"truncated\",\"seq\":{seq},\"reason\":\"output_cap\"}}";

    [Fact]
    public void Native_Only_Complete_Run_Joins_To_Its_Units()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), Unit(1, 1, 2), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.ComparableUpToRetired.Should().Be(2UL);
        result.Segments.Select(s => (s.Source, s.GlobalStart, s.GlobalEnd)).Should().Equal(
            (UnitTraceSegmentSource.Native, 0UL, 1UL),
            (UnitTraceSegmentSource.Native, 1UL, 2UL));
    }

    [Fact]
    public void Native_Fallback_Native_Places_The_Fallback_Between_The_Two_Native_Stretches()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Handoff(1, 1, 2), Unit(2, 2, 4, entry: 0x80010010u), End(true)],
            [FallbackHeader(), Fallback(0, 1, 3), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.Segments.Select(s => (s.Source, s.Handoff, s.GlobalStart, s.GlobalEnd)).Should().Equal(
            (UnitTraceSegmentSource.Native, 0U, 0UL, 2UL),
            (UnitTraceSegmentSource.Fallback, 1U, 2UL, 5UL),
            (UnitTraceSegmentSource.Native, 0U, 5UL, 7UL));
        result.ComparableUpToRetired.Should().Be(7UL);
    }

    [Fact]
    public void Zero_Length_Handoffs_And_Repeated_Native_Totals_Keep_Their_Recorded_Order()
    {
        // Two handoffs at native total 2: the first completes nothing, the second runs 3 instructions.
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Handoff(1, 1, 2), Handoff(2, 2, 2), Unit(3, 2, 5), End(true)],
            [FallbackHeader(), Fallback(0, 1, 0), Fallback(1, 2, 3, endPc: PcOf(2)), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.Segments.Select(s => (s.Source, s.Handoff, s.GlobalStart, s.GlobalEnd)).Should().Equal(
            (UnitTraceSegmentSource.Native, 0U, 0UL, 2UL),
            (UnitTraceSegmentSource.Fallback, 1U, 2UL, 2UL),
            (UnitTraceSegmentSource.Fallback, 2U, 2UL, 5UL),
            (UnitTraceSegmentSource.Native, 0U, 5UL, 8UL));
    }

    [Fact]
    public void Swapping_The_Fallback_Records_Is_Rejected_Because_Identity_Comes_From_The_Handoff_Number()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Handoff(1, 1, 2), Handoff(2, 2, 2), Unit(3, 2, 5), End(true)],
            [FallbackHeader(), Fallback(0, 2, 3), Fallback(1, 1, 0), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("names handoff 2"));
    }

    [Fact]
    public void A_Different_Session_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader("A"), Unit(0, 0, 1), End(true)],
            [FallbackHeader("B"), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("session mismatch"));
    }

    [Fact]
    public void A_Sequence_Gap_In_The_Native_Trace_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), Unit(2, 1, 2), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("sequence gap"));
    }

    [Fact]
    public void A_Unit_That_Does_Not_Continue_The_Native_Total_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Unit(1, 3, 4), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("gap or reorder"));
    }

    [Fact]
    public void A_Unit_Whose_Retired_Count_Disagrees_With_Its_Totals_Is_Rejected()
    {
        var bad = Unit(0, 0, 1).Replace("\"retired\":1", "\"retired\":5", StringComparison.Ordinal);
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), bad, End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("does not equal end"));
    }

    [Fact]
    public void A_Handoff_At_The_Wrong_Native_Total_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Handoff(1, 1, 9), End(true)],
            [FallbackHeader(), Fallback(0, 1, 1), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("at native total 9"));
    }

    [Fact]
    public void A_Handoff_Without_A_Fallback_Record_Is_Rejected_When_Both_Traces_Ended_Normally()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Handoff(0, 1, 0), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("has no fallback record"));
    }

    [Fact]
    public void A_Fallback_Trace_Cut_Before_A_Handoff_Gives_A_Partial_Prefix_Only()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), Handoff(1, 1, 2), Unit(2, 2, 4), End(true)],
            [FallbackHeader(), Truncated(0), End(false)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.Segments.Should().HaveCount(1, "only the native stretch before the handoff is verified");
        result.ComparableUpToRetired.Should().Be(2UL);
    }

    [Fact]
    public void A_Truncated_Native_Trace_Gives_A_Partial_Prefix_Only()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), Unit(1, 1, 2), Truncated(2), End(false)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.Reason.Should().Contain("truncated");
        result.ComparableUpToRetired.Should().Be(2UL);
    }

    [Fact]
    public void A_Native_Trace_Without_An_End_Line_Is_Partial_Not_Complete()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.Reason.Should().Contain("no end line");
        result.ComparableUpToRetired.Should().Be(1UL);
    }

    [Fact]
    public void A_Torn_Final_Line_Is_Partial_And_The_Lines_Before_It_Are_Kept()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), "{\"v\":1,\"kind\":\"un"],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.Reason.Should().Contain("cut off");
        result.ComparableUpToRetired.Should().Be(1UL);
    }

    [Fact]
    public void A_Malformed_Line_In_The_Middle_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), "{not json", Unit(0, 0, 1), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("malformed JSON"));
    }

    [Fact]
    public void Leftover_Fallback_Records_With_A_Complete_Native_Trace_Are_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), End(true)],
            [FallbackHeader(), Fallback(0, 1, 2), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("no native handoff marker"));
    }

    [Fact]
    public void Leftover_Fallback_Records_With_A_Cut_Native_Trace_Are_Only_Partial()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), Truncated(1), End(false)],
            [FallbackHeader(), Fallback(0, 1, 2), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.ComparableUpToRetired.Should().Be(1UL);
    }

    [Fact]
    public void A_Not_Run_Segment_That_Claims_Retired_Instructions_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Handoff(0, 1, 0), End(true)],
            [FallbackHeader(), Fallback(0, 1, 2, status: "not_run"), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("not_run"));
    }

    [Fact]
    public void A_Stopped_Fallback_Segment_Is_Kept_And_Its_Partial_Run_Is_Reported()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Handoff(0, 1, 0), End(false)],
            [FallbackHeader(), Fallback(0, 1, 3, status: "stopped"), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.Segments.Should().ContainSingle(s => s.Status == "stopped" && s.GlobalEnd == 3UL);
    }

    [Fact]
    public void Interrupted_And_Exception_Flags_Do_Not_Change_The_Ordering()
    {
        var interrupted = Unit(0, 0, 1).Replace("\"interrupted\":0", "\"interrupted\":1", StringComparison.Ordinal);
        var raised = Unit(1, 1, 2).Replace("\"exception\":0", "\"exception\":1", StringComparison.Ordinal);
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), interrupted, raised, End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.Segments.Select(s => s.GlobalEnd).Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void A_Record_After_The_End_Line_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), End(true), Unit(0, 0, 1)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("after the end line"));
    }

    [Fact]
    public void A_Header_Without_A_Session_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [$"{{\"v\":1,\"kind\":\"header\",\"cap\":1024}}", End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("no session id"));
    }

    [Fact]
    public void A_Record_With_A_Different_Schema_Version_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1).Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("schema v1"));
    }

    [Fact]
    public void An_Unknown_Record_Kind_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), "{\"v\":1,\"kind\":\"mystery\",\"seq\":0}", End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("unknown record kind"));
    }

    [Fact]
    public void A_Fallback_Record_In_The_Native_Trace_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Fallback(0, 1, 1), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("does not belong"));
    }

    [Fact]
    public void An_Empty_Trace_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join([], [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("empty trace"));
    }

    // Issue #774 (B-5): the control-flow and stop invariants. Each native stretch must enter where the previous one ended.

    /// <summary>A unit with explicit termination and flags, for the stop and interrupt cases.</summary>
    private static string UnitWith(ulong seq, ulong start, ulong end, uint entry, uint next, int termination = 0, int interrupted = 0, int exception = 0) =>
        $"{{\"v\":1,\"kind\":\"unit\",\"seq\":{seq},\"entry\":\"0x{entry:X8}\",\"next\":\"0x{next:X8}\",\"start\":{start},\"end\":{end},\"retired\":{end - start},\"interrupted\":{interrupted},\"exception\":{exception},\"termination\":{termination}}}";

    private static string HandoffAt(ulong seq, ulong handoff, ulong nativeTotal, uint pc) =>
        $"{{\"v\":1,\"kind\":\"handoff\",\"seq\":{seq},\"handoff\":{handoff},\"native_total\":{nativeTotal},\"pc\":\"0x{pc:X8}\"}}";

    [Fact]
    public void A_Unit_That_Enters_Away_From_The_Previous_Next_Pc_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), UnitWith(0, 0, 1, 0x80000000u, 0x80000004u), UnitWith(1, 1, 2, 0x80000100u, 0x80000104u), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("control-flow break"));
    }

    [Fact]
    public void A_Zero_Retired_Unit_That_Is_Not_A_Transfer_Iteration_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 1), UnitWith(1, 1, 1, 0x80000004u, 0x80000004u), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("completes no instruction"));
    }

    [Fact]
    public void A_Transfer_Iteration_Must_Name_The_Pc_Where_The_Fallback_Returned()
    {
        // The iteration after handoff 1 enters at the transfer pc and claims it resumes at 0x80000200, but the fallback
        // returned to 0x80010010.
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), HandoffAt(1, 1, 2, 0x80010000u), UnitWith(2, 2, 2, 0x80010000u, 0x80000200u), End(true)],
            [FallbackHeader(), Fallback(0, 1, 3), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("transfer iteration ends at"));
    }

    [Fact]
    public void A_Host_Transfer_Iteration_With_A_Matching_Exit_Pc_Is_Accepted()
    {
        // The exit transfer: the host serves pc 0 with no guest instruction, and the dispatcher's pass ends at pc 0.
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), HandoffAt(1, 1, 2, 0u), UnitWith(2, 2, 2, 0u, 0u), End(true)],
            [FallbackHeader(), TransferLine(0, 1, 0x00000000u), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.Segments.Should().ContainSingle(s => s.Source == UnitTraceSegmentSource.Host);
    }

    [Fact]
    public void An_Interrupt_Boundary_May_Enter_At_The_Vector_Without_Breaking_The_Chain()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), UnitWith(1, 2, 2, 0x80000080u, 0x80000080u, interrupted: 1), End(true)],
            [FallbackHeader(), End(true)]);

        // The interrupted stretch completes nothing; its entry is the vector, which is not a break in the chain.
        result.Status.Should().Be(UnitTraceJoinStatus.Complete);
        result.ComparableUpToRetired.Should().Be(2UL);
    }

    [Fact]
    public void A_Unit_After_A_Fallback_That_Stopped_The_Run_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), HandoffAt(1, 1, 2, 0x80010000u), Unit(2, 2, 3, entry: 0x80010000u), End(true)],
            [FallbackHeader(), Fallback(0, 1, 1, status: "stopped"), End(false)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("stopped the run"));
    }

    [Fact]
    public void A_Record_After_A_Native_Stop_Is_Rejected()
    {
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), UnitWith(0, 0, 0, 0x80000000u, 0x80000000u, termination: 5), Unit(1, 0, 1), End(true)],
            [FallbackHeader(), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        result.Errors.Should().Contain(e => e.Contains("follows the run's stop"));
    }

    [Fact]
    public void A_Fallback_Trace_Complete_But_A_Native_Trace_Cut_After_A_Handoff_Is_Partial_Not_Complete()
    {
        // The parent closed its trace normally, but the child stopped writing right after handoff 1. The handoff is
        // unanswered on the native side, so nothing past it may be compared.
        var result = UnitTraceJoiner.Join(
            [NativeHeader(), Unit(0, 0, 2), HandoffAt(1, 1, 2, 0x80010000u)],
            [FallbackHeader(), Fallback(0, 1, 3), End(true)]);

        result.Status.Should().Be(UnitTraceJoinStatus.Partial);
        result.ComparableUpToRetired.Should().Be(5UL);
    }

    private static string TransferLine(ulong seq, ulong handoff, uint pc) =>
        $"{{\"v\":1,\"kind\":\"transfer\",\"seq\":{seq},\"handoff\":{handoff},\"retired\":0,\"status\":\"host\",\"start_pc\":\"0x{pc:X8}\",\"end_pc\":\"0x{pc:X8}\"}}";
}

using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

/// <summary>Issue #732: the probe's fallback accounting counts exactly what it observed, by region, reason and PC.</summary>
[Test]
public sealed class OpenBiosProbeAccountingTests
{
    [Theory]
    [InlineData(0xBFC00000u, "rom")]
    [InlineData(0x9FC7FFFCu, "rom")]
    [InlineData(0xBFC80000u, "other")]
    [InlineData(0x000000B0u, "kernel-ram")]
    [InlineData(0x8000FFFCu, "kernel-ram")]
    [InlineData(0x80010000u, "user-ram")]
    [InlineData(0x8002FFFCu, "user-ram")]
    [InlineData(0x80030000u, "shell")]
    [InlineData(0xA00450BCu, "shell")]
    [InlineData(0x800450C0u, "user-ram")]
    [InlineData(0x80600100u, "kernel-ram")] // the 8 MiB RAM mirror
    [InlineData(0x1F800000u, "other")]
    public void Classifies_Regions_By_Physical_Address(uint pc, string region) =>
        Assert.Equal(region, OpenBiosProbeAccounting.RegionOf(pc));

    [Theory]
    [InlineData(0x80000080u, "exception-vector")]
    [InlineData(0x000000B0u, "kernel-call-vector")]
    [InlineData(0xA00000A0u, "kernel-call-vector")]
    [InlineData(0xBFC05734u, "rom-no-block")]
    [InlineData(0x00000540u, "ram-no-code-image")]
    [InlineData(0x80030C44u, "ram-no-code-image")]
    [InlineData(0x1F800000u, "other-no-block")]
    public void Classifies_Transition_Reasons_By_Entry_Pc(uint pc, string reason) =>
        Assert.Equal(reason, OpenBiosProbeAccounting.ReasonOf(pc));

    private static readonly Func<(uint, uint)> NoException = () => (0u, 0u);

    private static JsonNode Report(OpenBiosProbeAccounting accounting, ulong? native, ulong? retired, uint[] blocks, ProbeSymbols? symbols = null) =>
        JsonSerializer.SerializeToNode(accounting.Report(native, retired, blocks, symbols))!;

    [Theory]
    [InlineData(0x80001000u, "aot-coverage-gap:loaded-image")]
    [InlineData(0xA0001000u, "aot-coverage-gap:loaded-image")]
    [InlineData(0x00001000u, "aot-coverage-gap:loaded-image")]
    [InlineData(0x80201000u, "aot-coverage-gap:loaded-image")]
    [InlineData(0x9FC00000u, "aot-coverage-gap:rom")]
    [InlineData(0x1FC00000u, "aot-coverage-gap:rom")]
    [InlineData(0xBFC00000u, "aot-coverage-gap:rom")]
    [InlineData(0xC0001000u, "unknown-code")]
    public void FallbackCoverage_RecognizesAliasesWithoutChangingTheReportedPc(uint pc, string reason)
    {
        var manifest = new PSXRecomp.Core.Recompiler.LoadImageManifest(
            [new PSXRecomp.Core.Recompiler.LoadedImageSpec("ram", 0x80001000, [0u, 0u], [])], new HashSet<uint>());
        var cause = OpenBiosProbeCommand.ClassifyFallback(pc, manifest, PSXRecomp.Core.Recompiler.LoadedCodeTable.Empty, 8);
        Assert.Equal(reason, cause);
        var accounting = new OpenBiosProbeAccounting();
        accounting.OnTransition(new MixedFallbackTransition(pc, true, FallbackSegmentStatus.Returned, pc + 4, 1, null));
        var report = JsonSerializer.SerializeToNode(accounting.Report(1, 1, [0xBFC00000], null, _ => cause))!;
        Assert.Equal($"0x{pc:X8}", report["hotEntries"]![0]!["pc"]!.ToString());
        Assert.Equal(reason, report["hotEntries"]![0]!["reason"]!.ToString());
        Assert.Equal(reason, report["transitions"]!["byReason"]![0]!["reason"]!.ToString());
    }

    [Fact]
    public void Counts_Fetches_Transitions_And_Exits_Without_Inflating_Native()
    {
        var accounting = new OpenBiosProbeAccounting();
        foreach (var pc in new uint[] { 0xB0, 0x540, 0x544, 0x540, 0x80030000, 0x80030004 })
        {
            accounting.OnFetch(pc, NoException, static _ => 0);
        }

        accounting.OnTransition(new MixedFallbackTransition(0xB0, true, FallbackSegmentStatus.Returned, 0xBFC01000, 3, null));
        accounting.OnTransition(new MixedFallbackTransition(0xB0, true, FallbackSegmentStatus.Returned, 0xBFC01000, 1, null));
        accounting.OnTransition(new MixedFallbackTransition(0x80030000, false, FallbackSegmentStatus.BudgetExhausted, 0x80030004, 2, "ARTIFACT_FALLBACK_BUDGET_EXHAUSTED"));

        var report = Report(accounting, native: 1000, retired: 5, blocks: [0xBFC00000, 0xBFC00100]);

        Assert.Equal(1000ul, report["native"]!["instructions"]!.GetValue<ulong>());
        Assert.Equal("rom", report["native"]!["region"]!.ToString());
        Assert.Equal(6ul, report["fallback"]!["fetches"]!.GetValue<ulong>());
        Assert.Equal(1ul, report["fallback"]!["fetchesNotRetired"]!.GetValue<ulong>());

        var regions = report["regions"]!.AsArray().ToDictionary(r => r!["region"]!.ToString());
        Assert.Equal(1000ul, regions["rom"]!["nativeInstructions"]!.GetValue<ulong>());
        Assert.Equal(0ul, regions["kernel-ram"]!["nativeInstructions"]!.GetValue<ulong>()); // fallback fetches never count as native
        Assert.Equal(4ul, regions["kernel-ram"]!["fallbackFetches"]!.GetValue<ulong>());
        Assert.Equal(2ul, regions["shell"]!["fallbackFetches"]!.GetValue<ulong>());
        Assert.Equal(2ul, regions["kernel-ram"]!["transitionsEntered"]!.GetValue<ulong>());
        Assert.Equal(0.6667, regions["kernel-ram"]!["transitionShare"]!.GetValue<double>());

        var transitions = report["transitions"]!;
        Assert.Equal(3ul, transitions["total"]!.GetValue<ulong>());
        Assert.Equal(2ul, transitions["indirect"]!.GetValue<ulong>());
        var byReason = transitions["byReason"]!.AsArray();
        Assert.Equal("kernel-call-vector", byReason[0]!["reason"]!.ToString());
        Assert.Equal(4ul, byReason[0]!["retiredInstructions"]!.GetValue<ulong>());
        Assert.Equal("ram-no-code-image", byReason[1]!["reason"]!.ToString());
        Assert.Equal(
            ["budget-exhausted", "returned-to-block"],
            transitions["byExit"]!.AsArray().Select(e => e!["exit"]!.ToString()).ToArray());

        Assert.Equal("0x000000B0", report["hotEntries"]![0]!["pc"]!.ToString());
        Assert.Equal(2ul, report["hotEntries"]![0]!["transitions"]!.GetValue<ulong>());
        Assert.Equal("0x00000540", report["hotPcs"]![0]!["pc"]!.ToString());
        Assert.Equal(2ul, report["hotPcs"]![0]!["fetches"]!.GetValue<ulong>());
        Assert.Null(report["hotSymbols"]);
    }

    [Fact]
    public void Classifies_Aot_Class_From_The_Address_Unless_The_Handoff_Names_It()
    {
        var accounting = new OpenBiosProbeAccounting();
        accounting.OnTransition(new MixedFallbackTransition(0xBFC05734, true, FallbackSegmentStatus.Returned, 0xBFC01000, 4, null));
        accounting.OnTransition(new MixedFallbackTransition(0x000000B0, true, FallbackSegmentStatus.Returned, 0xBFC01000, 2, null));
        accounting.OnTransition(new MixedFallbackTransition(0x000000B0, true, FallbackSegmentStatus.Returned, 0xBFC01000, 2, null));
        accounting.OnTransition(new MixedFallbackTransition(
            0x80010000, true, FallbackSegmentStatus.Returned, 0xBFC01000, 8, null, MixedFallbackAotClass.RuntimeGenerated));

        var classes = Report(accounting, 0, 16, [0xBFC00000])["transitions"]!["byAotClass"]!.AsArray();

        Assert.Equal(
            [MixedFallbackAotClass.NotInAnyImage, MixedFallbackAotClass.KnownNotYetAot, MixedFallbackAotClass.RuntimeGenerated],
            classes.Select(c => c!["aotClass"]!.ToString()).ToArray());
        Assert.Equal([true, true, false], classes.Select(c => c!["preDeterminable"]!.GetValue<bool>()).ToArray());
        Assert.Equal(0.5, classes[2]!["retiredShare"]!.GetValue<double>());
        Assert.Equal("ps-x-exe", OpenBiosProbeAccounting.CodeImageOfRegion(OpenBiosProbeAccounting.RegionOf(0x80010000)));
    }

    [Theory]
    [InlineData("""{"differential":{"0x80030000":{"match":true}},"milestoneComparison":{"match":true}}""", true)]
    [InlineData("""{"differential":{"0x80030000":{"match":true},"0x80010000":"not reached"},"milestoneComparison":{"match":true}}""", false)]
    [InlineData("""{"differential":{"0x80030000":{"match":true}},"milestoneComparison":{"match":false}}""", false)]
    [InlineData("""{"differential":{},"milestoneComparison":{"match":true}}""", false)]
    public void Differential_Passes_Only_When_Every_Boundary_And_The_Milestones_Match(string document, bool pass) =>
        Assert.Equal(pass, OpenBiosProbeCommand.Consistency(JsonNode.Parse(document)!.AsObject())["differentialPass"]!.GetValue<bool>());

    [Fact]
    public void Native_Instructions_Stay_Unattributed_When_Blocks_Span_Regions()
    {
        var report = Report(new OpenBiosProbeAccounting(), native: 10, retired: 0, blocks: [0xBFC00000, 0x80010000]);

        Assert.Null(report["native"]!["region"]);
        Assert.All(report["regions"]!.AsArray(), r => Assert.Null(r!["nativeInstructions"]));
    }

    [Fact]
    public void Records_The_First_Reserved_Or_Coprocessor_Fault_Of_Each_Opcode()
    {
        var accounting = new OpenBiosProbeAccounting();
        var words = new Dictionary<uint, uint> { [0x80010010] = 0x4A000001u, [0x80010024] = 0x4A180001u, [0x80010030] = 0xFC000000u };
        (uint Cause, uint Epc) cop0 = (0, 0);
        void Exception(uint cause, uint epc)
        {
            cop0 = (cause, epc);
            accounting.OnFetch(0x80000080, () => cop0, address => words.GetValueOrDefault(address));
        }

        Exception(11u << 2 | 2u << 28, 0x80010010);              // CpU, COP2
        Exception(11u << 2 | 0x80000000u, 0x80010020);            // CpU in a branch delay slot: the fault is at EPC + 4
        Exception(10u << 2, 0x80010030);                          // RI
        Exception(0, 0x80010040);                                 // an interrupt is not an unsupported instruction

        var unsupported = Report(accounting, null, null, []).AsObject()["unsupportedOpcodes"]!.AsArray();
        Assert.Equal(2, unsupported.Count); // one per (ExcCode, opcode): the second COP2 fault is not new
        Assert.Equal(10u, unsupported[0]!["excCode"]!.GetValue<uint>());
        Assert.Equal("0x80010030", unsupported[0]!["pc"]!.ToString());
        Assert.Equal("0xFC000000", unsupported[0]!["word"]!.ToString());
        Assert.Equal(11u, unsupported[1]!["excCode"]!.GetValue<uint>());
        Assert.Equal(0x12u, unsupported[1]!["opcode"]!.GetValue<uint>());
        Assert.Equal("0x80010010", unsupported[1]!["pc"]!.ToString());
        Assert.Equal(1ul, unsupported[1]!["atFallbackFetch"]!.GetValue<ulong>());
    }

    [Fact]
    public void Symbols_Resolve_Through_Aliases_Within_One_Region_Only()
    {
        var symbols = ProbeSymbols.Parse(
        [
            "bfc00000 T _reset",
            "bfc05734 T flushCache",
            "00000540 T fastMemset",
            "00000600 D someData",
            "garbage",
        ]);

        Assert.Equal(3, symbols.Count);
        Assert.Equal("flushCache", symbols.Lookup(0x9FC05740));
        Assert.Equal("fastMemset", symbols.Lookup(0x80000600)); // data symbols are ignored
        Assert.Null(symbols.Lookup(0x80030000)); // the shell has no symbols: a kernel symbol below it is another region
        Assert.Null(symbols.Lookup(0x000000B0));

        var accounting = new OpenBiosProbeAccounting();
        accounting.OnFetch(0x00000544, NoException, static _ => 0);
        accounting.OnFetch(0x00000548, NoException, static _ => 0);
        accounting.OnFetch(0x80030000, NoException, static _ => 0);
        var hot = Report(accounting, null, null, [], symbols)["hotSymbols"]!.AsArray();
        Assert.Equal("kernel-ram:fastMemset", hot[0]!["symbol"]!.ToString());
        Assert.Equal(2ul, hot[0]!["fetches"]!.GetValue<ulong>());
        Assert.Equal("shell:(no symbol)", hot[1]!["symbol"]!.ToString());
    }

    [Fact]
    public void Rejects_Compare_At_Without_Differential_And_Host_Only_Options_On_The_Interpreter()
    {
        var error = new StringWriter();
        Assert.Equal(1, OpenBiosProbeCommand.Run(["rom.bin", "--engine", "generated-host", "--roots", "r.txt", "--compare-at", "80030000"], TextWriter.Null, error));
        Assert.Contains("--compare-at requires --differential", error.ToString());
        Assert.Equal(1, OpenBiosProbeCommand.Run(["rom.bin", "--stop-at", "80030000"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(1, OpenBiosProbeCommand.Run(["rom.bin", "--engine", "generated-host", "--roots", "r.txt", "--stop-at", "zz"], TextWriter.Null, TextWriter.Null));
    }
    [Theory]
    [InlineData("--code-bytes", "0")]
    [InlineData("--code-bytes", "4")]
    [InlineData("--roots", "roots.txt")]
    public void InterpreterRejectsHostOnlyOptionsEvenExplicitZero(string option, string value)
    {
        var error = new StringWriter();
        OpenBiosProbeCommand.Run(["missing-openbios.bin", option, value], TextWriter.Null, error).Should().Be(1);
        error.ToString().Should().Contain("require --engine generated-host").And.NotContain("FileNotFoundException");
    }

    [Theory]
    [InlineData("--engine", "generated-host", "--roots", "r.txt", "--capture-at", "0x80010000")]
    [InlineData("--segments", "0")]
    [InlineData("--segment-budget", "-1")]
    [InlineData("--segments", "4294967296")]
    [InlineData("--segments", "+1")]
    [InlineData("--segments", "1", "--segments", "2")]
    [InlineData("--capture-at", "0x0x80010000")]
    [InlineData("--roots", "--json")]
    [InlineData("--unknown")]
    public void ProbeRejectsInvalidOptionsBeforeOpeningFirmware(params string[] options)
    {
        var error = new StringWriter();
        OpenBiosProbeCommand.Run(["missing-openbios.bin", .. options], TextWriter.Null, error).Should().Be(1);
        error.ToString().Should().NotContain("FileNotFoundException");
    }

    [Theory]
    [InlineData("--capture-at", "0X80010000", "--segments", "1")]
    [InlineData("--engine", "generated-host", "--roots", "r.txt", "--code-bytes", "0")]
    [InlineData("--engine", "generated-host", "--roots", "r.txt", "--differential", "--compare-at", "80010000", "--compare-at", "80030000")]
    public void ValidEngineOptionsReachFirmwareInputValidation(params string[] options)
    {
        var error = new StringWriter();
        OpenBiosProbeCommand.Run(["missing-openbios.bin", .. options], TextWriter.Null, error).Should().Be(1);
        error.ToString().Should().Contain("FileNotFoundException");
    }

    [Fact]
    public void GlobalHelpIncludesTheSharedCompleteProbeUsage()
    {
        var output = new StringWriter();
        Program.Execute(["--help"], output, TextWriter.Null);
        output.ToString().Should().Contain(OpenBiosProbeCommand.Usage);
        var error = new StringWriter();
        OpenBiosProbeCommand.Run([], TextWriter.Null, error);
        error.ToString().TrimEnd().Should().Be(OpenBiosProbeCommand.Usage);
    }

}

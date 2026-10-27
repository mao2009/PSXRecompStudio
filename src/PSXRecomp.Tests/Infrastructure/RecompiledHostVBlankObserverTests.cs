using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Infrastructure;

// Issue #774 (W03): the standalone (generated-host) run reports each VBlank of its host scheduler to the same boundary log
// the interpreter writes, so the two can be compared on the same boundary. Synthetic only: no artifact or ROM runs here.
[Test]
public sealed class RecompiledHostVBlankObserverTests
{
    [Fact]
    public void The_Observer_Runs_At_Each_VBlank_With_The_Host_Devices()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var seen = new List<(PsxDeviceGraph Devices, ulong Vblank)>();
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: null, (devices, vblank, _) => seen.Add((devices, vblank)));

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        seen.Select(static s => s.Vblank).Should().Equal(1UL, 2UL);
        seen.All(s => ReferenceEquals(s.Devices, graph)).Should().BeTrue();
    }

    [Fact]
    public void A_Standalone_Record_Has_The_Same_Keys_As_An_Interpreter_Record()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var writer = new StringWriter();
        using var log = new OpenBiosProbeBoundaryLog(writer);
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: null,
            (devices, vblank, cycles) => log.Write("standalone", devices, vblank, cycles));

        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        var line = JsonNode.Parse(writer.ToString().Trim())!.AsObject();
        var interpreter = OpenBiosProbeBoundaryLog.Describe("interpreter", graph, 1, 0);
        line.Select(static p => p.Key).Should().Equal(interpreter.Select(static p => p.Key));
        line["engine"]!.GetValue<string>().Should().Be("standalone");
        log.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void Live_Frames_And_The_Observer_Both_Run_At_Each_VBlank()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var live = new List<ulong>();
        var observed = new List<ulong>();
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: live.Add, (_, vblank, _) => observed.Add(vblank));

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        live.Should().Equal(1UL, 2UL);
        observed.Should().Equal(1UL, 2UL);
    }
}

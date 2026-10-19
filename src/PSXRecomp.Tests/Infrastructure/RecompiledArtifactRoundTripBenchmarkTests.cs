using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using Xunit.Abstractions;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #768: a synthetic measure of the generated-C to RuntimeHost round trips (no guest program, no disc). The parent
/// is the scripted harness, so each round trip is a real pipe exchange with the artifact process. The assertions pin the
/// protocol ceiling (an access costs at most one fused round trip plus one report after it); the numbers are written to the
/// test output for the Issue comparison and are synthetic only.
/// </summary>
[Test]
public sealed class RecompiledArtifactRoundTripBenchmarkTests(ITestOutputHelper output)
{
    private const uint MmioBase = 0x1F801800u; // CD-ROM region (0x1F801800 index/status register): the scripted parent answers every access with V 0
    private const int Accesses = 400;
    private const uint SegmentBudget = 1u << 20; // the whole synthetic program runs in one segment

    [Fact]
    public void RoundTripsPerMmioAccess_StayWithinTheFusedCeiling_AndComputeReportsOnlyByThreshold()
    {
        using var mmioDir = new TempDirectory();
        Run(BuildMmioLoop(Accesses), mmioDir, withRuntime: false, segmentBudget: SegmentBudget);
        var (mmioRun, mmioWall) = Timed(() => RunScripted(mmioDir, _ => "V 0"));

        using var computeDir = new TempDirectory();
        Run(BuildCompute(4096), computeDir, withRuntime: false, segmentBudget: SegmentBudget);
        var (computeRun, computeWall) = Timed(() => RunScripted(computeDir, _ => "V 0"));

        var mmioRoundTrips = RoundTrips(mmioRun);
        var computeRoundTrips = RoundTrips(computeRun);
        var mmioInstructions = mmioRun.Retired.Sum(static r => (long)r);
        var computeInstructions = computeRun.Retired.Sum(static r => (long)r);

        output.WriteLine($"[768 synthetic] mmio: accesses={mmioRun.Requests.Count} fused={mmioRun.FusedRequests} " +
                         $"instructions={mmioInstructions} roundTrips={mmioRoundTrips} wall={mmioWall.TotalMilliseconds:F1} ms");
        output.WriteLine($"[768 synthetic] compute: instructions={computeInstructions} roundTrips={computeRoundTrips} " +
                         $"wall={computeWall.TotalMilliseconds:F1} ms");

        mmioRun.Requests.Should().HaveCountGreaterThanOrEqualTo(Accesses, "every read reaches the host once");
        // One fused request (report + access) and at most one stand-alone report after it per access.
        mmioRoundTrips.Should().BeLessThanOrEqualTo(2 * mmioRun.Requests.Count + 2,
            "an access costs one fused round trip plus at most one report after it");
        // Without accesses only the 1024-instruction threshold and the device deadlines report.
        computeRoundTrips.Should().BeLessThanOrEqualTo((int)(computeInstructions / 1024) + 2,
            "without accesses only the 1024-instruction threshold and the device deadlines report");
    }

    /// <summary>Round trips: every line the artifact sent that waits for a reply. A fused line is one of them, not two.</summary>
    private static int RoundTrips(ScriptedRun run) => run.Retired.Count + run.Requests.Count - run.FusedRequests;

    private static (ScriptedRun Run, TimeSpan Wall) Timed(Func<ScriptedRun> body)
    {
        var start = Stopwatch.GetTimestamp();
        var run = body();
        return (run, Stopwatch.GetElapsedTime(start));
    }

    /// <summary>One retired instruction before each read of the same device register.</summary>
    private static uint[] BuildMmioLoop(int count)
    {
        var parts = new List<uint[]> { Li(R3000aRegister.T0, MmioBase) };
        for (var i = 0; i < count; i++)
        {
            parts.Add([MipsEncoding.Nop]);
            parts.Add(Load(R3000aOpcode.Lw, R3000aRegister.S0, R3000aRegister.T0, 0));
        }

        return Program([.. parts]);
    }

    /// <summary>Straight-line register arithmetic with no device access.</summary>
    private static uint[] BuildCompute(int count)
    {
        var parts = new List<uint[]>();
        for (var i = 0; i < count; i++) parts.Add([Ori(R3000aRegister.T1, R3000aRegister.Zero, (ushort)i)]);
        return Program([.. parts]);
    }
}

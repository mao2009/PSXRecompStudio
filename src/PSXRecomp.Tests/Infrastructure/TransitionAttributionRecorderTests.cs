using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>PR #792 measurement: the opt-in transition recorder classifies, aggregates and bounds its own cost.</summary>
[Test]
public sealed class TransitionAttributionRecorderTests
{
    private static MixedFallbackTransition T(uint entry, int registered, int matched, uint? prev = null, uint exit = 0x80000010u) =>
        new(entry, true, FallbackSegmentStatus.Returned, exit, 3, null, Attribution: new FallbackEntryAttribution(registered, matched, prev));

    private static JsonElement Write(TransitionAttributionRecorder recorder) =>
        JsonDocument.Parse(recorder.ToJson(new Dictionary<string, object?> { ["state"] = "test" })).RootElement;

    [Fact]
    public void Classifies_Unregistered_GuardMismatch_AndAnomaly_WithoutDoubleCounting()
    {
        var r = new TransitionAttributionRecorder();
        for (var i = 0; i < 5; i++) r.Observe(T(0x80084C88, 0, -1));
        for (var i = 0; i < 3; i++) r.Observe(T(0x80084C88, 2, -1, prev: 0x8008AECC));
        r.Observe(T(0x80084C88, 2, 1));
        r.Observe(new MixedFallbackTransition(0x80000100, false, FallbackSegmentStatus.Returned, 0x80000110, 1, null));

        {
            var doc = Write(r);
            doc.GetProperty("totalTransitions").GetUInt64().Should().Be(10);
            var byCause = doc.GetProperty("byCause").EnumerateArray().ToDictionary(e => e.GetProperty("cause").GetString()!, e => e.GetProperty("entries").GetUInt64());
            byCause.Should().BeEquivalentTo(new Dictionary<string, ulong>
            {
                [TransitionAttributionRecorder.Unregistered] = 5,
                [TransitionAttributionRecorder.GuardMismatch] = 3,
                [TransitionAttributionRecorder.GuardMatchedAnomaly] = 1,
                [TransitionAttributionRecorder.NoAttribution] = 1,
            });
            byCause.Values.Sum(static v => (long)v).Should().Be(10, "every transition is counted in exactly one cause");
        }
    }

    [Fact]
    public void IsBounded_AndReportsWhatItDropped()
    {
        var r = new TransitionAttributionRecorder();
        var n = TransitionAttributionRecorder.MaxKeys + 100;
        for (var i = 0; i < n; i++) r.Observe(T((uint)(0x80000000 + i * 4), 0, -1));

        {
            var doc = Write(r);
            doc.GetProperty("distinctKeys").GetInt32().Should().Be(TransitionAttributionRecorder.MaxKeys);
            doc.GetProperty("droppedKeys").GetUInt64().Should().Be(100);
            doc.GetProperty("droppedDetailEvents").GetUInt64().Should().Be((ulong)(n - TransitionAttributionRecorder.MaxDetailEvents));
            doc.GetProperty("firstEvents").GetArrayLength().Should().Be(TransitionAttributionRecorder.MaxDetailEvents);
        }
    }

    [Fact]
    public void AnUnwritablePath_NeverThrows()
    {
        var r = new TransitionAttributionRecorder();
        r.Observe(T(0x80000000, 0, -1));
        var error = new StringWriter();
        using var dir = new TempDirectory();
        var act = () => r.TryWrite(Path.Combine(dir.FullPath, "missing", "x.json"), error, new Dictionary<string, object?>());
        act.Should().NotThrow();
        error.ToString().Should().Contain("could not write transition attribution");
    }
}

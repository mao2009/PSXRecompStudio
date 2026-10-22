using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #630: the synthetic PS-X program runs through the real Runtime, and the frames its GP0 fills produce at VBlank reach a
/// <see cref="LiveFrameSlot"/> of the same process in the order the program drew them. No frame is produced by the test itself.
/// </summary>
[Test]
public sealed class LiveGameRunnerTests
{
    private const uint SegmentBudget = 20_000_000u;

    /// <summary>Reads the newest frame's pixel at (0, 0) as (R, G, B) until stopped; each new frame is recorded once.</summary>
    private static void CollectFrames(LiveFrameSlot slot, CancellationToken stop, List<(byte R, byte G, byte B)> pixels)
    {
        long last = 0;
        var spin = new SpinWait();
        while (!stop.IsCancellationRequested)
        {
            if (slot.TryGetNewer(last, out var frame))
            {
                last = frame.Sequence;
                lock (pixels)
                {
                    pixels.Add((frame.Bgra32[2], frame.Bgra32[1], frame.Bgra32[0]));
                }

                spin.Reset();
            }
            else
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    [Fact]
    public async Task SyntheticProgram_RunsThroughTheRuntime_AndItsFillsReachTheSlotInOrder()
    {
        using var dir = new TempDirectory();
        var slot = new LiveFrameSlot();
        var pixels = new List<(byte R, byte G, byte B)>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => CollectFrames(slot, stop.Token, pixels));

        var result = await LiveGameRunner.RunAsync(
            SyntheticLiveProgram.Words(), SyntheticLiveProgram.EntryPc, SegmentBudget, slot, dir.FullPath);
        stop.Cancel();
        await reader;

        result.State.Should().Be(TitleExecutionState.BudgetExhausted, "the program never ends; its finite budget does");
        List<(byte R, byte G, byte B)> distinct = [];
        foreach (var pixel in pixels)
        {
            if (distinct.Count == 0 || distinct[^1] != pixel)
            {
                distinct.Add(pixel);
            }
        }

        // The fills are red, green, blue. Each must reach the slot, in that order, as the frame the GPU showed at that VBlank.
        distinct.Should().ContainInOrder((0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0x00, 0x00, 0xFF));
    }
}

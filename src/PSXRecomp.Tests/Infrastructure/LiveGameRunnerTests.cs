using FluentAssertions;
using PSXRecomp.Core.DiscImage;
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

    [Fact]
    public async Task StopDuringTheRun_EndsCancelled_AfterAFrameReachedTheSlot()
    {
        using var dir = new TempDirectory();
        var slot = new LiveFrameSlot();
        var pixels = new List<(byte R, byte G, byte B)>();
        using var readerStop = new CancellationTokenSource();
        var reader = Task.Run(() => CollectFrames(slot, readerStop.Token, pixels));
        using var stop = new CancellationTokenSource();

        // The budget is unbounded for this run, so only the stop can end it. A frame proves the run was live when it was stopped.
        var run = LiveGameRunner.RunAsync(
            SyntheticLiveProgram.Words(), SyntheticLiveProgram.EntryPc, uint.MaxValue, slot, dir.FullPath, stop.Token);
        var spin = new SpinWait();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            lock (pixels)
            {
                if (pixels.Count > 0)
                {
                    break;
                }
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }

        stop.Cancel();
        try
        {
            var result = await run;
            pixels.Should().NotBeEmpty("the run must have shown a frame before it was stopped");
            result.State.Should().Be(TitleExecutionState.Cancelled, "a stop is a Cancelled outcome, never a completed run");
        }
        finally
        {
            readerStop.Cancel();
            await reader;
        }

    }

    [Fact]
    public void SyntheticPsxExe_ParsesWithTheExistingLoader_AndPointsAtItsProgram()
    {
        var exe = PsxExe.Load(SyntheticPsxExe.Build(), "synthetic.exe");

        exe.Header.EntryPoint.Should().Be(SyntheticPsxExe.LoadAddress);
        exe.Header.TextStart.Should().Be(SyntheticPsxExe.LoadAddress);
        exe.DecodedInstructionCount.Should().Be(SyntheticLiveProgram.Words(SyntheticPsxExe.LoadAddress).Length);
        exe.Header.SpInitial.Should().NotBe(0u, "the stack pointer is set from the header's base");
    }

    [Fact]
    public async Task PsxExeFileOnDisk_RunsThroughTheRuntime_AndItsFillsReachTheSlotInOrder()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.FullPath, "synthetic.exe");
#pragma warning disable AARC003 // Test-layer I/O for verification: the EXE is written to disk and read back, as a game file is.
        File.WriteAllBytes(path, SyntheticPsxExe.Build());
        var fileBytes = File.ReadAllBytes(path);
#pragma warning restore AARC003
        var slot = new LiveFrameSlot();
        var pixels = new List<(byte R, byte G, byte B)>();
        using var readerStop = new CancellationTokenSource();
        var reader = Task.Run(() => CollectFrames(slot, readerStop.Token, pixels));

        var result = await LiveGameRunner.RunPsxExeAsync(fileBytes, "synthetic.exe", SegmentBudget, slot, dir.FullPath);
        readerStop.Cancel();
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

        distinct.Should().ContainInOrder((0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0x00, 0x00, 0xFF));
    }

    [Fact]
    public void TruncatedPsxExe_IsRejectedBeforeAnythingRuns()
    {
        var file = SyntheticPsxExe.Build();
        var truncated = file[..^4];

        Action act = () => _ = LiveGameRunner.RunPsxExeAsync(truncated, "truncated.exe", SegmentBudget, new LiveFrameSlot(), Path.GetTempPath());

        act.Should().Throw<ArgumentException>().WithMessage("*truncated*");
    }

    // Issue #B2 (R1 scope): a live run whose load range lies in the BIOS ROM is refused before anything is lowered or started.
    [Fact]
    public void BiosRange_PsxExeTextInBiosRange_IsRefusedBeforeAnythingRuns()
    {
        var file = SyntheticPsxExeBuilder.Build(
            entryPoint: 0xBFC00000u, textStart: 0xBFC00000u, textSize: 4, storedTextBytes: new byte[4]);

        Action act = () => _ = LiveGameRunner.RunPsxExeAsync(file, "bios.exe", SegmentBudget, new LiveFrameSlot(), Path.GetTempPath());

        act.Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
    }

    [Fact]
    public void BiosRange_RawWordsAtBiosRange_AreRefusedBeforeAnythingRuns()
    {
        Action act = () => _ = LiveGameRunner.RunAsync([0u], 0xBFC00000u, SegmentBudget, new LiveFrameSlot(), Path.GetTempPath());

        act.Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
    }
}

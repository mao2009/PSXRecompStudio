using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
// Issue #209 Stage B vertical slice: memory. The fixtures prove the whole
// recompiler pipeline (decode -> lower -> validate -> host codegen -> gcc -> run)
// agrees with the native interpreter on store/load widths, little-endian byte
// order, sign vs zero extending loads, pre-populated guest RAM, and the R3000A
// load-delay slot — verified through registers AND a memory window sample.
public sealed class RecompilerStageBEndToEndTests
{
    [Fact]
    public void StoreAndLoadAtEveryWidth_Produce_LittleEndianBytes_InTheWindow()
    {
        var fixture = RecompilerFixtures.Issue209MemoryRoundTrip();
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Reference.Status);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Actual.Snapshot!.Termination);

        // Loads round-trip the stored word, halfword (sign-extended) and byte.
        Assert.Equal(0x11223344u, result.Reference.Snapshot!.Gpr[10]);
        Assert.Equal(0x00003344u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(0x44u, result.Reference.Snapshot!.Gpr[12]);
        Assert.Equal(0x11223344u, result.Actual.Snapshot!.Gpr[10]);

        // The window samples the stored bytes: the word is little-endian
        // (44 33 22 11), the halfword occupies the high half and the byte the top.
        Assert.Equal(
            new byte[] { 0x44, 0x33, 0x22, 0x11, 0x44, 0x33, 0x44, 0x00 },
            WindowBytes(result.Reference.Snapshot!));
        Assert.Equal(WindowBytes(result.Reference.Snapshot!), WindowBytes(result.Actual.Snapshot!));
    }

    [Fact]
    public void LoadDelaySlot_ReadsThePreLoadValue_ThenTheCommit_MatchesTheInterpreter()
    {
        var fixture = RecompilerFixtures.Issue209LoadDelay();
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, result.Reference.Status == RecompilerExecutionStatus.Completed
            ? $"host executor failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}"
            : "interpreter executor failed.");
        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);

        // The instruction in the load-delay slot observes the pre-load $t2; the
        // instruction after it observes the loaded value (docs/cpu/pipeline.md).
        Assert.Equal(0x55u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(0x12345678u, result.Reference.Snapshot!.Gpr[12]);
        Assert.Equal(0x55u, result.Actual.Snapshot!.Gpr[11]);
        Assert.Equal(0x12345678u, result.Actual.Snapshot!.Gpr[12]);
    }

    [Fact]
    public void InitialMemory_ReachedByEveryLoadWidth_MatchesTheInterpreter()
    {
        var fixture = RecompilerFixtures.Issue209MemoryInitLoads();
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, result.Reference.Status == RecompilerExecutionStatus.Completed
            ? $"host executor failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}"
            : "interpreter executor failed.");
        Assert.True(result.IsMatch, result.Diff!.Describe());
        Assert.Equal(RecompilerIrTerminationReason.Success, result.Reference.Snapshot!.Termination);

        // LW reads the 32-bit word, LHU zero-extends and LH sign-extends the same
        // halfword whose top bit is set (byte 0x80 at the low byte).
        Assert.Equal(0x12345678u, result.Reference.Snapshot!.Gpr[9]);
        Assert.Equal(0x00008180u, result.Reference.Snapshot!.Gpr[10]);
        Assert.Equal(0xFFFF8180u, result.Reference.Snapshot!.Gpr[11]);
        Assert.Equal(0xFFFF8180u, result.Actual.Snapshot!.Gpr[11]);

        // The window confirms the pre-populated bytes were visible on both sides.
        Assert.Equal(
            new byte[] { 0x78, 0x56, 0x34, 0x12, 0x80, 0x81 },
            WindowBytes(result.Reference.Snapshot!));
        Assert.Equal(WindowBytes(result.Reference.Snapshot!), WindowBytes(result.Actual.Snapshot!));
    }

    [Fact]
    public void MemoryHostSnapshots_Are_Deterministic_Across_Independent_Runs()
    {
        var executor = new RecompilerHostExecutor();
        foreach (var fixture in new[]
                 {
                     RecompilerFixtures.Issue209MemoryRoundTrip(),
                     RecompilerFixtures.Issue209LoadDelay(),
                     RecompilerFixtures.Issue209MemoryInitLoads(),
                 })
        {
            var first = executor.Execute(fixture);
            var second = executor.Execute(fixture);

            Assert.True(first.Status == RecompilerExecutionStatus.Completed,
                $"[{fixture.Name}] host executor failed: [{first.DiagnosticCode}] {first.DiagnosticMessage}");
            Assert.True(first.Status == RecompilerExecutionStatus.Completed
                        && second.Status == RecompilerExecutionStatus.Completed);
            var diff = RecompilerStateDiff.Compare(first.Snapshot!, second.Snapshot!);
            Assert.True(diff.IsMatch, $"[{fixture.Name}]: {diff.Describe()}");
        }
    }

    private static byte[] WindowBytes(RecompilerStateSnapshot snapshot)
    {
        var bytes = new byte[snapshot.Memory.Count];
        for (var i = 0; i < snapshot.Memory.Count; i++)
        {
            bytes[i] = (byte)snapshot.Memory[i].Value;
        }
        return bytes;
    }
}
#pragma warning restore PSXR005
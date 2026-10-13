using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #739 Phase 2 Step C: a prebuilt artifact launched in <c>RuntimeHost</c> mode. Guest firmware has a
/// bridge (its device graph) even though it has no BIOS HLE Runtime, so the launch must also pass
/// <c>--host-transfer</c>: that flag is the only thing that enables the artifact's MMIO bridge, and without it
/// the first guest MMIO access exits with the MMIO-unavailable code. These tests drive the real compiled
/// artifact end to end rather than asserting on argument strings.
/// </summary>
[Test]
public sealed class PrebuiltArtifactRuntimeHostLaunchTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint GpuStat = 0x1F801814u;

    private static uint[] Rom(Block reset)
    {
        var words = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) words[i] = reset.Words[i];
        return words;
    }

    private static TitleExecutionRequest Request(uint entry, uint budget) =>
        new(entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    private static string BuiltBinary(TempDirectory dir)
    {
#pragma warning disable AARC003 // Test-layer I/O for verification
        return Directory.GetFiles(dir.FullPath, "recompiled-artifact*").Single(f => !f.EndsWith(".o") && !f.EndsWith(".c"));
#pragma warning restore AARC003
    }

    [Fact]
    public void GuestFirmware_RuntimeHostLaunch_ServesMmioThroughTheBridge_LikeTheInterpreter()
    {
        // Reset: read GPUSTAT (a device register, not guest RAM) into T1, then spin.
        var reset = new Block(RomBase);
        reset.Emit(Li(T0, GpuStat), [Lw(T1, T0, 0), Nop]);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);
        var rom = Rom(reset);
        var program = ReachableProgramBuilder.BuildFirmwareImage(RomBase, rom[..(0x400 / 4)], RomBase, []);

        // Build the standalone artifact once; its binary is what the packaged distribution would ship.
        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program.Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(RomBase, 200))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            BuiltBinary(dir),
            blockEntryPcs: new HashSet<uint>(program.Program.Blocks.Select(b => b.EntryPc)),
            guestFirmware: true,
            mixedFallback: new MixedFallbackOptions(),
            imageWords: rom,
            imageLoadAddress: RomBase,
            launchMode: RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.RuntimeHost);

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(RomBase, 200));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(RomBase, 200));

        host.FinalSnapshot.Should().NotBeNull(
            $"guest MMIO must not exit with the MMIO-unavailable code: {host.DiagnosticCode} {host.DiagnosticMessage}");
        engine.HostRoundTrips.Should().NotBeNull("the RuntimeHost launch of guest firmware attaches the bridge");
        engine.HostRoundTrips!.Value.MmioAccesses.Should().BeGreaterThan(0, "the GPUSTAT load must travel over the bridge");
        host.FinalSnapshot!.Gpr[(int)T1].Should().Be(
            reference.FinalSnapshot!.Gpr[(int)T1], "the device value read over the bridge must match the interpreter's");
    }

    [Fact]
    public void NoBridge_RuntimeHostLaunch_RunsStandaloneWithoutHostTransferFlags()
    {
        // A program without a Runtime: no bridge exists, so no bridge flag may be passed (a stray --host-transfer
        // would block the artifact on an RHOST_INIT handshake nobody serves).
        uint[] words = [.. Li(T0, 0x1234u), Nop];
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);

        using var dir = new TempDirectory();
        using (var builder = new RecompiledHostExecutionEngine(
            program, words, Entry, new GeneratedHostBuildService(), dir.FullPath, embedImage: true))
        {
            new ExecutionOrchestrator().Execute(builder, handoff: null, Request(Entry, 64))
                .FinalSnapshot.Should().NotBeNull("the embedded-image build itself must run");
        }

        using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
            BuiltBinary(dir),
            blockEntryPcs: new HashSet<uint>(program.Blocks.Select(b => b.EntryPc)),
            imageWords: words,
            imageLoadAddress: Entry,
            launchMode: RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.RuntimeHost);

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(Entry, 64));

        host.FinalSnapshot.Should().NotBeNull($"{host.DiagnosticCode} {host.DiagnosticMessage}");
        host.FinalSnapshot!.Gpr[(int)T0].Should().Be(0x1234u);
        engine.HostRoundTrips.Should().BeNull("with no Runtime and no firmware there is no bridge to attach");
    }
}

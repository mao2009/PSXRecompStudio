using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The native artifact hands its general-purpose registers to the interpreter over RHOST_TRANSFER and takes them back at the
/// return. Values are synthetic and identifiable. Both directions are checked, and the guest state of a native-to-fallback run
/// is compared with the pure interpreter through the same comparison the probe uses.
/// </summary>
[Test]
public sealed class MixedFallbackStateTransferTests
{
    private const uint Marker = 0xC0FFEE11u;
    private const uint Returned = 0xDEAD0042u;

    private static readonly MixedFallbackOptions On = new();

    /// <summary>main: S2 = Marker, then jalr to an uncompiled callee. The callee reads S2 (native to fallback), writes V0 = S2 + 1
    /// (fallback to native), and returns to the artifact. The program ends by an indirect jump to PC 0.</summary>
    private static uint[] TransferProgram()
    {
        var main = new Block(Entry);
        main.Emit(Li(S2, Marker));
        main.Emit(Li(T0, Target));
        main.Emit(Jalr(T0), Nop);
        main.Emit(Nop, Nop, Nop);
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Addiu(V0, S2, 1), Jr(Ra), Nop);
        return Image(main, callee);
    }

    [Fact]
    public void RhostTransfer_HandsTheGprsToTheFallbackAndTakesTheResultBack()
    {
        var words = TransferProgram();
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Evidence.Should().NotBeNull();
        run.Evidence!.Targets.Should().NotBeEmpty("the callee has no native block, so it runs on the interpreter fallback");
        // The fallback saw S2 (the native-to-fallback direction) and its V0 reached the artifact (the return direction).
        run.Result.FinalSnapshot!.Gpr[(int)V0].Should().Be(Marker + 1u, "the fallback must read the handed-over S2");
        run.Result.FinalSnapshot.Gpr[(int)S2].Should().Be(Marker);
    }

    [Fact]
    public void NativeFallbackNative_GuestStateCompares_EqualToThePureInterpreter()
    {
        var words = TransferProgram();
        using var dir = new TempDirectory();
        var mixed = RunArtifact(words, dir, On).Result;
        var reference = RunInterpreter(words);

        var interpreterState = ToProbeState(reference.FinalSnapshot!);
        var mixedState = ToProbeState(mixed.FinalSnapshot!);

        // The probe's own comparison: the mixed run and the interpreter are the same guest state.
        var same = Compare(interpreterState, mixedState);
        same["match"]!.GetValue<bool>().Should().BeTrue("the mixed run must match the pure interpreter");
        same["firstMismatch"].Should().BeNull();

        // The comparison still detects a difference: the same run with one result register changed.
        var changed = ToProbeState(mixed.FinalSnapshot!, v0Override: Returned);
        var different = Compare(interpreterState, changed);
        different["match"]!.GetValue<bool>().Should().BeFalse("a changed result register must fail the comparison");
        different["firstMismatch"]!["name"]!.GetValue<string>().Should().Be("r2", "V0 is r2 and is the first mismatch");
    }

    private static JsonNode Compare(ProbeGuestState reference, ProbeGuestState actual) =>
        JsonNode.Parse(JsonSerializer.Serialize(ProbeGuestState.Compare(reference, actual)))!;

    /// <summary>The guest state of a result, in the probe's own shape: r0..r31, then hi, lo, pc. RAM and devices are the same
    /// for both sides (the test programs touch no device or RAM), so the comparison is over the CPU state.</summary>
    private static ProbeGuestState ToProbeState(RecompilerStateSnapshot snapshot, uint? v0Override = null)
    {
        var cpu = new List<(string Name, uint Value)>();
        for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
        {
            var value = snapshot.Gpr[i];
            if (i == (int)V0 && v0Override is { } replaced) value = replaced;
            cpu.Add(($"r{i}", value));
        }

        cpu.Add(("hi", snapshot.HI));
        cpu.Add(("lo", snapshot.LO));
        cpu.Add(("pc", snapshot.PC));
        return new ProbeGuestState(snapshot.PC, 0, cpu, [], new byte[ProbeGuestState.RamBytes]);
    }
}

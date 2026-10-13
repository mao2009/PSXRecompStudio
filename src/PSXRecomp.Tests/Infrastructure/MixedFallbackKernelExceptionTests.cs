using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #763: a hardware INT leaves the artifact at the unpopulated general exception vector; the Runtime's kernel
/// exception handler completes the chain and fires the guest's registered hook (B0:19 HookEntryInt), continuing at the
/// hook's saved <c>ra</c> with <c>$v0 = 1</c>. That continuation is the real kernel's <c>jr</c> out of its exception
/// handler, so the host marks it with <c>J</c>. When the saved <c>ra</c> is in-image code the artifact has no block for
/// (the return site after a <c>setjmp</c>: the real Persona stop at 0x800253B8), the fallback may take it; without the
/// mark the query reports it as not indirect and the run stops at <c>NOT_INDIRECT</c>. The run goes through the
/// production build service, host protocol, kernel exception handler and mixed fallback, and is compared with the pure
/// interpreter.
/// </summary>
[Test]
public sealed class MixedFallbackKernelExceptionTests
{
    private const uint Timer2Irq = 1u << (DeviceScheduler.Timer0Irq + 2);
    private const uint InterruptMask = 0x1F801074u;
    private const uint Timer2Mode = 0x1F801124u;
    private const uint Timer2Target = 0x1F801128u;
    private const uint HookBuffer = 0x80002000u;
    private const uint HookSp = 0x801FFF00u;
    private const uint HookS0 = 0x1234u;
    private const ushort Marker = 0x77;
    private const uint HiValue = 0xA1B2C3D4u;
    private const uint LoValue = 0x0F1E2D3Cu;

    private static readonly MixedFallbackOptions On = new();

    private sealed record Program(uint[] Words, uint Landing, uint Done);

    private static uint[] StoreWord(uint address, uint value) =>
        [.. Li(T0, address & ~0xFFFFu), .. Li(T1, value), Sw(T1, T0, (short)(address & 0xFFFF))];

    /// <summary>
    /// main: stores a hook buffer (ra = the landing, sp, s0, gp), registers it through B0:19, sets HI/LO, enables INT
    /// (SYS 02h), arms Timer 2 and spins. The timer's INT completes the chain and fires the hook. The landing is uncompiled
    /// code (never reached statically: only the hook's saved ra names it) that sets a marker, copies HI/LO and jumps
    /// register-indirect to <c>Done</c>, a compiled block that ends the program.
    /// </summary>
    private static Program Build(uint landing = Entry + 0x300u) // default: clear of main, which ends before Done at Entry + 0x200
    {
        var main = new Block(Entry);
        uint[] saved = [landing, HookSp, 0x801FFF10u, HookS0, 0, 0, 0, 0, 0, 0, 0, 0x80008000u];
        for (var i = 0; i < saved.Length; i++)
        {
            main.Emit(StoreWord(HookBuffer + (uint)i * 4u, saved[i]));
        }

        main.Emit(
            [Ori(T1, Zero, BiosHleRuntime.HookEntryIntFunction)],
            Li(A0, HookBuffer),
            [MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress), Nop],
            Li(T2, HiValue),
            [Mthi(T2)],
            Li(T2, LoValue),
            [Mtlo(T2)],
            [Ori(A0, Zero, (ushort)BiosKernelSyscall.ExitCriticalSection), MipsEncoding.Syscall()],
            Li(T0, 0x1F800000u),
            [Ori(T1, Zero, 1 << 6), Sw(T1, T0, (short)(InterruptMask & 0xFFFF))],
            [Ori(T1, Zero, 100), Sw(T1, T0, (short)(Timer2Target & 0xFFFF))],
            [Ori(T1, Zero, 0x10), Sw(T1, T0, (short)(Timer2Mode & 0xFFFF))]);
        var spin = main.Here;
        main.Emit(MipsEncoding.Branch(0x04, (byte)Zero, (byte)Zero, spin, spin), Nop);

        var done = new Block(Entry + 0x200u);
        done.Emit(End());

        var land = new Block(landing);
        land.Emit([Ori(S2, Zero, Marker), Mfhi(T8), Mflo(T9)], Li(T2, done.Address), [Jr(T2), Nop]);
        var inImage = landing >= Entry && landing < Entry + ImageWords * 4u;
        return new Program(inImage ? Image(main, done, land) : Image(main, done), landing, done.Address);
    }

    private static BiosExceptionChain Chain(List<uint> epcs) => ctx =>
    {
        epcs.Add(ctx.Exception.Epc);
        ctx.Interrupts.Acknowledge(~Timer2Irq); // the INT is serviced once: any further delivery is a bug
        return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
    };

    private static RecompilerIrProgram StaticProgram(Program program) =>
        ReachableProgramBuilder.Build(Entry, program.Words, Entry, [program.Done]);

    private static (TitleExecutionResult Result, MixedFallbackEvidence? Evidence, FallbackDeclinedTransfer? Declined) RunArtifact(
        Program program, List<uint> epcs, TempDirectory dir, MixedFallbackOptions? fallback)
    {
        using var engine = new RecompiledHostExecutionEngine(
            StaticProgram(program),
            program.Words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
            exceptionChain: Chain(epcs),
            mixedFallback: fallback);
        var result = new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(200_000));
        return (result, engine.FallbackEvidence, engine.LastDeclinedTransfer);
    }

    private static TitleExecutionResult RunInterpreter(Program program, List<uint> epcs)
    {
        using var engine = new InterpreterTitleExecutionEngine(
            program.Words, Entry, (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer), Chain(epcs));
        return new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(200_000));
    }

    [Fact]
    public void KernelExceptionReturn_ToAnUncompiledHookPc_IsAnIndirectTarget_TheFallbackResumesIt_AndMatchesTheInterpreter()
    {
        var program = Build();
        StaticProgram(program).Blocks.Select(b => b.EntryPc).Should().NotContain(program.Landing, "the landing is uncompiled: only the fallback can run it");
        var epcs = new List<uint>();
        using var dir = new TempDirectory();

        var (result, evidence, declined) = RunArtifact(program, epcs, dir, On);

        // The only refusal allowed is the program's own end marker (jr zero -> pc 0, handled by the exit handoff).
        (declined is null || declined.TargetPc == 0u).Should().BeTrue($"the hook return must not be refused ({declined?.RejectReason} at 0x{declined?.TargetPc:X8})");
        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        epcs.Should().ContainSingle($"the INT is delivered exactly once: no double delivery, no re-entry; pc=0x{result.FinalSnapshot?.PC:X8} state={result.State} code={result.DiagnosticCode} {result.DiagnosticMessage}");

        evidence.Should().NotBeNull();
        evidence!.Targets.Should().Contain(t => t.Target == program.Landing && t.Entries == 1, "the fallback entered the hook's pc once");
        evidence.Transitions.Should().Be(1u);
        evidence.FallbackInstructions.Should().BeGreaterThan(0UL).And.BeLessThan(20UL, "only the landing's own few instructions ran in the interpreter");

        var interpreterEpcs = new List<uint>();
        var reference = RunInterpreter(program, interpreterEpcs);
        reference.State.Should().Be(TitleExecutionState.Completed, reference.DiagnosticMessage);
        interpreterEpcs.Should().ContainSingle();

        var artifact = result.FinalSnapshot!;
        var expected = reference.FinalSnapshot!;
        artifact.Gpr[(int)S2].Should().Be(Marker, "control continued at the hook's saved ra");
        artifact.Gpr[(int)V0].Should().Be(1u, "the hook is entered with $v0 = 1");
        artifact.Gpr[(int)S0].Should().Be(HookS0);
        artifact.Gpr[(int)R3000aRegister.Sp].Should().Be(HookSp);
        artifact.Gpr[(int)T8].Should().Be(HiValue, "HI survived the exception and the fallback");
        artifact.Gpr[(int)T9].Should().Be(LoValue, "LO survived the exception and the fallback");
        artifact.HI.Should().Be(expected.HI);
        artifact.LO.Should().Be(expected.LO);
        foreach (var register in new[] { S0, S2, V0, T8, T9, R3000aRegister.Sp, R3000aRegister.Gp, R3000aRegister.Fp })
        {
            artifact.Gpr[(int)register].Should().Be(expected.Gpr[(int)register], $"{register} must match the pure interpreter");
        }
    }

    [Fact]
    public void KernelExceptionReturn_ToAnOutOfImageHookPc_StaysFailClosed_TheMarkNeverBypassesStaticEligibility()
    {
        // The J mark only answers "is this a runtime-resolved target"; the image/alignment/block checks still apply.
        var program = Build(landing: 0x80100000u);
        var epcs = new List<uint>();
        using var dir = new TempDirectory();

        var (result, evidence, declined) = RunArtifact(program, epcs, dir, On);

        epcs.Should().ContainSingle();
        declined.Should().NotBeNull();
        declined!.RejectReason.Should().Be(MixedFallbackDiagnostics.IneligibleOutOfImage);
        declined.TargetPc.Should().Be(program.Landing);
        evidence!.Transitions.Should().Be(0u, "nothing outside the image is ever handed to the interpreter");
        result.FinalSnapshot!.Gpr[(int)S2].Should().NotBe(Marker);
    }
}

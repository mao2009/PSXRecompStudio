using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Issue #717: a blocking BIOS HLE call. A synthetic, test-only service (B0:F0, not a real BIOS call) stays
/// <see cref="BiosServiceStatus.Pending"/> until a guest-RAM counter reaches its target; the counter is advanced only
/// by the VBlank IRQ, which reaches the kernel exception handler's chain while the guest waits at the vector. The call
/// then returns exactly once with the counter in <c>$v0</c>.
/// </summary>
[Test]
public sealed class BiosBlockingCallWaitTests
{
    private const uint Entry = 0x80001000u;
    private const uint Counter = 0x80003000u;
    private const uint B0Vector = 0x800000B0u; // JAL keeps the caller's KSEG0 segment
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint Timer2Bit = 1u << (DeviceScheduler.Timer0Irq + 2);

    private const R3000aRegister T0 = R3000aRegister.T0;
    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister A0 = R3000aRegister.A0;
    private const R3000aRegister A1 = R3000aRegister.A1;
    private const R3000aRegister V0 = R3000aRegister.V0;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    /// <summary>The synthetic Runtime: B0:F0 Wait(counterAddr, target) blocks; B0:F1 Echo(x) returns x + 1 at once.</summary>
    private sealed class SyntheticRuntime(IGuestMemoryReader reader) : IBiosRuntime
    {
        public const byte WaitFunction = 0xF0;
        public const byte EchoFunction = 0xF1;

        public int WaitInvocations { get; private set; }

        public BiosServiceResult Invoke(BiosCallIdentity identity)
        {
            if (identity.Family != BiosCallFamily.B0)
            {
                return BiosServiceResult.Unsupported(identity);
            }

            switch (identity.FunctionNumber)
            {
                case EchoFunction:
                    return BiosServiceResult.Supported(identity, identity.Arguments[0] + 1);
                case WaitFunction:
                    WaitInvocations++;
                    Span<byte> word = stackalloc byte[4];
                    if (!reader.TryRead(identity.Arguments[0], word))
                    {
                        return BiosServiceResult.UnsupportedState(identity, "counter unreadable");
                    }

                    var value = BitConverter.ToUInt32(word);
                    return value >= identity.Arguments[1] ? BiosServiceResult.Supported(identity, value) : BiosServiceResult.Pending(identity);
                default:
                    return BiosServiceResult.Unsupported(identity);
            }
        }

        public bool TryGetServiceArgumentCount(BiosCallFamily family, byte functionNumber, out int argumentCount)
        {
            argumentCount = functionNumber == WaitFunction ? 2 : 1;
            return family == BiosCallFamily.B0 && functionNumber is WaitFunction or EchoFunction;
        }
    }

    /// <summary>What the kernel chain saw: every exception's EPC and how many VBlank / Timer 2 IRQs it serviced.</summary>
    private sealed class ChainLog
    {
        public List<uint> Epcs { get; } = [];
        public int Vblanks { get; set; }
        public int Timer2 { get; set; }

        /// <summary>Acknowledges VBlank (counting it into guest RAM) and Timer 2; completes into the default exit.</summary>
        public BiosExceptionChain Chain(bool countVblanks = true) => ctx =>
        {
            Epcs.Add(ctx.Exception.Epc);
            var pending = ctx.Interrupts.Status & ctx.Interrupts.Mask;
            if ((pending & VblankBit) != 0)
            {
                ctx.Interrupts.Acknowledge(~VblankBit);
                Vblanks++;
                if (countVblanks)
                {
                    Span<byte> word = stackalloc byte[4];
                    ctx.Reader.TryRead(Counter, word).Should().BeTrue();
                    ctx.Writer.TryWrite(Counter, BitConverter.GetBytes(BitConverter.ToUInt32(word) + 1)).Should().BeTrue();
                }
            }

            if ((pending & Timer2Bit) != 0)
            {
                ctx.Interrupts.Acknowledge(~Timer2Bit);
                Timer2++;
            }

            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        };
    }

    /// <summary>
    /// SYS(02h) enables interrupts; I_MASK = VBlank (+ Timer 2 one-shot at <paramref name="timer2Target"/>);
    /// Echo(7) → s4; Wait(Counter, target) → s2 and s3++ (one return); Echo(9) → s5.
    /// </summary>
    private static uint[] Program(ushort target, ushort? timer2Target = null)
    {
        var code = new List<uint>
        {
            Ori(A0, Zero, (ushort)BiosKernelSyscall.ExitCriticalSection), MipsEncoding.Syscall(),
            Lui(T0, 0x1F80),
            Ori(T1, Zero, (ushort)(VblankBit | (timer2Target is null ? 0 : Timer2Bit))), Mem(R3000aOpcode.Sw, T1, T0, 0x1074),
        };
        if (timer2Target is { } t2)
        {
            code.AddRange([Ori(T1, Zero, t2), Mem(R3000aOpcode.Sw, T1, T0, 0x1128), Ori(T1, Zero, 0x10), Mem(R3000aOpcode.Sw, T1, T0, 0x1124)]);
        }

        code.AddRange(Call(SyntheticRuntime.EchoFunction, [Ori(A0, Zero, 7)]));
        code.Add(Ori(R3000aRegister.S4, V0, 0));
        code.AddRange(Call(SyntheticRuntime.WaitFunction, [.. Li(A0, Counter), Ori(A1, Zero, target)]));
        code.Add(MipsEncoding.I(0x09, (byte)R3000aRegister.S3, (byte)R3000aRegister.S3, 1)); // addiu s3, s3, 1: counts returns
        code.Add(Ori(R3000aRegister.S2, V0, 0));
        code.AddRange(Call(SyntheticRuntime.EchoFunction, [Ori(A0, Zero, 9)]));
        code.Add(Ori(R3000aRegister.S5, V0, 0));
        code.Add(MipsEncoding.Nop);
        return [.. code];
    }

    private static uint[] Call(byte function, uint[] arguments) =>
        [Ori(T1, Zero, function), .. arguments, MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress), MipsEncoding.Nop];

    private sealed record Observed(TitleExecutionResult Result, int WaitInvocations, ChainLog Chain);

    private static Observed RunInterpreter(uint[] program, bool countVblanks = true, uint segment = 200_000)
    {
        var log = new ChainLog();
        SyntheticRuntime? runtime = null;
        using var engine = new InterpreterTitleExecutionEngine(
            program, Entry, (reader, _) => runtime = new SyntheticRuntime(reader), log.Chain(countVblanks));
        var result = new ExecutionOrchestrator().Execute(
            engine,
            new ExitHandoff(),
            new TitleExecutionRequest(Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: segment));
        return new Observed(result, runtime!.WaitInvocations, log);
    }

    private static uint G(TitleExecutionResult result, R3000aRegister r) => result.FinalSnapshot!.Gpr[(int)r];

    private static string Describe(TitleExecutionResult result) => $"{result.State} {result.DiagnosticCode} {result.DiagnosticMessage}";

    private static void ShouldHaveReturnedOnce(Observed run, uint target)
    {
        run.Result.State.Should().Be(TitleExecutionState.Completed, Describe(run.Result));
        G(run.Result, R3000aRegister.S3).Should().Be(1u, "the blocking call returned exactly once");
        G(run.Result, R3000aRegister.S2).Should().Be(target, "it returned only once the condition held");
        G(run.Result, R3000aRegister.S4).Should().Be(8u, "an ordinary call before the wait is unaffected");
        G(run.Result, R3000aRegister.S5).Should().Be(10u, "an ordinary call after the wait is unaffected");
        run.Chain.Vblanks.Should().Be((int)target, "the call completed on the first poll after the target VBlank");
        run.Chain.Epcs.Should().OnlyContain(epc => epc == B0Vector, "every IRQ was taken while the guest waited at the vector");
    }

    // ---- interpreter -------------------------------------------------------------------------------

    [Fact]
    public void Wait_OneVblank_CompletesAfterTheVblankHandlerRan_AndReturnsOnce()
    {
        var run = RunInterpreter(Program(target: 1));

        ShouldHaveReturnedOnce(run, 1);
        run.WaitInvocations.Should().BeGreaterThan(1, "the call was pending and re-polled before the VBlank");
    }

    [Fact]
    public void Wait_MultipleVblanks_KeepsDeliveringVblanks_UntilTheTarget()
    {
        var run = RunInterpreter(Program(target: 5));

        ShouldHaveReturnedOnce(run, 5);
        run.WaitInvocations.Should().BeGreaterThan(4 * 64, "five VBlank intervals of polls passed");
    }

    [Fact]
    public void OtherIrqs_DuringTheWait_AreStillTaken()
    {
        // Timer 2 (sysclk) reaches 0xF000 well inside the first VBlank interval, i.e. during the wait.
        var run = RunInterpreter(Program(target: 2, timer2Target: 0xF000));

        ShouldHaveReturnedOnce(run, 2);
        run.Chain.Timer2.Should().Be(1, "the timer IRQ was serviced while the call waited");
    }

    [Fact]
    public void Wait_ThatNeverCompletes_FailsClosedAtTheBound_WithoutReturning()
    {
        var run = RunInterpreter(Program(target: 1), countVblanks: false);

        run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure, Describe(run.Result));
        run.Result.DiagnosticCode.Should().Be(BiosBlockingCallWait.TimeoutDiagnosticCode);
        run.Result.FinalSnapshot!.PC.Should().Be(B0Vector, "the guest never returned from the call");
        G(run.Result, R3000aRegister.S3).Should().Be(0u);
        run.WaitInvocations.Should().Be((int)BiosBlockingCallWait.MaxPolls + 1);
        run.Chain.Vblanks.Should().BeGreaterThanOrEqualTo((int)BiosBlockingCallWait.MaxWaitVblanks - 1, "VBlanks kept being delivered while it waited");
    }

    [Fact]
    public void RepeatedRuns_AreIdentical()
    {
        var first = RunInterpreter(Program(target: 3, timer2Target: 0xF000));
        var second = RunInterpreter(Program(target: 3, timer2Target: 0xF000));

        first.Result.State.Should().Be(TitleExecutionState.Completed, Describe(first.Result));
        second.Result.FinalSnapshot!.Gpr.Should().Equal(first.Result.FinalSnapshot!.Gpr);
        second.Result.FinalSnapshot.PC.Should().Be(first.Result.FinalSnapshot.PC);
        second.WaitInvocations.Should().Be(first.WaitInvocations);
        second.Chain.Epcs.Should().Equal(first.Chain.Epcs);
        second.Chain.Timer2.Should().Be(first.Chain.Timer2);
    }

    // ---- generated host ------------------------------------------------------------------------------

    [Fact]
    public void GeneratedHost_WaitsAcrossVblanks_AndReturnsOnce_LikeTheInterpreter()
    {
        var words = Program(target: 2);
        var log = new ChainLog();
        SyntheticRuntime? runtime = null;
        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.Build(Entry, words, Entry, []),
            words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, _) => runtime = new SyntheticRuntime(reader),
            exceptionChain: log.Chain());

        var result = new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request(segmentBudget: 100_000));

        ShouldHaveReturnedOnce(new Observed(result, runtime!.WaitInvocations, log), 2);
        var interpreter = RunInterpreter(words);
        runtime.WaitInvocations.Should().Be(interpreter.WaitInvocations, "both paths poll on the same device time");
    }

    // ---- the shared dispatch -------------------------------------------------------------------------

    private static uint[] Registers(byte function, uint ra)
    {
        var gpr = new uint[32];
        gpr[(int)T1] = function;
        gpr[(int)A0] = Counter;
        gpr[(int)A1] = 1;
        gpr[(int)R3000aRegister.Ra] = ra;
        return gpr;
    }

    private static SyntheticRuntime ZeroCounterRuntime() => new(new GuestMemoryReader(_ => 0));

    [Fact]
    public void Pending_WithoutAWait_FailsClosed()
    {
        var outcome = BiosVectorDispatch.Dispatch(ZeroCounterRuntime(), BiosCallFamily.B0, Registers(SyntheticRuntime.WaitFunction, Entry));

        outcome.ContinueExecution.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosBlockingCallWait.UnsupportedDiagnosticCode);
    }

    [Fact]
    public void Pending_LeavesNoReturnValue_AndASecondDifferentPendingCallFailsClosed()
    {
        var wait = new BiosBlockingCallWait();
        var runtime = ZeroCounterRuntime();

        var first = BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.B0, Registers(SyntheticRuntime.WaitFunction, Entry), wait);
        var nested = BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.B0, Registers(SyntheticRuntime.WaitFunction, Entry + 0x100), wait);

        first.Should().Match<BiosVectorDispatchOutcome>(o => o.ContinueExecution && o.IsPending && o.ReturnValue == null && o.CpuState == null);
        nested.ContinueExecution.Should().BeFalse();
        nested.DiagnosticCode.Should().Be(BiosBlockingCallWait.NestedDiagnosticCode);
    }

    [Fact]
    public void UnrelatedCalls_DuringAWait_NeitherCompleteNorResetIt()
    {
        var wait = new BiosBlockingCallWait();
        var runtime = ZeroCounterRuntime();
        var pending = Registers(SyntheticRuntime.WaitFunction, Entry);

        for (var i = 0; i < BiosBlockingCallWait.MaxPolls; i++)
        {
            BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.B0, pending, wait).IsPending.Should().BeTrue();
            var echo = BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.B0, Registers(SyntheticRuntime.EchoFunction, Entry + 0x200), wait);
            echo.ReturnValue.Should().Be(Counter + 1);
        }

        BiosVectorDispatch.Dispatch(runtime, BiosCallFamily.B0, pending, wait).DiagnosticCode
            .Should().Be(BiosBlockingCallWait.TimeoutDiagnosticCode, "the bound counts the one pending call, not every dispatch");
    }
}

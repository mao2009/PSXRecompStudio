using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using PSXRecomp.Tests.Runtime;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #661 S2: the priority-2 PadCardIrq element runs from the shared kernel exception handler, so the interpreter and
/// the generated-host engines reach the same guest state from the same program: the guest calls InitCARD2(1), B0:15 and
/// StartCARD2 itself, enables interrupts, and waits for the first VBlank's pad stage to fill <c>button_dest</c>.
/// </summary>
[Test]
public sealed class PadCardIrqBackendParityTests
{
    private const uint Entry = 0x80001000u; // the generated-host helper's entry
    private const uint ButtonDest = 0x80006000u;
    private const ushort Marker = 0x77;
    private const uint Segment = 2 * DeviceScheduler.VblankIntervalCycles;

    private const R3000aRegister T0 = R3000aRegister.T0;
    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister T2 = R3000aRegister.T2;
    private const R3000aRegister A0 = R3000aRegister.A0;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    private static uint[] CallB0(byte function, params uint[][] setArguments) =>
        [Ori(T1, Zero, function), .. setArguments.SelectMany(a => a), MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress), MipsEncoding.Nop];

    /// <summary>The Persona start sequence, an optional B0:5B afterwards, SYS(02h), then a wait on <c>[button_dest]</c>.</summary>
    private static uint[] Program(uint? changeClearPadAfterStart)
    {
        var words = new List<uint>();
        words.AddRange(CallB0(BiosHleRuntime.InitCard2Function, [Ori(A0, Zero, 1)]));
        words.AddRange(CallB0(
            BiosHleRuntime.OutdatedPadInitAndStartFunction,
            Li(A0, 0x20000001),
            Li(R3000aRegister.A1, ButtonDest),
            [Ori(R3000aRegister.A2, Zero, 0), Ori(R3000aRegister.A3, Zero, 0)]));
        words.AddRange(CallB0(BiosHleRuntime.StartCard2Function));
        if (changeClearPadAfterStart is { } value)
        {
            words.AddRange(CallB0(BiosHleRuntime.ChangeClearPadFunction, [Ori(A0, Zero, (ushort)value)]));
        }

        words.AddRange([Ori(A0, Zero, (ushort)BiosKernelSyscall.ExitCriticalSection), MipsEncoding.Syscall()]);
        words.Add(Lui(T0, (ushort)(ButtonDest >> 16)));
        var loop = Entry + (uint)words.Count * 4;
        words.AddRange(
        [
            Mem(R3000aOpcode.Lw, R3000aRegister.S0, T0, unchecked((ushort)ButtonDest)),
            MipsEncoding.Nop,
            MipsEncoding.Branch(0x04, (byte)R3000aRegister.S0, 0, pc: loop + 8, target: loop), // beq s0, zero, loop
            MipsEncoding.Nop,
            Lui(T2, 0x1F80),
            Mem(R3000aOpcode.Lw, R3000aRegister.S3, T2, 0x1070), // I_STAT after the exception returned
            MipsEncoding.Nop,
            Ori(R3000aRegister.S2, Zero, Marker),
            MipsEncoding.Nop,
        ]);
        return [.. words];
    }

    private static TitleExecutionResult RunInterpreter(uint[] program)
    {
        using var engine = new InterpreterTitleExecutionEngine(
            program, Entry, (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer));
        return new ExecutionOrchestrator().Execute(
            engine,
            new ExitHandoff(),
            new TitleExecutionRequest(Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: Segment));
    }

    private static TitleExecutionResult RunGeneratedHost(uint[] program)
    {
        using var dir = new TempDirectory();
        return Run(program, dir, withRuntime: true, segmentBudget: Segment);
    }

    private static uint G(TitleExecutionResult result, R3000aRegister r) => result.FinalSnapshot!.Gpr[(int)r];

    private static string Describe(TitleExecutionResult result) => $"{result.State} {result.DiagnosticCode} {result.DiagnosticMessage}";

    [Fact]
    public void Both_Engines_Run_The_Element_Store_ButtonDest_And_Acknowledge_Irq0()
    {
        var program = Program(changeClearPadAfterStart: null);

        foreach (var result in new[] { RunInterpreter(program), RunGeneratedHost(program) })
        {
            result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
            G(result, R3000aRegister.S0).Should().Be(BiosPadCardIrqHandler.DisconnectedPadButtons, Describe(result));
            G(result, R3000aRegister.S2).Should().Be(Marker);
            (G(result, R3000aRegister.S3) & 1u).Should().Be(0u, "StartCARD2 left the auto-ack at 1, so priority 2 acknowledged IRQ0");
        }
    }

    [Fact]
    public void Both_Engines_Stop_With_The_Same_Diagnostic_On_An_Undocumented_Auto_Ack()
    {
        var program = Program(changeClearPadAfterStart: 2);

        var interpreter = RunInterpreter(program);
        var host = RunGeneratedHost(program);

        static string Detail(TitleExecutionResult r) => r.DiagnosticMessage![..r.DiagnosticMessage!.IndexOf("|EPC=", StringComparison.Ordinal)];
        interpreter.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode, Describe(interpreter));
        host.DiagnosticCode.Should().Be(interpreter.DiagnosticCode, Describe(host));
        Detail(host).Should().Be(Detail(interpreter)).And.Contain("priority 2 PadCardIrq").And.Contain("0x2");
    }
}

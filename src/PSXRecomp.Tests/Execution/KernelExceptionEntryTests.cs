using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;
using PSXRecomp.Tests.Runtime;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #662, interpreter engine: a hardware INT at the unpopulated general exception vector enters the
/// shared kernel exception handler (C0:06), which saves the context, walks the chain, and only after the
/// chain ran to the end completes it — through the hook or ReturnFromException. A guest-owned vector is unchanged.
/// </summary>
[Test]
public sealed class KernelExceptionEntryTests
{
    private const uint Entry = 0x80001000u;
    private const uint Vector = 0x80000080u;
    private const uint HookBuffer = 0x80002000u;
    private const uint Timer2Bit = 1u << (DeviceScheduler.Timer0Irq + 2);
    private const ushort Sr = 0x0403; // IM2 | the interpreter's IEc (bit 1, docs/cpu/cop0.md) | the R3000A's (bit 0)
    private const ushort Marker = 0x77;

    private const R3000aRegister T1 = R3000aRegister.T1;
    private const R3000aRegister T2 = R3000aRegister.T2;
    private const R3000aRegister T5 = R3000aRegister.T5;
    private const R3000aRegister Zero = R3000aRegister.Zero;

    private static uint Mfc0(R3000aRegister rt, byte rd) => 0x40000000u | ((uint)rt << 16) | ((uint)rd << 11);

    private static uint Mtc0(R3000aRegister rt, byte rd) => 0x40800000u | ((uint)rt << 16) | ((uint)rd << 11);

    /// <summary>I_MASK = IRQ6; Timer 2 raises IRQ6 at counter 100; SR enables INT; <c>$t5 = counter</c>.</summary>
    private static List<uint> ArmTimer2Irq(ushort counter) =>
    [
        Lui(T1, 0x1F80),
        Ori(T2, Zero, (ushort)Timer2Bit), Mem(R3000aOpcode.Sw, T2, T1, 0x1074),
        Ori(T2, Zero, 100), Mem(R3000aOpcode.Sw, T2, T1, 0x1128),
        Ori(T2, Zero, 0x10), Mem(R3000aOpcode.Sw, T2, T1, 0x1124),
        Ori(T5, Zero, counter),
        Ori(T2, Zero, Sr), Mtc0(T2, 12),
    ];

    /// <summary>Counts <c>$t5</c> down to 0 (the interrupted loop), then sets the marker in <c>$s2</c>.</summary>
    private static uint[] CountDownThenMark(uint at) =>
    [
        MipsEncoding.I(0x09, (byte)T5, (byte)T5, 0xFFFF), // addiu t5, t5, -1
        MipsEncoding.Branch(0x05, (byte)T5, 0, pc: at + 4, target: at), // bne t5, zero, <addiu>
        MipsEncoding.Nop,
        Ori(R3000aRegister.S2, Zero, Marker),
    ];

    private static uint[] SpinForever(uint at) => [MipsEncoding.Branch(0x04, 0, 0, pc: at, target: at), MipsEncoding.Nop];

    private static IReadOnlyList<RecompilerInitialMemoryItem> Words(uint address, params uint[] words) =>
        [.. words.SelectMany((w, i) => Enumerable.Range(0, 4)
            .Select(b => new RecompilerInitialMemoryItem(address + (uint)(i * 4 + b), (byte)(w >> (8 * b)))))];

    private static TitleExecutionResult Run(
        uint[] program,
        IReadOnlyList<RecompilerInitialMemoryItem>? memory = null,
        BiosExceptionChain? chain = null,
        bool withRuntime = true,
        uint segment = 20_000)
    {
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? factory = withRuntime
            ? (reader, writer) => new BiosHleRuntime(new CapturedOutputSink(), reader, writer)
            : null;
        using var engine = new InterpreterTitleExecutionEngine(program, Entry, factory, chain);
        return new ExecutionOrchestrator().Execute(
            engine,
            new ExitHandoff(),
            new TitleExecutionRequest(
                Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, memory ?? [], outerBudget: 1, segmentBudget: segment));
    }

    private static uint G(TitleExecutionResult result, R3000aRegister r) => result.FinalSnapshot!.Gpr[(int)r];

    // ---- entry: default chain fails closed ---------------------------------------------------

    [Fact]
    public void Int_AtTheUnpopulatedVector_EntersTheKernelHandler_AndStopsAtTheChain_WithoutFiringTheHook()
    {
        var withHook = RegisterHookThen(ArmTimer2Irq(1000).Concat(SpinForever(Entry)).ToArray(), out var landing, landingMark: 0x55);

        var result = Run(withHook, Words(HookBuffer, HookBufferWords(landing, s0: 0x1234)));

        result.State.Should().Be(TitleExecutionState.RuntimeFailure, Describe(result));
        result.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        result.DiagnosticMessage.Should().Contain($"I_STAT=0x{Timer2Bit:X4}").And.Contain("CAUSE=0x00000400").And.Contain("Timer2 IRQ6").And.Contain("#660");
        G(result, R3000aRegister.V0).Should().NotBe(1u, "the hook fires only after the chains ran to the end");
        G(result, R3000aRegister.S0).Should().NotBe(0x1234u);
        result.FinalSnapshot!.PC.Should().Be(Vector, "the run stops where the unmodelled chain element is reached");
    }

    // ---- entry: chain completes -> ReturnFromException ----------------------------------------------

    [Fact]
    public void ChainCompletion_ReturnsToEpc_WithRegistersRestored_AndRfeReenablingInterrupts()
    {
        // The first pass leaves IRQ6 pending (the chain completed without acknowledging): after the return's
        // RFE the CPU takes it again, which is only possible if the interrupted loop resumed with IEc restored.
        var calls = 0;
        var program = ArmTimer2Irq(3000).Concat(CountDownThenMark(Entry)).ToArray();

        var result = Run(program, chain: ctx =>
        {
            if (++calls == 2)
            {
                ctx.Interrupts.Acknowledge(~Timer2Bit);
            }

            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        calls.Should().Be(2, "the unacknowledged IRQ was taken again after RFE");
        G(result, R3000aRegister.T5).Should().Be(0u, "the interrupted countdown resumed with its registers intact and finished");
        G(result, R3000aRegister.S2).Should().Be(Marker);
        G(result, R3000aRegister.T1).Should().Be(0x1F800000u);
    }

    // ---- entry: chain completes -> the hook ----------------------------------------------------------

    [Fact]
    public void ChainCompletion_WithARegisteredHook_EntersTheHook_WithV0One_AndInterruptsStillDisabled()
    {
        const uint hookS0 = 0x1234;
        var chainCalls = 0;
        var program = RegisterHookThen(
            ArmTimer2Irq(1000).Concat(SpinForever(Entry)).ToArray(),
            out var landing,
            landingMark: Marker);

        var result = Run(program, Words(HookBuffer, HookBufferWords(landing, hookS0)), chain: ctx =>
        {
            chainCalls++;
            ctx.Interrupts.Acknowledge(~Timer2Bit);
            return new BiosExceptionChainResult(BiosExceptionChainStatus.Completed);
        });

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        chainCalls.Should().Be(1);
        G(result, R3000aRegister.S2).Should().Be(Marker, "control continued at the hook's saved ra");
        G(result, R3000aRegister.V0).Should().Be(1u);
        G(result, R3000aRegister.S0).Should().Be(hookS0, "the hook restored its saved registers");
        (G(result, R3000aRegister.S4) & 0x3u).Should().Be(0u, "no RFE: SR still holds the exception-entry push");
    }

    // ---- guest-owned vector --------------------------------------------------------------------------

    [Fact]
    public void AGuestHandlerAtTheVector_StillRunsItself_EvenWithARuntimeAttached()
    {
        var handler = new[]
        {
            Lui(R3000aRegister.K0, 0x1F80),
            Mem(R3000aOpcode.Sw, Zero, R3000aRegister.K0, 0x1070),                    // I_STAT &= 0
            MipsEncoding.I(0x09, (byte)R3000aRegister.S1, (byte)R3000aRegister.S1, 1), // s1++
            Mfc0(R3000aRegister.K1, 14),                                               // EPC
            MipsEncoding.Nop,
            MipsEncoding.JumpRegister((byte)R3000aRegister.K1),
            0x42000010u,                                                               // RFE
        };
        var program = ArmTimer2Irq(1000)
            .Concat(
            [
                MipsEncoding.Branch(0x04, (byte)R3000aRegister.S1, 0, pc: Entry + 4 * 10, target: Entry + 4 * 10), // wait for the handler
                MipsEncoding.Nop,
                Ori(R3000aRegister.S2, Zero, Marker),
            ]).ToArray();

        var result = Run(program, Words(Vector, handler), chain: _ => throw new InvalidOperationException("The kernel handler must not run."));

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        G(result, R3000aRegister.S1).Should().Be(1u, "the guest's own handler ran once");
        G(result, R3000aRegister.S2).Should().Be(Marker);
    }

    [Fact]
    public void WithoutARuntime_TheVectorStaysTheGuestsOwn()
    {
        // No BIOS Runtime attached: there is no kernel to enter, so the INT keeps the pre-#662 behaviour.
        var program = ArmTimer2Irq(1000).Concat(SpinForever(Entry)).ToArray();

        var result = Run(program, withRuntime: false, segment: 2_000);

        result.DiagnosticCode.Should().NotBe(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
    }

    // ---- program with a registered B0:19 hook ---------------------------------------------------------

    private static uint[] HookBufferWords(uint landing, uint s0) =>
        // Documented jmp_buf order: ra, sp, fp, s0..s7, gp.
        [landing, 0x801FFF00, 0x801FFF10, s0, 0, 0, 0, 0, 0, 0, 0, 0x80008000];

    /// <summary>
    /// Prepends <c>B0:19 HookEntryInt(HookBuffer)</c> to <paramref name="program"/>'s head and appends a landing pad
    /// (reached only through the hook) that records SR in <c>$s4</c> and sets <paramref name="landingMark"/> in <c>$s2</c>.
    /// </summary>
    private static uint[] RegisterHookThen(uint[] program, out uint landing, ushort landingMark)
    {
        uint[] register =
        [
            Ori(T1, Zero, BiosHleRuntime.HookEntryIntFunction),
            Lui(R3000aRegister.A0, unchecked((ushort)(HookBuffer >> 16))),
            Ori(R3000aRegister.A0, R3000aRegister.A0, unchecked((ushort)HookBuffer)),
            MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress),
            MipsEncoding.Nop,
        ];
        var all = register.Concat(program).ToList(); // branches are PC-relative, so the shift needs no fix-up
        landing = Entry + (uint)all.Count * 4;
        all.AddRange([Mfc0(R3000aRegister.S4, 12), MipsEncoding.Nop, Ori(R3000aRegister.S2, Zero, landingMark), MipsEncoding.Nop]);
        return [.. all];
    }

    private static string Describe(TitleExecutionResult result) =>
        $"{result.State} {result.DiagnosticCode} {result.DiagnosticMessage}";
}

using System;
using System.Collections.Generic;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecompStudio.Services;

/// <summary>
/// One full-title execution run: the orchestrator's classified outcome plus the
/// guest's TTY output as raw bytes (the Domain layer never fixes an encoding —
/// ADR-014 — so the host decides how to render them).
/// </summary>
[Application]
public sealed record TitleExecutionRun(TitleExecutionResult Result, IReadOnlyList<byte> Output);

/// <summary>
/// The Studio's composition root for running a title: it assembles the
/// production execution engine, the BIOS HLE runtime, and the
/// <see cref="ExecutionOrchestrator"/>, and hands back a classified result
/// (ADR-015).
/// </summary>
/// <remarks>
/// This class owns only wiring. Every execution rule — CPU semantics, BIOS
/// dispatch, outcome classification — lives in the Domain layer; nothing here
/// reimplements any of it. It is deliberately UI-free so it is callable from a
/// view model, a future CLI, or a test.
/// </remarks>
[Application]
public sealed class TitleExecutionService
{
    /// <summary>The guest address <see cref="RunDiagnostic"/> loads its program at.</summary>
    public const uint DiagnosticEntryPc = 0x80001000u;

    private const byte OriOpcode = 0x0D;
    private const byte JalOpcode = 0x03;

    // A0/B0/C0 dispatch reads the function number from R9 (T1) and the first
    // argument from R4 (A0), per docs/REFERENCES.md.
    private const byte FunctionNumberRegister = 9;
    private const byte FirstArgumentRegister = 4;
    private const byte MarkerRegister = 17;

    /// <summary>The value the diagnostic program leaves in S1 when it ran to its end.</summary>
    public const uint DiagnosticMarker = 0x1234u;

    /// <summary>The character the diagnostic program asks the BIOS to print.</summary>
    public const byte DiagnosticCharacter = (byte)'P';

    /// <summary>
    /// Runs <paramref name="program"/> from <paramref name="entryPc"/> to a
    /// classified outcome, dispatching BIOS vector hits through
    /// <see cref="BiosHleRuntime"/> over the engine's own guest memory.
    /// </summary>
    /// <param name="program">The guest instruction words to load and execute.</param>
    /// <param name="entryPc">The guest address the program is loaded at and entered from.</param>
    /// <param name="outerBudget">How many segments the orchestrator may run.</param>
    /// <param name="segmentBudget">How many steps each segment may spend.</param>
    /// <returns>The classified outcome and the bytes the guest wrote to the TTY.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> is empty.</exception>
    public TitleExecutionRun Run(
        IReadOnlyList<uint> program,
        uint entryPc,
        uint outerBudget,
        uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(program);

        var sink = new CollectedOutput();
        using var engine = new InterpreterTitleExecutionEngine(
            program,
            entryPc,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var request = new TitleExecutionRequest(
            entryPc,
            new uint[TitleExecutionRequest.GprCount],
            initialHi: 0,
            initialLo: 0,
            initialMemory: [],
            outerBudget,
            segmentBudget);

        var result = new ExecutionOrchestrator().Execute(
            engine, new ProgramEndHandoff(entryPc, program.Count), request);

        return new TitleExecutionRun(result, sink.Bytes);
    }

    /// <summary>
    /// Runs a real PS-X EXE image (Issue #409) through the same production execution
    /// path as <see cref="RunDiagnostic"/>: the whole text image is loaded at the
    /// header's text start and execution enters at the header's entry point, with
    /// SP/GP seeded from the header. Reaching the declared end of the text image is a
    /// natural exit; every other unresolved transfer, unsupported BIOS service, or
    /// runtime boundary is classified rather than silently emulated.
    /// </summary>
    /// <param name="exe">A legally user-supplied, already-parsed PS-X EXE.</param>
    /// <param name="outerBudget">How many segments the orchestrator may run.</param>
    /// <param name="segmentBudget">How many steps each segment may spend.</param>
    /// <returns>The classified outcome and the bytes the guest wrote to the TTY.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exe"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="exe"/>'s image or header is
    /// malformed for execution (see <see cref="PsxExeTitleInput.Build"/>).</exception>
    public TitleExecutionRun Run(PsxExe exe, uint outerBudget, uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(exe);

        var input = PsxExeTitleInput.Build(exe, outerBudget, segmentBudget);
        var sink = new CollectedOutput();
        using var engine = new InterpreterTitleExecutionEngine(
            input.InstructionWords,
            input.LoadAddress,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var result = new ExecutionOrchestrator().Execute(
            engine,
            new ProgramEndHandoff(input.LoadAddress, input.InstructionWords.Count),
            input.Request);

        return new TitleExecutionRun(result, sink.Bytes);
    }

    /// <summary>
    /// Runs the built-in diagnostic title: a guest program that calls BIOS
    /// <c>A0:3C putchar</c> and then runs off its own end. It proves the whole
    /// production path — engine load, bounded run, BIOS handoff, outcome
    /// classification — is reachable from the Studio assembly.
    /// </summary>
    /// <returns>A run that ends <see cref="TitleExecutionState.Completed"/> on a
    /// working installation, with <see cref="DiagnosticCharacter"/> as its output.</returns>
    public TitleExecutionRun RunDiagnostic() =>
        Run(
            [
                // ori $t1, $zero, 0x3C     -- BIOS function number: A0:3C putchar
                Immediate(OriOpcode, FunctionNumberRegister, BiosHleRuntime.PutCharFunction),
                // ori $a0, $zero, 'P'      -- the character to print
                Immediate(OriOpcode, FirstArgumentRegister, DiagnosticCharacter),
                // jal 0xA0                 -- the A0 BIOS trampoline vector
                (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFCu) >> 2,
                // nop                      -- branch delay slot
                0u,
                // ori $s1, $zero, 0x1234   -- resumed here from the BIOS call
                Immediate(OriOpcode, MarkerRegister, DiagnosticMarker),
            ],
            DiagnosticEntryPc,
            outerBudget: 4,
            segmentBudget: 64);

    private static uint Immediate(byte opcode, byte rt, uint immediate) =>
        (uint)opcode << 26 | (uint)rt << 16 | (immediate & 0xFFFFu);

    /// <summary>
    /// Collects the guest's TTY bytes. Byte-level and encoding-free, as
    /// <see cref="IRuntimeOutputSink"/> requires; the caller decides how to
    /// render them.
    /// </summary>
    private sealed class CollectedOutput : IRuntimeOutputSink
    {
        public List<byte> Bytes { get; } = [];

        public void WriteByte(byte value) => Bytes.Add(value);
    }

    /// <summary>
    /// Reports the guest running off the end of its own program image as a
    /// natural exit, and declines every other unresolved transfer so it is
    /// classified as <see cref="TitleExecutionState.UnsupportedTransfer"/>
    /// rather than silently treated as success.
    /// </summary>
    private sealed class ProgramEndHandoff(uint loadAddress, int instructionCount) : ITitleExecutionHandoff
    {
        private readonly uint _programEnd = unchecked(loadAddress + (uint)instructionCount * 4u);

        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
        {
            ArgumentNullException.ThrowIfNull(segmentState);
            return segmentState.PC == _programEnd ? TitleExecutionHandoffResult.Exit() : null;
        }
    }
}

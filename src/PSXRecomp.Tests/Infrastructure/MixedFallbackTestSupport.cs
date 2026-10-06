using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Synthetic-program support for the mixed-execution tests (Issue #693). A program is a main routine at
/// <see cref="Entry"/> plus "uncompiled" code placed at fixed addresses that the static reachable-program build never
/// reaches (it is only entered through a register-indirect jump), so the artifact has no block for it. Nothing here is
/// title-specific: the addresses are test fixtures, never production constants.
/// </summary>
[Test]
internal static class MixedFallbackTestSupport
{
    public const uint Entry = 0x80001000u;
    public const uint Target = 0x80001100u;
    public const uint Data = 0x80002000u;
    public const int ImageWords = 0x400 / 4; // 0x80001000..0x80001400

    public const R3000aRegister Zero = R3000aRegister.Zero;
    public const R3000aRegister V0 = R3000aRegister.V0;
    public const R3000aRegister A0 = R3000aRegister.A0;
    public const R3000aRegister T0 = R3000aRegister.T0;
    public const R3000aRegister T1 = R3000aRegister.T1;
    public const R3000aRegister T2 = R3000aRegister.T2;
    public const R3000aRegister T3 = R3000aRegister.T3;
    public const R3000aRegister T4 = R3000aRegister.T4;
    public const R3000aRegister T5 = R3000aRegister.T5;
    public const R3000aRegister T6 = R3000aRegister.T6;
    public const R3000aRegister T7 = R3000aRegister.T7;
    public const R3000aRegister T8 = R3000aRegister.T8;
    public const R3000aRegister T9 = R3000aRegister.T9;
    public const R3000aRegister S0 = R3000aRegister.S0;
    public const R3000aRegister S1 = R3000aRegister.S1;
    public const R3000aRegister S2 = R3000aRegister.S2;
    public const R3000aRegister S3 = R3000aRegister.S3;
    public const R3000aRegister S4 = R3000aRegister.S4;
    public const R3000aRegister S5 = R3000aRegister.S5;
    public const R3000aRegister S6 = R3000aRegister.S6;
    public const R3000aRegister S7 = R3000aRegister.S7;
    public const R3000aRegister Ra = R3000aRegister.Ra;

    // ---- assembler ---------------------------------------------------------------------------------------------

    public static uint[] Li(R3000aRegister rt, uint value) =>
        [MipsEncoding.I(0x0F, (byte)rt, 0, (ushort)(value >> 16)), MipsEncoding.I(0x0D, (byte)rt, (byte)rt, (ushort)value)];

    public static uint Addiu(R3000aRegister rt, R3000aRegister rs, short imm) => MipsEncoding.I(0x09, (byte)rt, (byte)rs, (ushort)imm);

    public static uint Ori(R3000aRegister rt, R3000aRegister rs, ushort imm) => MipsEncoding.I(0x0D, (byte)rt, (byte)rs, imm);

    public static uint Sltiu(R3000aRegister rt, R3000aRegister rs, short imm) => MipsEncoding.I(0x0B, (byte)rt, (byte)rs, (ushort)imm);

    public static uint Addu(R3000aRegister rd, R3000aRegister rs, R3000aRegister rt) => MipsEncoding.R(0x21, (byte)rd, (byte)rs, (byte)rt, 0);

    public static uint Lw(R3000aRegister rt, R3000aRegister b, short off) => MipsEncoding.Load(R3000aOpcode.Lw, (byte)rt, (byte)b, (ushort)off);

    public static uint Sw(R3000aRegister rt, R3000aRegister b, short off) => MipsEncoding.Load(R3000aOpcode.Sw, (byte)rt, (byte)b, (ushort)off);

    public static uint Mfc0(R3000aRegister rt, int cop0) => (0x10u << 26) | ((uint)rt << 16) | ((uint)cop0 << 11);

    public static uint Mtc0(R3000aRegister rt, int cop0) => (0x10u << 26) | (4u << 21) | ((uint)rt << 16) | ((uint)cop0 << 11);

    public static uint Mfhi(R3000aRegister rd) => MipsEncoding.MoveFromHiLo(0x10, (byte)rd);

    public static uint Mflo(R3000aRegister rd) => MipsEncoding.MoveFromHiLo(0x12, (byte)rd);

    public static uint Mthi(R3000aRegister rs) => MipsEncoding.MoveToHiLo(0x11, (byte)rs);

    public static uint Mtlo(R3000aRegister rs) => MipsEncoding.MoveToHiLo(0x13, (byte)rs);

    public static uint Mult(R3000aRegister rs, R3000aRegister rt) => MipsEncoding.MultiplyDivide(0x18, (byte)rs, (byte)rt);

    public static uint Jalr(R3000aRegister rs) => MipsEncoding.JumpAndLinkRegister((byte)Ra, (byte)rs);

    public static uint Jr(R3000aRegister rs) => MipsEncoding.JumpRegister((byte)rs);

    public const uint Nop = MipsEncoding.Nop;

    /// <summary>A growing instruction stream at a fixed address, so a test can name a label's address.</summary>
    public sealed class Block(uint address)
    {
        private readonly List<uint> _words = [];

        public uint Address => address;

        /// <summary>The address the next emitted word will have.</summary>
        public uint Here => address + (uint)_words.Count * 4u;

        public IReadOnlyList<uint> Words => _words;

        public Block Emit(params uint[] words)
        {
            _words.AddRange(words);
            return this;
        }

        public Block Emit(params uint[][] parts)
        {
            foreach (var part in parts)
            {
                _words.AddRange(part);
            }

            return this;
        }
    }

    /// <summary>Lays blocks into one image starting at <see cref="Entry"/>; the rest is NOPs.</summary>
    public static uint[] Image(params Block[] blocks)
    {
        var words = new uint[ImageWords];
        foreach (var block in blocks)
        {
            var index = (int)((block.Address - Entry) / 4);
            for (var i = 0; i < block.Words.Count; i++)
            {
                words[index + i] = block.Words[i];
            }
        }

        return words;
    }

    /// <summary>The program's end: an indirect jump to PC 0, which the handoff below treats as the exit.</summary>
    public static uint[] End() => [Jr(Zero), Nop];

    // ---- running -----------------------------------------------------------------------------------------------

    public sealed class NullSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }

    /// <summary>Exits only at PC 0 (the program's own end marker); any other unresolved PC has no continuation rule.</summary>
    public sealed class ExitAtZeroHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot snapshot) =>
            snapshot.PC == 0 ? TitleExecutionHandoffResult.Exit() : null;
    }

    public static TitleExecutionRequest Request(uint segmentBudget = 100_000) =>
        new(Entry, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: segmentBudget);

    public sealed record MixedRun(
        TitleExecutionResult Result,
        MixedFallbackEvidence? Evidence,
        MixedFallbackTimings? Timings);

    /// <summary>Builds the artifact for <paramref name="words"/> with the static roots only and runs it to its end.</summary>
    public static MixedRun RunArtifact(
        uint[] words, TempDirectory dir, MixedFallbackOptions? mixedFallback, uint segmentBudget = 100_000)
    {
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
            mixedFallback: mixedFallback);
        var result = new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(segmentBudget));
        return new MixedRun(result, engine.FallbackEvidence, engine.FallbackTimings);
    }

    /// <summary>The pure-interpreter reference: the same words, no artifact, the same end rule.</summary>
    public static TitleExecutionResult RunInterpreter(uint[] words, uint segmentBudget = 100_000)
    {
        using var engine = new InterpreterTitleExecutionEngine(
            words, Entry, (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer));
        return new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(segmentBudget));
    }
}

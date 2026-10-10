using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Issue #732: MFC0/MTC0/RFE, ADD and the SR.IsC store guard, carried through the
// lowering, the validator, the IR evaluator and the generated host. Every program
// runs three ways — the native interpreter (the oracle), the IR evaluator and the
// gcc-built generated host — and all three must agree.
public sealed class RecompilerCop0Tests
{
    internal const uint EntryPc = 0x80000000u;
    private const uint DataBase = 0x80001000u;
    private const byte T0 = 8, T1 = 9, T2 = 10, T3 = 11;

    [Theory]
    [InlineData(RecompilerCop0.Status)]
    [InlineData(RecompilerCop0.Epc)]
    [InlineData(RecompilerCop0.BadVAddr)]
    [InlineData((byte)3)]
    public void Mtc0ThenMfc0_RoundTripsTheWholeValue(byte register)
    {
        var run = RunThreeWay($"cop0-roundtrip-{register}",
        [
            Lui(T0, 0x1234), Ori(T0, T0, 0x5678),
            Mtc0(T0, register),
            Mfc0(T1, register),
            MipsEncoding.Nop,
        ]);

        run.Gpr[T1].Should().Be(0x12345678u);
        run.Cop0![register].Should().Be(0x12345678u);
    }

    [Fact]
    public void Mtc0ToCause_WritesOnlyTheSoftwareInterruptBits()
    {
        var run = RunThreeWay("cop0-cause-mask",
        [
            MipsEncoding.I(0x09, rt: T0, rs: 0, immediate: 0xFFFF),   // $t0 = -1
            Mtc0(T0, RecompilerCop0.Cause),
            Mfc0(T1, RecompilerCop0.Cause),
            MipsEncoding.Nop,
        ]);

        run.Gpr[T1].Should().Be(RecompilerCop0.CauseWritableMask);
    }

    [Fact]
    public void Mfc0_IsLoadDelayed_ItsDelaySlotObservesThePreviousValue()
    {
        var run = RunThreeWay("cop0-mfc0-load-delay",
        [
            Ori(T0, 0, 0x77),
            Mtc0(T0, RecompilerCop0.Epc),
            Ori(T1, 0, 0x55),
            Mfc0(T1, RecompilerCop0.Epc),
            MipsEncoding.R(0x21, rd: T2, rs: T1, rt: 0, shamt: 0),   // load-delay slot: old $t1
            MipsEncoding.R(0x21, rd: T3, rs: T1, rt: 0, shamt: 0),   // after it: the COP0 value
        ]);

        run.Gpr[T2].Should().Be(0x55u);
        run.Gpr[T3].Should().Be(0x77u);
    }

    [Fact]
    public void Rfe_PopsTheStatusKuIeStack()
    {
        var run = RunThreeWay("cop0-rfe",
        [
            Ori(T0, 0, 0x14),                                       // IEp = IEo = 1
            Mtc0(T0, RecompilerCop0.Status),
            Rfe,
            Mfc0(T1, RecompilerCop0.Status),
            MipsEncoding.Nop,
        ]);

        run.Gpr[T1].Should().Be(RecompilerCop0.ReturnFromException(0x14u)).And.Be(0x15u);
    }

    [Fact]
    public void Add_WithoutOverflow_MatchesAddu()
    {
        var run = RunThreeWay("add-no-overflow",
        [
            Ori(T0, 0, 40), Ori(T1, 0, 2),
            MipsEncoding.R(0x20, rd: T2, rs: T0, rt: T1, shamt: 0),
        ]);

        run.Gpr[T2].Should().Be(42u);
    }

    [Fact]
    public void Add_Overflow_RaisesAndSuppressesTheWrite()
    {
        var program = Lower([MipsEncoding.R(0x20, rd: T2, rs: T0, rt: T1, shamt: 0)]);
        var gpr = new uint[32];
        gpr[T0] = 0x7FFFFFFFu;
        gpr[T1] = 1u;

        var result = RecompilerIrEvaluator.Run(program, EntryPc, gpr, new RecompilerGuestMemory(), blockBudget: 1);

        result.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        result.Gpr[T2].Should().Be(0u);
    }

    [Fact]
    public void StoresBelowKseg1_AreDroppedWhileSrIsolatesTheCache()
    {
        var run = RunThreeWay("cop0-isc-store-drop",
        [
            Lui(T0, 0x8000), Ori(T0, T0, 0x1000),                 // KSEG0 data base
            Lui(T3, 0xA000), Ori(T3, T3, 0x1004),                 // KSEG1 alias of base + 4
            Lui(T1, 0x0001),                                       // SR.IsC
            Ori(T2, 0, 0xAB),
            Mtc0(T1, RecompilerCop0.Status),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: T2, baseRegister: T0, offset: 0),   // dropped
            MipsEncoding.Load(R3000aOpcode.Sb, rt: T2, baseRegister: T3, offset: 0),   // KSEG1: kept
            Mtc0(0, RecompilerCop0.Status),
            MipsEncoding.Load(R3000aOpcode.Sb, rt: T2, baseRegister: T0, offset: 1),   // kept
            MipsEncoding.Nop,
        ], windowBytes: 8);

        run.Memory.Should().Equal(0x00, 0xAB, 0x00, 0x00, 0xAB, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void Validator_AcceptsTheCop0Shapes_AndRejectsAMalformedOne()
    {
        var valid = new RecompilerIrBlock(EntryPc,
        [
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadCop0, resultValueId: 0, register: 12),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteCop0, inputValueA: 0, register: 14),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReturnFromException),
        ], new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
        RecompilerIrValidator.Validate(new RecompilerIrProgram([valid])).IsValid.Should().BeTrue();

        var malformed = new RecompilerIrBlock(EntryPc,
        [
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteCop0, register: 12),
        ], new RecompilerIrExit(RecompilerIrTerminationReason.Success, EntryPc + 4));
        RecompilerIrValidator.Validate(new RecompilerIrProgram([malformed])).Diagnostics
            .Should().ContainSingle(d => d.Code == RecompilerIrDiagnosticCode.InvalidOperationShape);
    }

    internal static uint Lui(byte rt, ushort immediate) => MipsEncoding.I(0x0F, rt: rt, rs: 0, immediate: immediate);

    internal static uint Ori(byte rt, byte rs, ushort immediate) => MipsEncoding.I(0x0D, rt: rt, rs: rs, immediate: immediate);

    internal static uint Mfc0(byte rt, byte rd) => 0x40000000u | ((uint)rt << 16) | ((uint)rd << 11);

    internal static uint Mtc0(byte rt, byte rd) => 0x40800000u | ((uint)rt << 16) | ((uint)rd << 11);

    internal const uint Rfe = 0x42000010u;

    internal static RecompilerIrProgram Lower(uint[] words)
    {
        var program = MipsToIrLowerer.LowerProgram(words
            .Select((word, index) => (R3000aDecoder.Decode(word), EntryPc + (uint)(index * 4)))
            .ToArray());
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();
        return program;
    }

    /// <summary>
    /// Runs <paramref name="words"/> on the native interpreter and the generated host (the differential runner)
    /// and on the IR evaluator, asserts all three agree on the GPRs and the data window, and returns the IR run.
    /// Every program ends on an instruction that settles a trailing load delay inside the program.
    /// </summary>
    internal static (IReadOnlyList<uint> Gpr, IReadOnlyList<uint>? Cop0, byte[] Memory) RunThreeWay(
        string name, uint[] words, uint windowBytes = 0)
    {
        var window = Enumerable.Range(0, (int)windowBytes).Select(i => DataBase + (uint)i).ToArray();
        var count = (uint)words.Length;
        var fixture = new RecompilerDifferentialFixture(
            name, words, EntryPc, stepBudget: count, memoryWindow: window, referenceStepBudget: count);

        var differential = RecompilerDifferentialRunner.Run(fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());
        Assert.True(differential.BothCompleted, $"[{differential.Actual.DiagnosticCode}] {differential.Actual.DiagnosticMessage}");
        Assert.True(differential.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(differential));
        var reference = differential.Reference.Snapshot!;
        reference.Termination.Should().Be(RecompilerIrTerminationReason.Success);

        var memory = new RecompilerGuestMemory();
        var ir = RecompilerIrEvaluator.Run(Lower(words), EntryPc, new uint[32], memory, blockBudget: count);
        ir.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        ir.Gpr.Should().Equal(reference.Gpr, "the IR evaluator must agree with the native interpreter");

        var irWindow = window.Select(memory.Read8).ToArray();
        irWindow.Should().Equal(reference.Memory.Select(observation => (byte)observation.Value),
            "the IR evaluator must agree with the native interpreter on guest memory");
        return (ir.Gpr, ir.Cop0, irWindow);
    }
}

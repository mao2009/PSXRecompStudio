using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Issue #663: the kernel SYSCALL boundary (SYS(02h) ExitCriticalSection) — the
/// shared contract, the interpreter that applies it through the native CPU, and
/// the generated-host artifact that applies it to its own state.
/// </summary>
[Test]
public sealed class BiosKernelSyscallTests
{
    private const uint Entry = 0x80001000u;
    private const uint IrqMaskAndEnableBits = (1u << 2) | (1u << 10);

    // ---- Shared contract -------------------------------------------------

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x00000008u)] // exception entry of SR=2: KUc pushed to KUp
    [InlineData(0x40000000u)] // COP0 usable bit and unrelated high bits survive
    [InlineData(0xFFFFFBFBu)] // both target bits clear, everything else set
    [InlineData(0xFFFFFFFFu)]
    public void ExitCriticalSection_SetsSrBit2AndBit10_AndChangesNothingElse(uint srAtEntry)
    {
        var outcome = BiosKernelSyscallDispatch.Dispatch((uint)BiosKernelSyscall.ExitCriticalSection, srAtEntry);

        outcome.Handled.Should().BeTrue();
        outcome.SrAtReturn.Should().Be(srAtEntry | IrqMaskAndEnableBits);
        outcome.DiagnosticCode.Should().BeNull();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)] // EnterCriticalSection: real, but not implemented yet — must not be a silent success
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void UnimplementedSyscall_FailsClosed(uint number)
    {
        var outcome = BiosKernelSyscallDispatch.Dispatch(number, 0x0000_0400u);

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosKernelSyscallDispatch.UnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain($"SYS({number:X2}h)");
    }

    // ---- Interpreter -----------------------------------------------------

    private static uint Mfc0(R3000aRegister rt, byte rd) => 0x40000000u | ((uint)rt << 16) | ((uint)rd << 11);

    private static uint Mtc0(R3000aRegister rt, byte rd) => 0x40800000u | ((uint)rt << 16) | ((uint)rd << 11);

    private static uint Ori(R3000aRegister rt, R3000aRegister rs, ushort imm) =>
        MipsEncoding.I(0x0D, (byte)rt, (byte)rs, imm);

    private sealed class NullSink : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }

    private sealed class ExitHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot snapshot) =>
            TitleExecutionHandoffResult.Exit();
    }

    private static TitleExecutionRequest Request(
        uint entry, uint[]? gpr = null, uint hi = 0, uint lo = 0, uint segment = 64) =>
        new(entry, gpr ?? new uint[TitleExecutionRequest.GprCount], hi, lo, [], outerBudget: 1, segmentBudget: segment);

    private static TitleExecutionResult RunInterpreter(
        uint[] words, bool withBios = true, uint[]? gpr = null, uint hi = 0, uint lo = 0)
    {
        using var engine = new InterpreterTitleExecutionEngine(
            words,
            Entry,
            withBios ? (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer) : null);
        return new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request(Entry, gpr, hi, lo));
    }

    [Fact]
    public void Interpreter_ExitCriticalSection_SetsSrThroughTheCpu_AndReturnsAfterTheSyscall()
    {
        // SR=2 (KUc) first, so the result proves the entry push, the kernel's
        // bit 2 / bit 10 set, and the RFE pop compose: SR | 0x401 on return.
        uint[] words =
        [
            Ori(R3000aRegister.T0, R3000aRegister.Zero, 0x0002),
            Mtc0(R3000aRegister.T0, 12),
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Syscall(),
            Mfc0(R3000aRegister.S2, 12),
            Ori(R3000aRegister.S1, R3000aRegister.Zero, 0x0055),
            MipsEncoding.Nop,
        ];
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.S3] = 0xA5A5A5A5u;
        gpr[(int)R3000aRegister.K0] = 0x1234u;
        gpr[(int)R3000aRegister.Ra] = 0x80001234u;

        var result = RunInterpreter(words, gpr: gpr, hi: 0x11111111u, lo: 0x22222222u);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var snap = result.FinalSnapshot!;
        snap.Gpr[(int)R3000aRegister.S2].Should().Be(0x00000403u, "SR = 0x2 | 0x401 (IEc, IM2 set; KUc kept)");
        snap.Gpr[(int)R3000aRegister.S1].Should().Be(0x55u, "execution resumed at the instruction after SYSCALL");
        snap.PC.Should().Be(Entry + (uint)words.Length * 4);

        // Nothing but SR changes: $a0 is the syscall argument, no return value.
        snap.Gpr[(int)R3000aRegister.A0].Should().Be(2u);
        snap.Gpr[(int)R3000aRegister.V0].Should().Be(0u);
        snap.Gpr[(int)R3000aRegister.V1].Should().Be(0u);
        snap.Gpr[(int)R3000aRegister.S3].Should().Be(0xA5A5A5A5u);
        snap.Gpr[(int)R3000aRegister.K0].Should().Be(0x1234u);
        snap.Gpr[(int)R3000aRegister.Ra].Should().Be(0x80001234u);
        snap.HI.Should().Be(0x11111111u);
        snap.LO.Should().Be(0x22222222u);
    }

    [Fact]
    public void Interpreter_UnknownSyscall_StopsWithDiagnostic_NotASilentSuccess()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0001),
            MipsEncoding.Syscall(),
            Ori(R3000aRegister.S1, R3000aRegister.Zero, 0x0055),
            MipsEncoding.Nop,
        ];

        var result = RunInterpreter(words);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be(BiosKernelSyscallDispatch.UnsupportedDiagnosticCode);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(0u, "the code after the syscall must not run");
    }

    [Fact]
    public void Interpreter_WithoutRuntime_SyscallStaysACpuException()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Syscall(),
            MipsEncoding.Nop,
        ];

        var result = RunInterpreter(words, withBios: false);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION");
    }

    [Fact]
    public void Interpreter_BreakIsNotAbsorbedByTheSyscallDispatcher()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Break(),
            MipsEncoding.Nop,
        ];

        var result = RunInterpreter(words);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION");
    }

    [Fact]
    public void Interpreter_SyscallInABranchDelaySlot_StaysACpuException()
    {
        // EPC would be the branch, not the syscall; that return is not modelled.
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Jump(Entry + 16),
            MipsEncoding.Syscall(),
            MipsEncoding.Nop,
            MipsEncoding.Nop,
        ];

        var result = RunInterpreter(words);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION");
    }

    // ---- Generated host --------------------------------------------------

    // The production discovery path (#669): the code after a SYSCALL exists only if
    // reachable-program discovery compiles it, so the tests must not hand-lower it.
    private static RecompilerIrProgram Lower(uint[] words) =>
        ReachableProgramBuilder.Build(Entry, words, Entry);

    private static TitleExecutionResult RunGeneratedHost(uint[] words, out string directory)
    {
        var dir = new TempDirectory();
        directory = dir.FullPath;
        using var engine = new RecompiledHostExecutionEngine(
            Lower(words),
            words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer));
        return new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request(Entry));
    }

    [Fact]
    public void GeneratedHost_ExitCriticalSection_ResumesAfterTheSyscall_WithNoStaleException()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Syscall(),
            Ori(R3000aRegister.S1, R3000aRegister.Zero, 0x0055),
            MipsEncoding.Nop,
        ];

        var result = RunGeneratedHost(words, out _);

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        var snap = result.FinalSnapshot!;
        snap.Gpr[(int)R3000aRegister.S1].Should().Be(0x55u);
        snap.Gpr[(int)R3000aRegister.V0].Should().Be(0u);
        snap.Exception.IsRaised.Should().BeFalse("the completed SYSCALL exception is consumed");
    }

    [Fact]
    public void GeneratedHost_UnknownSyscall_StopsWithDiagnostic()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0004),
            MipsEncoding.Syscall(),
            Ori(R3000aRegister.S1, R3000aRegister.Zero, 0x0055),
            MipsEncoding.Nop,
        ];

        var result = RunGeneratedHost(words, out _);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be(BiosKernelSyscallDispatch.UnsupportedDiagnosticCode);
        var snap = result.FinalSnapshot!;
        snap.Gpr[(int)R3000aRegister.S1].Should().Be(0u);
        snap.Exception.IsRaised.Should().BeTrue("a declined syscall never returns, so its exception entry is not consumed");
        snap.Exception.Code.Should().Be(0x08u);
    }

    [Fact]
    public void GeneratedHost_BreakIsNotAbsorbed_AndKeepsItsExceptionExit()
    {
        uint[] words =
        [
            Ori(R3000aRegister.A0, R3000aRegister.Zero, 0x0002),
            MipsEncoding.Break(),
            MipsEncoding.Nop,
        ];

        var result = RunGeneratedHost(words, out _);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION");
    }
}

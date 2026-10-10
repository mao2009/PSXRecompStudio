using FluentAssertions;
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
/// Issue #732: a guest firmware (OpenBIOS-shaped, synthetic) executed through the generated host. The ROM is compiled
/// natively; the exception handler and a routine it writes to RAM at run time have no block and run in the
/// mixed-execution fallback, which is reported as fallback, never as native. The same ROM on the firmware interpreter
/// is the reference.
/// </summary>
[Test]
public sealed class FirmwareGeneratedHostTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint RomData = RomBase + 0x400;
    private const uint DataWord = 0xCAFEF00Du;
    private const uint RamRoutine = 0x80001000u;
    private const uint GeneralVector = 0x80000080u;
    private const R3000aRegister K0 = (R3000aRegister)26;

    /// <summary>
    /// Reset: read a ROM data word through KSEG1 and its KSEG0 alias, install a RAM exception handler and a RAM routine
    /// that itself executes SYSCALL, clear SR (BEV = 0), SYSCALL from ROM, call the RAM routine, then spin.
    /// The handler counts its entries in $s4 and returns to EPC + 4.
    /// </summary>
    private static uint[] Rom()
    {
        var reset = new Block(RomBase);
        reset.Emit(Li(T0, RomData), [Lw(T1, T0, 0), Nop]);
        reset.Emit(Li(T2, RomData - 0x20000000u), [Lw(T3, T2, 0), Nop]);
        Install(reset, GeneralVector,
        [
            Mfc0(K0, 14), Nop, Addiu(S4, S4, 1), Addiu(K0, K0, 4), Jr(K0), RecompilerCop0Tests.Rfe,
        ]);
        Install(reset, RamRoutine, [MipsEncoding.Syscall(), Ori(S2, Zero, 0x55), Jr(Ra), Nop]);
        reset.Emit(Mtc0(Zero, 12), MipsEncoding.Syscall(), Ori(S1, Zero, 0x66));
        reset.Emit(Li(T9, RamRoutine), [Jalr(T9), Nop, Ori(S3, Zero, 0x77)]);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);

        var words = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) words[i] = reset.Words[i];
        words[(RomData - RomBase) / 4] = DataWord;
        return words;
    }

    private static void Install(Block block, uint address, uint[] code)
    {
        block.Emit(Li(A0, address));
        for (var i = 0; i < code.Length; i++)
        {
            block.Emit(Li(T5, code[i]), [Sw(T5, A0, (short)(i * 4))]);
        }
    }

    /// <summary>The code part of a ROM (the words before its data), as the CLI's --code-bytes selects it.</summary>
    private static uint[] Code(uint[] rom) => rom[..(int)((RomData - RomBase) / 4)];

    private static TitleExecutionRequest Request(uint budget) =>
        new(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    [Fact]
    public void GeneratedHost_RunsTheRomNatively_AndRunTimeRamCodeAsCountedFallback_LikeTheInterpreter()
    {
        var rom = Rom();
        using var dir = new TempDirectory();
        var image = ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []);
        var fetched = new List<uint>();
        using var engine = new RecompiledHostExecutionEngine(
            image.Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true)
        {
            FallbackFetchObserver = (_, pc) => fetched.Add(pc),
        };

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(2_000));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(2_000));

        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        var gpr = host.FinalSnapshot!.Gpr;
        gpr[(int)T1].Should().Be(DataWord, "a ROM load through KSEG1 is served from the artifact's ROM window");
        gpr[(int)T3].Should().Be(DataWord, "the KSEG0 alias reaches the same ROM bytes");
        gpr[(int)S4].Should().Be(2u, "both SYSCALLs (native, and inside the fallback) entered the guest's RAM vector");
        gpr[(int)S2].Should().Be(0x55u);
        gpr[(int)S1].Should().Be(0x66u);
        gpr[(int)S3].Should().Be(0x77u, "the fallback handed control back to the ROM after the RAM routine's own SYSCALL");
        foreach (var r in new[] { T1, T3, S1, S2, S3, S4 })
        {
            gpr[(int)r].Should().Be(reference.FinalSnapshot!.Gpr[(int)r], $"{r} matches the firmware interpreter");
        }

        engine.NativeRetiredInstructions.Should().BeGreaterThan(0);
        var evidence = engine.FallbackEvidence!;
        evidence.Returns.Should().BeGreaterThanOrEqualTo(2);
        // Every retired fallback instruction was fetched; the RAM routine's SYSCALL is fetched but retires nothing.
        evidence.FallbackInstructions.Should().Be((ulong)fetched.Count - 1);
        evidence.Targets.Select(t => t.Target).Should().Contain([GeneralVector, RamRoutine]);
        fetched.Should().NotContain(pc => pc >= RomBase, "ROM code with a block is native, never counted as fallback");
    }

    [Theory]
    [InlineData(false, 3, false)]
    [InlineData(true, 3, false)]
    [InlineData(false, 8, false)]
    [InlineData(true, 8, false)]
    [InlineData(false, 3, true)]
    [InlineData(true, 3, true)]
    public void Cop0OtherRegisters_AgreeAcrossNativeFallbackTransitions(bool writeInRam, byte cop0Register, bool delaySlot)
    {
        const uint value = 0x12345678;
        var reset = new Block(RomBase);
        uint[] routine = writeInRam
            ? [.. Li(T0, value), Mtc0(T0, cop0Register), Jr(Ra), Nop]
            : [Mfc0(S0, cop0Register), Nop, Jr(Ra), Nop];
        if (delaySlot && writeInRam)
            routine = [.. Li(T0, value), Jr(Ra), Mtc0(T0, cop0Register)];
        if (delaySlot && !writeInRam)
            routine = [Jr(Ra), Mfc0(S0, cop0Register)];
        Install(reset, RamRoutine, routine);
        if (!writeInRam)
        {
            reset.Emit(Li(T0, value));
            if (delaySlot) reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here + 8), Mtc0(T0, cop0Register));
            else reset.Emit(Mtc0(T0, cop0Register));
        }
        reset.Emit(Li(T9, RamRoutine), [Jalr(T9), Nop]);
        if (writeInRam) reset.Emit(Mfc0(S0, cop0Register), Nop);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);
        var rom = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) rom[i] = reset.Words[i];
        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []).Program,
            rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true);
        var host = new ExecutionOrchestrator().Execute(engine, null, Request(500));
        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, null, Request(500));

        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        reference.FinalSnapshot!.Gpr[(int)S0].Should().Be(value);
        host.FinalSnapshot!.Gpr[(int)S0].Should().Be(reference.FinalSnapshot.Gpr[(int)S0]);
    }

    [Fact]
    public void FirmwareCop0_ResetPridMatchesTheNativeCoreInBothEngines()
    {
        var reset = new Block(RomBase);
        Install(reset, RamRoutine, [Mfc0(S0, 15), Nop, Jr(Ra), Nop]);
        reset.Emit(Mfc0(S1, 15), Nop);
        reset.Emit(Li(T9, RamRoutine), [Jalr(T9), Nop]);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);
        var rom = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) rom[i] = reset.Words[i];
        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []).Program,
            rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true);
        var host = new ExecutionOrchestrator().Execute(engine, null, Request(500));
        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, null, Request(500));
        foreach (var reg in new[] { S0, S1 })
            host.FinalSnapshot!.Gpr[(int)reg].Should().Be(reference.FinalSnapshot!.Gpr[(int)reg]);
        host.FinalSnapshot!.Gpr[(int)S0].Should().Be(host.FinalSnapshot.Gpr[(int)S1]);
    }

    [Fact]
    public void GeneratedHost_DropsAStoreToTheRom()
    {
        var reset = new Block(RomBase);
        reset.Emit(Li(T0, RomData), Li(T4, 0x12345678u), [Sw(T4, T0, 0), Lw(S0, T0, 0), Nop]);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);
        var rom = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) rom[i] = reset.Words[i];
        rom[(RomData - RomBase) / 4] = DataWord;
        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []).Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath);

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(100));

        result.FinalSnapshot!.Gpr[(int)S0].Should().Be(DataWord, "the BIOS ROM is not writable");
    }

    [Fact]
    public void GuestFirmware_RefusesABiosHleRuntime()
    {
        using var dir = new TempDirectory();
        var rom = new uint[] { Nop, Nop };
        var create = () => new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []).Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer), guestFirmware: true);

        create.Should().Throw<ArgumentException>();
    }
}

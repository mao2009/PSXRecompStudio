using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #739 Phase 2 Step B (BIOS handoff): a guest firmware (OpenBIOS-shaped, synthetic) runs through the
/// <em>standalone embedded-image</em> artifact — the ROM image and the run's initial state are compiled into the
/// generated C source, so no <c>artifact-input.txt</c>/<c>artifact-image.bin</c> are written and the artifact attaches
/// the shared Runtime host bridge over <c>--host-transfer</c>. The firmware interpreter on the same ROM is the
/// reference; ROM code with a block is native, code the firmware runs from RAM without a pre-generated version is
/// counted fallback, and a pre-generated RAM executable the firmware hands control to runs natively.
/// </summary>
[Test]
public sealed class FirmwareStandaloneHandoffTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint RomData = RomBase + 0x400;
    private const uint DataWord = 0xCAFEF00Du;
    private const uint RamRoutine = 0x80001000u;
    private const uint ExecutableEntry = 0x80010000u;
    private const uint GeneralVector = 0x80000080u;
    private const R3000aRegister K0 = (R3000aRegister)26;

    private static uint Beq(uint pc, uint target) => MipsEncoding.Branch(0x04, 0, 0, pc, target);

    private static void Install(Block block, uint address, uint[] code)
    {
        block.Emit(Li(A0, address));
        for (var i = 0; i < code.Length; i++)
        {
            block.Emit(Li(T5, code[i]), [Sw(T5, A0, (short)(i * 4))]);
        }
    }

    /// <summary>The code part of a ROM (the words before its data), as the CLI's <c>--code-bytes</c> selects it.</summary>
    private static uint[] Code(uint[] rom) => rom[..(int)((RomData - RomBase) / 4)];

    private static uint[] Pack(Block reset)
    {
        var words = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) words[i] = reset.Words[i];
        return words;
    }

    private static TitleExecutionRequest Request(uint budget) =>
        new(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    /// <summary>
    /// The embedded-image artifact must not write the file-driven input: the file-driven
    /// engine stages <c>artifact-input.txt</c>/<c>artifact-image.bin</c> in its output
    /// directory, and the standalone path is exactly the one that does not.
    /// </summary>
    private static void AssertNoFileDrivenInputs(TempDirectory dir)
    {
#pragma warning disable AARC003
        File.Exists(dir.Combine("artifact-input.txt")).Should().BeFalse("the standalone artifact embeds its state");
        File.Exists(dir.Combine("artifact-image.bin")).Should().BeFalse("the standalone artifact embeds its ROM image");
#pragma warning restore AARC003
    }

    [Fact]
    public void Standalone_BootsTheFirmwareNatively_WithCountedRamFallback_LikeTheInterpreter_AndWritesNoInputFiles()
    {
        // Reset: read a ROM data word through KSEG1 and its KSEG0 alias, install a RAM exception handler and a RAM
        // routine that itself executes SYSCALL, clear SR (BEV = 0), SYSCALL from ROM, call the RAM routine, then spin.
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
        reset.Emit(Beq(reset.Here, reset.Here), Nop);
        var rom = Pack(reset);
        rom[(RomData - RomBase) / 4] = DataWord;

        using var dir = new TempDirectory();
        var image = ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []);
        var fetched = new List<uint>();
        using var engine = new RecompiledHostExecutionEngine(
            image.Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, embedImage: true)
        {
            FallbackFetchObserver = (_, pc) => fetched.Add(pc),
        };

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(2_000));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(2_000));

        AssertNoFileDrivenInputs(dir);
        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        var gpr = host.FinalSnapshot!.Gpr;
        gpr[(int)T1].Should().Be(DataWord, "a ROM load through KSEG1 is served from the artifact's ROM window");
        gpr[(int)T3].Should().Be(DataWord, "the KSEG0 alias reaches the same ROM bytes");
        gpr[(int)S4].Should().Be(2u, "both SYSCALLs (native, and inside the fallback) entered the guest's RAM vector");
        gpr[(int)S3].Should().Be(0x77u, "the fallback handed control back to the ROM after the RAM routine's own SYSCALL");
        gpr.Should().Equal(reference.FinalSnapshot!.Gpr, "the standalone artifact must agree with the firmware interpreter");

        engine.NativeRetiredInstructions.Should().BeGreaterThan(0, "ROM code with a block runs natively, never as fallback");
        var evidence = engine.FallbackEvidence!;
        evidence.Returns.Should().BeGreaterThanOrEqualTo(2);
        evidence.FallbackInstructions.Should().Be((ulong)fetched.Count - 1);
        evidence.Targets.Select(t => t.Target).Should().Contain([GeneralVector, RamRoutine]);
        fetched.Should().NotContain(pc => pc >= RomBase, "ROM code with a block is native, never counted as fallback");
    }

    [Fact]
    public void Standalone_BiosHandsControlToAPreGeneratedRamExecutable_ThatRunsNatively_LikeTheInterpreter()
    {
        // A synthetic PS-X EXE: the firmware copies it to 0x80010000 and jumps to it; the same bytes are given to the
        // artifact ahead of time as a loaded image, so the handoff entry runs natively instead of in the fallback.
        uint[] executable = [Addiu(S2, S2, 0x11), Ori(S3, Zero, 0x99), Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, ExecutableEntry, executable);
        reset.Emit(Li(T9, ExecutableEntry), [Jalr(T9), Nop, Ori(S1, Zero, 0x66)]);
        reset.Emit(Beq(reset.Here, reset.Here), Nop);
        var rom = Pack(reset);

        using var dir = new TempDirectory();
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(ExecutableEntry, executable, [ExecutableEntry], new HashSet<uint>())]);
        var fetched = new List<uint>();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, Code(rom), RomBase, []).Program,
            rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, loadedCode: loaded, embedImage: true)
        {
            FallbackFetchObserver = (_, pc) => fetched.Add(pc),
        };

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(1_000));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(1_000));

        AssertNoFileDrivenInputs(dir);
        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        var gpr = host.FinalSnapshot!.Gpr;
        gpr[(int)S2].Should().Be(0x11u, "the handed-off PS-X EXE ran to completion");
        gpr[(int)S3].Should().Be(0x99u);
        gpr[(int)S1].Should().Be(0x66u, "control returned from the executable to the firmware");
        gpr.Should().Equal(reference.FinalSnapshot!.Gpr, "the standalone artifact must agree with the firmware interpreter");

        engine.NativeRetiredInstructions.Should().BeGreaterThan(0);
        fetched.Should().NotContain(ExecutableEntry, "the pre-generated executable has a current block and runs natively");
        (engine.FallbackEvidence?.Targets.Select(t => t.Target) ?? [])
            .Should().NotContain(ExecutableEntry, "the BIOS -> executable handoff is native, never reported as fallback");
    }
}

using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecompStudio.Services;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Tests;

// Issue #380 / ADR-015: the production execution path must be reachable from a
// production assembly. These tests drive it end to end — production engine,
// production orchestrator, real BIOS HLE dispatch — with no test-only engine.
[Test]
public class TitleExecutionServiceTests
{
    [Fact]
    public void RunDiagnostic_DrivesTheWholePipeline_ToAClassifiedCompletion()
    {
        var run = new TitleExecutionService().RunDiagnostic();

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Result.EngineName.Should().Be(InterpreterTitleExecutionEngine.EngineName);
        run.Result.SegmentsRetired.Should().Be(1);
        run.Result.DiagnosticCode.Should().BeNull();

        // The BIOS handoff actually ran: putchar emitted its byte, and the guest
        // resumed at the instruction after the call rather than stopping there.
        run.Output.Should().Equal(TitleExecutionService.DiagnosticCharacter);
        run.Result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1]
            .Should().Be(TitleExecutionService.DiagnosticMarker);
        run.Frame.Width.Should().Be(256);
        run.Frame.Height.Should().Be(240);
        run.Frame.ComputeStableHash().Should().HaveCount(32);
    }

    // Issue #279: A0:39 InitHeap(addr,size) is the first BIOS call real-ROM crt0
    // startup code makes on every locally-observed executable
    // (docs/runtime/bios-hle-evidence.md §3.4, 5 of 5). Before this call was
    // registered, a real title reaching it here stopped at
    // BIOS_HLE_UNSUPPORTED_CALL instead of Completed. InitHeap has no documented
    // return value, so this also proves the production path's live BIOS trap
    // honours a null ReturnValue by leaving $v0 untouched, not just that the call
    // is accepted.
    [Fact]
    public void RunDiagnostic_InitHeap_DrivesTheWholePipeline_ToAClassifiedCompletion_WithV0Untouched()
    {
        const uint entry = 0x80001000u;
        const uint v0Marker = 0xBEEFu;

        var run = new TitleExecutionService().Run(
            [
                Immediate(0x0D, (byte)R3000aRegister.V0, v0Marker), // ori $v0, $zero, 0xBEEF
                Immediate(0x0D, (byte)R3000aRegister.A0, 0x1000u),  // ori $a0, $zero, addr
                Immediate(0x0D, (byte)R3000aRegister.A1, 0x2000u),  // ori $a1, $zero, size
                Immediate(0x0D, (byte)R3000aRegister.T1, 0x39u),    // ori $t1, $zero, InitHeap
                (uint)0x03 << 26 | (0x800000A0u & 0x0FFFFFFCu) >> 2, // jal 0xA0
                0u, // nop (branch delay slot); resumed here, then PC == end -> Completed
            ],
            entry,
            outerBudget: 4,
            segmentBudget: 64);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Result.EngineName.Should().Be(InterpreterTitleExecutionEngine.EngineName);
        run.Result.DiagnosticCode.Should().BeNull();
        run.Output.Should().BeEmpty("InitHeap has no TTY side effect");
        run.Result.FinalSnapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(v0Marker,
            "InitHeap has no documented return value, so $v0 must be left untouched");
    }

    private static uint Immediate(byte opcode, byte rt, uint immediate) =>
        (uint)opcode << 26 | (uint)rt << 16 | (immediate & 0xFFFFu);

    [Fact]
    public void Run_TransferToAnAddressWithNoRule_IsClassifiedAsUnsupported_NotAsSuccess()
    {
        // A single `jal` into the middle of nowhere: the guest leaves the program
        // image at an address the handoff has no continuation rule for.
        const uint entry = 0x80001000u;
        var run = new TitleExecutionService().Run(
            [0x0C000000u | (0x80002000u & 0x0FFFFFFCu) >> 2, 0u],
            entry,
            outerBudget: 2,
            segmentBudget: 16);

        run.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        run.Result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
    }

    [Fact]
    public void MainWindowViewModel_RunDiagnosticTitleCommand_ReportsTheOutcome()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.ExecutionStatus.Should().Be("Not run");
        viewModel.RunDiagnosticTitleCommand.Execute(null);

        viewModel.ExecutionStatus.Should().Contain(nameof(TitleExecutionState.Completed));
        viewModel.ExecutionStatus.Should().Contain(InterpreterTitleExecutionEngine.EngineName);
        viewModel.ExecutionStatus.Should().Contain("\"P\"");
    }

    // Issue #409: synthetic PS-X EXE e2e through the production execution path.

    [Fact]
    public void Run_SyntheticExe_EntryAtTextStart_LoadsImageAndEmitsExpectedBytes()
    {
        // The program reads its own first instruction word from the loaded image
        // (proving the bytes landed at TextStart), then prints the GP-initial low
        // byte (proving header-derived register state propagated), then falls off
        // the end of the text region → Completed.
        //
        // TextStart = 0x80010000
        // First word = lui $t6, 0x8001 = 0x3C0E8001 → low byte 0x01.
        // GpInitial = 0xAAAABBCC → low byte 0xCC.
        const uint textStart = 0x80010000u;
        const uint gpInitial = 0xAAAABBCCu;
        var exe = BuildSyntheticExe(
            textStart,
            entryPoint: textStart,
            spInitial: 0x801FFF00,
            gpInitial: gpInitial,
            // word0: lui $t6, 0x8001               (0x3C0E8001)  — starts at TextStart; first-word oracle
            // word1: lw  $a0, 0($t6)                — loads word0 into $a0 (load delay: commits next step)
            // word2: nop                            — load-delay slot
            // word3: andi $a0, $a0, 0x00FF          — isolate low byte → 0x01
            // word4: ori  $t1, $zero, 0x3C          — $t1 = putchar
            // word5: jal  0x800000A0                — BIOS A0 vector
            // word6: nop                            — delay slot
            // word7: ori  $t1, $zero, 0x3C          — $t1 = putchar (second call)
            // word8: add  $a0, $zero, $gp           — $a0 = gp
            // word9: andi $a0, $a0, 0x00FF          — low byte of gp → 0xCC
            // word10: jal  0x800000A0               — BIOS A0 vector
            // word11: nop                           — delay slot; then PC == textEnd → Completed
            [
                0x3C0E8001u,
                0x8DC40000u,    // lw $a0, 0($t6)
                0u,             // nop (load delay)
                0x308400FFu,    // andi $a0, $a0, 0xFF
                0x3409003Cu,    // ori $t1, $zero, 0x3C
                0x0C000028u,    // jal 0xA0
                0u,
                0x3409003Cu,
                0x001C2020u,    // add $a0, $zero, $gp
                0x308400FFu,
                0x0C000028u,
                0u,
            ]);

        var run = new TitleExecutionService().Run(exe, outerBudget: 4, segmentBudget: 256);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Result.EngineName.Should().Be(InterpreterTitleExecutionEngine.EngineName);
        run.Result.FinalSnapshot.Should().NotBeNull();
        run.Output.Should().Equal([0x01, 0xCC]);
    }

    [Fact]
    public void Run_SyntheticExe_EntryInsideTextRegion_ExecutesFromEntry()
    {
        // Same program but entry is at TextStart+4 (second word). The first word
        // is a filler (never executed). The lw reads from TextStart+4 (the lui
        // instruction = its own position), proving the full image was loaded at
        // TextStart and that execution entered at the declared entry point.
        const uint textStart = 0x80010000u;
        const uint gpInitial = 0xAAAABBCCu;
        var exe = BuildSyntheticExe(
            textStart,
            entryPoint: textStart + 4,
            spInitial: 0x801FFF00,
            gpInitial: gpInitial,
            [
                0xFFFFFFFFu,     // word0 filler (never executed)
                0x3C0E8001u,     // word1: lui $t6, 0x8001   — entry starts here
                0x8DC40004u,     // word2: lw $a0, 4($t6)    — reads word1 (lui) → 0x3C0E8001 (load delay)
                0u,              // word3: nop (load-delay slot)
                0x308400FFu,     // word4: andi $a0, $a0, 0xFF
                0x3409003Cu,     // word5: ori $t1, $zero, 0x3C
                0x0C000028u,     // word6: jal 0xA0
                0u,              // word7: nop (branch delay slot)
                0x3409003Cu,     // word8: ori $t1, $zero, 0x3C
                0x001C2020u,     // word9: add $a0, $zero, $gp
                0x308400FFu,     // word10: andi $a0, $a0, 0xFF
                0x0C000028u,     // word11: jal 0xA0
                0u,              // word12: nop (branch delay slot; then PC == textEnd)
            ]);

        var run = new TitleExecutionService().Run(exe, outerBudget: 4, segmentBudget: 256);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Result.FinalSnapshot!.PC.Should().Be(textStart + 13 * 4, "execution reaches the end of the text region");
        run.Output.Should().Equal([0x01, 0xCC]);
    }

    // ---- helpers ----

    private static PsxExe BuildSyntheticExe(
        uint textStart,
        uint entryPoint,
        uint spInitial,
        uint gpInitial,
        uint[] words)
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + words.Length * 4];

        // Write magic "PS-X EXE" at offset 0.
        Buffer.BlockCopy(BitConverter.GetBytes(PsxExeHeader.Magic), 0, fileContent, 0, 8);

        // Header fields at their standard offsets.
        BitConverter.GetBytes(entryPoint).CopyTo(fileContent, 0x10);
        BitConverter.GetBytes(gpInitial).CopyTo(fileContent, 0x14);
        BitConverter.GetBytes(textStart).CopyTo(fileContent, 0x18);
        BitConverter.GetBytes((uint)(words.Length * 4)).CopyTo(fileContent, 0x1C);
        BitConverter.GetBytes(spInitial).CopyTo(fileContent, 0x30);

        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(fileContent, PsxExeHeader.HeaderSize + i * 4);
        }

        return PsxExe.Load(fileContent, "SLUS_TEST.EXE");
    }
}

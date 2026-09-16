using PSXRecomp.Core.Cpu;
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
    }

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
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PSXRecomp.Core.Execution;
using PSXRecompStudio.ViewModels;
using PSXRecompStudio.Views;

namespace PSXRecompStudio.Tests;

// Issue #179: high-value GUI workflow coverage. Startup (scenario 1) is already
// covered by StartupWorkflowTests/MainWindowSmokeTests; Project/Analysis/Recompile/
// Approval workflow screens (#40/#128/#133) do not exist in the GUI yet, so they are
// not tested here. The workflow that IS shipped and user-reachable is title execution
// (#380 / ADR-015 and #409): a user clicks a button on the main window and observes a
// classified outcome. These tests drive that through the real window's controls — a
// simulated mouse click, not a direct ViewModel call — so a broken XAML binding or
// command wiring fails the suite. No fixed sleeps, no real disc/network/compiler:
// outcomes are awaited through state transitions / the command's own task.
[Test]
public class GUIWorkflowTests
{
    [AvaloniaFact]
    public void RunDiagnosticTitleButton_Click_ShowsCompletedOutcomeOnScreen()
    {
        var window = ShowMainWindow(out _);

        var status = window.FindControl<TextBlock>("ExecutionStatusText")!;
        status.Text.Should().Be("Not run", "the visible status starts in its idle state");

        Click(window, window.FindControl<Button>("RunDiagnosticTitleButton")!);

        status.Text.Should().Contain(nameof(TitleExecutionState.Completed));
        status.Text.Should().Contain(InterpreterTitleExecutionEngine.EngineName);
        status.Text.Should().Contain("\"P\"", "the BIOS putchar output must reach the user-visible status");
    }

    [AvaloniaFact]
    public void RunRealTitleButton_Click_WithoutLoadedDisc_ShowsValidationMessageOnScreen()
    {
        var window = ShowMainWindow(out var viewModel);
        viewModel.DiscImageBytes = null;

        var status = window.FindControl<TextBlock>("RealTitleExecutionStatusText")!;
        status.Text.Should().Be("Not run");

        Click(window, window.FindControl<Button>("RunRealTitleButton")!);

        status.Text.Should().Contain("Load a disc image",
            "the user must be told why the real-title action cannot proceed");
    }

    [AvaloniaFact]
    public async Task RunRealTitleButton_Click_WithLoadedDisc_RunsAnalyzedTitleToCompletion()
    {
        var discImageBytes = SyntheticDiscImage.BuildExecIso(ExecutableThatRunsToCompletion());
        var window = ShowMainWindow(out var viewModel);
        viewModel.DiscImageBytes = discImageBytes;

        Click(window, window.FindControl<Button>("RunRealTitleButton")!);
        await viewModel.RunRealTitleCommand.ExecutionTask!;
        Dispatcher.UIThread.RunJobs();

        window.FindControl<TextBlock>("RealTitleExecutionStatusText")!.Text
            .Should().Contain(nameof(TitleExecutionState.Completed));
    }

    [AvaloniaFact]
    public async Task RunRealTitleButton_Click_WhenExecutableLayoutRejected_ShowsFailureOnScreen()
    {
        // Text region [0x7FFFFFF0, 0x80000010) straddles the KUSEG/KSEG0 boundary:
        // analysis passes but the production execution layout check rejects the
        // executable, and the command must surface that instead of throwing.
        var rejectedExe = SyntheticDiscImage.BuildSyntheticExe(
            0x7FFFFFF0u, entryPoint: 0x7FFFFFF0u, spInitial: 0x801FFF00u,
            gpInitial: 0xAAAABBCCu,
            [0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x03E00008u]);
        var window = ShowMainWindow(out var viewModel);
        viewModel.DiscImageBytes = SyntheticDiscImage.BuildExecIso(rejectedExe);

        Click(window, window.FindControl<Button>("RunRealTitleButton")!);
        await viewModel.RunRealTitleCommand.ExecutionTask!;
        Dispatcher.UIThread.RunJobs();

        window.FindControl<TextBlock>("RealTitleExecutionStatusText")!.Text
            .Should().Contain("rejected for execution",
                "an execution-layout rejection must be reported to the user, not thrown");
    }

    // The real startup path: App.CreateMainWindow() builds the production window with
    // its production MainWindowViewModel, exactly as the desktop app does.
    private static MainWindow ShowMainWindow(out MainWindowViewModel viewModel)
    {
        var window = App.CreateMainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        viewModel = (MainWindowViewModel)window.DataContext!;
        return window;
    }

    // A real mouse click through the Avalonia input pipeline, so the bound command is
    // only reached if the XAML binding resolves. Point is in window (TopLevel) space.
    private static void Click(Window window, Button button)
    {
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        center.Should().NotBeNull("the button must be laid out before it can be clicked");

        window.MouseDown(center!.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // A PS-X EXE that reads its own first word and prints two BIOS bytes, then falls
    // off the end of its text region → Completed with TTY "01CC".
    private static byte[] ExecutableThatRunsToCompletion()
    {
        const uint textStart = 0x80010000u;
        return SyntheticDiscImage.BuildSyntheticExe(
            textStart, entryPoint: textStart, spInitial: 0x801FFF00u, gpInitial: 0xAAAABBCCu,
            [
                0x3C0E8001u, 0x8DC40000u, 0u, 0x308400FFu,
                0x3409003Cu, 0x0C000028u, 0u,
                0x3409003Cu, 0x001C2020u, 0x308400FFu, 0x0C000028u, 0u,
            ]);
    }
}

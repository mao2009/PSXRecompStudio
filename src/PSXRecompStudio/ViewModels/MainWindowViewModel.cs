using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSXRecomp.Architecture;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.ViewModels;

[Application]
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly TitleExecutionService _execution = new();

    public string AppName { get; } = "PSXRecompStudio";
    public string Version { get; } = "0.1.0-dev";
    public string Status { get; } = "Phase 2: Native Core + C ABI + P/Invoke Established";
    public string RuntimeInfo { get; } = $".NET {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
    public string PlatformInfo { get; } = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";

    /// <summary>
    /// The outcome of the last <see cref="RunDiagnosticTitleCommand"/> invocation.
    /// </summary>
    [ObservableProperty]
    private string _executionStatus = "Not run";

    /// <summary>
    /// Runs the built-in diagnostic title through the production execution path
    /// (ADR-015) and reports its classified outcome. This is the Studio's wiring
    /// proof that a title can be executed from the product, not only from tests.
    /// </summary>
    [RelayCommand]
    private void RunDiagnosticTitle()
    {
        var run = _execution.RunDiagnostic();
        var output = Encoding.ASCII.GetString([.. run.Output]);
        ExecutionStatus =
            $"{run.Result.State} via {run.Result.EngineName} " +
            $"({run.Result.SegmentsRetired} segment(s), TTY \"{output}\")" +
            (run.Result.DiagnosticCode is null ? string.Empty : $" — {run.Result.DiagnosticCode}");
    }
}

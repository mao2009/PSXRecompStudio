using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.ViewModels;

[Application]
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly TitleExecutionService _execution = new();
    private readonly RealRomTitleExecutionService _realExecution = new();

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
    /// The outcome of the last <see cref="RunRealTitleCommand"/> invocation.
    /// </summary>
    [ObservableProperty]
    private string _realTitleExecutionStatus = "Not run";

    /// <summary>
    /// The raw bytes of the disc image the real-ROM production flow analyzes and executes.
    /// I/O-free by design: the Application layer is forbidden <see cref="System.IO.File"/> /
    /// <see cref="System.IO.Directory"/> by the architecture contract, so disc bytes are
    /// supplied pre-read (Issue #38). Null means no disc image has been loaded.
    /// </summary>
    [ObservableProperty]
    private byte[]? _discImageBytes;

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

    /// <summary>
    /// Runs the real-ROM production flow (Issue #409): the loaded disc image is analyzed by
    /// <see cref="RealRomTitleExecutionService"/>, the analyzed PS-X EXE is retained, and the
    /// same executable is fed into the production execution path through
    /// <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>, whose classified outcome
    /// is reported here. This is the product wiring for a real title, distinct from
    /// <see cref="RunDiagnosticTitleCommand"/>, which runs the built-in diagnostic program.
    /// All execution semantics stay in the Domain layer (ADR-015).
    /// </summary>
    [RelayCommand]
    private void RunRealTitle()
    {
        var bytes = DiscImageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            RealTitleExecutionStatus = "Load a disc image to analyze and execute";
            return;
        }

        string sha256;
        using (var hasher = System.Security.Cryptography.SHA256.Create())
        {
            sha256 = Convert.ToHexString(hasher.ComputeHash(bytes)).ToLowerInvariant();
        }

        var result = _realExecution.AnalyzeAndExecuteFromDiscImage(bytes, sha256);

        if (result.ExecutionLayoutRejectionReason is not null)
        {
            RealTitleExecutionStatus =
                $"{result.Analysis.Status} — executable rejected for execution: {result.ExecutionLayoutRejectionReason}";
            return;
        }

        var run = result.Run;
        if (run is null)
        {
            RealTitleExecutionStatus =
                $"{result.Analysis.Status} — no executable produced" +
                (result.Analysis.FailureReason is null ? string.Empty : $": {result.Analysis.FailureReason}");
            return;
        }

        var output = Encoding.ASCII.GetString([.. run.Output]);
        RealTitleExecutionStatus =
            $"{run.Result.State} via {run.Result.EngineName} " +
            $"({run.Result.SegmentsRetired} segment(s), TTY \"{output}\")" +
            (run.Result.DiagnosticCode is null ? string.Empty : $" — {run.Result.DiagnosticCode}");
    }
}

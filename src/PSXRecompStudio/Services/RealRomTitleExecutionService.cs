using System;
using System.IO;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;

namespace PSXRecompStudio.Services;

/// <summary>
/// One disc-analysis plus execution run: the analysis outcome (including the
/// analyzed PS-X EXE) and the classified production execution run, when the
/// analysis produced an executable.
/// </summary>
[Application]
public sealed record RealRomTitleExecutionResult
{
    /// <summary>The analysis outcome, including the analyzed <see cref="RomAnalysisOutcome.Executable"/>.</summary>
    public required RomAnalysisOutcome Analysis { get; init; }

    /// <summary>The classified execution run, or <c>null</c> when analysis did not produce an executable.</summary>
    public TitleExecutionRun? Run { get; init; }

    /// <summary>
    /// The exact <see cref="PsxExe"/> that was handed to
    /// <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/> — the same object
    /// instance as <see cref="RomAnalysisOutcome.Executable"/>. Tests can use this
    /// to verify the analyzed executable was not swapped or reloaded before execution.
    /// </summary>
    public PsxExe? Executable { get; init; }
}

/// <summary>
/// The Studio's composition root for the real-ROM production flow (Issue #409):
/// disc analysis → analyzed PS-X EXE → production execution.
/// </summary>
/// <remarks>
/// This deliberately keeps the analyzed executable alive. <see cref="DiscImageAnalyzer"/>
/// is a report-only façade that drops <see cref="RomAnalysisOutcome.Executable"/>, so
/// this service routes through <see cref="RomAnalysisPipeline"/> directly and passes the
/// outcome's executable into <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/> —
/// the same production composition root that runs the built-in diagnostic title. No new
/// analysis, CPU, or execution semantics are implemented here: this is wiring only, which
/// is why it lives in the Application layer (ADR-015).
///
/// This service is deliberately I/O-free: the architecture contract forbids
/// <see cref="System.IO.File"/> / <see cref="System.IO.Directory"/> at every layer
/// (including Application); disc bytes are supplied pre-read by the caller. Disc-image
/// acquisition (file I/O) remains behind the Infrastructure seam, deferred to Issue #38.
/// </remarks>
[Application]
public sealed class RealRomTitleExecutionService
{
    /// <summary>The number of segments the orchestrator may run for one real title.</summary>
    public const uint DefaultOuterBudget = 64;

    /// <summary>The maximum steps one segment may spend.</summary>
    public const uint DefaultSegmentBudget = 4096;

    private readonly TitleExecutionService _execution;

    public RealRomTitleExecutionService(TitleExecutionService? execution = null)
    {
        _execution = execution ?? new TitleExecutionService();
    }

    /// <summary>
    /// Runs the production flow over a plain ISO 9660 image supplied as in-memory bytes
    /// (no CHD container). The image is analyzed, the analyzed PS-X EXE is held, and —
    /// when analysis succeeds — that same executable is fed into the production execution
    /// path via <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>.
    /// </summary>
    /// <param name="discImageBytes">Plain ISO 9660 image bytes (2048-byte user-data sectors).</param>
    /// <param name="discImageSha256">SHA-256 of <paramref name="discImageBytes"/>, the formal analysis input identity.</param>
    /// <param name="outerBudget">Execution budget passed through to <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>.</param>
    /// <param name="segmentBudget">Segment execution budget passed through to <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>.</param>
    public RealRomTitleExecutionResult AnalyzeAndExecuteFromDiscImage(
        byte[] discImageBytes,
        string discImageSha256,
        uint outerBudget = DefaultOuterBudget,
        uint segmentBudget = DefaultSegmentBudget)
    {
        ArgumentNullException.ThrowIfNull(discImageBytes);
        var outcome = RomAnalysisPipeline.RunFromIsoImage(discImageBytes, discImageSha256);
        return ExecuteAnalyzed(outcome, outerBudget, segmentBudget);
    }

    /// <summary>
    /// Runs the production flow over a CHD disc image supplied as a stream.
    /// The stream is passed directly to <see cref="RomAnalysisPipeline.RunFromChd(Stream, string, int?, RomAnalysisStageRecorder?)"/>;
    /// it must be readable and seekable, and the caller retains ownership and disposal.
    /// </summary>
    /// <param name="chdStream">Caller-owned, readable, seekable CHD stream. Not disposed.</param>
    /// <param name="discImageSha256">SHA-256 of the disc image, the formal analysis input identity.</param>
    /// <param name="outerBudget">Execution budget passed through to <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>.</param>
    /// <param name="segmentBudget">Segment execution budget passed through to <see cref="TitleExecutionService.Run(PsxExe, uint, uint)"/>.</param>
    public RealRomTitleExecutionResult AnalyzeAndExecuteFromDisc(
        Stream chdStream,
        string discImageSha256,
        uint outerBudget = DefaultOuterBudget,
        uint segmentBudget = DefaultSegmentBudget)
    {
        ArgumentNullException.ThrowIfNull(chdStream);
        var outcome = RomAnalysisPipeline.RunFromChd(chdStream, discImageSha256);
        return ExecuteAnalyzed(outcome, outerBudget, segmentBudget);
    }

    /// <summary>
    /// When the analysis outcome holds a non-null <see cref="RomAnalysisOutcome.Executable"/>,
    /// hands it to the production execution service unchanged, returning both the outcome and
    /// the classified execution run. Returns the outcome-only result when the analysis failed
    /// or did not produce an executable.
    /// </summary>
    private RealRomTitleExecutionResult ExecuteAnalyzed(
        RomAnalysisOutcome outcome, uint outerBudget, uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Status != RomAnalysisStatus.Pass || outcome.Executable is null)
        {
            return new RealRomTitleExecutionResult { Analysis = outcome };
        }

        var run = _execution.Run(outcome.Executable, outerBudget, segmentBudget);

        return new RealRomTitleExecutionResult
        {
            Analysis = outcome,
            Executable = outcome.Executable,
            Run = run,
        };
    }
}

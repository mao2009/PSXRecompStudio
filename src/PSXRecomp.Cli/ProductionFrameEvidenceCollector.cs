using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Headless production-frame evidence for Issue #575.
///
/// <para>
/// This deliberately runs the production interpreter path over the same
/// <see cref="PsxExeTitleExecution"/> the CLI already resolved from the user's
/// input. The generated-host <c>run</c> result remains authoritative for the
/// command's exit code; this collector exists only to expose the production
/// GPU/VRAM state as deterministic evidence without inventing a framebuffer in
/// the generated-host path.
/// </para>
/// </summary>
[Infrastructure]
internal static class ProductionFrameEvidenceCollector
{
    public const string AvailableStatus = "available";
    public const string UnavailableStatus = "unavailable";

    public static FrameEvidence Collect(PsxExeTitleExecution input)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            using var engine = new InterpreterTitleExecutionEngine(
                input.InstructionWords,
                input.LoadAddress,
                (reader, writer) => new BiosHleRuntime(DiscardOutputSink.Instance, reader, writer));

            var programEnd = checked(input.LoadAddress + (uint)input.InstructionWords.Count * 4u);
            var result = new ExecutionOrchestrator().Execute(
                engine,
                new ProgramEndHandoff(programEnd),
                input.Request);

            var frame = engine.CaptureFrame();
            if (frame.Width == 0 || frame.Height == 0)
            {
                return new FrameEvidence(
                    Status: UnavailableStatus,
                    Width: frame.Width,
                    Height: frame.Height,
                    Sha256: null,
                    ProductionState: result.State,
                    DiagnosticCode: result.DiagnosticCode,
                    Reason: "empty-display-region");
            }

            return new FrameEvidence(
                Status: AvailableStatus,
                Width: frame.Width,
                Height: frame.Height,
                Sha256: Convert.ToHexString(frame.ComputeStableHash()).ToLowerInvariant(),
                ProductionState: result.State,
                DiagnosticCode: result.DiagnosticCode,
                Reason: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Frame evidence is supplemental to the generated-host run. A
            // production-path preparation failure must not replace or rewrite
            // that run's classified result; report the evidence as unavailable.
            return new FrameEvidence(
                Status: UnavailableStatus,
                Width: null,
                Height: null,
                Sha256: null,
                ProductionState: null,
                DiagnosticCode: null,
                Reason: "production-frame-capture-failed");
        }
    }

    [Infrastructure]
    internal sealed record FrameEvidence(
        string Status,
        int? Width,
        int? Height,
        string? Sha256,
        TitleExecutionState? ProductionState,
        string? DiagnosticCode,
        string? Reason);

    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }

    private sealed class DiscardOutputSink : IRuntimeOutputSink
    {
        public static DiscardOutputSink Instance { get; } = new();

        public void WriteByte(byte value)
        {
        }
    }
}

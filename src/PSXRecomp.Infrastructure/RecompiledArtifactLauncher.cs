using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The single production entrypoint for Issue #459: builds a runnable native
/// artifact through <see cref="IGeneratedHostBuildService"/> (Issue #458),
/// launches it with the shared <see cref="BiosHleRuntime"/> attached, and
/// returns the classified, machine-readable result. This is the composition
/// root #460's CLI is meant to call directly — it introduces no execution model
/// of its own: <see cref="ExecutionOrchestrator"/> and <see cref="BiosHleRuntime"/>
/// are the same production types <c>PSXRecompStudio.Services.TitleExecutionService</c>
/// already wires for the interpreter path (ADR-015), and no Studio/Avalonia
/// dependency is required to reach them here.
/// </summary>
[Infrastructure]
public sealed class RecompiledArtifactLauncher
{
    /// <summary>One launch's classified result, its canonical JSON encoding, and the guest's TTY bytes.</summary>
    public sealed record LaunchOutcome(RecompiledArtifactResult Result, string Json, IReadOnlyList<byte> Output);

    /// <summary>
    /// Builds and runs one runnable artifact for <paramref name="program"/> from
    /// <paramref name="request"/>'s initial state.
    /// </summary>
    /// <param name="program">The lowered Recompiler IR to build a runnable artifact from.</param>
    /// <param name="request">The initial guest state. <c>OuterBudget</c> should be 1: this
    /// milestone's engine supports exactly one artifact launch (see
    /// <see cref="RecompiledHostExecutionEngine"/>'s remarks).</param>
    /// <param name="handoff">Optional continuation rule-set for an unresolved segment end;
    /// null classifies every such transfer as <see cref="TitleExecutionState.UnsupportedTransfer"/>.</param>
    /// <param name="outputDirectory">Where the artifact's source and binary are written. The
    /// caller owns this directory's lifecycle.</param>
    /// <param name="buildService">The build substrate to use; defaults to the production
    /// <see cref="GeneratedHostBuildService"/> adapter.</param>
    /// <param name="resultRegister">GPR index treated as the generated program's observable
    /// result marker (e.g. <c>2</c> for V0); null reports none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or
    /// <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Code generation or the artifact build failed.</exception>
    public LaunchOutcome Launch(
        RecompilerIrProgram program,
        TitleExecutionRequest request,
        ITitleExecutionHandoff? handoff,
        string outputDirectory,
        IGeneratedHostBuildService? buildService = null,
        int? resultRegister = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var sink = new CollectedOutput();
        using var engine = new RecompiledHostExecutionEngine(
            program,
            buildService ?? new GeneratedHostBuildService(),
            outputDirectory,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var execution = new ExecutionOrchestrator().Execute(engine, handoff, request);
        var result = RecompiledArtifactResult.From(execution, resultRegister);

        return new LaunchOutcome(result, ArtifactJson.Serialize(result), sink.Bytes);
    }

    /// <summary>Collects the guest's TTY bytes, encoding-free, as <see cref="IRuntimeOutputSink"/> requires.</summary>
    private sealed class CollectedOutput : IRuntimeOutputSink
    {
        public List<byte> Bytes { get; } = [];
        public void WriteByte(byte value) => Bytes.Add(value);
    }
}

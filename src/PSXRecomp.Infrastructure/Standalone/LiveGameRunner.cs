using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Runs a PS-X program through the real Runtime (Issue #630) on a background thread, with its VBlank frames published into a
/// <see cref="LiveFrameSlot"/> of the same process. The engine is the existing <see cref="RecompiledHostExecutionEngine"/>, built
/// the way the Runtime builds it (embedded image, HLE BIOS, device bridge). Nothing here is a second CPU or GPU implementation.
/// </summary>
/// <remarks>
/// The run owns the GPU/VRAM on the pump thread. A caller on the UI thread only polls the slot, which never touches the GPU.
/// A stop request (the cancellation token, Issue #820) ends the run as Cancelled and kills a live child; the segment budget bounds a
/// run that is not stopped.
/// </remarks>
[Infrastructure]
public static class LiveGameRunner
{
    /// <summary>
    /// Starts a run of <paramref name="imageWords"/> from <paramref name="entryPc"/> with <paramref name="segmentBudget"/> dispatch
    /// steps. The returned task completes when the run ends.
    /// </summary>
    public static Task<TitleExecutionResult> RunAsync(
        IReadOnlyList<uint> imageWords,
        uint entryPc,
        uint segmentBudget,
        LiveFrameSlot slot,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageWords);
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: segmentBudget);
        return RunCore(imageWords, loadAddress: entryPc, request, slot, outputDirectory, cancellationToken);
    }

    /// <summary>
    /// Runs a PS-X EXE file (Issue #630). The file is parsed and validated by the repository's canonical PS-X EXE input
    /// (<see cref="PsxExeTitleInput"/>), which places the text at the header's text address and starts at its entry PC, with GP
    /// and SP from the header. Throws for a file that is not a valid PS-X EXE; nothing is run in that case.
    /// </summary>
    public static Task<TitleExecutionResult> RunPsxExeAsync(
        byte[] exeFile,
        string fileName,
        uint segmentBudget,
        LiveFrameSlot slot,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exeFile);
        var exe = PsxExe.Load(exeFile, fileName);
        var input = PsxExeTitleInput.Build(exe, outerBudget: 1, segmentBudget: segmentBudget);
        return RunCore(input.InstructionWords, input.LoadAddress, input.Request, slot, outputDirectory, cancellationToken);
    }

    private static Task<TitleExecutionResult> RunCore(
        IReadOnlyList<uint> imageWords,
        uint loadAddress,
        TitleExecutionRequest request,
        LiveFrameSlot slot,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        return Task.Run(() =>
        {
            var program = ReachableProgramBuilder.Build(loadAddress, imageWords, request.EntryPc, []);
            using var engine = new RecompiledHostExecutionEngine(
                program,
                imageWords,
                loadAddress,
                new GeneratedHostBuildService(),
                outputDirectory,
                biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardOutput(), reader, writer),
                embedImage: true,
                liveFrames: slot);
            return new ExecutionOrchestrator().Execute(engine, handoff: null, request, cancellationToken: cancellationToken);
        });
    }

    /// <summary>The guest's TTY output is not shown by the window yet, so it is dropped.</summary>
    private sealed class DiscardOutput : IRuntimeOutputSink
    {
        public void WriteByte(byte value)
        {
        }
    }
}

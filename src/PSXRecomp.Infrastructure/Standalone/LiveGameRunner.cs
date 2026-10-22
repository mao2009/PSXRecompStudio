using System.Buffers.Binary;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
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
/// The run has no stop request yet (Issue #820 is not integrated here); the segment budget bounds how long it can run.
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
        return RunCore(imageWords, entryPc, new uint[TitleExecutionRequest.GprCount], segmentBudget, slot, outputDirectory, cancellationToken);
    }

    /// <summary>
    /// Runs a PS-X EXE file (Issue #630): its header gives the entry PC, the GP and SP, and its text is the program image. The
    /// file is parsed by the existing <see cref="PsxExe"/> loader; the run is the same as <see cref="RunAsync"/>.
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
        var words = new uint[exe.DecodedInstructionCount];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(exe.TextSegment.AsSpan(i * sizeof(uint)));
        }

        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.Gp] = exe.Header.GpInitial;
        gpr[(int)R3000aRegister.Sp] = exe.Header.SpInitial;
        return RunCore(words, exe.Header.EntryPoint, gpr, segmentBudget, slot, outputDirectory, cancellationToken);
    }

    private static Task<TitleExecutionResult> RunCore(
        IReadOnlyList<uint> imageWords,
        uint entryPc,
        uint[] initialGpr,
        uint segmentBudget,
        LiveFrameSlot slot,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        return Task.Run(() =>
        {
            var program = ReachableProgramBuilder.Build(entryPc, imageWords, entryPc, []);
            using var engine = new RecompiledHostExecutionEngine(
                program,
                imageWords,
                entryPc,
                new GeneratedHostBuildService(),
                outputDirectory,
                biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardOutput(), reader, writer),
                embedImage: true,
                liveFrames: slot);
            var request = new TitleExecutionRequest(
                entryPc, initialGpr, 0, 0, [], outerBudget: 1, segmentBudget: segmentBudget);
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

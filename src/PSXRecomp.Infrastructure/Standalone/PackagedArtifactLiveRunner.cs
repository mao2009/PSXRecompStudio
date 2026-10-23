using System.Buffers.Binary;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Runs a packaged distribution's prebuilt AOT artifact in the calling process for the game window (Issue #830). It reads
/// <c>runtime/runtime-host.json</c> and the image it names, then drives the artifact the same way the Runtime host does: the
/// same <see cref="RecompiledHostExecutionEngine.ForPrebuiltArtifact"/> in host-transfer mode and the same orchestrated run. The
/// VBlank frames of the artifact are published into <paramref name="slot"/> through the host-transfer bridge. No compiler runs.
/// </summary>
/// <remarks>
/// Guest firmware packages (OpenBIOS, Issue #732) are not run here; the window supports the HLE packages only.
/// The artifact is a child process of the window, so a stop request reaches it through the engine's cancellation path (Issue #820),
/// and a Job Object around the window reaps it when the window's launcher goes away.
/// </remarks>
[Infrastructure]
public static class PackagedArtifactLiveRunner
{
    /// <summary>
    /// Starts the packaged run described by <paramref name="manifestPath"/>. The manifest and the image are read before this returns,
    /// so an invalid package throws here and nothing is started. The returned task completes when the run ends.
    /// </summary>
    public static Task<TitleExecutionResult> RunAsync(
        string manifestPath,
        LiveFrameSlot slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(slot);

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidOperationException("The runtime host manifest has no directory component.");
        var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(fullManifestPath));
        if (manifest.GuestFirmware)
        {
            throw new InvalidOperationException("The game window runs HLE packages only; this package boots guest firmware.");
        }

        var artifactPath = RuntimeHostLayout.ResolveArtifactPath(manifestDirectory, manifest.Artifact);
        var imagePath = RuntimeHostLayout.ResolveImagePath(manifestDirectory, manifest.Image);
        var imageBytes = File.ReadAllBytes(imagePath);
        var imageWords = new uint[imageBytes.Length / 4];
        for (var i = 0; i < imageWords.Length; i++)
        {
            imageWords[i] = BinaryPrimitives.ReadUInt32LittleEndian(imageBytes.AsSpan(i * 4));
        }

        var mixedFallback = manifest.MixedFallback
            ? new MixedFallbackOptions(manifest.FallbackSegmentInstructionBudget, manifest.FallbackMaxTransitions)
            : null;

        return Task.Run(() =>
        {
            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                artifactPath,
                (manifest.BlockEntryPcs ?? []).ToHashSet(),
                biosRuntimeFactory: (reader, writer) => new BiosHleRuntime(new DiscardOutput(), reader, writer),
                mixedFallback: mixedFallback,
                runTimeout: TimeSpan.FromMilliseconds(manifest.RunTimeoutMs),
                imageWords: imageWords,
                imageLoadAddress: manifest.ImageLoadAddress,
                liveFrames: slot);

            var request = new TitleExecutionRequest(
                entryPc: manifest.EntryPc,
                initialGpr: new uint[TitleExecutionRequest.GprCount],
                initialHi: 0,
                initialLo: 0,
                initialMemory: [],
                outerBudget: 1,
                segmentBudget: uint.MaxValue);
            return new ExecutionOrchestrator().Execute(
                engine,
                new ProgramEndHandoff(manifest.ProgramEnd),
                request,
                cancellationToken: cancellationToken);
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

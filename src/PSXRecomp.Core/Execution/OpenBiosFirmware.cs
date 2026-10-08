using System.Buffers.Binary;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// User-provided OpenBIOS ROM code image. It contains firmware, not an HLE service table.
/// Source: PCSX-Redux Nugget OpenBIOS (see docs/REFERENCES.md).
/// No ROM bytes are tracked or distributed by this project.
/// </summary>
[Domain]
public sealed class OpenBiosFirmware
{
    public const int ImageSize = 512 * 1024;
    public const uint ResetVector = 0xBFC00000u;
    private readonly IReadOnlyList<uint> _words;

    private OpenBiosFirmware(uint[] words)
    {
        _words = Array.AsReadOnly(words);
    }

    /// <summary>Immutable little-endian MIPS words suitable for the existing interpreter's ROM window.</summary>
    public IReadOnlyList<uint> Words => _words;

    /// <summary>Copies the complete ROM; rejects partial/truncated data instead of padding with zeroes.</summary>
    public static OpenBiosFirmware FromBytes(ReadOnlySpan<byte> image)
    {
        if (image.Length != ImageSize)
        {
            throw new ArgumentException(
                $"OpenBIOS requires a complete {ImageSize}-byte ROM; got {image.Length} bytes.",
                nameof(image));
        }

        var words = new uint[ImageSize / sizeof(uint)];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(i * sizeof(uint), sizeof(uint)));
        }

        return new OpenBiosFirmware(words);
    }
}

/// <summary>
/// Replaceable BIOS *startup and execution* boundary, distinct from IBiosRuntime
/// (the legacy A0/B0/C0 HLE API). A future project-owned HLE backend can provide
/// another implementation without changing the full-title orchestrator.
/// </summary>
[Domain]
public interface IBiosBootBackend
{
    string Id { get; }
    uint EntryPc { get; }
    IRecompiledExecutionEngine CreateEngine();
}

/// <summary>
/// OpenBIOS primary firmware backend (real MIPS ROM code, not stubbed HLE calls).
/// Runs on the existing native CPU/device graph; the present slice executes
/// firmware and kernel code, but does NOT yet launch a game or claim a boot PASS.
/// </summary>
[Domain]
public sealed class OpenBiosBootBackend(OpenBiosFirmware image) : IBiosBootBackend
{
    private readonly OpenBiosFirmware _image = image ?? throw new ArgumentNullException(nameof(image));

    public string Id => "openbios-mips";
    public uint EntryPc => OpenBiosFirmware.ResetVector;

    public IRecompiledExecutionEngine CreateEngine() =>
        new InterpreterTitleExecutionEngine(
            _image.Words,
            OpenBiosFirmware.ResetVector,
            biosRuntimeFactory: null, // The real OpenBIOS must own the kernel, vectors and events.
            allowRuntimeRamExecution: true);
}

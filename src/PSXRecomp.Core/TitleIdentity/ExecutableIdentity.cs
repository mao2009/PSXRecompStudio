using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// Metadata describing the boot executable of a disc: file name, image load address,
/// entry point, size and a file hash. The hash is carried as a hex string for determinism.
/// </summary>
[Domain]
public sealed record ExecutableIdentity(
    string FileName,
    uint ImageLoadAddress,
    uint EntryPoint,
    uint Size,
    string FileHashHex)
{
    /// <summary>
    /// Stable, deterministic canonical key over the canonical fields of the executable.
    /// </summary>
    public string CanonicalKey =>
        $"{FileName}:{ImageLoadAddress:X8}:{EntryPoint:X8}:{Size:X8}:{FileHashHex}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;
}

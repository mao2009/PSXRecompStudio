using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Classifies a firmware image by its SHA-256, at run time, from the bytes the run actually loaded (Issue #B2, MAJOR-1).
/// The label never comes from the packaging-time declaration: that is a user statement kept for comparison only.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>openbios-rom</c>: the digest is in <see cref="VerifiedOpenBiosSha256"/>, an allowlist of independently verified
/// OpenBIOS builds. The only label that may be called a verified OpenBIOS run.</item>
/// <item><c>openbios-synthetic</c>: the digest is in <see cref="KnownSyntheticSha256"/>, the digests of this project's
/// deterministic test stubs. A stub is not BIOS code.</item>
/// <item><c>openbios-unverified</c>: any other image. It may run locally, but it is never a verified OpenBIOS run.</item>
/// </list>
/// An unregistered image is never classified as synthetic automatically.
/// </remarks>
[Infrastructure]
public static class BiosProvenance
{
    /// <summary>A digest in the verified allowlist: a verified OpenBIOS build.</summary>
    public const string Verified = "openbios-rom";

    /// <summary>A registered test stub: not OpenBIOS code.</summary>
    public const string Synthetic = "openbios-synthetic";

    /// <summary>Any other image: runs locally, is not a verified OpenBIOS run.</summary>
    public const string Unverified = "openbios-unverified";

    /// <summary>
    /// The verified OpenBIOS SHA-256 values. Empty: no build has yet been verified through a reproducible procedure from a
    /// pinned upstream commit (Issue #794/#732/#730). The local build used by this project's real-image gate is NOT registered,
    /// because its provenance and reproducibility have not been independently verified. Register a value only with its procedure
    /// and its independent verification recorded in the docs.
    /// </summary>
    public static IReadOnlySet<string> VerifiedOpenBiosSha256 { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The digests of this project's deterministic test stubs (not BIOS images): the SyntheticRom test image, the reproducible
    /// BIOS-shaped random image, and the RomWithGame stub. These are the only images labelled <c>openbios-synthetic</c>.
    /// </summary>
    public static IReadOnlySet<string> KnownSyntheticSha256 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "09948e1cfb2a76480f75179545ebe88f7bc4e40b3a6d0e946fe15e28a46d5d58",
        "53e39c65cc96541f8f6dc4f31de32e7ba47c48b271f30387b0396580fe1fe5a4",
        "085070b4ceede85ce720083b1d3c4e6363a683f3b0a0351f3d354539669e852e",
    };

    /// <summary>The product classification, with the registries above.</summary>
    public static string Classify(string sha256) => Classify(sha256, VerifiedOpenBiosSha256, KnownSyntheticSha256);

    /// <summary>
    /// The classification with injected registries. Tests use this entry point, so the verified path is exercised without
    /// registering a real OpenBIOS digest.
    /// </summary>
    public static string Classify(string sha256, IReadOnlySet<string> verified, IReadOnlySet<string> synthetic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(synthetic);
        var digest = sha256.ToLowerInvariant();
        if (verified.Contains(digest))
        {
            return Verified;
        }

        return synthetic.Contains(digest) ? Synthetic : Unverified;
    }

    /// <summary>
    /// The warning for a declaration that the run's own classification does not confirm (for example a real image declared
    /// user-supplied but not verified). Null when they agree or no declaration was made.
    /// </summary>
    public static string? DeclarationWarning(string? declared, string classified)
    {
        if (declared is null)
        {
            return null;
        }

        var expected = declared == "user-supplied" ? Verified : Synthetic;
        return classified == expected
            ? null
            : $"runtime host: warning: the image was declared '{declared}' at packaging, but at run time it is classified '{classified}'. " +
              "The label follows the run-time digest; the declaration is not evidence.";
    }
}

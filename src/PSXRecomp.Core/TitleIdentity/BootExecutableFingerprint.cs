using System.Security.Cryptography;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.TitleIdentity;

/// <summary>
/// A stable cryptographic fingerprint (e.g. "SHA-1") derived deterministically from an
/// <see cref="ExecutableIdentity"/>. No randomness, time, I/O or environment access is
/// involved, so equal inputs always yield equal fingerprints.
/// </summary>
[Domain]
public sealed record BootExecutableFingerprint(
    string Algorithm,
    string Value)
{
    /// <summary>Algorithm identifier used by <see cref="Compute"/>.</summary>
    public const string DefaultAlgorithm = "SHA-1";

    /// <summary>
    /// Stable, deterministic canonical key for this fingerprint.
    /// </summary>
    public string CanonicalKey => $"{Algorithm}:{Value}";

    /// <inheritdoc />
    public override string ToString() => CanonicalKey;

    /// <summary>
    /// Computes a stable fingerprint over the canonical fields of the supplied
    /// <see cref="ExecutableIdentity"/>. Uses SHA-1 hashing (pure computation, no I/O).
    /// </summary>
    public static BootExecutableFingerprint Compute(ExecutableIdentity executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var canonical = Encoding.UTF8.GetBytes(executable.CanonicalKey);
        var hash = SHA1.HashData(canonical);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return new BootExecutableFingerprint(DefaultAlgorithm, hex);
    }
}

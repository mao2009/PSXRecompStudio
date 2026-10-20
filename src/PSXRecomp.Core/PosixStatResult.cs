using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The fields of a POSIX <c>stat</c> call that identify a path (Issue #786 review round 6). <see cref="Errno"/> is 0 on success and a system
/// errno on failure. It is -1 when this build cannot read the identity here, and <see cref="Cause"/> then names why, so a refusal is diagnosable.
/// </summary>
[Domain]
public readonly record struct PosixStatResult(int Errno, uint Mode, ulong Device, ulong Inode, string? Cause = null)
{
    /// <summary>
    /// The cause named when the libc does not export <c>stat</c>. Minimum to verify: glibc 2.33 (the first release exporting <c>stat</c> as a
    /// symbol). That minimum is unverified: no glibc before 2.33 was run here.
    /// </summary>
    public const string StatNotExportedCause =
        "stat is not exported by this libc (identity unavailable; verification needs glibc 2.33 or later, and that minimum is unverified)";

    /// <summary>The result for a platform or layout this build cannot read, with the cause named.</summary>
    public static PosixStatResult Unsupported(string cause) => new(-1, 0, 0, 0, cause);

    /// <summary>True when the call succeeded.</summary>
    public bool Succeeded => Errno == 0;
}

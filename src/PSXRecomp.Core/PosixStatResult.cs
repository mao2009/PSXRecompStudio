using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The fields of a POSIX <c>stat</c> call that identify a path (Issue #786 review round 6). <see cref="Errno"/> is 0 on success,
/// a system errno on failure, and -1 when this platform's layout or libc symbol is not known here (the caller then refuses).
/// </summary>
[Domain]
public readonly record struct PosixStatResult(int Errno, uint Mode, ulong Device, ulong Inode)
{
    /// <summary>The result for a platform whose stat layout this build does not read.</summary>
    public static PosixStatResult Unsupported => new(-1, 0, 0, 0);

    /// <summary>True when the call succeeded.</summary>
    public bool Succeeded => Errno == 0;
}

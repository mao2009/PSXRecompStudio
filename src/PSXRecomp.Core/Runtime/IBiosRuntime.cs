using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Common BIOS service boundary for recompiled code, the reference interpreter,
/// and future diagnostics. Implementations return structured outcomes and must
/// not turn an unsupported call into a dummy success.
/// </summary>
[Domain]
public interface IBiosRuntime
{
    /// <summary>Dispatches a guest BIOS call through the configured Runtime services.</summary>
    BiosServiceResult Invoke(BiosCallIdentity identity);

    /// <summary>
    /// Queries the exact argument count a registered service expects for
    /// <paramref name="family"/>/<paramref name="functionNumber"/>, resolved
    /// through the same canonical alias mapping <see cref="Invoke"/> uses (so a
    /// C0 high-range alias reports its mirrored B0 service's arity, never a
    /// separate one). This is the single source of truth for service arity: a
    /// caller with a live register-only ABI boundary (such as a interpreter or
    /// recompiled trap) must read this rather than assume a fixed argument
    /// count of its own. Richer, machine-readable service descriptors are
    /// out of scope here (Issue #365); this covers only the minimal query a
    /// live trap needs to pass the right number of arguments.
    /// </summary>
    /// <returns>
    /// True when a service is registered for the canonical identity, with
    /// <paramref name="argumentCount"/> set to its expected argument count;
    /// false when no service is registered, with <paramref name="argumentCount"/>
    /// set to 0.
    /// </returns>
    bool TryGetServiceArgumentCount(BiosCallFamily family, byte functionNumber, out int argumentCount);
}

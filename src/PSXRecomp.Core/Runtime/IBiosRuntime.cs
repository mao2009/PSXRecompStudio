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
}

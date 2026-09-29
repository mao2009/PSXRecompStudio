using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Refines the orchestrator's generic <c>UNRESOLVED_TRANSFER</c> stop (Issue #644): when the
/// guest landed on a 4-byte-aligned PC inside the executable text image that has no
/// compiled block, the stop is reported as <see cref="Code"/> and says the target can be
/// added as an explicit root. It only re-labels the diagnostic; it never claims, compiles
/// or continues at the target. Targets outside the image keep the original diagnostic.
/// </summary>
[Infrastructure]
internal static class InImageTransferDiagnostic
{
    public const string UnresolvedTransferCode = "UNRESOLVED_TRANSFER";
    public const string Code = "UNRESOLVED_TRANSFER_IN_IMAGE";

    public static TitleExecutionResult Apply(
        TitleExecutionResult result,
        RecompilerIrProgram program,
        uint loadAddress,
        int wordCount)
    {
        if (result.DiagnosticCode != UnresolvedTransferCode || result.FinalSnapshot is not { } snapshot)
        {
            return result;
        }

        var pc = snapshot.PC;
        var imageEnd = (ulong)loadAddress + (ulong)wordCount * 4UL;
        if ((pc & 3) != 0 || pc < loadAddress || pc >= imageEnd || program.Blocks.Any(b => b.EntryPc == pc))
        {
            return result;
        }

        return result with
        {
            DiagnosticCode = Code,
            DiagnosticMessage =
                $"Guest control transferred to 0x{pc:X8}, an address inside the executable text image " +
                $"[0x{loadAddress:X8}, 0x{imageEnd:X8}) for which no block was compiled. It was not reachable " +
                "from the entry point; to compile it, supply the address as an explicit additional root " +
                $"(psxrecomp run|recompile --entry-root 0x{pc:X8}).",
        };
    }
}

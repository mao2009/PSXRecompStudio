using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// The production execution input assembled from a real PS-X EXE (Issue #409): the
/// text image as instruction words, the guest address the image is loaded at, and the
/// <see cref="TitleExecutionRequest"/> describing the initial architectural state and
/// budgets. It is the title-agnostic bridge between the existing disc/executable
/// analysis and <see cref="TitleExecutionService"/> / <see cref="ExecutionOrchestrator"/> /
/// <see cref="InterpreterTitleExecutionEngine"/> — it carries no Studio UI type and no
/// game-specific knowledge.
/// </summary>
[Domain]
public sealed record PsxExeTitleExecution(
    uint LoadAddress,
    IReadOnlyList<uint> InstructionWords,
    TitleExecutionRequest Request);

/// <summary>
/// Converts a loaded <see cref="PsxExe"/> into the production execution input of
/// <see cref="TitleExecutionService"/>. Execution stays on the existing production
/// composition root; this type only derives the image and initial state the request
/// already models.
/// </summary>
[Domain]
public static class PsxExeTitleInput
{
    /// <summary>
    /// Builds the execution input for <paramref name="exe"/>.
    /// </summary>
    /// <param name="exe">A legally user-supplied, already-parsed PS-X EXE.</param>
    /// <param name="outerBudget">The number of segments <see cref="ExecutionOrchestrator"/>
    /// may run before giving up.</param>
    /// <param name="segmentBudget">The per-segment budget handed to the engine.</param>
    /// <returns>The image words (loaded at the text start), the load address, and the
    /// request carrying the header-derived entry PC and initial SP/GP.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exe"/> is null.</exception>
    /// <exception cref="ArgumentException">The executable image is empty, is not a whole
    /// number of 32-bit words, loads at an address that is not 4-byte aligned or not
    /// translatable to physical RAM, or declares an entry point outside the text region
    /// or not 4-byte aligned. The bridge fails closed rather than fabricating a loadable
    /// program from a malformed image.</exception>
    public static PsxExeTitleExecution Build(PsxExe exe, uint outerBudget, uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(exe);

        var header = exe.Header;
        if (exe.TextSegment.Length == 0)
        {
            throw new ArgumentException(
                "A PS-X EXE with an empty text segment has no program to execute.", nameof(exe));
        }

        if ((exe.TextSegment.Length & 3) != 0)
        {
            throw new ArgumentException(
                $"The PS-X EXE text segment is {exe.TextSegment.Length} bytes, which is not a whole number of 32-bit instructions.",
                nameof(exe));
        }

        if ((header.TextStart & 3) != 0)
        {
            throw new ArgumentException(
                $"The PS-X EXE text start 0x{header.TextStart:X8} is not 4-byte aligned.", nameof(exe));
        }

        if (!Ps1AddressTranslation.TryTranslate(header.TextStart, out _))
        {
            throw new ArgumentException(
                $"The PS-X EXE text start 0x{header.TextStart:X8} is outside any translatable KUSEG/KSEG0/KSEG1 address.",
                nameof(exe));
        }

        var textEnd = header.TextStart + (uint)exe.TextSegment.Length;
        if (header.EntryPoint < header.TextStart || header.EntryPoint >= textEnd)
        {
            throw new ArgumentException(
                $"The PS-X EXE entry point 0x{header.EntryPoint:X8} is outside the text region " +
                $"[0x{header.TextStart:X8}..0x{textEnd:X8}).", nameof(exe));
        }

        if ((header.EntryPoint & 3) != 0)
        {
            throw new ArgumentException(
                $"The PS-X EXE entry point 0x{header.EntryPoint:X8} is not 4-byte aligned.", nameof(exe));
        }

        var words = new uint[exe.TextSegment.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(exe.TextSegment, i * 4);
        }

        // SP and GP are the only header-derived initial architectural state the
        // repository's PS-X EXE model carries (PsxExeHeader); everything else
        // starts zero like the engine's reset does.
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.Gp] = header.GpInitial;
        gpr[(int)R3000aRegister.Sp] = header.SpInitial;

        var request = new TitleExecutionRequest(
            header.EntryPoint,
            gpr,
            initialHi: 0,
            initialLo: 0,
            initialMemory: [],
            outerBudget,
            segmentBudget);

        return new PsxExeTitleExecution(header.TextStart, Array.AsReadOnly(words), request);
    }
}
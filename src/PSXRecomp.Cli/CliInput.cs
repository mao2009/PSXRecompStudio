using System.Security.Cryptography;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Reads a legally supplied input and lowers it into the production
/// <see cref="RecompilerIrProgram"/> both commands drive. This is the CLI's single
/// input resolver (Issue #457): the file extension decides the interpretation, with
/// <c>.chd</c> (OrdinalIgnoreCase) routed through the production CHD analysis path
/// and everything else through the existing PS-X EXE path:
///
/// <list type="bullet">
///   <item><b>CHD</b> — streaming SHA-256 plus
///   <c>RomAnalysisPipeline.RunFromChd</c>, taking the classified
///   <c>outcome.Executable</c> into <c>PsxExeTitleInput.Build</c>. A CHD never
///   reaches <see cref="PsxExe.Load"/>; a failed or boot-executable-less analysis
///   fails closed as a <see cref="CliChdInputException"/> carrying the pipeline's
///   <c>FailureKind</c>/<c>FailureReason</c>, so it is never misread as an invalid
///   PS-X EXE.</item>
///   <item><b>Any other extension</b> — the existing <see cref="LoadExe"/> path
///   unchanged.</item>
/// </list>
///
/// This is a pure composition of production Domain contracts — <c>PsxExe.Load</c>,
/// <c>RomAnalysisPipeline</c>, <c>PsxExeTitleInput.Build</c>, <c>R3000aDecoder.Decode</c>
/// and <c>MipsToIrLowerer.LowerProgram</c> — with no parsing or lowering of its own;
/// the host-I/O here (file reads) is Infrastructure-layer responsibility.
/// </summary>
[Infrastructure]
internal static class CliInput
{
    /// <summary>
    /// Resolves <paramref name="inputPath"/> into the production execution input,
    /// dispatching on the file extension. See the type summary for the exact contract.
    /// </summary>
    public static PsxExeTitleExecution Load(string inputPath, uint outerBudget, uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return Path.GetExtension(inputPath).Equals(".chd", StringComparison.OrdinalIgnoreCase)
            ? LoadChd(inputPath, outerBudget, segmentBudget)
            : LoadExe(inputPath, outerBudget, segmentBudget);
    }

    /// <summary>
    /// Existing PS-X EXE path: whole-file read, <see cref="PsxExe.Load"/>,
    /// <see cref="PsxExeTitleInput.Build"/>. Behavior-compatible with the
    /// pre-#457 CLI; a malformed EXE still fails as <c>PsxExe.Load</c> raises.
    /// </summary>
    public static PsxExeTitleExecution LoadExe(string exePath, uint outerBudget, uint segmentBudget)
    {
        var bytes = File.ReadAllBytes(exePath);
        var exe = PsxExe.Load(bytes, exePath);
        return PsxExeTitleInput.Build(exe, outerBudget, segmentBudget);
    }

    /// <summary>
    /// CHD path, following <see cref="RealRomAnalyzer"/>'s two-open pattern: the whole
    /// CHD is never buffered into memory. Pass 1 streams the file through SHA-256 for
    /// the formal input identity; pass 2 hands a second caller-owned stream straight to
    /// <c>RomAnalysisPipeline.RunFromChd</c>, which manages only its own
    /// <see cref="ChdReader"/> lifetime and never disposes this stream.
    /// </summary>
    private static PsxExeTitleExecution LoadChd(string chdPath, uint outerBudget, uint segmentBudget)
    {
        string sha256;
        using (var hashStream = File.OpenRead(chdPath))
        {
            using var hasher = SHA256.Create();
            sha256 = Convert.ToHexString(hasher.ComputeHash(hashStream)).ToLowerInvariant();
        }

        RomAnalysisOutcome outcome;
        using (var runStream = File.OpenRead(chdPath))
        {
            outcome = RomAnalysisPipeline.RunFromChd(runStream, sha256);
        }

        if (outcome.Status != RomAnalysisStatus.Pass || outcome.Executable is null)
        {
            throw new CliChdInputException(
                outcome.FailureKind ?? "ChdInputFailure",
                outcome.FailureReason);
        }

        return PsxExeTitleInput.Build(outcome.Executable, outerBudget, segmentBudget);
    }

    public static RecompilerIrProgram Lower(PsxExeTitleExecution input)
    {
        return ReachableProgramBuilder.Build(
            input.LoadAddress,
            input.InstructionWords,
            input.Request.EntryPc);
    }
}

/// <summary>
/// CLI-layer typed failure for a CHD input that could not be turned into a bootable
/// PS-X EXE by the production <c>RomAnalysisPipeline</c>. It derives from
/// <see cref="ArgumentException"/> so the commands' existing input-failure
/// classification applies unchanged (recompile: <c>InvalidInput</c>/<c>INVALID_INPUT</c>;
/// run: stderr + exit 1) while its message carries the pipeline's machine-readable
/// <see cref="FailureKind"/>/<see cref="FailureReason"/>. A CHD is therefore never
/// reported as <c>PsxExe.Load</c>'s "Invalid PS-X EXE magic".
/// </summary>
[Infrastructure]
public sealed class CliChdInputException : ArgumentException
{
    public CliChdInputException(string failureKind, string? failureReason)
        : base(BuildMessage(failureKind, failureReason))
    {
        FailureKind = failureKind;
        FailureReason = failureReason;
    }

    /// <summary>The pipeline's stable machine-readable failure classification.</summary>
    public string FailureKind { get; }

    /// <summary>The pipeline's failure reason text, when one was produced.</summary>
    public string? FailureReason { get; }

    private static string BuildMessage(string failureKind, string? failureReason)
    {
        var detail = string.IsNullOrEmpty(failureReason) ? failureKind : $"{failureKind}: {failureReason}";
        return $"CHD input could not be resolved to a bootable PS-X EXE: {detail}.";
    }
}

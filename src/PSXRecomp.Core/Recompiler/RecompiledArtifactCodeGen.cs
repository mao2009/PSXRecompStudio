using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>The source produced by <see cref="RecompiledArtifactCodeGen"/>, or why it failed.</summary>
[Domain]
public sealed record RecompiledArtifactCodeGenResult(
    bool Success,
    string? Source,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>
/// Production entrypoint driver for a runnable recompiled artifact (Issue #459).
/// Appends a title-agnostic <c>main()</c> to <see cref="RecompilerHostCodeGen"/>'s
/// generated dispatch so the result is a self-contained C program: read one guest
/// state from an input file, run <c>recompiler_dispatch</c> once, print a stable
/// state snapshot, and exit with <see cref="RecompilerIrTerminationReason"/>'s own
/// byte value as the process exit code.
/// </summary>
/// <remarks>
/// <para>
/// This is the production counterpart of the differential harness's test-only
/// driver (<c>RecompilerHostExecutor.DriverSource</c> in <c>PSXRecomp.Tests</c>):
/// same memory model and host-transfer wire protocol, but with no differential-only
/// concerns (checkpoint tracing, memory-window sampling, multi-segment RAM
/// preload). It is Domain-owned pure text generation — building and running it is
/// an Infrastructure concern (<see cref="IGeneratedHostBuildService"/> and the
/// engine that launches it), exactly as <c>RecompilerHostCodeGen</c> already is.
/// </para>
/// <para>
/// The optional host-transfer hook (<see cref="HostTransferFlag"/>) is the
/// artifact's only concession to a Runtime it cannot embed natively: the shared
/// PSX Runtime / BIOS HLE contract (<c>IBiosRuntime</c>, <c>BiosVectorDispatch</c>)
/// is a managed component, so a native artifact reaches it only by relaying the
/// unresolved transfer to a parent process over this protocol (ADR-014). Run
/// without the flag, the artifact is still independently runnable: it executes
/// every block it has generated code for and stops at the first unresolved
/// transfer with no Runtime attached, which is a legitimate classified boundary,
/// not a failure.
/// </para>
/// </remarks>
[Domain]
public static class RecompiledArtifactCodeGen
{
    /// <summary>Command-line flag that opts the artifact into the host-transfer protocol.</summary>
    public const string HostTransferFlag = "--host-transfer";

    /// <summary>
    /// Command-line flag (any position after the image) that runs the artifact in firmware mode (Issue #732): a
    /// guest SYSCALL/BREAK enters the guest's own exception vector instead of the HLE <c>RHOST_SYSCALL</c> offer.
    /// </summary>
    public const string GuestExceptionsFlag = "--guest-exceptions";

    /// <summary>Require deadline credits from the host in every synchronization reply.</summary>
    public const string ExactDeviceTimeFlag = "--exact-device-time";

    /// <summary>Supplied by the runtime host to a packaged artifact to tell it to run the
    /// embedded image standalone, without the launcher's delegation to the bundled
    /// Runtime host (Phase 2 Step C, Issue #739, ADR-027).
    /// </summary>
    public const string RuntimeHostModeFlag = "--runtime-host";

    /// <summary>The child's handshake line, sent once before the first guest instruction runs.</summary>
    public const string ProtocolInitLine = "RHOST_INIT";

    /// <summary>Line prefix for the child's control-transfer offer: <c>pc gpr0 .. gpr31</c>.</summary>
    public const string ProtocolTransferPrefix = "RHOST_TRANSFER ";

    /// <summary>Line prefix for the parent's per-byte memory read request.</summary>
    public const string ProtocolReadCommand = "R";

    /// <summary>Line prefix for the parent's per-byte memory write request.</summary>
    public const string ProtocolWriteCommand = "W";

    /// <summary>The parent's reply that means "this pc is not claimed".</summary>
    public const string ProtocolDeclineReply = "N";

    /// <summary>Prefix of the child's reply carrying one read byte.</summary>
    public const string ProtocolDataPrefix = "RHOST_DATA ";

    /// <summary>The child's acknowledgement of a completed write.</summary>
    public const string ProtocolWriteAck = "RHOST_OK";

    /// <summary>Line prefix for the child's SYSCALL offer: <c>fault_pc a0 sr_at_entry</c> (Issue #663).</summary>
    public const string ProtocolSyscallPrefix = "RHOST_SYSCALL ";

    /// <summary>Line prefix for the parent's COP0 SR write, sent before its decision: <c>C sr</c> (Issue #663).</summary>
    public const string ProtocolCop0SrCommand = "C";

    /// <summary>
    /// The parent's request for the CPU's exception state: <c>E</c>. The child answers
    /// <c>RHOST_COP0 epc cause sr hi lo intEntry</c> (<see cref="ProtocolCop0ReplyPrefix"/>); <c>intEntry</c> is 1 only
    /// when the pc the artifact is at was reached by its hardware INT entry (Issue #662).
    /// </summary>
    public const string ProtocolCop0QueryCommand = "E";

    /// <summary>Prefix of the child's reply to <see cref="ProtocolCop0QueryCommand"/>.</summary>
    public const string ProtocolCop0ReplyPrefix = "RHOST_COP0 ";

    /// <summary>Line prefix for the parent's GPR write: <c>G index value</c>, sent before its decision (Issue #662).</summary>
    public const string ProtocolGprCommand = "G";

    /// <summary>Line prefix for the parent's HI/LO write: <c>H hi lo</c>, sent before its decision (Issue #662).</summary>
    public const string ProtocolHiLoCommand = "H";

    /// <summary>
    /// Line prefix for the parent's interrupt-line update: <c>L 0|1</c>, sent before its decision once a kernel handler
    /// ran (Issue #662). The artifact only learns the line from a guest-time ack, and a handler that acknowledged
    /// I_STAT retires no instruction, so without this the artifact would take the same INT again from a stale line.
    /// </summary>
    public const string ProtocolInterruptLineCommand = "L";

    /// <summary>The parent's request that the artifact's CPU perform RFE (pop the SR KU/IE stack): <c>P</c> (Issue #662).</summary>
    public const string ProtocolRfeCommand = "P";

    /// <summary>
    /// The parent's mark that the very next decision (<see cref="ProtocolDecisionPrefix"/>) is its register-indirect
    /// jump (an HLE <c>jr ra</c> or the kernel exception handler's <c>jr k0; rfe</c> to EPC/hook pc): <c>J</c> (Issue #763).
    /// Nothing may be sent between <c>J</c> and its decision. Only that decision's <c>next_pc</c> becomes the artifact's <c>indirect_target</c>,
    /// so the mixed-fallback query reports it as a runtime-discovered target. Unmarked decisions never touch it.
    /// </summary>
    public const string ProtocolIndirectResumeCommand = "J";

    /// <summary>Prefix of the parent's decision reply: <c>D termination next_pc has_v0 v0</c>.</summary>
    public const string ProtocolDecisionPrefix = "D ";

    /// <summary>Line prefix for the child's MMIO read request: <c>RHOST_MMIO_READ width physical</c> (Issue #678).</summary>
    public const string ProtocolMmioReadPrefix = "RHOST_MMIO_READ ";

    /// <summary>Line prefix for the child's MMIO write request: <c>RHOST_MMIO_WRITE width physical value</c> (Issue #678).</summary>
    public const string ProtocolMmioWritePrefix = "RHOST_MMIO_WRITE ";

    /// <summary>Tag of the parent's MMIO success reply: <c>V value</c> (the value read; <c>0</c> for a write).</summary>
    public const string ProtocolMmioValueReply = "V";

    /// <summary>
    /// A GTE (COP2) access relayed to the parent's single GTE (Issue #447): <c>R reg</c>, <c>W reg value</c> or
    /// <c>C command</c> (reg 0-31 data, 32-63 control), answered like an MMIO access with
    /// <see cref="ProtocolMmioValueReply"/> or, for a command the GTE does not implement,
    /// <see cref="ProtocolMmioRefusedReply"/> (the artifact stops; the parent names the command).
    /// </summary>
    public const string ProtocolGtePrefix = "RHOST_GTE ";

    /// <summary>Tag of the parent's MMIO refusal: <c>X</c>. The child stops with <see cref="MmioRefusedExitCode"/>.</summary>
    public const string ProtocolMmioRefusedReply = "X";

    /// <summary>
    /// Line prefix for the child's guest-time report: <c>RHOST_RETIRED count</c> (Issue #679). <c>count</c> is the
    /// number of guest instructions retired since the previous report — a positive integer; the child never sends 0.
    /// The artifact knows no cycle costs: converting instructions to device time is the parent's Runtime contract.
    /// </summary>
    public const string ProtocolRetiredPrefix = "RHOST_RETIRED ";

    /// <summary>Tag of the parent's acceptance of a guest-time report: <c>A</c>, sent after any device-originated RAM requests.</summary>
    public const string ProtocolRetiredAckReply = "A";

    /// <summary>
    /// Tag of the parent's acceptance of a guest-time report while the Interrupt Controller's aggregate line
    /// (<c>I_STAT &amp; I_MASK != 0</c>, the CPU's CAUSE.IP2) is asserted: <c>I</c> (Issue #680). <see cref="ProtocolRetiredAckReply"/>
    /// means the line is deasserted. The artifact takes the INT exception itself, at its next dispatch boundary, only if
    /// its own SR enables it; the host never injects an exception.
    /// </summary>
    public const string ProtocolRetiredAckInterruptReply = "I";

    /// <summary>Tag of the parent's refusal of a guest-time report: <c>X</c>. The child stops with <see cref="RetiredRefusedExitCode"/>.</summary>
    public const string ProtocolRetiredRefusedReply = "X";

    /// <summary>
    /// Additional cap on unreported retirements; exact device deadlines can require an earlier report.
    /// Retired guest instructions the artifact accumulates before it reports them without
    /// being asked to by an MMIO access, a host transfer, a SYSCALL or the end of the run. It bounds how long a device
    /// event can go unobserved by a parent that has no other reason to hear from the child.
    /// </summary>
    public const int RetiredReportThreshold = 1024;

    /// <summary>Firmware-only host-owned COP0 request: register, write flag and value; reply is V value or X.</summary>
    public const string ProtocolCop0AccessPrefix = "RHOST_COP0_ACCESS ";

    /// <summary>
    /// The parent's request for the mixed-execution fallback state (Issue #693): <c>F version</c>. The child answers
    /// <c>RHOST_FALLBACK version indirect irq hi lo sr cause epc dirtyPages</c> (<see cref="ProtocolFallbackReplyPrefix"/>) or
    /// <c>RHOST_FALLBACK_REFUSED reason</c>. <c>indirect</c> is 1 only when the pc the artifact is at is the runtime target of its most
    /// recent register-indirect (JR/JALR) block exit. The request changes no artifact state.
    /// </summary>
    public const string ProtocolFallbackQueryCommand = "F";

    /// <summary>Prefix of the child's reply to <see cref="ProtocolFallbackQueryCommand"/>.</summary>
    public const string ProtocolFallbackReplyPrefix = "RHOST_FALLBACK ";

    /// <summary>Prefix of the child's refusal of a fallback request or commit: <c>RHOST_FALLBACK_REFUSED reason</c>.</summary>
    public const string ProtocolFallbackRefusedPrefix = "RHOST_FALLBACK_REFUSED ";

    /// <summary>
    /// The parent's request for the dirty RAM pages: <c>Y</c>. The child sends one <c>RHOST_PAGE index hex</c> line per page that differs
    /// from the image the host last held (ascending index, 4096 bytes as 8192 lowercase hex characters), then
    /// <c>RHOST_PAGES_END hash</c>, and records those pages as held by the host.
    /// </summary>
    public const string ProtocolFallbackPagesCommand = "Y";

    /// <summary>Line prefix of one page the child sends in answer to <see cref="ProtocolFallbackPagesCommand"/>.</summary>
    public const string ProtocolFallbackPagePrefix = "RHOST_PAGE ";

    /// <summary>Line prefix of the child's end-of-pages line carrying the FNV-1a hash of every (index, page bytes) it sent.</summary>
    public const string ProtocolFallbackPagesEndPrefix = "RHOST_PAGES_END ";

    /// <summary>The parent's staged write-back of one RAM page: <c>B index hex</c>, ascending and distinct. Applied only by <see cref="ProtocolFallbackCommitCommand"/>.</summary>
    public const string ProtocolFallbackStageCommand = "B";

    /// <summary>
    /// The parent's commit of the staged pages: <c>K count hash</c>. The child applies all staged pages to guest RAM only if the count and
    /// the FNV-1a hash match what it received, then answers <c>RHOST_OK</c>; otherwise it discards them and answers
    /// <c>RHOST_FALLBACK_REFUSED checksum</c>, leaving guest RAM unchanged.
    /// </summary>
    public const string ProtocolFallbackCommitCommand = "K";

    /// <summary>The parent's full CPU-state write: <c>S hi lo sr cause epc irq gpr1 .. gpr31</c> (CAUSE.IP2 follows <c>irq</c>).</summary>
    public const string ProtocolFallbackStateCommand = "S";

    /// <summary>The fallback protocol version this driver implements.</summary>
    public const int ProtocolFallbackVersion = 1;

    /// <summary>RAM page size of the fallback copy-sync.</summary>
    public const int FallbackPageSize = 4096;

    /// <summary>Artifact exit code: a fallback command was malformed (Issue #693).</summary>
    public const int FallbackProtocolExitCode = 101;

    /// <summary>Marks the start of the stable state snapshot on stdout.</summary>
    public const string SnapshotBeginMarker = "RSNAPSHOT_BEGIN";

    /// <summary>Marks the end of the stable state snapshot on stdout.</summary>
    public const string SnapshotEndMarker = "RSNAPSHOT_END";

    /// <summary>The maximum init-memory entries the driver's input format accepts.
    /// The driver's <c>PSX_MAX_INIT</c> is emitted from this constant
    /// (<see cref="Generate"/>), making it the source of truth for the C bound.</summary>
    public const int MaxInitEntries = 4096;

    /// <summary>
    /// Appends the production driver to <paramref name="generatedDispatch"/>'s
    /// source. Pure text concatenation: deterministic for identical input, and
    /// independent of machine, locale, or filesystem state. The driver's
    /// <c>PSX_MAX_INIT</c> is emitted from <see cref="MaxInitEntries"/> (the
    /// <c>Replace</c> below is the verbatim-string analogue of an interpolation), so
    /// the C bound and the C# constant cannot drift.
    /// </summary>
    public static RecompiledArtifactCodeGenResult Generate(RecompilerHostCodeGenResult generatedDispatch)
    {
        ArgumentNullException.ThrowIfNull(generatedDispatch);

        if (!generatedDispatch.Success || generatedDispatch.Source is null)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                generatedDispatch.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                generatedDispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        return new RecompiledArtifactCodeGenResult(
            true,
            generatedDispatch.Source + "\n" + DriverSource
                .Replace("#define PSX_MAX_INIT 4096u", $"#define PSX_MAX_INIT {MaxInitEntries}u", StringComparison.Ordinal)
                .Replace("@MMIO_READ@", ProtocolMmioReadPrefix, StringComparison.Ordinal)
                .Replace("@GTE@", ProtocolGtePrefix, StringComparison.Ordinal)
                .Replace("@MMIO_WRITE@", ProtocolMmioWritePrefix, StringComparison.Ordinal)
                .Replace("@MMIO_VALUE@", ProtocolMmioValueReply, StringComparison.Ordinal)
                .Replace("@MMIO_REFUSED@", ProtocolMmioRefusedReply, StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_UNAVAILABLE@", MmioUnavailableExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_REFUSED@", MmioRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_MMIO_PROTOCOL@", MmioProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@RETIRED@", ProtocolRetiredPrefix, StringComparison.Ordinal)
                .Replace("@RETIRED_ACK@", ProtocolRetiredAckReply, StringComparison.Ordinal)
                .Replace("@RETIRED_ACK_IRQ@", ProtocolRetiredAckInterruptReply, StringComparison.Ordinal)
                .Replace("@RETIRED_REFUSED@", ProtocolRetiredRefusedReply, StringComparison.Ordinal)
                .Replace("@RETIRED_THRESHOLD@", RetiredReportThreshold.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_RETIRED_REFUSED@", RetiredRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_RETIRED_PROTOCOL@", RetiredProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@EXIT_FALLBACK_PROTOCOL@", FallbackProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@COP0_ACCESS@", ProtocolCop0AccessPrefix, StringComparison.Ordinal)
                .Replace("@GUEST_EXCEPTIONS@", GuestExceptionsFlag, StringComparison.Ordinal)
                .Replace("@EXACT_DEVICE_TIME@", ExactDeviceTimeFlag, StringComparison.Ordinal)
                // Restore the original main() signature over the standalone-marker comment.
                // The marker only sits where the signature used to be, so this perfectly
                // reinstates the original main function for the non-standalone artifact.
                .Replace("/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */", "int main(int argc, char** argv) {", StringComparison.Ordinal),
            null,
            null);
    }

    /// <summary>
    /// Generates a standalone artifact with embedded initial state and program image.
    /// The resulting C source can be compiled into a single executable that runs
    /// without any external input files.
    /// </summary>
    /// <param name="hostBridge">
    /// When true, the generated <c>main()</c> can attach the shared Runtime host
    /// bridge (<c>--host-transfer</c>), so the embedded-image artifact runs
    /// recompiled code against the shared Runtime with no input files and no Studio
    /// (Phase 2, Issue #757). When false the artifact is exactly Phase 1's
    /// parentless run.
    /// </param>
    public static RecompiledArtifactCodeGenResult GenerateStandalone(
        RecompilerHostCodeGenResult generatedDispatch,
        IReadOnlyList<uint> initialGpr,
        uint initialHi,
        uint initialLo,
        uint initialPc,
        uint budget,
        IReadOnlyList<(uint Address, uint Value)> initialMemory,
        uint imageLoadAddress,
        IReadOnlyList<byte> programImage,
        bool hostBridge = false,
        string? directLaunchHostRelativePath = null,
        WindowedLaunch? windowedLaunch = null)
    {
        ArgumentNullException.ThrowIfNull(generatedDispatch);
        ArgumentNullException.ThrowIfNull(initialGpr);
        ArgumentNullException.ThrowIfNull(initialMemory);
        ArgumentNullException.ThrowIfNull(programImage);

        if (!generatedDispatch.Success || generatedDispatch.Source is null)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                generatedDispatch.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                generatedDispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        if (initialGpr.Count != 32)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "INVALID_GPR_COUNT",
                "Initial GPR array must have exactly 32 elements.");
        }

        if (initialMemory.Count > MaxInitEntries)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "TOO_MANY_INIT_ENTRIES",
                $"Initial memory has {initialMemory.Count} entries, exceeding the {MaxInitEntries} limit.");
        }

        // Build the standalone driver with embedded data; validate and propagate any
        // structured failure (missing placeholder, file-IO leak) before returning.
        var standaloneSource = BuildStandaloneSource(
            generatedDispatch.Source!,
            DriverSource,
            initialGpr,
            initialHi,
            initialLo,
            initialPc,
            budget,
            initialMemory,
            imageLoadAddress,
            programImage,
            hostBridge,
            directLaunchHostRelativePath,
            windowedLaunch);

        if (!standaloneSource.Success)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                standaloneSource.DiagnosticCode ?? "UPSTREAM_CODEGEN_FAILED",
                standaloneSource.DiagnosticMessage ?? "Standalone source generation failed.");
        }

        return standaloneSource;
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle) =>
        needle.Length == 0 ? 0 : haystack.Split(needle, StringSplitOptions.None).Length - 1;

    internal static RecompiledArtifactCodeGenResult BuildStandaloneSource(
        string dispatchSource,
        string driverTemplate,
        IReadOnlyList<uint> initialGpr,
        uint initialHi,
        uint initialLo,
        uint initialPc,
        uint budget,
        IReadOnlyList<(uint Address, uint Value)> initialMemory,
        uint imageLoadAddress,
        IReadOnlyList<byte> programImage,
        bool hostBridge = false,
        string? directLaunchHostRelativePath = null,
        WindowedLaunch? windowedLaunch = null)
    {
        var sb = new System.Text.StringBuilder();

        // Embedded initial GPRs
        sb.AppendLine("static const uint32_t embedded_gpr[32] = {");
        for (int i = 0; i < 32; i++)
        {
            sb.Append($"    0x{initialGpr[i]:X8}u");
            if (i < 31) sb.Append(",");
            sb.AppendLine();
        }
        sb.AppendLine("};");

        // Embedded HI/LO
        sb.AppendLine($"static const uint32_t embedded_hi = 0x{initialHi:X8}u;");
        sb.AppendLine($"static const uint32_t embedded_lo = 0x{initialLo:X8}u;");

        // Embedded PC
        sb.AppendLine($"static const uint32_t embedded_pc = 0x{initialPc:X8}u;");

        // Embedded budget
        sb.AppendLine($"static const uint32_t embedded_budget = {budget}u;");

        // Embedded initial memory
        sb.AppendLine($"static const uint32_t embedded_init_count = {initialMemory.Count}u;");
        if (initialMemory.Count > 0)
        {
            sb.AppendLine("static const uint32_t embedded_init_addrs[] = {");
            for (int i = 0; i < initialMemory.Count; i++)
            {
                sb.Append($"    0x{initialMemory[i].Address:X8}u");
                if (i < initialMemory.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("};");

            sb.AppendLine("static const uint32_t embedded_init_vals[] = {");
            for (int i = 0; i < initialMemory.Count; i++)
            {
                sb.Append($"    0x{initialMemory[i].Value:X8}u");
                if (i < initialMemory.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("};");
        }
        else
        {
            sb.AppendLine("static const uint32_t embedded_init_addrs[1] = {0};");
            sb.AppendLine("static const uint32_t embedded_init_vals[1] = {0};");
        }

        // Embedded program image
        sb.AppendLine($"static const uint32_t embedded_image_load_address = 0x{imageLoadAddress:X8}u;");
        sb.AppendLine($"static const uint32_t embedded_image_len = {programImage.Count}u;");
        sb.AppendLine("static const uint8_t embedded_image[] = {");
        for (int i = 0; i < programImage.Count; i++)
        {
            if (i % 16 == 0) sb.Append("    ");
            sb.Append($"0x{programImage[i]:X2}");
            if (i < programImage.Count - 1) sb.Append(", ");
            if (i % 16 == 15 || i == programImage.Count - 1) sb.AppendLine();
        }
        sb.AppendLine("};");

        // Build the standalone dispatch source: add the unsupported-transfer reason and
        // remap the unknown-PC-with-steps branch so a run that stops at an unresolved
        // transfer (no host_transfer attached) is not reported as success (Issue #757).
        // These Replace targets are the exact lines emitted by RecompilerHostCodeGen.Generate;
        // the legacy host-transfer artifact path leaves host_transfer != null, so this patch
        // applies only to the standalone path.
        // The unsupported-transfer reason is defined for the standalone unknown-PC path
        // (Issue #757). Insert its #define right after the budget-exceeded reason so it is
        // available to recompiler_dispatch before main runs the guest.
        const string unsupportedTransferReasonDefine = "\n#define RECOMPILER_REASON_UNSUPPORTED_TRANSFER 5";
        var unsupportedTransferReasonLine =
            "#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED 7" + unsupportedTransferReasonDefine;

        const string unknownPcSuccessLine =
            "        if (steps > 0) { state->termination_reason = RECOMPILER_REASON_SUCCESS; return 0; }";
        const string unknownPcUnsupportedTransferLine =
            "        if (steps > 0) { state->termination_reason = RECOMPILER_REASON_UNSUPPORTED_TRANSFER; return 0; }";

        var unknownPcLineCount = CountOccurrences(dispatchSource, unknownPcSuccessLine);
        if (unknownPcLineCount != 1)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                unknownPcLineCount == 0
                    ? "STANDALONE_DISPATCH_SEMANTICS_MISSING_TARGET"
                    : "STANDALONE_DISPATCH_SEMANTICS_DUPLICATE_TARGET",
                unknownPcLineCount == 0
                    ? "The dispatch source's unknown-PC boundary rewrite target was not found; the standalone rewrite must not apply."
                    : "The dispatch source's unknown-PC boundary rewrite target appears more than once; the standalone rewrite must not apply.");
        }

        var standaloneDispatchSource = dispatchSource.Replace(
            "#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED 7",
            unsupportedTransferReasonLine,
            StringComparison.Ordinal).Replace(
            unknownPcSuccessLine,
            unknownPcUnsupportedTransferLine,
            StringComparison.Ordinal);

        // Verify the rewrite applied exactly once and no other branch was mutated.
        if (CountOccurrences(standaloneDispatchSource, unknownPcSuccessLine) != 0
            || CountOccurrences(standaloneDispatchSource, unknownPcUnsupportedTransferLine) != 1
            || !standaloneDispatchSource.Contains("#define RECOMPILER_REASON_UNSUPPORTED_TRANSFER 5"))
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "STANDALONE_DISPATCH_SEMANTICS_NOT_APPLIED",
                "The standalone dispatch rewrite did not apply exactly once as expected.");
        }

        // Build the full driver source with replacements, then replace the driver's
        // original main() with the standalone version at the explicit placeholder.
        var fullDriver = driverTemplate
            .Replace("#define PSX_MAX_INIT 4096u", $"#define PSX_MAX_INIT {MaxInitEntries}u", StringComparison.Ordinal)
            .Replace("@MMIO_READ@", ProtocolMmioReadPrefix, StringComparison.Ordinal)
            .Replace("@GTE@", ProtocolGtePrefix, StringComparison.Ordinal)
            .Replace("@MMIO_WRITE@", ProtocolMmioWritePrefix, StringComparison.Ordinal)
            .Replace("@MMIO_VALUE@", ProtocolMmioValueReply, StringComparison.Ordinal)
            .Replace("@MMIO_REFUSED@", ProtocolMmioRefusedReply, StringComparison.Ordinal)
            .Replace("@EXIT_MMIO_UNAVAILABLE@", MmioUnavailableExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@EXIT_MMIO_REFUSED@", MmioRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@EXIT_MMIO_PROTOCOL@", MmioProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@RETIRED@", ProtocolRetiredPrefix, StringComparison.Ordinal)
            .Replace("@RETIRED_ACK@", ProtocolRetiredAckReply, StringComparison.Ordinal)
            .Replace("@RETIRED_ACK_IRQ@", ProtocolRetiredAckInterruptReply, StringComparison.Ordinal)
            .Replace("@RETIRED_REFUSED@", ProtocolRetiredRefusedReply, StringComparison.Ordinal)
            .Replace("@RETIRED_THRESHOLD@", RetiredReportThreshold.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@EXIT_RETIRED_REFUSED@", RetiredRefusedExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@EXIT_RETIRED_PROTOCOL@", RetiredProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@EXIT_FALLBACK_PROTOCOL@", FallbackProtocolExitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@COP0_ACCESS@", ProtocolCop0AccessPrefix, StringComparison.Ordinal)
            .Replace("@GUEST_EXCEPTIONS@", GuestExceptionsFlag, StringComparison.Ordinal)
            .Replace("@EXACT_DEVICE_TIME@", ExactDeviceTimeFlag, StringComparison.Ordinal);

        // Replace the original driver's main function with the standalone version. The
        // marker comment is the explicit replacement point: we match on a dedicated
        // comment that sits exactly where the old main began, not on the signature
        // text itself (fragile). The old main body immediately follows the marker, so
        // we replace the whole [marker .. old main closing brace] region at once.
        var (launcherPrologue, standaloneMain) = BuildStandaloneMain(hostBridge, directLaunchHostRelativePath, windowedLaunch);
        const string mainMarker = "/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */";

        // Split the launcher prologue into a file-scope header (preprocessor directives
        // needed for POSIX declarations before the dispatch source includes) and a runtime
        // block (run_via_runtime_host + delegation check, placed just before the main
        // marker). This avoids defining run_via_runtime_host twice.
        string prologueHeader = "";
        string prologueRuntime = launcherPrologue ?? "";
        if (prologueRuntime.Length > 0)
        {
            int split = prologueRuntime.IndexOf("    static int run_via_runtime_host(void)",
                                                StringComparison.Ordinal);
            // The runtime block marker is always present inside a launcher prologue.
            System.Diagnostics.Debug.Assert(split > 0, "launcher prologue runtime block not found");
            prologueHeader = prologueRuntime.Substring(0, split);
            prologueRuntime = prologueRuntime.Substring(split);
        }
        var mainStart = fullDriver.IndexOf(mainMarker, StringComparison.Ordinal);
        if (mainStart < 0)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "STANDALONE_MAIN_MISSING",
                "The driver template is missing the standalone main marker; the standalone artifact cannot be built.");
        }

        // Detect a duplicated marker: the replacement point must be unique, otherwise
        // the region we would clobber is ambiguous.
        if (fullDriver.Split(mainMarker, StringSplitOptions.None).Length - 1 > 1)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "STANDALONE_MAIN_DUPLICATED",
                "The driver template contains the standalone main marker more than once; the main replacement point is ambiguous.");
        }

        // The original driver's main is the last function in the template, so its
        // closing brace is the last '}' in the generated source. Replace the entire
        // old-main region so the standalone artifact cannot accidentally inherit the
        // old main's file-reading body.
        var mainEnd = fullDriver.LastIndexOf('}');
        if (mainEnd <= mainStart)
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "STANDALONE_MAIN_PARSE_ERROR",
                "The driver template's main marker and its closing brace are in an unexpected order.");
        }

                if (prologueRuntime.Length > 0)
        {
            fullDriver = prologueRuntime + fullDriver.Substring(0, mainStart) + standaloneMain + fullDriver.Substring(mainEnd + 1);
        }
        else
        {
            fullDriver = fullDriver.Substring(0, mainStart) + standaloneMain + fullDriver.Substring(mainEnd + 1);
        }

        // Validate that the standalone main was inserted and that no file-reading
        // helper from the original driver leaked into the source (the artifact must be
        // self-contained). If the region replacement missed the old main, its fopen /
        // fscanf usage would still be present and this check catches that.
        if (fullDriver.Contains("fopen") || fullDriver.Contains("fscanf") || fullDriver.Contains("argv[1]") || fullDriver.Contains("argv[2]"))
        {
            return new RecompiledArtifactCodeGenResult(
                false,
                null,
                "STANDALONE_FILE_IO_LEAK",
                "The standalone artifact must be self-contained and must not contain file-reading helpers.");
        }

        return new RecompiledArtifactCodeGenResult(
            true, prologueHeader + standaloneDispatchSource + "\n" + sb.ToString() + "\n" + fullDriver, null, null);
    }

    /// <summary>
    /// A windowed (GUI) direct launch (Issue #830): the launcher starts <paramref name="HostBinaryName"/> from the
    /// <c>runtime/</c> directory with <paramref name="ArgumentRelativePath"/> as its argument, creates it without a console, and
    /// ties its lifetime to a Job Object with kill-on-close on Windows. Only the Windows launcher supports it.
    /// </summary>
    public sealed record WindowedLaunch(string HostBinaryName, string ArgumentRelativePath);

    /// <summary>
    /// Turns the direct-launch prologue into the windowed launcher (Issue #830). Each replacement must find its target, so a
    /// change to the headless prologue fails here instead of silently producing a console launcher. Headless output never passes
    /// through this method.
    /// </summary>
    private static string WindowedLauncherPatch(string prologue, WindowedLaunch windowed)
    {
        var text = Patch(prologue, "\"PSXRecomp.RuntimeHost.exe\"", "\"" + windowed.HostBinaryName + "\"");
        text = Patch(text, "\"runtime/runtime-host.json\"", "\"" + windowed.ArgumentRelativePath + "\"");
        text = Patch(text, "TRUE, 0, NULL, NULL, &si, &pi", "TRUE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi");
        text = Patch(text, "STARTUPINFOW si = { sizeof(si) };", @"HANDLE job = CreateJobObjectW(NULL, NULL);
        if (job) {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION job_limits;
            memset(&job_limits, 0, sizeof(job_limits));
            job_limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, &job_limits, sizeof(job_limits));
        }
        STARTUPINFOW si = { sizeof(si) };");
        text = Patch(text, "WaitForSingleObject(pi.hProcess, INFINITE);", @"if (job) AssignProcessToJobObject(job, pi.hProcess);
        WaitForSingleObject(pi.hProcess, INFINITE);");
        // Closing the last handle of the job ends any process still in it, so a child left behind is reaped.
        text = Patch(text, "CloseHandle(pi.hProcess);", @"if (job) CloseHandle(job);
        CloseHandle(pi.hProcess);");
        return text;
    }

    private static string Patch(string text, string target, string replacement)
    {
        if (!text.Contains(target, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Windowed launcher patch target not found in the direct-launch prologue: {target}");
        }

        return text.Replace(target, replacement, StringComparison.Ordinal);
    }

    private static (string? LauncherPrologue, string Main) BuildStandaloneMain(
        bool hostBridge, string? directLaunchHostRelativePath, WindowedLaunch? windowedLaunch)
    {
        // Phase 2 (Issue #757): the embedded-image artifact may attach the shared
        // Runtime host bridge when the caller opts in (--host-transfer), so it runs
        // recompiled code with no input files and no Studio. The attach path is the
        // driver's single artifact_attach_host_bridge helper (shared with the
        // file-driven main), and the parentless Phase 1 behavior is preserved when
        // the bridge is not requested.
        string bridgePrologue = hostBridge
            ? @"
int main(int argc, char** argv) {
"
            : @"
int main(int argc, char** argv) {
    (void)argc; (void)argv;
";

        string bridgeAttach = hostBridge ? @"
    /* Firmware mode (Issue #732): guest SYSCALL/BREAK enter the guest's own exception vector. */
    state.guest_exceptions = 0;
    state.host_transfer = 0;
    state.host_syscall = 0;
    state.host_retired = 0;
    state.host_interrupt = 0;
    artifact_mmio_bridge = 0;

    /* Attach the shared Runtime host bridge, opt-in only: without --host-transfer
       the run never blocks on a handshake (a parentless Phase 1 launch or stray
       argument takes this branch, exactly as before). */
    {
        int attach_bridge = 0;
        for (int argi = 1; argi < argc; argi++) {
            if (strcmp(argv[argi], ""--guest-exceptions"") == 0) state.guest_exceptions = 1u;
            if (strcmp(argv[argi], ""--exact-device-time"") == 0) artifact_exact_time = 1;
            if (strcmp(argv[argi], ""--host-transfer"") == 0) attach_bridge = 1;
        }
        if (attach_bridge) artifact_attach_host_bridge(&state);
    }
" : @"
    /* Firmware mode (Issue #732): guest SYSCALL/BREAK enter the guest's own exception vector. */
    state.guest_exceptions = 0;
    state.host_transfer = 0;
    state.host_syscall = 0;
    state.host_retired = 0;
    state.host_interrupt = 0;
    artifact_mmio_bridge = 0;
";

        var launcherPrologue = directLaunchHostRelativePath is not null
            ? @"
    /* Bundled Runtime host discovery for direct launch (Issue #739 Phase 2 Step C,
       ADR-027 Option B): the runtime host and its manifest live in a <runtime>
       subdirectory next to this executable, and the names are fixed so a generated
       artifact never needs an absolute path or an environment variable. */
#if defined(_WIN32)
#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <wchar.h>
#define RUNTIME_HOST_BINARY ""PSXRecomp.RuntimeHost.exe""
#if !defined(PATH_MAX)
#define PATH_MAX MAX_PATH
#endif
#else
/* macOS: dyld API for executable path resolution (see below); Linux and
   other Unix: /proc/self/exe. Enable POSIX declarations under strict
   -std=c11. */
#define _POSIX_C_SOURCE 200809L
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <errno.h>
#include <limits.h>
#if defined(__APPLE__)
#include <mach-o/dyld.h>
#endif
#define RUNTIME_HOST_BINARY ""PSXRecomp.RuntimeHost""
#endif
#if !defined(PATH_MAX)
#define PATH_MAX 1024
#endif
#define RUNTIME_HOST_REL_PATH ""runtime/"" RUNTIME_HOST_BINARY
#define RUNTIME_HOST_MANIFEST ""runtime/runtime-host.json""
#define MissingRuntimeHostExitCode 78

    /* Launch the bundled Runtime host with the manifest path so it can run the
       embedded artifact (direct launch, Issue #739 Phase 2 Step C, ADR-027). On
       success the runtime host replaces this process; on failure it returns
       MissingRuntimeHostExitCode (78) with a diagnostic. */
    static int run_via_runtime_host(void) {
        char exe_dir[PATH_MAX];
#ifdef _WIN32
        wchar_t exe_w[MAX_PATH];
        DWORD n = GetModuleFileNameW(NULL, exe_w, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) {
            fprintf(stderr, ""error: cannot resolve this executable path (error %lu)\n"", (unsigned long)GetLastError());
            return MissingRuntimeHostExitCode;
        }
        wchar_t* slash = wcsrchr(exe_w, L'\\');
        if (slash) *slash = L'\0';
        else { exe_w[0] = L'.'; exe_w[1] = L'\0'; }
        int len = WideCharToMultiByte(CP_UTF8, 0, exe_w, -1, NULL, 0, NULL, NULL);
        char* utf8 = malloc(len);
        if (!utf8) return MissingRuntimeHostExitCode;
        WideCharToMultiByte(CP_UTF8, 0, exe_w, -1, utf8, len, NULL, NULL);
        snprintf(exe_dir, sizeof(exe_dir), ""%s"", utf8);
        free(utf8);
#else
#if defined(__APPLE__)
        /* macOS: resolve executable path via dyld. */
        char exe_path[PATH_MAX * 2];
        uint32_t len = sizeof(exe_path);
        if (_NSGetExecutablePath(exe_path, &len) != 0) {
            fprintf(stderr, ""error: cannot resolve executable path\n"");
            return MissingRuntimeHostExitCode;
        }
        strncpy(exe_dir, exe_path, sizeof(exe_dir) - 1);
        exe_dir[sizeof(exe_dir) - 1] = '\0';
        char* slash = strrchr(exe_dir, '/');
        if (slash) *slash = '\0';
#else
        /* Linux and other Unix systems: resolve executable path via /proc/self/exe. */
        ssize_t r = readlink(""/proc/self/exe"", exe_dir, sizeof(exe_dir) - 1);
        if (r <= 0) {
            fprintf(stderr, ""error: cannot resolve this executable path: %s\n"", strerror(errno));
            return MissingRuntimeHostExitCode;
        }
        exe_dir[r] = '\0';
        char* slash = strrchr(exe_dir, '/');
        if (slash) *slash = '\0';
#endif
#endif

        char host_path[PATH_MAX + 64];
        char manifest_path[PATH_MAX + 64];
        snprintf(host_path, sizeof(host_path), ""%s/%s"", exe_dir, RUNTIME_HOST_REL_PATH);
        snprintf(manifest_path, sizeof(manifest_path), ""%s/%s"", exe_dir, RUNTIME_HOST_MANIFEST);

#ifdef _WIN32
        wchar_t host_w[PATH_MAX];
        wchar_t manifest_w[PATH_MAX];
        int hl = MultiByteToWideChar(CP_UTF8, 0, host_path, -1, host_w, PATH_MAX);
        int ml = MultiByteToWideChar(CP_UTF8, 0, manifest_path, -1, manifest_w, PATH_MAX);
        if (hl == 0 || ml == 0) {
            fprintf(stderr, ""error: cannot encode runtime host paths (error %lu)\n"", (unsigned long)GetLastError());
            return MissingRuntimeHostExitCode;
        }
        wchar_t cmd_line[PATH_MAX * 2 + 16];
        /* Quote both paths to handle spaces and special characters safely. */
        int cmd_len = swprintf(cmd_line, PATH_MAX * 2 + 16, L""\""%ls\"" \""%ls\"""", host_w, manifest_w);
        if (cmd_len < 0) {
            fprintf(stderr, ""error: cannot build command line\n"");
            return MissingRuntimeHostExitCode;
        }

        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = { 0 };
        if (!CreateProcessW(NULL, cmd_line, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
            fprintf(stderr, ""error: could not start the runtime host (error %lu)\n"", (unsigned long)GetLastError());
            return MissingRuntimeHostExitCode;
        }
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD exit_code = 0;
        GetExitCodeProcess(pi.hProcess, &exit_code);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return (int)exit_code;
#else
        if (access(host_path, F_OK) != 0) {
            fprintf(stderr, ""error: runtime host not found: %s\n"", host_path);
            return MissingRuntimeHostExitCode;
        }
        execl(host_path, host_path, manifest_path, NULL);
        fprintf(stderr, ""error: could not launch the runtime host: %s\n"", strerror(errno));
        return MissingRuntimeHostExitCode;
#endif
    }
"
            : null;

        if (launcherPrologue is not null && windowedLaunch is not null)
        {
            launcherPrologue = WindowedLauncherPatch(launcherPrologue, windowedLaunch);
        }

        string delegationCheck = launcherPrologue is not null
            ? @"
    /* Direct launch (Issue #739 Phase 2 Step C, ADR-027 Option B): delegate to
       the bundled Runtime host so the packaged distribution can launch with no
       Studio, CLI or development repository present. Re-launched by the runtime
       host with --host-transfer or --runtime-host, which suppresses this delegation. */
    {
        int skip = 0;
        for (int argi = 1; argi < argc; argi++) {
            if (strcmp(argv[argi], ""--host-transfer"") == 0) skip = 1;
            if (strcmp(argv[argi], ""--runtime-host"") == 0) skip = 1;
        }
        if (!skip) {
            int ret = run_via_runtime_host();
            return ret;
        }
    }
"
            : @"";

        return (launcherPrologue, bridgePrologue + delegationCheck + @"    RecompilerState state;

    memset(&state, 0, sizeof(state));

    // Copy embedded initial GPRs
    for (int i = 0; i < 32; i++) {
        state.gpr[i] = embedded_gpr[i];
    }
    state.hi = embedded_hi;
    state.lo = embedded_lo;
    state.pc = embedded_pc;
    state.gpr[0] = 0;

    uint32_t budget = embedded_budget;

    // Apply embedded initial memory
    memset(artifact_ram, 0, sizeof(artifact_ram));
    for (uint32_t i = 0; i < embedded_init_count; i++) {
        artifact_ram_write8(embedded_init_addrs[i], (uint8_t)embedded_init_vals[i]);
    }

    // Load embedded program image
    uint32_t image_pa = artifact_translate(embedded_image_load_address);
    int image_in_rom = image_pa >= PSX_ROM_BASE && image_pa - PSX_ROM_BASE < PSX_ROM_SIZE;
    if (embedded_image_len == 0u
        || (image_in_rom && (unsigned long long)embedded_image_len > (unsigned long long)(PSX_ROM_SIZE - (image_pa - PSX_ROM_BASE)))) {
        return 95; /* InvalidImage */
    }
    artifact_rom_loaded = image_in_rom;
    if (!image_in_rom && (image_pa >= PSX_RAM_MIRROR_END
        || (unsigned long long)embedded_image_len > (unsigned long long)(PSX_RAM_MIRROR_END - image_pa))) {
        return 95; /* InvalidImage */
    }

    for (uint32_t image_i = 0; image_i < embedded_image_len; image_i++) {
        if (image_in_rom) artifact_rom[image_pa - PSX_ROM_BASE + image_i] = embedded_image[image_i];
        else artifact_ram[(image_pa + image_i) & (PSX_RAM_SIZE - 1u)] = embedded_image[image_i];
    }

" + bridgeAttach + @"    recompiler_dispatch(&state, budget);
    artifact_report_retired();

    printf(""RSNAPSHOT_BEGIN\n"");
    printf(""termination=%d\n"", (int)state.termination_reason);
    printf(""pc=0x%08X\n"", state.pc);
    printf(""hi=0x%08X\n"", state.hi);
    printf(""lo=0x%08X\n"", state.lo);
    for (int i = 0; i < 32; i++) printf(""gpr[%d]=0x%08X\n"", i, state.gpr[i]);
    printf(""exception.raised=%d\n"", (int)state.exception_raised);
    printf(""exception.code=0x%08X\n"", state.exception_code);
    printf(""exception.faultPc=0x%08X\n"", state.exception_fault_pc);
    printf(""exception.inDelaySlot=%d\n"", (int)state.exception_in_delay_slot);
    printf(""cop0.sr=0x%08X\n"", state.cop0_sr);
    printf(""cop0.cause=0x%08X\n"", state.cop0_cause);
    printf(""cop0.epc=0x%08X\n"", state.cop0_epc);
    printf(""RSNAPSHOT_END\n"");

    return (int)state.termination_reason;
}
");
    }

    /// <summary>Artifact exit code: the program image file named by <c>argv[2]</c> could not be opened.</summary>
    public const int CannotOpenImageExitCode = 94;

    /// <summary>Artifact exit code: the program image is empty, does not fit guest RAM, or its
    /// file length differs from the length the input file declares (Issue #637).</summary>
    public const int InvalidImageExitCode = 95;

    /// <summary>Artifact exit code: a guest access outside RAM with no host MMIO bridge attached (Issue #678).</summary>
    public const int MmioUnavailableExitCode = 96;

    /// <summary>Artifact exit code: the host refused a guest MMIO access (unsupported address or device failure; Issue #678).</summary>
    public const int MmioRefusedExitCode = 97;

    /// <summary>Artifact exit code: the host's reply to an MMIO request was malformed or missing (Issue #678).</summary>
    public const int MmioProtocolExitCode = 98;

    /// <summary>Artifact exit code: the host refused a guest-time report (a device or scheduler failure; Issue #679).</summary>
    public const int RetiredRefusedExitCode = 99;

    /// <summary>Artifact exit code: the host's reply to a guest-time report was malformed or missing (Issue #679).</summary>
    public const int RetiredProtocolExitCode = 100;

    // Self-contained artifact entrypoint. Reads one guest state from the input
    // file named by argv[1]: 32 GPRs, hi, lo, pc, budget, an init-memory write
    // list, then "<image load address> <image byte length>". argv[2] names the
    // program image: the raw little-endian PS-X EXE text bytes, loaded into guest
    // RAM at that address after the init-memory writes (so the code image wins
    // any overlap, exactly as InterpreterTitleExecutionEngine.Load orders them).
    // A missing, empty, truncated, oversized, or out-of-RAM image fails closed
    // (exit 94/95) before any guest instruction runs (Issue #637).
    // Runs recompiler_dispatch exactly once, prints the resulting
    // state as a stable snapshot, and exits with the raw termination reason —
    // a tested, stable byte value (RecompilerIrTerminationReasonTests / this
    // driver's own contract tests classify it into success/blocked/failure for
    // CLI use, so the artifact itself carries no extra classification logic).
    //
    // With argv[3] == "--host-transfer", an unresolved control transfer is
    // offered to a listening parent instead of stopping immediately, over the
    // same line protocol Issue #362 proved for the differential harness.
    private const string DriverSource = @"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PSX_RAM_SIZE (2u * 1024u * 1024u)
#define PSX_MAX_INIT 4096u
static uint8_t artifact_ram[PSX_RAM_SIZE];
static uint32_t init_addrs[PSX_MAX_INIT];
static uint32_t init_vals[PSX_MAX_INIT];

static uint32_t artifact_translate(uint32_t va) {
    if (va <= 0x7FFFFFFFu) return va;
    if (va <= 0xBFFFFFFFu) return va & 0x1FFFFFFFu;
    return 0xFFFFFFFFu;
}

/* Low-8-MiB RAM mirror, matching PSXMemory / memory.rs: a translated physical
   address below 0x00800000 aliases the 2 MiB RAM through (& (RAM_SIZE - 1)),
   and the width check runs on the aliased offset, so an access that would run
   off the end of the physical buffer is unmapped rather than wrapped. */
#define PSX_RAM_MIRROR_END 0x00800000u

static int artifact_ram_offset(uint32_t address, uint32_t width, uint32_t* offset) {
    uint32_t pa = artifact_translate(address);
    if (pa >= PSX_RAM_MIRROR_END) return 0;
    pa &= PSX_RAM_SIZE - 1u;
    if (pa > PSX_RAM_SIZE - width) return 0;
    *offset = pa;
    return 1;
}

/* Run-time code guard (Issue #732). Every write into artifact_ram — a guest store, a host/device byte write (W), a
   fallback commit (K) — bumps the generation of each 4 KiB page it touches; generations only grow. A block compiled
   from RAM runs only after recompiler_code_current confirmed that RAM still holds exactly the words it was compiled
   from; the check is redone (a word compare) whenever a covered page's generation moved, so stale code never runs. */
static uint64_t artifact_page_gen[PSX_RAM_SIZE >> 12];
#define ARTIFACT_WROTE(pa, width) (artifact_page_gen[(pa) >> 12]++, artifact_page_gen[((pa) + (width) - 1u) >> 12]++)

int recompiler_code_current(void* core, uint32_t pc, const uint32_t* words, uint32_t count, uint64_t* seen) {
    uint32_t pa, i, p, last;
    uint64_t gen = 0;
    (void)core;
    /* Issue #734 review: fail closed on a zero-length load (no words to vouch for) and on a count the RAM cannot hold.
       With count <= PSX_RAM_SIZE / 4, 4u * count and pa + 4u * count - 1u cannot wrap, and the page sum is bounded. */
    if (count == 0u || count > PSX_RAM_SIZE / 4u) return 0;
    if (!artifact_ram_offset(pc, 4u * count, &pa)) return 0;
    /* Issue #734: sum every page the block spans, not only its first and last (a middle-page write must change the sum).
       Overflow: the span is at most PSX_RAM_SIZE / 4096 = 512 pages. One write event (ARTIFACT_WROTE or one page of a K
       commit) raises the sum by at most 512, so a uint64 wrap that could hide a change needs >= 2^64 / 512 = 2^55 events. */
    last = (pa + 4u * count - 1u) >> 12;
    for (p = pa >> 12; p <= last; p++) gen += artifact_page_gen[p];
    if (gen == *seen) return 1;
    for (i = 0; i < count; i++) {
        const uint8_t* b = artifact_ram + pa + 4u * i;
        if (((uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24)) != words[i]) return 0;
    }
    *seen = gen;
    return 1;
}

/* BIOS ROM window (Issue #732): physical 0x1FC00000, 512 KiB, reached through KUSEG/KSEG0/KSEG1 like RAM. A firmware
   image loaded there is artifact-local and read-only: loads are served here, stores are dropped (the ROM is not
   writable), and neither crosses the host protocol. Without a ROM image the window is not served here: an access is
   relayed and refused like any address the Runtime does not model (Issue #678), never answered with 0. */
#define PSX_ROM_BASE 0x1FC00000u
#define PSX_ROM_SIZE (512u * 1024u)
static uint8_t artifact_rom[PSX_ROM_SIZE];
static int artifact_rom_loaded = 0;

static int artifact_rom_offset(uint32_t address, uint32_t width, uint32_t* offset) {
    uint32_t pa = artifact_translate(address);
    if (!artifact_rom_loaded || pa < PSX_ROM_BASE || pa - PSX_ROM_BASE > PSX_ROM_SIZE - width) return 0;
    *offset = pa - PSX_ROM_BASE;
    return 1;
}

/* Guest memory split (Issue #678): RAM is artifact-local and never crosses the
   host protocol; everything else is a device access the parent's Runtime owns.
   The artifact_ram_* accessors are RAM-only and return 0 / drop outside it, which
   is what the byte-wise BIOS transfer requests below rely on. */
static uint8_t artifact_ram_read8(uint32_t address) {
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return 0;
    return artifact_ram[pa];
}

static void artifact_ram_write8(uint32_t address, uint8_t value) {
    uint32_t pa;
    if (!artifact_ram_offset(address, 1u, &pa)) return;
    artifact_ram[pa] = value;
    ARTIFACT_WROTE(pa, 1u);
}

/* Set once the parent has completed the host handshake; without it a non-RAM
   access has no Runtime to reach and stops the run instead of reading 0. */
static int artifact_mmio_bridge = 0;
/* Provenance of a vector arrival (Issue #662): 1 only between artifact_interrupt_boundary
   accepting a hardware INT and control leaving the vector it set pc to. */
static int artifact_int_entry = 0;

/* Guest time (#679/#744): the host owns device deadlines and sends execution credit
   in existing reply phases. Every interior/final instruction retirement checks that
   deadline locally. Reports synchronize before MMIO, transfers and traps, at deadlines,
   and at the additional 1024-instruction cap. No event is batched past its deadline.
   R/W requests during a report reach the artifact's RAM, then T + A/I refresh credit
   and IRQ level. Branch delay slots defer acceptance, not device delivery. */
static RecompilerState* artifact_state = (RecompilerState*)0;
static int artifact_exact_time = 0;

/* Credits are positive decimal tokens, not scanf's signed/partial conversions. */
static unsigned long long artifact_read_time_credit(void) {
    char token[32];
    unsigned long long value = 0ull;
    unsigned i;
    if (scanf(""%31s"", token) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
    for (i = 0; token[i] != '\0'; i++) {
        unsigned digit;
        if (token[i] < '0' || token[i] > '9') exit(@EXIT_RETIRED_PROTOCOL@);
        digit = (unsigned)(token[i] - '0');
        if (value > (UINT32_MAX - digit) / 10ull) exit(@EXIT_RETIRED_PROTOCOL@);
        value = value * 10ull + digit;
    }
    if (value == 0ull) exit(@EXIT_RETIRED_PROTOCOL@);
    return value;
}

/* The unreported retired instructions, now marked reported; 0 when there is nothing to report or no host. */
static unsigned long long artifact_retired_take(void) {
    unsigned long long pending;
    if (!artifact_mmio_bridge || artifact_state == (RecompilerState*)0) return 0ull;
    pending = artifact_state->retired_total - artifact_state->retired_reported;
    if (pending == 0ull) return 0ull;
    artifact_state->retired_reported = artifact_state->retired_total;
    return pending;
}

/* Serves the host's replies to a guest-time report (device RAM requests, the credit, the ack) up to the ack. */
static void artifact_retired_receive(void) {
    char cmd[8];
    unsigned long a, v;
    unsigned long long credit;
    int time_credit_seen = 0;
    for (;;) {
        if (scanf(""%7s"", cmd) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
        if (strcmp(cmd, ""R"") == 0) {
            if (scanf(""%lu"", &a) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
            printf(""RHOST_DATA %u\n"", (unsigned)artifact_ram_read8((uint32_t)a));
            fflush(stdout);
        } else if (strcmp(cmd, ""W"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2) exit(@EXIT_RETIRED_PROTOCOL@);
            artifact_ram_write8((uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (strcmp(cmd, ""T"") == 0) {
            if (time_credit_seen) exit(@EXIT_RETIRED_PROTOCOL@);
            credit = artifact_read_time_credit();
            if (UINT64_MAX - artifact_state->retired_reported < credit) exit(@EXIT_RETIRED_PROTOCOL@);
            artifact_state->event_deadline = artifact_state->retired_reported + credit;
            time_credit_seen = 1;
        } else if (strcmp(cmd, ""@RETIRED_ACK@"") == 0 || strcmp(cmd, ""@RETIRED_ACK_IRQ@"") == 0) {
            if (artifact_exact_time && !time_credit_seen) exit(@EXIT_RETIRED_PROTOCOL@);
            /* The ack also carries the interrupt line: CAUSE.IP2 mirrors it (Issue #680). */
            artifact_state->irq_line = strcmp(cmd, ""@RETIRED_ACK_IRQ@"") == 0 ? 1u : 0u;
            artifact_state->cop0_cause = (artifact_state->cop0_cause & ~0x400u) | (artifact_state->irq_line ? 0x400u : 0u);
            return;
        } else if (strcmp(cmd, ""@RETIRED_REFUSED@"") == 0) {
            exit(@EXIT_RETIRED_REFUSED@);
        } else {
            exit(@EXIT_RETIRED_PROTOCOL@);
        }
    }
}

static void artifact_report_retired(void) {
    unsigned long long pending = artifact_retired_take();
    if (pending == 0ull) return;
    printf(""%s%llu\n"", ""@RETIRED@"", pending);
    fflush(stdout);
    artifact_retired_receive();
}

/* An MMIO write is reported as soon as its unit retires, not at the next observation: the
   interpreter advances its devices after every instruction, so what a store sets going (a
   DMA kick, a CD-ROM command) has already happened — device-originated RAM writes included
   — before the next guest instruction runs. */
static int artifact_flush_after_unit = 0;

static void artifact_after_unit(RecompilerState* state) {
    if (artifact_flush_after_unit || state->retired_total >= state->event_deadline || state->retired_total - state->retired_reported >= @RETIRED_THRESHOLD@ull) {
        artifact_flush_after_unit = 0;
        artifact_report_retired();
    }
}

/* One width-aware request: the access is relayed as a single read/write of its
   guest width, never as bytes (MMIO has width-sensitive registers, FIFOs and
   read side effects). The parent answers V <value> or X; anything else, or no
   answer, stops the run. */
static uint32_t artifact_mmio_access(const char* tag, uint32_t width, uint32_t address, int is_write, uint32_t value) {
    char reply[8];
    unsigned long v;
    unsigned long long pending;
    if (!artifact_mmio_bridge) exit(@EXIT_MMIO_UNAVAILABLE@);
    /* Issue #768: the guest-time report that must precede the access and the access itself travel as ONE request line
       (@RETIRED@ count, R or W, width, physical, [value]): the host handles the report first and the access second, exactly
       the order two requests had, and answers with the report's replies followed by the access's reply. One round trip
       instead of two; nothing retires between them, so no device observes a different order. */
    pending = artifact_retired_take();
    if (pending != 0ull) {
        if (is_write) printf(""%s%llu W %u %lu %lu\n"", ""@RETIRED@"", pending, (unsigned)width, (unsigned long)artifact_translate(address), (unsigned long)value);
        else printf(""%s%llu R %u %lu\n"", ""@RETIRED@"", pending, (unsigned)width, (unsigned long)artifact_translate(address));
        fflush(stdout);
        artifact_retired_receive();
    } else {
        if (is_write) printf(""%s%u %lu %lu\n"", tag, (unsigned)width, (unsigned long)artifact_translate(address), (unsigned long)value);
        else printf(""%s%u %lu\n"", tag, (unsigned)width, (unsigned long)artifact_translate(address));
        fflush(stdout);
    }
    if (scanf(""%7s"", reply) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    if (strcmp(reply, ""@MMIO_REFUSED@"") == 0) exit(@EXIT_MMIO_REFUSED@);
    if (strcmp(reply, ""@MMIO_VALUE@"") != 0 || scanf(""%lu"", &v) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    artifact_flush_after_unit = 1;
    return (uint32_t)v;
}

/* Firmware COP0 outside SR/CAUSE/EPC belongs to the shared native core, including reset values.
   This additive service uses the ordinary request/reply phase; it never masquerades as MMIO. */
static uint32_t artifact_cop0_access(RecompilerState* state, uint32_t reg, uint32_t is_write, uint32_t value) {
    char reply[8];
    char token[32];
    uint32_t result = 0u;
    unsigned i, digit;
    (void)state;
    artifact_report_retired();
    printf(""@COP0_ACCESS@%u %u %lu\n"", (unsigned)reg, (unsigned)is_write, (unsigned long)value);
    fflush(stdout);
    if (scanf(""%7s"", reply) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    if (strcmp(reply, ""@MMIO_REFUSED@"") == 0) exit(@EXIT_MMIO_REFUSED@);
    if (strcmp(reply, ""@MMIO_VALUE@"") != 0 || scanf(""%31s"", token) != 1 || strlen(token) > 10u) exit(@EXIT_MMIO_PROTOCOL@);
    for (i = 0u; token[i] != '\0'; i++) {
        if (token[i] < '0' || token[i] > '9') exit(@EXIT_MMIO_PROTOCOL@);
        digit = (unsigned)(token[i] - '0');
        if (result > (UINT32_MAX - digit) / 10u) exit(@EXIT_MMIO_PROTOCOL@);
        result = result * 10u + digit;
    }
    return result;
}

/* The RAM mirror window that is not addressable (the seam past the 2 MiB
   buffer) stays unmapped, exactly as before; it is not a device. */
static int artifact_in_ram_window(uint32_t address) {
    return artifact_translate(address) < PSX_RAM_MIRROR_END;
}

uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 1u, &pa)) return artifact_ram[pa];
    if (artifact_in_ram_window(address)) return 0;
    if (artifact_rom_offset(address, 1u, &pa)) return artifact_rom[pa];
    return (uint8_t)artifact_mmio_access(""@MMIO_READ@"", 1u, address, 0, 0u);
}

uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 2u, &pa)) {
        return (uint16_t)(artifact_ram[pa] | ((uint16_t)artifact_ram[pa + 1] << 8));
    }
    if (artifact_in_ram_window(address)) return 0;
    if (artifact_rom_offset(address, 2u, &pa)) return (uint16_t)(artifact_rom[pa] | ((uint16_t)artifact_rom[pa + 1] << 8));
    return (uint16_t)artifact_mmio_access(""@MMIO_READ@"", 2u, address, 0, 0u);
}

uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 4u, &pa)) {
        return (uint32_t)(artifact_ram[pa]
            | ((uint32_t)artifact_ram[pa + 1] << 8)
            | ((uint32_t)artifact_ram[pa + 2] << 16)
            | ((uint32_t)artifact_ram[pa + 3] << 24));
    }
    if (artifact_in_ram_window(address)) return 0;
    if (artifact_rom_offset(address, 4u, &pa)) {
        return (uint32_t)(artifact_rom[pa]
            | ((uint32_t)artifact_rom[pa + 1] << 8)
            | ((uint32_t)artifact_rom[pa + 2] << 16)
            | ((uint32_t)artifact_rom[pa + 3] << 24));
    }
    return artifact_mmio_access(""@MMIO_READ@"", 4u, address, 0, 0u);
}

/* GTE (COP2, Issue #447): the GTE state is the parent's one GteRegisterBank, the same one its fallback
   interpreter executes against, so every access is relayed and nothing is cached here. */
static uint32_t artifact_gte_request(const char* op, uint32_t a, uint32_t b, int has_b) {
    char reply[8];
    unsigned long v;
    if (!artifact_mmio_bridge) exit(@EXIT_MMIO_UNAVAILABLE@);
    if (has_b) printf(""@GTE@%s %lu %lu\n"", op, (unsigned long)a, (unsigned long)b);
    else printf(""@GTE@%s %lu\n"", op, (unsigned long)a);
    fflush(stdout);
    if (scanf(""%7s"", reply) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    if (strcmp(reply, ""@MMIO_REFUSED@"") == 0) exit(@EXIT_MMIO_REFUSED@);
    if (strcmp(reply, ""@MMIO_VALUE@"") != 0 || scanf(""%lu"", &v) != 1) exit(@EXIT_MMIO_PROTOCOL@);
    return (uint32_t)v;
}

uint32_t recompiler_gte_read(void* core, uint32_t reg) { (void)core; return artifact_gte_request(""R"", reg, 0u, 0); }
void recompiler_gte_write(void* core, uint32_t reg, uint32_t value) { (void)core; (void)artifact_gte_request(""W"", reg, value, 1); }
int32_t recompiler_gte_command(void* core, uint32_t command) { (void)core; return (int32_t)artifact_gte_request(""C"", command, 0u, 0); }

void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 1u, &pa)) { artifact_ram[pa] = value; ARTIFACT_WROTE(pa, 1u); return; }
    if (artifact_in_ram_window(address) || artifact_rom_offset(address, 1u, &pa)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 1u, address, 1, (uint32_t)value);
}

void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 2u, &pa)) {
        artifact_ram[pa] = (uint8_t)value;
        artifact_ram[pa + 1] = (uint8_t)(value >> 8);
        ARTIFACT_WROTE(pa, 2u);
        return;
    }
    if (artifact_in_ram_window(address) || artifact_rom_offset(address, 2u, &pa)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 2u, address, 1, (uint32_t)value);
}

void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa;
    if (artifact_ram_offset(address, 4u, &pa)) {
        artifact_ram[pa] = (uint8_t)value;
        artifact_ram[pa + 1] = (uint8_t)(value >> 8);
        artifact_ram[pa + 2] = (uint8_t)(value >> 16);
        artifact_ram[pa + 3] = (uint8_t)(value >> 24);
        ARTIFACT_WROTE(pa, 4u);
        return;
    }
    if (artifact_in_ram_window(address) || artifact_rom_offset(address, 4u, &pa)) return;
    artifact_mmio_access(""@MMIO_WRITE@"", 4u, address, 1, value);
}

/* Host control-transfer protocol (ADR-014 / Issue #362, promoted from the
   differential harness for Issue #459). The artifact knows nothing about the
   BIOS: it only offers an unresolved pc and relays the parent's byte-level
   memory requests, so every BIOS semantic stays on the Runtime side of the
   boundary.

   child -> parent:  RHOST_INIT
                     RHOST_TRANSFER <pc> <gpr0> .. <gpr31>
                     RHOST_DATA <byte>                        (reply to R)
                     RHOST_OK                                 (reply to W)
                     RHOST_MMIO_READ <width> <physical>       (guest load outside RAM)
                     RHOST_MMIO_WRITE <width> <physical> <v>  (guest store outside RAM)
                     RHOST_RETIRED <count>                    (guest instructions retired; Issue #679)
   parent -> child:  R <physical address>
                     W <physical address> <byte>
                     N                                        (pc not claimed)
                     E                                        (reply: RHOST_COP0 <epc> <cause> <sr> <hi> <lo> <int entry>; Issue #662)
                     G <gpr index> <value> | H <hi> <lo>      (state write before a decision; Issue #662)
                     C <sr> | P                               (SR write | RFE pop, before a decision; Issues #663/#662)
                     L <0|1>                                  (interrupt line after a kernel handler; Issue #662)
                     F <version>                              (mixed-fallback query; reply RHOST_FALLBACK <version> <indirect> <irq>
                                                               <hi> <lo> <sr> <cause> <epc> <dirty pages> | RHOST_FALLBACK_REFUSED <why>; Issue #693)
                     Y                                        (reply RHOST_PAGE <index> <hex> ... RHOST_PAGES_END <hash>)
                     B <index> <hex> ... K <count> <hash>     (staged RAM write-back, applied only by a verified K -> RHOST_OK)
                     S <hi> <lo> <sr> <cause> <epc> <irq> <gpr1..gpr31>   (full CPU state write)
                     D <termination> <next pc> <has v0> <v0>   (decision)
                     V <value> | X                            (reply to an MMIO request)
                     T <instruction credit>                   (fresh deadline, before A/I or D/N; Issue #744)
                     A | I | X                                (reply to RHOST_RETIRED; I = interrupt line asserted, Issue #680)

   R/W stay RAM-only byte requests: they carry the BIOS HLE's own RAM access and, since
   Issue #679, the device-originated RAM access a scheduler advance can cause (CD-ROM
   DMA3). They are only ever sent while the child is waiting in a reply phase that serves
   them (a host transfer, a SYSCALL, the RHOST_RETIRED reply), so an MMIO request can
   never interleave with one. */
#define PSX_REG_V0 2

/* Mixed-execution fallback (Issue #693). Guest RAM stays artifact_ram; the interpreter works on a private copy the
   host keeps in step by page-granular copy-sync. fallback_shadow is the RAM image the host last held (all zero
   before the first sync, like the host's own fresh core RAM), so a page is dirty exactly when it differs from it.
   Pages travel as 4096 bytes in 8192 lowercase hex characters in ascending index order, covered by an FNV-1a hash
   over (index as 4 little-endian bytes, page bytes) per page. The write-back is staged and applied only by a
   verified commit (K), so artifact_ram is never left half-updated. Any malformed fallback command is fatal. */
#define PSX_PAGE_SIZE 4096u
#define PSX_PAGE_COUNT (PSX_RAM_SIZE / PSX_PAGE_SIZE)
#define PSX_FALLBACK_VERSION 1ul
static uint8_t fallback_shadow[PSX_RAM_SIZE];
static uint8_t fallback_stage[PSX_RAM_SIZE];
static uint32_t fallback_stage_page[PSX_PAGE_COUNT];
static uint32_t fallback_stage_count = 0u;
static uint32_t fallback_stage_hash = 2166136261u;

static uint32_t fallback_hash_page(uint32_t hash, uint32_t page, const uint8_t* data) {
    uint32_t i;
    for (i = 0; i < 4u; i++) { hash ^= (uint8_t)(page >> (8u * i)); hash *= 16777619u; }
    for (i = 0; i < PSX_PAGE_SIZE; i++) { hash ^= data[i]; hash *= 16777619u; }
    return hash;
}

/* Issue #732: a page whose generation (artifact_page_gen, bumped by every RAM writer) is unchanged since it last
   agreed with the shadow cannot differ from it, so only written pages are compared. */
static uint64_t fallback_shadow_gen[PSX_PAGE_COUNT];
static uint8_t fallback_shadow_known[PSX_PAGE_COUNT];

static void fallback_shadow_agrees(uint32_t p) {
    fallback_shadow_gen[p] = artifact_page_gen[p];
    fallback_shadow_known[p] = 1u;
}

static int fallback_page_dirty(uint32_t p) {
    if (fallback_shadow_known[p] && fallback_shadow_gen[p] == artifact_page_gen[p]) return 0;
    if (memcmp(artifact_ram + p * PSX_PAGE_SIZE, fallback_shadow + p * PSX_PAGE_SIZE, PSX_PAGE_SIZE) != 0) return 1;
    fallback_shadow_agrees(p);
    return 0;
}

static uint32_t fallback_dirty_count(void) {
    uint32_t p, n = 0u;
    for (p = 0; p < PSX_PAGE_COUNT; p++) {
        if (fallback_page_dirty(p)) n++;
    }
    return n;
}

static int fallback_nibble(int c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

/* Reads exactly one page of hex (leading whitespace skipped, a line end required after it). */
static int fallback_read_page(uint8_t* dst) {
    int c;
    uint32_t i;
    do { c = getchar(); } while (c == ' ' || c == '\n' || c == '\r');
    for (i = 0; i < PSX_PAGE_SIZE; i++) {
        int hi = fallback_nibble(c);
        int lo;
        if (hi < 0) return 0;
        lo = fallback_nibble(getchar());
        if (lo < 0) return 0;
        dst[i] = (uint8_t)((hi << 4) | lo);
        c = getchar();
    }
    return c == '\n' || c == '\r' || c == EOF;
}

static void fallback_send_pages(void) {
    static const char hex_digits[] = ""0123456789abcdef"";
    static char line[2u * PSX_PAGE_SIZE + 1u];
    uint32_t p, i, hash = 2166136261u;
    for (p = 0; p < PSX_PAGE_COUNT; p++) {
        uint8_t* page = artifact_ram + p * PSX_PAGE_SIZE;
        if (!fallback_page_dirty(p)) continue;
        for (i = 0; i < PSX_PAGE_SIZE; i++) {
            line[2u * i] = hex_digits[page[i] >> 4];
            line[2u * i + 1u] = hex_digits[page[i] & 15u];
        }
        line[2u * PSX_PAGE_SIZE] = 0;
        printf(""RHOST_PAGE %lu %s\n"", (unsigned long)p, line);
        hash = fallback_hash_page(hash, p, page);
        memcpy(fallback_shadow + p * PSX_PAGE_SIZE, page, PSX_PAGE_SIZE);
        fallback_shadow_agrees(p);
    }
    printf(""RHOST_PAGES_END %lu\n"", (unsigned long)hash);
    fflush(stdout);
}

static int32_t artifact_host_serve(RecompilerState* state) {
    int time_credit_seen = 0;
    int host_indirect_resume = 0;
    for (;;) {
        char cmd[8];
        unsigned long a, v, t, np, has_v0;
        if (scanf(""%7s"", cmd) != 1) exit(@EXIT_RETIRED_PROTOCOL@);
        /* `J` marks only the decision that immediately follows it: any other command between them (a second J, N, a
           query, a write, ...) is a protocol violation, so the mark can never reach a later or unrelated transfer. */
        if (host_indirect_resume && strcmp(cmd, ""D"") != 0) exit(@EXIT_RETIRED_PROTOCOL@);
        if (strcmp(cmd, ""T"") == 0) {
            unsigned long long credit;
            if (time_credit_seen) exit(@EXIT_RETIRED_PROTOCOL@);
            credit = artifact_read_time_credit();
            if (UINT64_MAX - state->retired_total < credit) exit(@EXIT_RETIRED_PROTOCOL@);
            state->event_deadline = state->retired_total + credit;
            time_credit_seen = 1;
        } else if (strcmp(cmd, ""F"") == 0) {
            if (scanf(""%lu"", &v) != 1) exit(@EXIT_FALLBACK_PROTOCOL@);
            if (v != PSX_FALLBACK_VERSION) {
                printf(""RHOST_FALLBACK_REFUSED version\n"");
            } else {
                printf(""RHOST_FALLBACK %lu %d %u %lu %lu %lu %lu %lu %lu\n"", PSX_FALLBACK_VERSION,
                       state->pc == state->indirect_target ? 1 : 0, (unsigned)state->irq_line,
                       (unsigned long)state->hi, (unsigned long)state->lo, (unsigned long)state->cop0_sr,
                       (unsigned long)state->cop0_cause, (unsigned long)state->cop0_epc, (unsigned long)fallback_dirty_count());
            }
            fflush(stdout);
        } else if (strcmp(cmd, ""Y"") == 0) {
            fallback_send_pages();
        } else if (strcmp(cmd, ""B"") == 0) {
            if (scanf(""%lu"", &a) != 1 || a >= PSX_PAGE_COUNT || fallback_stage_count >= PSX_PAGE_COUNT
                || (fallback_stage_count != 0u && a <= fallback_stage_page[fallback_stage_count - 1u])
                || !fallback_read_page(fallback_stage + fallback_stage_count * PSX_PAGE_SIZE)) exit(@EXIT_FALLBACK_PROTOCOL@);
            fallback_stage_page[fallback_stage_count] = (uint32_t)a;
            fallback_stage_hash = fallback_hash_page(fallback_stage_hash, (uint32_t)a, fallback_stage + fallback_stage_count * PSX_PAGE_SIZE);
            fallback_stage_count++;
        } else if (strcmp(cmd, ""K"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2) exit(@EXIT_FALLBACK_PROTOCOL@);
            if (a != (unsigned long)fallback_stage_count || v != (unsigned long)fallback_stage_hash) {
                fallback_stage_count = 0u;
                fallback_stage_hash = 2166136261u;
                printf(""RHOST_FALLBACK_REFUSED checksum\n"");
            } else {
                uint32_t s;
                for (s = 0; s < fallback_stage_count; s++) {
                    memcpy(artifact_ram + fallback_stage_page[s] * PSX_PAGE_SIZE, fallback_stage + s * PSX_PAGE_SIZE, PSX_PAGE_SIZE);
                    artifact_page_gen[fallback_stage_page[s]]++;
                    fallback_shadow_agrees(fallback_stage_page[s]);
                    memcpy(fallback_shadow + fallback_stage_page[s] * PSX_PAGE_SIZE, fallback_stage + s * PSX_PAGE_SIZE, PSX_PAGE_SIZE);
                }
                fallback_stage_count = 0u;
                fallback_stage_hash = 2166136261u;
                printf(""RHOST_OK\n"");
            }
            fflush(stdout);
        } else if (strcmp(cmd, ""S"") == 0) {
            unsigned long hi, lo, sr, cause, epc, irq;
            int g;
            if (scanf(""%lu %lu %lu %lu %lu %lu"", &hi, &lo, &sr, &cause, &epc, &irq) != 6 || irq > 1ul) exit(@EXIT_FALLBACK_PROTOCOL@);
            state->hi = (uint32_t)hi;
            state->lo = (uint32_t)lo;
            state->cop0_sr = (uint32_t)sr;
            state->cop0_epc = (uint32_t)epc;
            state->irq_line = (uint32_t)irq;
            state->cop0_cause = ((uint32_t)cause & ~0x400u) | (irq ? 0x400u : 0u);
            for (g = 1; g < 32; g++) {
                if (scanf(""%lu"", &v) != 1) exit(@EXIT_FALLBACK_PROTOCOL@);
                state->gpr[g] = (uint32_t)v;
            }
        } else if (strcmp(cmd, ""R"") == 0) {
            if (scanf(""%lu"", &a) != 1) return 1;
            printf(""RHOST_DATA %u\n"", (unsigned)artifact_ram_read8((uint32_t)a));
            fflush(stdout);
        } else if (strcmp(cmd, ""W"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            artifact_ram_write8((uint32_t)a, (uint8_t)v);
            printf(""RHOST_OK\n"");
            fflush(stdout);
        } else if (strcmp(cmd, ""C"") == 0) {
            if (scanf(""%lu"", &v) != 1) return 1;
            state->cop0_sr = (uint32_t)v;
        } else if (strcmp(cmd, ""E"") == 0) {
            printf(""RHOST_COP0 %lu %lu %lu %lu %lu %d\n"", (unsigned long)state->cop0_epc, (unsigned long)state->cop0_cause,
                   (unsigned long)state->cop0_sr, (unsigned long)state->hi, (unsigned long)state->lo, artifact_int_entry);
            fflush(stdout);
        } else if (strcmp(cmd, ""G"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2 || a == 0ul || a > 31ul) return 1;
            state->gpr[a] = (uint32_t)v;
        } else if (strcmp(cmd, ""H"") == 0) {
            if (scanf(""%lu %lu"", &a, &v) != 2) return 1;
            state->hi = (uint32_t)a;
            state->lo = (uint32_t)v;
        } else if (strcmp(cmd, ""L"") == 0) {
            if (scanf(""%lu"", &v) != 1) return 1;
            state->irq_line = v != 0ul ? 1u : 0u;
            state->cop0_cause = (state->cop0_cause & ~0x400u) | (state->irq_line ? 0x400u : 0u);
        } else if (strcmp(cmd, ""P"") == 0) {
            state->cop0_sr = (state->cop0_sr & ~0xFu) | ((state->cop0_sr >> 2) & 0xFu);
        } else if (strcmp(cmd, ""J"") == 0) {
            host_indirect_resume = 1; /* scoped to this serve call, so it can never mark a later decision */
        } else if (strcmp(cmd, ""D"") == 0) {
            if (artifact_exact_time && !time_credit_seen) exit(@EXIT_RETIRED_PROTOCOL@);
            if (scanf(""%lu %lu %lu %lu"", &t, &np, &has_v0, &v) != 4) return 1;
            state->termination_reason = (int32_t)t;
            state->next_pc = (uint32_t)np;
            /* Only a resume the host marked with `J` just before this decision is its register-indirect jump (an HLE
               BIOS `jr ra`, or the kernel exception handler's `jr k0; rfe`): the next pc is then a runtime target the mixed-fallback query may see as indirect.
               Every other success (patched jump table, SYSCALL resume, B0:17, fallback return) leaves
               indirect_target as the artifact's own JR/JALR set it (Issue #763). */
            if (t == 0ul && host_indirect_resume) state->indirect_target = (uint32_t)np;
            if (has_v0 != 0ul) state->gpr[PSX_REG_V0] = (uint32_t)v;
            return 0;
        } else if (strcmp(cmd, ""N"") == 0) {
            if (artifact_exact_time && !time_credit_seen) exit(@EXIT_RETIRED_PROTOCOL@);
            return 1; /* 'N': the parent does not claim this pc. */
        } else {
            exit(@EXIT_RETIRED_PROTOCOL@);
        }
    }
}

static int32_t artifact_host_transfer(RecompilerState* state) {
    int i;
    artifact_report_retired();
    printf(""RHOST_TRANSFER %lu"", (unsigned long)state->pc);
    for (i = 0; i < 32; i++) printf("" %lu"", (unsigned long)state->gpr[i]);
    printf(""\n"");
    fflush(stdout);
    {
        int32_t declined = artifact_host_serve(state);
        artifact_int_entry = 0; /* the vector arrival is consumed by this transfer */
        return declined;
    }
}

/* SYSCALL exception (Issue #663). The artifact is the CPU here: it does the exception
   entry (SR KU/IE push), the host says what the kernel handler leaves in SR (C), and
   the artifact does the return (RFE pop) and resumes after the SYSCALL. */
static int32_t artifact_host_syscall(RecompilerState* state) {
    uint32_t sr_entry;
    artifact_report_retired();
    /* The exception entry is part of the state, not just of the offer: commit the
       pushed KU/IE now, as the interpreter's CPU does, so every path that does not
       return to the guest (unsupported SYS, host decline, fail-closed) snapshots the
       post-entry SR. A serviced call overwrites it with the parent's C <sr> first. */
    (void)recompiler_exception_entry(state, 8u, state->exception_fault_pc, 0u);
    sr_entry = state->cop0_sr;
    printf(""RHOST_SYSCALL %lu %lu %lu\n"", (unsigned long)state->exception_fault_pc,
           (unsigned long)state->gpr[4], (unsigned long)sr_entry);
    fflush(stdout);
    int32_t declined = artifact_host_serve(state);
    if (declined == 0 && state->termination_reason == 0) {
        state->cop0_sr = (state->cop0_sr & ~0xFu) | ((state->cop0_sr >> 2) & 0xFu);
    }
    return declined;
}

/* Hardware INT delivery (Issue #680). Called by the dispatch at every instruction-fetch
   boundary outside a delay slot. The line (CAUSE.IP2) is the Interrupt Controller's aggregate
   pending state the host reported in its last guest-time ack; the artifact accepts it exactly
   as the interpreter's CPU does: only with SR.IEc (bit 0) and SR.IM2 (bit 10) set. The entry
   is the R3000A one (docs/cpu/exceptions.md): EPC = the interrupted instruction's pc, CAUSE
   Excode 0 / BD 0 with IP2 kept, the SR KU/IE stack pushed (the same push as the SYSCALL entry
   above), pc = the general exception vector chosen by SR.BEV. No guest handler is called and
   no instruction retires (no device time): the guest's own kernel path starts at the vector. */
static void artifact_interrupt_boundary(RecompilerState* state) {
    uint32_t sr = state->cop0_sr;
    /* Provenance only lasts while control is still at the vector the entry set. */
    if (state->pc != 0x80000080u && state->pc != 0xBFC00180u) artifact_int_entry = 0;
    if (state->irq_line == 0u || (sr & 0x1u) == 0u || (sr & 0x400u) == 0u) return;
    /* The same entry a guest SYSCALL/BREAK takes in firmware mode (Issue #732); IP2 is kept, not replaced. */
    state->pc = recompiler_exception_entry(state, 0u, state->pc, 0u);
    artifact_int_entry = 1;
}

/* Attach the shared Runtime host bridge (Issue #757 Phase 2). This is the
   artifact's only channel to the managed Runtime / BIOS HLE (ADR-014). One
   definition, called by both the file-driven main and the embedded-image main,
   so the two attach paths cannot diverge. */
static void artifact_attach_host_bridge(RecompilerState* state) {
    state->host_transfer = &artifact_host_transfer;
    state->host_syscall = &artifact_host_syscall;
    state->event_deadline = UINT64_MAX;
    state->host_retired = &artifact_after_unit;
    state->host_interrupt = &artifact_interrupt_boundary;
    artifact_state = state;
    artifact_mmio_bridge = 1;
    if (state->guest_exceptions) state->host_cop0 = &artifact_cop0_access;
    printf(""RHOST_INIT\n"");
    fflush(stdout);
    /* The parent's Runtime seeds its own jump-table sentinels here (byte
       R/W requests) before replying 'N' to this initial handshake, so its
       seeding is visible to the guest exactly as it is to the interpreter. */
    artifact_host_serve(state);
    state->termination_reason = 0;
    state->next_pc = 0;
}

/* STANDALONE_MAIN_FUNCTION_PLACEHOLDER */
    if (argc < 3) return 90; /* MissingInput: argv[1] input file, argv[2] program image */
    FILE* in = fopen(argv[1], ""r"");
    if (!in) return 91;      /* CannotOpenInput */

    RecompilerState state;
    memset(&state, 0, sizeof(state));

    unsigned long u, a, v;
    int i;
    for (i = 0; i < 32; i++) { if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.gpr[i] = (uint32_t)u; }
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.hi = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.lo = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } state.pc = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; } unsigned long budget = u;

    if (fscanf(in, ""%lu"", &u) != 1) { fclose(in); return 92; }
    if (u > PSX_MAX_INIT) { fclose(in); return 93; } /* TooManyInits */
    unsigned long init_count = u;
    for (i = 0; i < (int)init_count; i++) {
        if (fscanf(in, ""%lu %lu"", &a, &v) != 2) { fclose(in); return 92; }
        init_addrs[i] = (uint32_t)a;
        init_vals[i] = (uint32_t)v;
    }
    if (fscanf(in, ""%lu %lu"", &a, &u) != 2) { fclose(in); return 92; }
    fclose(in);
    uint32_t image_pa = artifact_translate((uint32_t)a);
    unsigned long image_len = u;
    /* The whole image must sit inside the mapped low-8-MiB window (bytes past it
       are unmapped and cannot be represented); within it, bytes alias into the
       2 MiB RAM exactly as PSXMemory does, including across the 2 MiB seam.
       Or (Issue #732) it is a firmware image wholly inside the BIOS ROM window. */
    int image_in_rom = image_pa >= PSX_ROM_BASE && image_pa - PSX_ROM_BASE < PSX_ROM_SIZE;
    if (image_len == 0ul
        || (image_in_rom && (unsigned long long)image_len > (unsigned long long)(PSX_ROM_SIZE - (image_pa - PSX_ROM_BASE))))
        return 95; /* InvalidImage */
    artifact_rom_loaded = image_in_rom;
    if (!image_in_rom && (image_pa >= PSX_RAM_MIRROR_END
        || (unsigned long long)image_len > (unsigned long long)(PSX_RAM_MIRROR_END - image_pa))) return 95; /* InvalidImage */

    state.gpr[0] = 0;
    state.core = (void*)0;
    memset(artifact_ram, 0, sizeof(artifact_ram));
    for (i = 0; i < (int)init_count; i++) {
        artifact_ram_write8(init_addrs[i], (uint8_t)init_vals[i]);
    }

    /* The program image goes in last so it wins any overlap with the init
       writes. Its file length must equal the declared length exactly. */
    FILE* image = fopen(argv[2], ""rb"");
    if (!image) return 94; /* CannotOpenImage */
    unsigned long image_i;
    for (image_i = 0; image_i < image_len; image_i++) {
        int image_byte = fgetc(image);
        if (image_byte == EOF) { fclose(image); return 95; } /* InvalidImage: truncated */
        if (image_in_rom) artifact_rom[image_pa - PSX_ROM_BASE + (uint32_t)image_i] = (uint8_t)image_byte;
        else artifact_ram[(image_pa + (uint32_t)image_i) & (PSX_RAM_SIZE - 1u)] = (uint8_t)image_byte;
    }
    int image_extra = fgetc(image);
    fclose(image);
    if (image_extra != EOF) return 95; /* InvalidImage: oversized */

    /* Firmware mode (Issue #732): guest SYSCALL/BREAK enter the guest's own exception vector. */
    for (i = 3; i < argc; i++) {
        if (strcmp(argv[i], ""@GUEST_EXCEPTIONS@"") == 0) state.guest_exceptions = 1u;
        if (strcmp(argv[i], ""@EXACT_DEVICE_TIME@"") == 0) artifact_exact_time = 1;
    }

    /* Opt-in only: a stray extra argument never enables the protocol, so a run
       with no parent listening can never block on the handshake. */
    if (argc >= 4 && strcmp(argv[3], ""--host-transfer"") == 0) {
        artifact_attach_host_bridge(&state);
    }

    recompiler_dispatch(&state, (uint32_t)budget);
    artifact_report_retired(); /* whatever the last units retired below the threshold */

    printf(""RSNAPSHOT_BEGIN\n"");
    printf(""termination=%d\n"", (int)state.termination_reason);
    printf(""pc=0x%08X\n"", state.pc);
    printf(""hi=0x%08X\n"", state.hi);
    printf(""lo=0x%08X\n"", state.lo);
    for (i = 0; i < 32; i++) printf(""gpr[%d]=0x%08X\n"", i, state.gpr[i]);
    printf(""exception.raised=%d\n"", (int)state.exception_raised);
    printf(""exception.code=0x%08X\n"", state.exception_code);
    printf(""exception.faultPc=0x%08X\n"", state.exception_fault_pc);
    printf(""exception.inDelaySlot=%d\n"", (int)state.exception_in_delay_slot);
    printf(""cop0.sr=0x%08X\n"", state.cop0_sr);
    printf(""cop0.cause=0x%08X\n"", state.cop0_cause);
    printf(""cop0.epc=0x%08X\n"", state.cop0_epc);
    printf(""RSNAPSHOT_END\n"");

    return (int)state.termination_reason;
}
";
}

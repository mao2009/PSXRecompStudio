using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Registry of well-known, documented <see cref="DiagnosticCode"/> values.
///
/// <para>
/// Code policy: each code is <c>&lt;SUBSYSTEM_PREFIX&gt;_&lt;CONDITION&gt;</c>
/// in SCREAMING_SNAKE with a subsystem-specific prefix (for example <c>DISC_</c>,
/// <c>RECOMP_</c>, <c>RUNTIME_</c>, <c>BUILD_</c>, <c>INPUT_</c>, <c>ANALYZER_</c>).
/// The value is the machine contract; a code does not need to be listed here to
/// be valid, but every code that production paths emit should be added so the
/// set stays discoverable. Codes already produced by existing subsystems as raw
/// strings (for example <c>BIOS_HLE_UNSUPPORTED_CALL</c>) are reused unchanged
/// rather than re-registered.
/// </para>
/// </summary>
[Domain]
public static class DiagnosticCodes
{
    /// <summary>The disc image input is missing, empty, or has no verifiable identity.</summary>
    public static readonly DiagnosticCode DiscInputInvalid = new("DISC_INPUT_INVALID");

    /// <summary>The compressed disc container (CHD) could not be opened.</summary>
    public static readonly DiagnosticCode DiscChdOpenFailed = new("DISC_CHD_OPEN_FAILED");

    /// <summary>The disc image is malformed: it is not a valid PS1 image.</summary>
    public static readonly DiagnosticCode DiscInvalidImage = new("DISC_INVALID_IMAGE");

    /// <summary>The ISO 9660 filesystem could not be read.</summary>
    public static readonly DiagnosticCode DiscFilesystemFailed = new("DISC_FILESYSTEM_FAILED");

    /// <summary>SYSTEM.CNF is missing or invalid.</summary>
    public static readonly DiagnosticCode DiscSystemCnfInvalid = new("DISC_SYSTEM_CNF_INVALID");

    /// <summary>The boot executable named by SYSTEM.CNF is missing or unreadable.</summary>
    public static readonly DiagnosticCode DiscBootExecutableUnreadable = new("DISC_BOOT_EXECUTABLE_UNREADABLE");

    /// <summary>The boot executable is not a valid PS-X EXE (header / entry point / text region).</summary>
    public static readonly DiagnosticCode DiscInvalidExecutable = new("DISC_INVALID_EXECUTABLE");

    /// <summary>Generic real-ROM analysis failure; the raw failure kind is in context.</summary>
    public static readonly DiagnosticCode DiscAnalysisFailed = new("DISC_ANALYSIS_FAILED");

    /// <summary>The linear MIPS decode could not decode the text region.</summary>
    public static readonly DiagnosticCode AnalyzerDecodeFailed = new("ANALYZER_DECODE_FAILED");

    /// <summary>Basic-block / control-flow analysis failed.</summary>
    public static readonly DiagnosticCode AnalyzerAnalysisFailed = new("ANALYZER_ANALYSIS_FAILED");

    /// <summary>The deterministic analysis report could not be produced.</summary>
    public static readonly DiagnosticCode AnalyzerReportFailed = new("ANALYZER_REPORT_FAILED");

    /// <summary>The recompiler lacks coverage for a specific MIPS instruction.</summary>
    public static readonly DiagnosticCode RecompUnsupportedInstruction = new("RECOMP_UNSUPPORTED_INSTRUCTION");

    /// <summary>The recompiler lacks coverage for a general operation / IR form.</summary>
    public static readonly DiagnosticCode RecompUnsupportedOperation = new("RECOMP_UNSUPPORTED_OPERATION");

    /// <summary>The recompiler cannot lower a memory access form.</summary>
    public static readonly DiagnosticCode RecompUnsupportedMemory = new("RECOMP_UNSUPPORTED_MEMORY");

    /// <summary>The recompiled host state mismatched the reference execution.</summary>
    public static readonly DiagnosticCode RecompStateMismatch = new("RECOMP_STATE_MISMATCH");

    /// <summary>Guest execution hit an MMIO boundary the Runtime does not support.</summary>
    public static readonly DiagnosticCode RuntimeUnsupportedMmio = new("RUNTIME_UNSUPPORTED_MMIO");

    /// <summary>Generic guest-execution failure; the raw engine code is in context.</summary>
    public static readonly DiagnosticCode RuntimeExecutionFailed = new("RUNTIME_EXECUTION_FAILED");

    /// <summary>The host compiler could not compile generated host code.</summary>
    public static readonly DiagnosticCode BuildCompilerFailed = new("BUILD_COMPILER_FAILED");

    /// <summary>An input binding / mapping configuration is invalid.</summary>
    public static readonly DiagnosticCode InputConfigurationInvalid = new("INPUT_CONFIGURATION_INVALID");

    /// <summary>An external input the operation needs is missing or unreadable.</summary>
    public static readonly DiagnosticCode InputFileMissing = new("INPUT_FILE_MISSING");
}
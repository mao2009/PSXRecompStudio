namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Ordered stages of the reusable real-ROM analysis flow.
///
/// The numeric order is part of the contract: stages are always recorded in
/// strictly increasing order, so the last recorded successful stage identifies
/// exactly how far a run progressed before it failed.
///
/// <see cref="Manifest"/> and <see cref="Complete"/> are not produced by
/// <see cref="RomAnalysisPipeline"/> itself (they require artifact I/O, which is
/// outside the domain layer); the flow driver records them after the pipeline
/// returns.
/// </summary>
public enum RomAnalysisStage
{
    /// <summary>Flow entry; nothing has been inspected yet.</summary>
    Start = 0,

    /// <summary>Input identity and byte buffer validation.</summary>
    Input = 1,

    /// <summary>Opening the compressed disc image container (CHD).</summary>
    ChdOpen = 2,

    /// <summary>Reading the ISO 9660 volume descriptor and directory tree.</summary>
    Filesystem = 3,

    /// <summary>Locating and parsing SYSTEM.CNF.</summary>
    SystemCnf = 4,

    /// <summary>Reading the boot executable named by SYSTEM.CNF.</summary>
    BootExecutable = 5,

    /// <summary>Verifying the boot executable is a PS-X EXE.</summary>
    PsxExe = 6,

    /// <summary>Parsing and validating the PS-X EXE header.</summary>
    ExeHeader = 7,

    /// <summary>Validating the entry point against the declared text region.</summary>
    EntryPoint = 8,

    /// <summary>Validating that the text region is actually present in the file.</summary>
    TextRegion = 9,

    /// <summary>Linear MIPS R3000A decode from the entry point.</summary>
    MipsDecode = 10,

    /// <summary>Basic block and direct control-flow construction.</summary>
    BasicBlock = 11,

    /// <summary>Assembling the deterministic analysis report.</summary>
    Report = 12,

    /// <summary>Persisting the analysis manifest / report artifacts.</summary>
    Manifest = 13,

    /// <summary>Flow finished successfully.</summary>
    Complete = 14,
}

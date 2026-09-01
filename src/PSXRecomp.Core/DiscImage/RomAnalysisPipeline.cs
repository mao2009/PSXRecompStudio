using System.Globalization;
using System.Security.Cryptography;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Stage-driven real-ROM analysis flow:
/// INPUT → CHD_OPEN → FILESYSTEM → SYSTEM_CNF → BOOT_EXECUTABLE → PSX_EXE →
/// EXE_HEADER → ENTRY_POINT → TEXT_REGION → MIPS_DECODE → BASIC_BLOCK → REPORT.
///
/// Unlike <see cref="DiscImageAnalyzer"/>, which raises the first error it meets,
/// this pipeline classifies every stage: a failure stops the run but is returned as
/// a <see cref="RomAnalysisOutcome"/> naming the last successful stage, the failing
/// stage, a machine-readable failure kind, and the reason. No error is swallowed —
/// the originating exception is retained on the outcome.
///
/// The pipeline is title-agnostic: it derives everything from SYSTEM.CNF and the
/// PS-X EXE header, and never hard-codes a game name, serial, or path.
///
/// It is a pure domain component: all bytes are supplied by the caller and no
/// artifact is written here. MANIFEST and COMPLETE are recorded by the flow driver
/// once artifacts have been persisted.
/// </summary>
[Domain]
public static class RomAnalysisPipeline
{
    /// <summary>Instructions decoded from the entry point when the caller does not specify a count.</summary>
    public const int DefaultInstructionCount = 128;

    private const int IsoSectorSize = Iso9660Reader.SectorSize;

    /// <summary>
    /// Runs the flow over a CHD disc image supplied as raw bytes.
    /// </summary>
    /// <param name="discImageBytes">Raw CHD file bytes.</param>
    /// <param name="discImageSha256">SHA-256 of <paramref name="discImageBytes"/>, the formal input identity.</param>
    /// <param name="instructionCount">Instructions to decode from the entry point.</param>
    /// <param name="recorder">Optional recorder to append to, so the caller can add MANIFEST/COMPLETE afterwards.</param>
    public static RomAnalysisOutcome RunFromChd(
        byte[] discImageBytes,
        string discImageSha256,
        int? instructionCount = null,
        RomAnalysisStageRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(discImageBytes);
        return RunFromChdCore(new MemoryStream(discImageBytes, writable: false), discImageSha256, instructionCount, recorder);
    }

    /// <summary>
    /// Runs the flow over a CHD disc image supplied as a stream.
    ///
    /// The stream is passed straight to <see cref="ChdReader.Open(Stream)"/> without
    /// whole-image buffering. The caller owns the stream: this pipeline never disposes it.
    /// Only the <see cref="ChdReader"/> lifetime is managed here. The stream must be
    /// readable and seekable (the CHD reader seeks the header and map); the caller is
    /// responsible for its position and disposal.
    /// </summary>
    /// <param name="chdStream">Caller-owned, readable, seekable CHD stream. Not disposed.</param>
    /// <param name="discImageSha256">SHA-256 of the disc image, the formal input identity.</param>
    /// <param name="instructionCount">Instructions to decode from the entry point.</param>
    /// <param name="recorder">Optional recorder to append to, so the caller can add MANIFEST/COMPLETE afterwards.</param>
    public static RomAnalysisOutcome RunFromChd(
        Stream chdStream,
        string discImageSha256,
        int? instructionCount = null,
        RomAnalysisStageRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(chdStream);
        return RunFromChdCore(chdStream, discImageSha256, instructionCount, recorder);
    }

    private static RomAnalysisOutcome RunFromChdCore(
        Stream chdStream,
        string discImageSha256,
        int? instructionCount,
        RomAnalysisStageRecorder? recorder)
    {
        recorder ??= new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Start, "Real-ROM analysis flow started");

        if (string.IsNullOrWhiteSpace(discImageSha256))
        {
            recorder.Fail(RomAnalysisStage.Input, "MissingInputIdentity",
                "Disc image SHA-256 is required as the formal input identity.");
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.Input, string.Create(CultureInfo.InvariantCulture,
            $"CHD input accepted: sha256={discImageSha256}"));

        ChdReader? chd = null;
        try
        {
            try
            {
                chd = ChdReader.Open(chdStream);
                var header = chd.Header;
                recorder.Pass(RomAnalysisStage.ChdOpen, string.Create(CultureInfo.InvariantCulture,
                    $"CHD v{header.Version} opened: logicalBytes={header.LogicalBytes} hunkBytes={header.HunkBytes} framesPerHunk={header.FramesPerHunk}"));
            }
            catch (Exception ex) when (IsClassifiableFailure(ex))
            {
                recorder.Fail(RomAnalysisStage.ChdOpen, "ChdOpenFailure", ex);
                return RomAnalysisOutcome.From(recorder);
            }

            var iso = DiscImageAnalyzer.CreateIsoReader(chd);
            return RunFromIsoSectorReader(recorder, iso, discImageSha256, instructionCount);
        }
        finally
        {
            chd?.Dispose();
        }
    }

    /// <summary>
    /// Runs the flow over a plain ISO 9660 image (2048-byte user-data sectors, no CHD
    /// container). CHD_OPEN is recorded as skipped. Used for uncompressed inputs and for
    /// exercising the post-filesystem stages against constructed images.
    /// </summary>
    public static RomAnalysisOutcome RunFromIsoImage(
        byte[] isoImageBytes,
        string discImageSha256,
        int? instructionCount = null,
        RomAnalysisStageRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(isoImageBytes);

        recorder ??= new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Start, "Real-ROM analysis flow started");

        if (!ValidateInput(recorder, isoImageBytes, discImageSha256, "ISO"))
        {
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Skip(RomAnalysisStage.ChdOpen, "Input is a plain ISO 9660 image; no CHD container to open");
        return RunFromIsoSectorReader(recorder, new Iso9660Reader(CreateIsoSectorReader(isoImageBytes)), discImageSha256, instructionCount);
    }

    private static bool ValidateInput(
        RomAnalysisStageRecorder recorder,
        byte[] imageBytes,
        string discImageSha256,
        string format)
    {
        if (imageBytes.Length == 0)
        {
            recorder.Fail(RomAnalysisStage.Input, "EmptyInput", "Disc image is empty (0 bytes).");
            return false;
        }

        if (string.IsNullOrWhiteSpace(discImageSha256))
        {
            recorder.Fail(RomAnalysisStage.Input, "MissingInputIdentity",
                "Disc image SHA-256 is required as the formal input identity.");
            return false;
        }

        recorder.Pass(RomAnalysisStage.Input, string.Create(CultureInfo.InvariantCulture,
            $"{format} input accepted: {imageBytes.Length} bytes, sha256={discImageSha256}"));
        return true;
    }

    private static RomAnalysisOutcome RunFromIsoSectorReader(
        RomAnalysisStageRecorder recorder,
        Iso9660Reader iso,
        string discImageSha256,
        int? instructionCount)
    {
        var count = instructionCount ?? DefaultInstructionCount;

        // FILESYSTEM
        try
        {
            iso.Initialize();
            var root = RootDirectoryEntry(iso);
            iso.CountEntries(root, out int fileCount, out int directoryCount);
            recorder.Pass(RomAnalysisStage.Filesystem, string.Create(CultureInfo.InvariantCulture,
                $"ISO 9660 volume '{iso.VolumeIdentifier}': volumeSpaceSize={iso.VolumeSpaceSize} files={fileCount} directories={directoryCount}"));
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.Filesystem, "FilesystemFailure", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        // SYSTEM_CNF
        string bootPathRaw;
        string bootPath;
        try
        {
            var systemCnfBytes = iso.ReadFile("SYSTEM.CNF");
            var systemCnf = SystemCnfParser.Parse(systemCnfBytes);
            bootPathRaw = systemCnf.BootPath;
            bootPath = NormalizeBootPath(bootPathRaw);
            recorder.Pass(RomAnalysisStage.SystemCnf, $"SYSTEM.CNF BOOT='{bootPathRaw}' resolved to '{bootPath}'");
        }
        catch (FileNotFoundException ex)
        {
            recorder.Fail(RomAnalysisStage.SystemCnf, "SystemCnfMissing", ex);
            return RomAnalysisOutcome.From(recorder);
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.SystemCnf, "SystemCnfInvalid", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        // BOOT_EXECUTABLE
        byte[] exeBytes;
        string exeFileName;
        string exeHash;
        try
        {
            exeBytes = iso.ReadFile(bootPath);
            exeFileName = Path.GetFileName(bootPath.Split(';')[0]);
            exeHash = ComputeSha256(exeBytes);
            recorder.Pass(RomAnalysisStage.BootExecutable, string.Create(CultureInfo.InvariantCulture,
                $"Boot executable '{exeFileName}' read: {exeBytes.Length} bytes, sha256={exeHash}"));
        }
        catch (FileNotFoundException ex)
        {
            recorder.Fail(RomAnalysisStage.BootExecutable, "BootExecutableMissing", ex);
            return RomAnalysisOutcome.From(recorder);
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.BootExecutable, "BootExecutableUnreadable", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        // PSX_EXE
        if (exeBytes.Length < PsxExeHeader.HeaderSize)
        {
            recorder.Fail(RomAnalysisStage.PsxExe, "InvalidPsxExe", string.Create(CultureInfo.InvariantCulture,
                $"Boot executable '{exeFileName}' is {exeBytes.Length} bytes, shorter than the {PsxExeHeader.HeaderSize}-byte PS-X EXE header."));
            return RomAnalysisOutcome.From(recorder);
        }

        var magic = BitConverter.ToUInt64(exeBytes, 0);
        if (magic != PsxExeHeader.Magic)
        {
            recorder.Fail(RomAnalysisStage.PsxExe, "InvalidPsxExe",
                $"Boot executable '{exeFileName}' does not start with the PS-X EXE magic (found 0x{magic:X16}).");
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.PsxExe, $"'{exeFileName}' identified as a PS-X EXE");

        // EXE_HEADER
        PsxExe exe;
        try
        {
            exe = PsxExe.Load(exeBytes, exeFileName);
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.ExeHeader, "InvalidExeHeader", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        var header = exe.Header;
        if (header.TextStart == 0)
        {
            recorder.Fail(RomAnalysisStage.ExeHeader, "InvalidExeHeader",
                "PS-X EXE header declares text start 0x00000000.");
            return RomAnalysisOutcome.From(recorder);
        }

        if (header.TextEnd < header.TextStart)
        {
            recorder.Fail(RomAnalysisStage.ExeHeader, "InvalidExeHeader", string.Create(CultureInfo.InvariantCulture,
                $"PS-X EXE header text region overflows: start=0x{header.TextStart:X8} size=0x{header.TextSize:X8}."));
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.ExeHeader, string.Create(CultureInfo.InvariantCulture,
            $"Header parsed: entry=0x{header.EntryPoint:X8} text=[0x{header.TextStart:X8}..0x{header.TextEnd:X8}) sp=0x{header.SpInitial:X8} gp=0x{header.GpInitial:X8}"));

        // ENTRY_POINT
        if (header.EntryPoint < header.TextStart || header.EntryPoint >= header.TextEnd)
        {
            recorder.Fail(RomAnalysisStage.EntryPoint, "InvalidEntryPoint", string.Create(CultureInfo.InvariantCulture,
                $"Entry point 0x{header.EntryPoint:X8} is outside the declared text region [0x{header.TextStart:X8}..0x{header.TextEnd:X8})."));
            return RomAnalysisOutcome.From(recorder);
        }

        if ((header.EntryPoint & 3) != 0)
        {
            recorder.Fail(RomAnalysisStage.EntryPoint, "InvalidEntryPoint",
                $"Entry point 0x{header.EntryPoint:X8} is not 4-byte aligned.");
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.EntryPoint, $"Entry point 0x{header.EntryPoint:X8} validated");

        // TEXT_REGION
        if (exe.TextSegment.Length == 0)
        {
            recorder.Fail(RomAnalysisStage.TextRegion, "TextRegionUnavailable", string.Create(CultureInfo.InvariantCulture,
                $"PS-X EXE declares a {header.TextSize}-byte text region but the file contains no text bytes."));
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.TextRegion, string.Create(CultureInfo.InvariantCulture,
            $"Text region available: {exe.TextSegment.Length} of {header.TextSize} declared bytes"));

        // MIPS_DECODE
        IReadOnlyList<DecodedInstruction> instructions;
        IReadOnlyList<DecodeFailure> decodeFailures;
        try
        {
            (instructions, decodeFailures) = DecodeInstructions(exe, count);
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.MipsDecode, "DecodeFailure", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        if (instructions.Count == 0)
        {
            var reason = decodeFailures.Count > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"No instruction could be decoded from the entry point; first failure at 0x{decodeFailures[0].Address:X8}: {decodeFailures[0].Reason}")
                : "No instruction could be decoded from the entry point.";
            recorder.Fail(RomAnalysisStage.MipsDecode, "DecodeFailure", reason);
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.MipsDecode, string.Create(CultureInfo.InvariantCulture,
            $"Decoded {instructions.Count} instruction(s) from 0x{header.EntryPoint:X8}; {decodeFailures.Count} decode failure(s)"));

        // BASIC_BLOCK
        IReadOnlyList<BasicBlock> basicBlocks;
        IReadOnlyList<CfgEdge> cfgEdges;
        try
        {
            (basicBlocks, cfgEdges) = BasicBlockBuilder.Build(instructions, header.EntryPoint, count);
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.BasicBlock, "BasicBlockAnalysisFailure", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        if (basicBlocks.Count == 0)
        {
            recorder.Fail(RomAnalysisStage.BasicBlock, "BasicBlockAnalysisFailure", string.Create(CultureInfo.InvariantCulture,
                $"Basic block analysis produced no block for {instructions.Count} decoded instruction(s)."));
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.BasicBlock, string.Create(CultureInfo.InvariantCulture,
            $"Built {basicBlocks.Count} basic block(s) and {cfgEdges.Count} CFG edge(s)"));

        // REPORT
        DiscImageAnalysisReport report;
        try
        {
            var (callCandidates, returnCandidates) = CountCallReturnCandidates(instructions);
            report = new DiscImageAnalysisReport
            {
                DiscImageSha256 = discImageSha256,
                SystemCnfBootPath = bootPathRaw,
                ExecutableFileName = exe.FileName,
                EntryPoint = header.EntryPoint,
                TextStart = header.TextStart,
                TextSize = header.TextSize,
                SpInitial = header.SpInitial,
                GpInitial = header.GpInitial,
                ExecutableFileSize = exe.FileSize,
                ExecutableFileHash = exeHash,
                DecodeStartAddress = header.EntryPoint,
                DecodedInstructionCount = instructions.Count,
                DecodedInstructions = instructions,
                DecodeFailures = decodeFailures,
                BasicBlocks = basicBlocks,
                CfgEdges = cfgEdges,
                CallCandidateCount = callCandidates,
                ReturnCandidateCount = returnCandidates,
            };
        }
        catch (Exception ex) when (IsClassifiableFailure(ex))
        {
            recorder.Fail(RomAnalysisStage.Report, "ReportGenerationFailure", ex);
            return RomAnalysisOutcome.From(recorder);
        }

        recorder.Pass(RomAnalysisStage.Report, string.Create(CultureInfo.InvariantCulture,
            $"Report assembled for '{report.ExecutableFileName}' ({report.DecodedInstructionCount} instructions, {report.BasicBlocks.Count} blocks)"));

        return RomAnalysisOutcome.From(recorder, report, decodeFailures.Count);
    }

    /// <summary>
    /// Builds an ISO 9660 sector reader over a plain image of 2048-byte user-data sectors.
    /// </summary>
    private static Func<int, byte[]> CreateIsoSectorReader(byte[] isoImageBytes)
    {
        return sector =>
        {
            var offset = (long)sector * IsoSectorSize;
            if (sector < 0 || offset + IsoSectorSize > isoImageBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(sector),
                    $"Sector {sector} is outside the {isoImageBytes.Length}-byte ISO image.");
            }

            var isoData = new byte[IsoSectorSize];
            Buffer.BlockCopy(isoImageBytes, (int)offset, isoData, 0, IsoSectorSize);
            return isoData;
        };
    }

    private static Iso9660DirectoryEntry RootDirectoryEntry(Iso9660Reader iso) => new()
    {
        Location = iso.RootDirectoryLocation,
        Size = iso.RootDirectorySize,
        Flags = 0x02,
        FileNameLength = 1,
        FileName = "\0",
    };

    /// <summary>
    /// Strips the <c>cdrom:</c> scheme and leading separator from a SYSTEM.CNF BOOT value
    /// and normalizes separators to the ISO 9660 reader's convention.
    /// </summary>
    internal static string NormalizeBootPath(string bootPath)
    {
        var path = bootPath;
        if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
        {
            path = path["cdrom:".Length..];
        }
        if (path.StartsWith('\\') || path.StartsWith('/'))
        {
            path = path[1..];
        }
        return path.Replace('\\', '/');
    }

    private static (IReadOnlyList<DecodedInstruction> Instructions, IReadOnlyList<DecodeFailure> Failures)
        DecodeInstructions(PsxExe exe, int count)
    {
        var instructions = new List<DecodedInstruction>();
        var failures = new List<DecodeFailure>();

        var maxAddress = exe.Header.TextEnd;
        uint currentAddress = exe.Header.EntryPoint;
        int decoded = 0;

        while (decoded < count && currentAddress + 4 <= maxAddress)
        {
            try
            {
                var rawWord = exe.GetInstructionWord(currentAddress);
                var instruction = R3000aDecoder.Decode(rawWord);

                instructions.Add(new DecodedInstruction
                {
                    Address = currentAddress,
                    RawWord = rawWord,
                    Mnemonic = MipsInstructionFormatter.FormatMnemonic(instruction.Opcode),
                    Operands = MipsInstructionFormatter.FormatOperands(instruction),
                    Format = instruction.Format.ToString(),
                    ControlFlow = instruction.ControlFlow.ToString(),
                });

                decoded++;
                currentAddress += 4;
            }
            catch (ArgumentOutOfRangeException)
            {
                failures.Add(new DecodeFailure
                {
                    Address = currentAddress,
                    Reason = "Address outside text segment bounds",
                });
                break;
            }
        }

        return (instructions, failures);
    }

    private static (int CallCandidates, int ReturnCandidates) CountCallReturnCandidates(
        IReadOnlyList<DecodedInstruction> instructions)
    {
        int callCandidates = 0;
        int returnCandidates = 0;

        foreach (var inst in instructions)
        {
            var raw = R3000aDecoder.Decode(inst.RawWord);
            if (raw.LinkInfo.WritesLink)
            {
                callCandidates++;
            }
            if (raw.Opcode == R3000aOpcode.Jr &&
                raw.Operand0.Kind == R3000aOperandKind.Register &&
                raw.Operand0.Register == 31)
            {
                returnCandidates++;
            }
        }

        return (callCandidates, returnCandidates);
    }

    /// <summary>
    /// Analysis-level errors are classified into stage results; process-level failures
    /// (out of memory, cancellation) are left to propagate.
    /// </summary>
    private static bool IsClassifiableFailure(Exception ex) =>
        ex is not (OutOfMemoryException or OperationCanceledException);

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
    }
}

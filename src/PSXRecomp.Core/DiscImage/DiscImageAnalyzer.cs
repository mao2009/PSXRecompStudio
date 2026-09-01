using System.Security.Cryptography;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Main analysis pipeline: CHD → ISO 9660 → SYSTEM.CNF → PS-X EXE → MIPS decode.
/// Produces a deterministic <see cref="DiscImageAnalysisReport"/>.
/// This is a pure domain method that accepts pre-read bytes.
/// </summary>
[Domain]
public static class DiscImageAnalyzer
{
    private const int DefaultInstructionCount = 128;
    private const int IsoSectorSize = 2048;

    /// <summary>
    /// Analyzes raw CHD bytes and produces a full analysis report.
    /// All file I/O is the caller's responsibility.
    /// </summary>
    public static DiscImageAnalysisReport Analyze(byte[] chdBytes, string chdSha256, int? instructionCount = null)
    {
        var count = instructionCount ?? DefaultInstructionCount;

        using var chdStream = new MemoryStream(chdBytes, writable: false);
        using var chd = ChdReader.Open(chdStream);
        return AnalyzeChd(chd, chdSha256, count);
    }

    /// <summary>
    /// Analyzes a CHD stream and produces a full analysis report.
    /// </summary>
    public static DiscImageAnalysisReport Analyze(Stream chdStream, string chdSha256, int? instructionCount = null)
    {
        var count = instructionCount ?? DefaultInstructionCount;
        using var chd = ChdReader.Open(chdStream);
        return AnalyzeChd(chd, chdSha256, count);
    }

    /// <summary>
    /// Creates an ISO 9660 reader over a CHD disc image, unwrapping each raw CD sector to
    /// its 2048-byte user-data area. The user-data offset depends on the sector mode
    /// (Mode 1 at 16, Mode 2 at 24), so this must not be assumed to be a fixed constant.
    ///
    /// This is the single definition of "how ISO sectors are read out of a CHD"; callers
    /// that need filesystem metadata alongside a report should use it rather than
    /// re-deriving the offset. The returned reader borrows <paramref name="chd"/> and is
    /// only valid while that reader is alive. <see cref="Iso9660Reader.Initialize"/> has
    /// not yet been called on it.
    /// </summary>
    public static Iso9660Reader CreateIsoReader(ChdReader chd)
    {
        ArgumentNullException.ThrowIfNull(chd);

        return new Iso9660Reader(sector =>
        {
            var cdSector = chd.ReadSector(sector);
            int userDataOffset = cdSector[15] switch
            {
                1 => 16,
                2 => 24,
                var mode => throw new InvalidDataException(
                    $"Unsupported CD sector mode {mode} at sector {sector}."),
            };
            if (cdSector.Length < userDataOffset + IsoSectorSize)
            {
                throw new InvalidDataException(
                    $"CD sector {sector} is too short: {cdSector.Length} bytes.");
            }

            var isoData = new byte[IsoSectorSize];
            Buffer.BlockCopy(cdSector, userDataOffset, isoData, 0, IsoSectorSize);
            return isoData;
        });
    }

    private static DiscImageAnalysisReport AnalyzeChd(ChdReader chd, string sha256, int instructionCount)
    {
        var iso = CreateIsoReader(chd);
        iso.Initialize();

        var systemCnfBytes = iso.ReadFile("SYSTEM.CNF");
        var systemCnf = SystemCnfParser.Parse(systemCnfBytes);

        var bootPath = NormalizeBootPath(systemCnf.BootPath);
        var exeBytes = iso.ReadFile(bootPath);
        var exeFileName = Path.GetFileName(bootPath.Split(';')[0]);

        var psxExe = PsxExe.Load(exeBytes, exeFileName);

        var exeHash = ComputeSha256(exeBytes);

        var (decodedInstructions, decodeFailures) = DecodeInstructions(psxExe, instructionCount);

        var (basicBlocks, cfgEdges) = BasicBlockBuilder.Build(decodedInstructions, psxExe.Header.EntryPoint, instructionCount);

        int callCandidateCount = 0;
        int returnCandidateCount = 0;
        foreach (var inst in decodedInstructions)
        {
            var raw = R3000aDecoder.Decode(inst.RawWord);
            if (raw.LinkInfo.WritesLink)
            {
                callCandidateCount++;
            }
            if (raw.Opcode == R3000aOpcode.Jr && raw.Operand0.Kind == R3000aOperandKind.Register && raw.Operand0.Register == 31)
            {
                returnCandidateCount++;
            }
        }

        return new DiscImageAnalysisReport
        {
            DiscImageSha256 = sha256,
            SystemCnfBootPath = systemCnf.BootPath,
            ExecutableFileName = psxExe.FileName,
            EntryPoint = psxExe.Header.EntryPoint,
            TextStart = psxExe.Header.TextStart,
            TextSize = psxExe.Header.TextSize,
            SpInitial = psxExe.Header.SpInitial,
            GpInitial = psxExe.Header.GpInitial,
            ExecutableFileSize = psxExe.FileSize,
            ExecutableFileHash = exeHash,
            DecodeStartAddress = psxExe.Header.EntryPoint,
            DecodedInstructionCount = decodedInstructions.Count,
            DecodedInstructions = decodedInstructions,
            DecodeFailures = decodeFailures,
            BasicBlocks = basicBlocks,
            CfgEdges = cfgEdges,
            CallCandidateCount = callCandidateCount,
            ReturnCandidateCount = returnCandidateCount,
        };
    }

    private static string NormalizeBootPath(string bootPath)
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
        path = path.Replace('\\', '/');
        return path;
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

                var mnemonic = MipsInstructionFormatter.FormatMnemonic(instruction.Opcode);
                var operands = MipsInstructionFormatter.FormatOperands(instruction);
                var format = instruction.Format.ToString();
                var controlFlow = instruction.ControlFlow.ToString();

                instructions.Add(new DecodedInstruction
                {
                    Address = currentAddress,
                    RawWord = rawWord,
                    Mnemonic = mnemonic,
                    Operands = operands,
                    Format = format,
                    ControlFlow = controlFlow,
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

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

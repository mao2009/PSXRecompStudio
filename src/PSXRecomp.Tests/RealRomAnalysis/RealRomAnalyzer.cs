using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Orchestrates the real-ROM analysis pipeline for a CHD disc image and produces
/// two artifacts:
///   - A deterministic <see cref="AnalysisSnapshot"/> (primary, for cross-ROM comparison)
///   - An <see cref="ExecutionLogWriter"/> JSONL log (debugging aid)
///
/// It reuses the existing domain pipeline (<see cref="DiscImageAnalyzer"/>,
/// <see cref="ChdReader"/>, <see cref="Iso9660Reader"/>, <see cref="PsxExe"/>) rather
/// than re-implementing any single computation.
/// </summary>
[Test]
public static class RealRomAnalyzer
{
    private const int IsoSectorSize = 2048;

    /// <summary>
    /// Runs the full pipeline against a CHD file and returns the deterministic snapshot
    /// together with the execution log entries.
    /// </summary>
    public static (AnalysisSnapshot Snapshot, List<ExecutionLogEntry> Log) Analyze(string chdPath, string fixtureName, int? instructionCount = null)
    {
        var log = new List<ExecutionLogEntry>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        void Record(string stage, string status, string message)
        {
            log.Add(new ExecutionLogEntry
            {
                Stage = stage,
                Status = status,
                Message = message,
                ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            });
        }

        Record("CHD_OPEN", "START", $"Opening CHD '{fixtureName}'");
        byte[] chdBytes;
        string chdSha256;
        try
        {
#pragma warning disable PSXR005
            chdBytes = File.ReadAllBytes(chdPath);
#pragma warning restore PSXR005
            chdSha256 = ComputeSha256(chdBytes);
            Record("CHD_OPEN", "PASS", $"Read {chdBytes.Length} bytes; SHA-256 {chdSha256}");
        }
        catch (Exception ex)
        {
            Record("CHD_OPEN", "FAIL", ex.Message);
            throw;
        }

        var report = DiscImageAnalyzer.Analyze(chdBytes, chdSha256, instructionCount);
        Record("ANALYZE", "PASS", $"DiscImageAnalyzer produced report ({report.DecodedInstructionCount} instructions)");

        ChdMapStatistics chdStats;
        using (var chd = ChdReader.Open(new MemoryStream(chdBytes, writable: false)))
        {
            chdStats = chd.ComputeMapStatistics();
            Record("CHD_META", "PASS",
                $"V5 hunks={chdStats.TotalHunks} cdlz={chdStats.CdlzCount} cdzl={chdStats.CdzlCount}");
        }

        IsoSnapshot iso = CaptureIso(chdBytes, chdSha256, Record);
        SystemCnfSnapshot systemCnf = new()
        {
            BootPath = report.SystemCnfBootPath,
            BootExecutable = report.ExecutableFileName,
        };

        PsxExeSnapshot psxExe = CapturePsxExe(chdBytes, report.ExecutableFileName, Record);

        var analysis = ComputeAnalysisSummary(report);
        var instructions = report.DecodedInstructions
            .Select(i => new InstructionSnapshot
            {
                Address = $"0x{i.Address:X8}",
                RawWord = $"0x{i.RawWord:X8}",
                Mnemonic = i.Mnemonic,
            })
            .ToList();

        var snapshot = new AnalysisSnapshot
        {
            SchemaVersion = AnalysisSnapshotSchemaVersion,
            Input = new AnalysisInputSnapshot
            {
                Sha256 = chdSha256,
                Size = chdBytes.Length,
                Format = "CHD",
                ChdVersion = (int)chdStats.Version,
            },
            Chd = new ChdSnapshot
            {
                Version = (int)chdStats.Version,
                LogicalBytes = (long)chdStats.LogicalBytes,
                HunkBytes = (long)chdStats.HunkBytes,
                TotalHunks = chdStats.TotalHunks,
                CdlzCount = chdStats.CdlzCount,
                CdzlCount = chdStats.CdzlCount,
                MapBytesConsumed = chdStats.MapBytesConsumed,
                DataRegionSize = chdStats.DataRegionSize,
            },
            Iso = iso,
            SystemCnf = systemCnf,
            PsxExe = psxExe,
            Analysis = analysis,
            Instructions = instructions,
        };

        Record("SNAPSHOT", "PASS", $"Built deterministic snapshot (schema v{snapshot.SchemaVersion})");
        return (snapshot, log);
    }

    private const int AnalysisSnapshotSchemaVersion = 1;

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a byte buffer. Shared so all artifact
    /// writers use a single implementation.
    /// </summary>
    public static string ComputeSha256ForTest(byte[] data) => ComputeSha256(data);

    private static IsoSnapshot CaptureIso(byte[] chdBytes, string chdSha256, Action<string, string, string> record)
    {
        record("FILESYSTEM", "START", "Reading ISO9660 filesystem");
        var iso = new Iso9660Reader(sector =>
        {
            using var chd = ChdReader.Open(new MemoryStream(chdBytes, writable: false));
            var cdSector = chd.ReadSector(sector);
            var isoData = new byte[IsoSectorSize];
            Buffer.BlockCopy(cdSector, 24, isoData, 0, IsoSectorSize);
            return isoData;
        });
        iso.Initialize();

        var systemCnfExists = iso.FileExists("SYSTEM.CNF");

        var root = new Iso9660DirectoryEntry
        {
            Location = iso.RootDirectoryLocation,
            Size = iso.RootDirectorySize,
            Flags = 0x02,
            FileNameLength = 1,
            FileName = "\0",
        };
        iso.CountEntries(root, out int fileCount, out int directoryCount);

        record("FILESYSTEM", "PASS",
            $"ISO9660 loaded; volume='{iso.VolumeIdentifier}' files={fileCount} dirs={directoryCount} systemCnf={systemCnfExists}");
        return new IsoSnapshot
        {
            VolumeIdentifier = iso.VolumeIdentifier,
            VolumeSpaceSize = iso.VolumeSpaceSize,
            RootDirectoryLocation = iso.RootDirectoryLocation,
            SystemCnfExists = systemCnfExists,
            FileCount = fileCount,
            DirectoryCount = directoryCount,
        };
    }

    private static PsxExeSnapshot CapturePsxExe(byte[] chdBytes, string exeFileName, Action<string, string, string> record)
    {
        record("PSX_EXE", "START", "Loading PS-X EXE");
        try
        {
            var iso = new Iso9660Reader(sector =>
            {
                using var chd = ChdReader.Open(new MemoryStream(chdBytes, writable: false));
                var cdSector = chd.ReadSector(sector);
                var isoData = new byte[IsoSectorSize];
                Buffer.BlockCopy(cdSector, 24, isoData, 0, IsoSectorSize);
                return isoData;
            });
            iso.Initialize();

            var systemCnfBytes = iso.ReadFile("SYSTEM.CNF");
            var systemCnf = SystemCnfParser.Parse(systemCnfBytes);
            var bootPath = NormalizeBootPath(systemCnf.BootPath);
            var exeBytes = iso.ReadFile(bootPath);
            var exeFileNameResolved = Path.GetFileName(bootPath.Split(';')[0]);
            var exe = PsxExe.Load(exeBytes, exeFileNameResolved);
            var hash = ComputeSha256(exeBytes);

            record("PSX_EXE", "PASS",
                $"Loaded '{exe.FileName}' entry=0x{exe.Header.EntryPoint:X8}");
            return new PsxExeSnapshot
            {
                FileName = exe.FileName,
                Serial = Path.GetFileNameWithoutExtension(exe.FileName),
                FileSize = exe.FileSize,
                FileHash = hash,
                EntryPoint = exe.Header.EntryPoint,
                TextStart = exe.Header.TextStart,
                TextSize = exe.Header.TextSize,
                DataStart = exe.Header.DataStart,
                DataSize = exe.Header.DataSize,
                BssStart = exe.Header.BssStart,
                BssSize = exe.Header.BssSize,
                SpInitial = exe.Header.SpInitial,
                GpInitial = exe.Header.GpInitial,
            };
        }
        catch (Exception ex)
        {
            record("PSX_EXE", "FAIL", ex.Message);
            throw;
        }
    }

    private static AnalysisSummarySnapshot ComputeAnalysisSummary(DiscImageAnalysisReport report)
    {
        int branchCount = 0;
        int jumpCount = 0;
        int callCandidateCount = 0;
        int returnCandidateCount = 0;
        int basicBlockCount = 0;

        for (int i = 0; i < report.DecodedInstructions.Count; i++)
        {
            var inst = report.DecodedInstructions[i];

            switch (inst.ControlFlow)
            {
                case "ConditionalBranch":
                case "LinkBranch":
                    branchCount++;
                    if (inst.ControlFlow == "LinkBranch") callCandidateCount++;
                    break;
                case "JumpAbsolute":
                    jumpCount++;
                    break;
                case "JumpRegister":
                    jumpCount++;
                    // jalr writes $ra -> call candidate; jr $ra -> return candidate.
                    if (inst.Mnemonic == "jalr") callCandidateCount++;
                    else if (inst.Mnemonic == "jr") returnCandidateCount++;
                    break;
            }

            if (i > 0 && inst.ControlFlow != "Sequential")
            {
                // A new basic block begins at every non-sequential instruction.
                basicBlockCount++;
            }
        }

        // Count the entry block itself plus one block per observed control-flow boundary.
        basicBlockCount = Math.Max(1, basicBlockCount + 1);

        return new AnalysisSummarySnapshot
        {
            DecodeStartAddress = report.DecodeStartAddress,
            DecodedInstructionCount = report.DecodedInstructionCount,
            DecodeFailureCount = report.DecodeFailures.Count,
            BasicBlockCount = basicBlockCount,
            BranchCount = branchCount,
            JumpCount = jumpCount,
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

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

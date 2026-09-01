using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Builds synthetic analysis inputs so the deterministic artifact format can be tested
/// without any real disc image. This is what lets the determinism, ordering, identity
/// and multi-fixture guarantees run in CI, where no ROM exists and never will.
///
/// The synthetic values imitate the shape of a real PS-X EXE analysis (kernel-segment
/// addresses, a Sony-style disc label, a branch/jump/fallthrough mix) without
/// reproducing any real title's data.
/// </summary>
[Test]
public static class SyntheticAnalysisReports
{
    /// <summary>A second, structurally different fixture, used for multi-fixture assertions.</summary>
    public const string AlternateExecutableName = "SLUS_987.65;1";

    /// <summary>
    /// A small but structurally complete analysis report: several decoded instructions
    /// covering multiple control-flow classes, one decode failure, two basic blocks and
    /// three CFG edges of different kinds.
    /// </summary>
    public static DiscImageAnalysisReport CreateReport(
        string discSha256,
        string executableFileName = "SLPS_012.34;1",
        uint entryPoint = 0x80010000)
    {
        var instructions = new List<DecodedInstruction>
        {
            Instruction(entryPoint + 0x00, 0x3C1C8005, "lui", "$gp, 0x8005", "IType", nameof(R3000aControlFlowKind.Sequential)),
            Instruction(entryPoint + 0x04, 0x279C8000, "addiu", "$gp, $gp, -32768", "IType", nameof(R3000aControlFlowKind.Sequential)),
            Instruction(entryPoint + 0x08, 0x1000FFFE, "beq", "$zero, $zero, -8", "IType", nameof(R3000aControlFlowKind.ConditionalBranch)),
            Instruction(entryPoint + 0x0C, 0x00000000, "nop", string.Empty, "RType", nameof(R3000aControlFlowKind.Sequential)),
            Instruction(entryPoint + 0x10, 0x0C004000, "jal", "0x80010000", "JType", nameof(R3000aControlFlowKind.LinkBranch)),
            Instruction(entryPoint + 0x14, 0x00000000, "nop", string.Empty, "RType", nameof(R3000aControlFlowKind.Sequential)),
            Instruction(entryPoint + 0x18, 0x03E00008, "jr", "$ra", "RType", nameof(R3000aControlFlowKind.JumpRegister)),
            Instruction(entryPoint + 0x1C, 0x00000000, "nop", string.Empty, "RType", nameof(R3000aControlFlowKind.Sequential)),
        };

        var basicBlocks = new List<BasicBlock>
        {
            new() { StartAddress = entryPoint, EndAddress = entryPoint + 0x0C, InstructionCount = 4 },
            new() { StartAddress = entryPoint + 0x10, EndAddress = entryPoint + 0x1C, InstructionCount = 4 },
        };

        var cfgEdges = new List<CfgEdge>
        {
            new(entryPoint + 0x08, entryPoint + 0x00, "branch"),
            new(entryPoint + 0x08, entryPoint + 0x10, "fallthrough"),
            new(entryPoint + 0x10, entryPoint + 0x00, "jump"),
            new(entryPoint + 0x18, 0x00000000, "indirect"),
        };

        var failures = new List<DecodeFailure>
        {
            new() { Address = entryPoint + 0x20, Reason = "Address outside text segment bounds" },
        };

        return new DiscImageAnalysisReport
        {
            DiscImageSha256 = discSha256,
            SystemCnfBootPath = @"cdrom:\" + executableFileName,
            ExecutableFileName = executableFileName,
            EntryPoint = entryPoint,
            TextStart = entryPoint,
            TextSize = 0x00020000,
            SpInitial = 0x801FFF00,
            GpInitial = 0x80050000,
            ExecutableFileSize = 0x00020800,
            ExecutableFileHash = Sha256Of(executableFileName),
            DecodeStartAddress = entryPoint,
            DecodedInstructionCount = instructions.Count,
            DecodedInstructions = instructions,
            DecodeFailures = failures,
            BasicBlocks = basicBlocks,
            CfgEdges = cfgEdges,
            CallCandidateCount = 1,
            ReturnCandidateCount = 1,
        };
    }

    /// <summary>
    /// A minimal report with exactly one instruction, one block and one edge, used by the
    /// golden-text tests that pin the canonical JSON encoding.
    /// </summary>
    public static DiscImageAnalysisReport CreateMinimalReport(string discSha256)
    {
        return new DiscImageAnalysisReport
        {
            DiscImageSha256 = discSha256,
            SystemCnfBootPath = @"cdrom:\SLPS_012.34;1",
            ExecutableFileName = "SLPS_012.34;1",
            EntryPoint = 0x80010000,
            TextStart = 0x80010000,
            TextSize = 0x00000010,
            SpInitial = 0x801FFF00,
            GpInitial = 0x80050000,
            ExecutableFileSize = 0x00000810,
            ExecutableFileHash = new string('b', 64),
            DecodeStartAddress = 0x80010000,
            DecodedInstructionCount = 1,
            DecodedInstructions = new List<DecodedInstruction>
            {
                Instruction(0x80010000, 0x03E00008, "jr", "$ra", "RType", nameof(R3000aControlFlowKind.JumpRegister)),
            },
            DecodeFailures = Array.Empty<DecodeFailure>(),
            BasicBlocks = new List<BasicBlock>
            {
                new() { StartAddress = 0x80010000, EndAddress = 0x80010000, InstructionCount = 1 },
            },
            CfgEdges = new List<CfgEdge> { new(0x80010000, 0x00000000, "indirect") },
            CallCandidateCount = 0,
            ReturnCandidateCount = 1,
        };
    }

    /// <summary>Container and filesystem statistics with fixed, obviously synthetic values.</summary>
    public static ChdMapStatistics CreateChdStatistics() => new()
    {
        Version = 5,
        LogicalBytes = 700_000_000,
        HunkBytes = 19_584,
        TotalHunks = 35_748,
        CdlzCount = 30_000,
        CdzlCount = 5_748,
        MapBytesConsumed = 123_456,
        DataRegionSize = 456_789_012,
    };

    /// <summary>Filesystem statistics with fixed, obviously synthetic values.</summary>
    public static IsoVolumeStatistics CreateIsoStatistics(string volumeIdentifier = "SYNTHETIC_VOLUME") => new()
    {
        VolumeIdentifier = volumeIdentifier,
        VolumeSpaceSize = 341_796,
        RootDirectoryLocation = 22,
        RootDirectorySize = 2_048,
        SystemCnfPresent = true,
        FileCount = 42,
        DirectoryCount = 7,
    };

    /// <summary>Assembles a complete artifact-builder input around a report.</summary>
    public static DeterministicArtifactInput CreateInput(
        string fixtureId,
        DiscImageAnalysisReport report,
        string? volumeIdentifier = null)
    {
        return new DeterministicArtifactInput
        {
            FixtureId = fixtureId,
            DiscImageFormat = "CHD",
            DiscImageSha256 = report.DiscImageSha256,
            DiscImageSizeBytes = 512_345_678,
            Chd = CreateChdStatistics(),
            Iso = volumeIdentifier is null ? CreateIsoStatistics() : CreateIsoStatistics(volumeIdentifier),
            Report = report,
        };
    }

    /// <summary>A stable synthetic 64-character lowercase hex digest derived from a seed string.</summary>
    public static string Sha256Of(string seed)
    {
        return System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant();
    }

    private static DecodedInstruction Instruction(
        uint address, uint rawWord, string mnemonic, string operands, string format, string controlFlow)
    {
        return new DecodedInstruction
        {
            Address = address,
            RawWord = rawWord,
            Mnemonic = mnemonic,
            Operands = operands,
            Format = format,
            ControlFlow = controlFlow,
        };
    }
}

using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// Inputs required to serialize one analysis run. This is a pure value: it carries the
/// runtime analysis result plus the container/filesystem statistics that
/// <see cref="DiscImageAnalysisReport"/> does not itself model. Nothing here is
/// environment-derived — in particular there is no path, no timestamp and no run id.
/// </summary>
[Domain]
public sealed record DeterministicArtifactInput
{
    /// <summary>Human-facing fixture alias; the formal identity is <see cref="DiscImageSha256"/>.</summary>
    public required string FixtureId { get; init; }

    /// <summary>Container format of the disc image, e.g. <c>"CHD"</c>.</summary>
    public required string DiscImageFormat { get; init; }

    /// <summary>Lowercase hex SHA-256 of the whole disc image file.</summary>
    public required string DiscImageSha256 { get; init; }

    /// <summary>Size of the disc image file in bytes.</summary>
    public required long DiscImageSizeBytes { get; init; }

    public required ChdMapStatistics Chd { get; init; }
    public required IsoVolumeStatistics Iso { get; init; }

    /// <summary>The existing runtime analysis result, reused verbatim rather than recomputed.</summary>
    public required DiscImageAnalysisReport Report { get; init; }
}

/// <summary>
/// Serialization layer that turns one runtime <see cref="DiscImageAnalysisReport"/> into
/// the persisted deterministic artifact set (<c>manifest.json</c>, <c>report.json</c>,
/// <c>instructions.json</c>, <c>cfg.json</c>).
///
/// The builder does not analyze anything: the analysis pipeline of Issue #212 remains
/// the single producer of results. Its only job is to project those results into a
/// stable, versioned, diffable shape.
///
/// Determinism rests on three rules, each enforced by a test:
/// <list type="number">
///   <item>Every array is sorted into an explicitly documented canonical order, never
///   left in discovery order.</item>
///   <item>Every scalar is rendered culture-invariantly (addresses as <c>0xXXXXXXXX</c>).</item>
///   <item>No environment-derived value is read. The Domain layer's forbidden-API rule
///   (PSXR005) makes that a compile-time property of this namespace rather than a
///   convention.</item>
/// </list>
/// </summary>
[Domain]
public static class DeterministicArtifactBuilder
{
    /// <summary>
    /// Builds all four documents and their canonical text. The detailed documents are
    /// serialized first so the manifest can reference them by content hash; the manifest
    /// therefore never hashes itself.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> carries a fixture id that is not canonical. Callers should
    /// pass <see cref="AnalysisArtifactSchema.NormalizeFixtureId"/> output.
    /// </exception>
    public static RealRomAnalysisArtifacts Build(DeterministicArtifactInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!AnalysisArtifactSchema.IsValidFixtureId(input.FixtureId))
        {
            throw new ArgumentException(
                $"Fixture id '{input.FixtureId}' is not canonical; " +
                "use AnalysisArtifactSchema.NormalizeFixtureId to derive one.",
                nameof(input));
        }

        var identity = BuildIdentity(input);
        var report = BuildReport(input, identity);
        var instructions = BuildInstructions(input.Report, identity);
        var cfg = BuildCfg(input.Report, identity);

        var reportText = ArtifactJson.Serialize(report);
        var instructionsText = ArtifactJson.Serialize(instructions);
        var cfgText = ArtifactJson.Serialize(cfg);

        var manifest = BuildManifest(
            input.Report,
            identity,
            Reference(AnalysisArtifactSchema.ReportFileName, AnalysisArtifactSchema.ReportArtifactKind, AnalysisArtifactSchema.ReportSchemaVersion, reportText),
            Reference(AnalysisArtifactSchema.InstructionsFileName, AnalysisArtifactSchema.InstructionsArtifactKind, AnalysisArtifactSchema.InstructionsSchemaVersion, instructionsText),
            Reference(AnalysisArtifactSchema.CfgFileName, AnalysisArtifactSchema.CfgArtifactKind, AnalysisArtifactSchema.CfgSchemaVersion, cfgText));

        var manifestText = ArtifactJson.Serialize(manifest);

        var files = new List<ArtifactFile>
        {
            new() { FileName = AnalysisArtifactSchema.CfgFileName, Content = cfgText },
            new() { FileName = AnalysisArtifactSchema.InstructionsFileName, Content = instructionsText },
            new() { FileName = AnalysisArtifactSchema.ManifestFileName, Content = manifestText },
            new() { FileName = AnalysisArtifactSchema.ReportFileName, Content = reportText },
        };
        files.Sort(static (left, right) => string.CompareOrdinal(left.FileName, right.FileName));

        return new RealRomAnalysisArtifacts
        {
            Manifest = manifest,
            Report = report,
            Instructions = instructions,
            Cfg = cfg,
            Files = files,
        };
    }

    private static ArtifactDocumentReference Reference(string fileName, string artifactKind, int schemaVersion, string content)
    {
        var bytes = ArtifactJson.ToUtf8Bytes(content);
        return new ArtifactDocumentReference
        {
            FileName = fileName,
            ArtifactKind = artifactKind,
            SchemaVersion = schemaVersion,
            SizeBytes = bytes.Length,
            Sha256 = ArtifactJson.Sha256Hex(bytes),
        };
    }

    private static ArtifactFixtureIdentity BuildIdentity(DeterministicArtifactInput input)
    {
        return new ArtifactFixtureIdentity
        {
            FixtureId = input.FixtureId,
            DiscImageFormat = input.DiscImageFormat,
            DiscImageSha256 = input.DiscImageSha256,
            DiscImageSizeBytes = input.DiscImageSizeBytes,
            ExecutableFileName = input.Report.ExecutableFileName,
            ExecutableSerial = AnalysisArtifactSchema.DeriveExecutableSerial(input.Report.ExecutableFileName),
            ExecutableSizeBytes = input.Report.ExecutableFileSize,
            ExecutableSha256 = input.Report.ExecutableFileHash,
        };
    }

    private static AnalysisManifestDocument BuildManifest(
        DiscImageAnalysisReport report,
        ArtifactFixtureIdentity identity,
        params ArtifactDocumentReference[] documents)
    {
        var ordered = documents.ToList();
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.FileName, right.FileName));

        var (branchCount, jumpCount) = CountBranchesAndJumps(report.DecodedInstructions);

        return new AnalysisManifestDocument
        {
            SchemaVersion = AnalysisArtifactSchema.ManifestSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.ManifestArtifactKind,
            Fixture = identity,
            Counts = new AnalysisCounts
            {
                DecodedInstructions = report.DecodedInstructionCount,
                DecodeFailures = report.DecodeFailures.Count,
                BasicBlocks = report.BasicBlocks.Count,
                CfgEdges = report.CfgEdges.Count,
                Branches = branchCount,
                Jumps = jumpCount,
                CallCandidates = report.CallCandidateCount,
                ReturnCandidates = report.ReturnCandidateCount,
            },
            Documents = ordered,
        };
    }

    private static AnalysisReportDocument BuildReport(DeterministicArtifactInput input, ArtifactFixtureIdentity identity)
    {
        var report = input.Report;
        var (branchCount, jumpCount) = CountBranchesAndJumps(report.DecodedInstructions);

        var failures = report.DecodeFailures
            .Select(static failure => new DecodeFailureRecord
            {
                Address = AnalysisArtifactSchema.FormatWord32(failure.Address),
                Reason = failure.Reason,
            })
            .OrderBy(static failure => failure.Address, StringComparer.Ordinal)
            .ThenBy(static failure => failure.Reason, StringComparer.Ordinal)
            .ToList();

        return new AnalysisReportDocument
        {
            SchemaVersion = AnalysisArtifactSchema.ReportSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.ReportArtifactKind,
            Fixture = identity,
            Chd = new ChdReportSection
            {
                FormatVersion = (int)input.Chd.Version,
                LogicalBytes = (long)input.Chd.LogicalBytes,
                HunkBytes = input.Chd.HunkBytes,
                TotalHunks = input.Chd.TotalHunks,
                CdlzHunks = input.Chd.CdlzCount,
                CdzlHunks = input.Chd.CdzlCount,
                MapBytesConsumed = input.Chd.MapBytesConsumed,
                DataRegionBytes = input.Chd.DataRegionSize,
            },
            Iso = new IsoReportSection
            {
                VolumeIdentifier = input.Iso.VolumeIdentifier,
                VolumeSpaceSize = input.Iso.VolumeSpaceSize,
                RootDirectoryLocation = input.Iso.RootDirectoryLocation,
                RootDirectorySize = input.Iso.RootDirectorySize,
                SystemCnfPresent = input.Iso.SystemCnfPresent,
                FileCount = input.Iso.FileCount,
                DirectoryCount = input.Iso.DirectoryCount,
            },
            SystemCnf = new SystemCnfReportSection
            {
                BootPath = report.SystemCnfBootPath,
                BootExecutable = report.ExecutableFileName,
            },
            Executable = new ExecutableReportSection
            {
                FileName = report.ExecutableFileName,
                Serial = identity.ExecutableSerial,
                FileSizeBytes = report.ExecutableFileSize,
                Sha256 = report.ExecutableFileHash,
                EntryPoint = AnalysisArtifactSchema.FormatWord32(report.EntryPoint),
                TextStart = AnalysisArtifactSchema.FormatWord32(report.TextStart),
                TextSizeBytes = report.TextSize,
                TextEnd = AnalysisArtifactSchema.FormatWord32(report.TextStart + report.TextSize),
                SpInitial = AnalysisArtifactSchema.FormatWord32(report.SpInitial),
                GpInitial = AnalysisArtifactSchema.FormatWord32(report.GpInitial),
            },
            Decode = new DecodeReportSection
            {
                StartAddress = AnalysisArtifactSchema.FormatWord32(report.DecodeStartAddress),
                InstructionCount = report.DecodedInstructionCount,
                FailureCount = report.DecodeFailures.Count,
                Failures = failures,
                DistributionOrdering = AnalysisArtifactSchema.DistributionOrdering,
                MnemonicMix = Distribution(report.DecodedInstructions, static instruction => instruction.Mnemonic),
                FormatMix = Distribution(report.DecodedInstructions, static instruction => instruction.Format),
                ControlFlowMix = Distribution(report.DecodedInstructions, static instruction => instruction.ControlFlow),
            },
            ControlFlow = new ControlFlowReportSection
            {
                BasicBlockCount = report.BasicBlocks.Count,
                EdgeCount = report.CfgEdges.Count,
                BranchCount = branchCount,
                JumpCount = jumpCount,
                CallCandidateCount = report.CallCandidateCount,
                ReturnCandidateCount = report.ReturnCandidateCount,
                EdgeKindMix = Distribution(report.CfgEdges, static edge => edge.Kind ?? string.Empty),
            },
        };
    }

    private static InstructionListDocument BuildInstructions(DiscImageAnalysisReport report, ArtifactFixtureIdentity identity)
    {
        var instructions = report.DecodedInstructions
            .OrderBy(static instruction => instruction.Address)
            .Select(static instruction => new InstructionRecord
            {
                Address = AnalysisArtifactSchema.FormatWord32(instruction.Address),
                RawWord = AnalysisArtifactSchema.FormatWord32(instruction.RawWord),
                Mnemonic = instruction.Mnemonic,
                Operands = instruction.Operands,
                Format = instruction.Format,
                ControlFlow = instruction.ControlFlow,
            })
            .ToList();

        return new InstructionListDocument
        {
            SchemaVersion = AnalysisArtifactSchema.InstructionsSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.InstructionsArtifactKind,
            Fixture = identity,
            Ordering = AnalysisArtifactSchema.InstructionOrdering,
            Count = instructions.Count,
            Instructions = instructions,
        };
    }

    private static ControlFlowGraphDocument BuildCfg(DiscImageAnalysisReport report, ArtifactFixtureIdentity identity)
    {
        var blocks = report.BasicBlocks
            .OrderBy(static block => block.StartAddress)
            .ThenBy(static block => block.EndAddress)
            .Select(static block => new BasicBlockRecord
            {
                StartAddress = AnalysisArtifactSchema.FormatWord32(block.StartAddress),
                EndAddress = AnalysisArtifactSchema.FormatWord32(block.EndAddress),
                InstructionCount = block.InstructionCount,
            })
            .ToList();

        var edges = report.CfgEdges
            .OrderBy(static edge => edge.SourceAddress)
            .ThenBy(static edge => edge.TargetAddress)
            .ThenBy(static edge => edge.Kind ?? string.Empty, StringComparer.Ordinal)
            .Select(static edge => new CfgEdgeRecord
            {
                SourceAddress = AnalysisArtifactSchema.FormatWord32(edge.SourceAddress),
                TargetAddress = AnalysisArtifactSchema.FormatWord32(edge.TargetAddress),
                Kind = edge.Kind ?? string.Empty,
            })
            .ToList();

        return new ControlFlowGraphDocument
        {
            SchemaVersion = AnalysisArtifactSchema.CfgSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.CfgArtifactKind,
            Fixture = identity,
            BlockOrdering = AnalysisArtifactSchema.BasicBlockOrdering,
            EdgeOrdering = AnalysisArtifactSchema.CfgEdgeOrdering,
            BasicBlockCount = blocks.Count,
            EdgeCount = edges.Count,
            BasicBlocks = blocks,
            Edges = edges,
        };
    }

    /// <summary>
    /// Counts branch and jump instructions using the same classification the #212
    /// pipeline records on each decoded instruction, so artifact counts and pipeline
    /// counts can never drift apart.
    /// </summary>
    private static (int BranchCount, int JumpCount) CountBranchesAndJumps(IReadOnlyList<DecodedInstruction> instructions)
    {
        var branchCount = 0;
        var jumpCount = 0;

        for (var index = 0; index < instructions.Count; index++)
        {
            switch (instructions[index].ControlFlow)
            {
                case nameof(R3000aControlFlowKind.ConditionalBranch):
                case nameof(R3000aControlFlowKind.LinkBranch):
                    branchCount++;
                    break;
                case nameof(R3000aControlFlowKind.JumpAbsolute):
                case nameof(R3000aControlFlowKind.JumpRegister):
                    jumpCount++;
                    break;
            }
        }

        return (branchCount, jumpCount);
    }

    /// <summary>
    /// Builds a histogram in canonical ordinal name order. Grouping alone is not enough:
    /// hash-based grouping has no defined enumeration order, so the explicit sort is what
    /// makes the resulting array reproducible.
    /// </summary>
    private static IReadOnlyList<NamedCount> Distribution<T>(IReadOnlyList<T> items, Func<T, string> selectName)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var name = selectName(items[index]);
            counts[name] = counts.TryGetValue(name, out var existing) ? existing + 1 : 1;
        }

        return counts
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new NamedCount { Name = pair.Key, Count = pair.Value })
            .ToList();
    }
}

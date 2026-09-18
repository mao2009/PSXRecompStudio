using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.AnalysisArtifacts;

/// <summary>
/// Pure projection that turns an already-selected guest range's basic blocks into the
/// versioned <see cref="SelectedFunctionProvenanceDocument"/>. It does not discover
/// functions, select ranges, or analyze anything: selection happens upstream (for
/// example <c>RealRomCandidateSelector</c>, Issue #225), and this type only renders the
/// existing evidence into the deterministic #215 provenance shape.
///
/// <para>
/// Determinism follows the same three rules as <see cref="DeterministicArtifactBuilder"/>:
/// every array is explicitly sorted into a recorded canonical order, every scalar is
/// rendered culture-invariantly through <see cref="AnalysisArtifactSchema.FormatWord32"/>,
/// and no environment-derived value is read (enforced for the whole Domain layer by the
/// architecture analyzer's AARC003 rule).
/// </para>
/// </summary>
[Domain]
public static class FunctionProvenanceBuilder
{
    /// <summary>
    /// Builds a provenance document for one selected guest range. The block list is the
    /// selected range's already-partitioned basic blocks; <paramref name="startAddress"/> and
    /// <paramref name="endAddress"/> are the addresses the producer recorded for the
    /// selection and are not required to be exactly the blocks' outer bounds (a range that
    /// begins mid-block is still a valid selection).
    /// </summary>
    public static SelectedFunctionProvenanceDocument Build(
        ArtifactFixtureIdentity fixture,
        uint startAddress,
        uint endAddress,
        IReadOnlyList<BasicBlock> blocks,
        string selectionRule,
        IReadOnlyList<string> requiredInstructionSubset,
        IReadOnlyList<string> unresolvedFlags)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionRule);
        ArgumentNullException.ThrowIfNull(requiredInstructionSubset);
        ArgumentNullException.ThrowIfNull(unresolvedFlags);

        if (blocks.Count == 0)
        {
            throw new ArgumentException("A selected range must contain at least one basic block.", nameof(blocks));
        }

        if (endAddress < startAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(endAddress), "endAddress must be >= startAddress.");
        }

        var orderedBlocks = blocks
            .OrderBy(static block => block.StartAddress)
            .ThenBy(static block => block.EndAddress)
            .Select(static block => new BasicBlockRecord
            {
                StartAddress = AnalysisArtifactSchema.FormatWord32(block.StartAddress),
                EndAddress = AnalysisArtifactSchema.FormatWord32(block.EndAddress),
                InstructionCount = block.InstructionCount,
            })
            .ToList();

        var subset = requiredInstructionSubset
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var flags = unresolvedFlags
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        return new SelectedFunctionProvenanceDocument
        {
            SchemaVersion = AnalysisArtifactSchema.FunctionProvenanceSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.FunctionProvenanceArtifactKind,
            Fixture = fixture,
            StartAddress = AnalysisArtifactSchema.FormatWord32(startAddress),
            EndAddress = AnalysisArtifactSchema.FormatWord32(endAddress),
            InstructionCount = orderedBlocks.Sum(static block => block.InstructionCount),
            SelectionRule = selectionRule,
            BlockOrdering = AnalysisArtifactSchema.BasicBlockOrdering,
            BasicBlocks = orderedBlocks,
            BlockIdentitySha256 = ComputeBlockIdentity(orderedBlocks),
            SubsetOrdering = AnalysisArtifactSchema.FunctionProvenanceSubsetOrdering,
            RequiredInstructionSubset = subset,
            FlagsOrdering = AnalysisArtifactSchema.FunctionProvenanceFlagsOrdering,
            UnresolvedFlags = flags,
        };
    }

    /// <summary>
    /// SHA-256 over the canonical JSON of the ordered block records alone, so the identity
    /// identifies the range's CFG subset without depending on the rest of the document —
    /// and without ever hashing the document that contains the hash.
    /// </summary>
    public static string ComputeBlockIdentity(IReadOnlyList<BasicBlockRecord> orderedBlocks)
    {
        ArgumentNullException.ThrowIfNull(orderedBlocks);
        return ArtifactJson.Sha256Hex(ArtifactJson.ToUtf8Bytes(ArtifactJson.Serialize(orderedBlocks.ToArray())));
    }
}
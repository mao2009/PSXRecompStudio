using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>Stable identity of the executable text supplied to function discovery.</summary>
[Domain]
public sealed record ExecutableTextRegion(uint StartAddress, uint SizeBytes)
{
    public uint EndAddress => unchecked(StartAddress + SizeBytes);
}

/// <summary>One function discovered from a reachable control-flow graph.</summary>
[Domain]
public sealed record DiscoveredFunction
{
    public required string FunctionId { get; init; }
    public required uint EntryAddress { get; init; }
    public required IReadOnlyList<BasicBlock> BasicBlocks { get; init; }
    public required IReadOnlyList<CfgEdge> Edges { get; init; }
    public required IReadOnlyList<uint> DirectCallTargets { get; init; }
    public required IReadOnlyList<uint> ReturnAddresses { get; init; }
    public required IReadOnlyList<uint> UnresolvedIndirectSources { get; init; }
}

/// <summary>
/// Deterministic #210 input artifact.  It is a projection of the existing decoded
/// instruction/basic-block analysis; it does not decode, emulate, or infer indirect
/// targets.  This is the hand-off consumed by later recompiler lowering work.
/// </summary>
[Domain]
public sealed record FunctionDiscoveryArtifact
{
    public const int CurrentSchemaVersion = 1;
    public const string ArtifactKind = "psxrecomp.function-discovery";

    public required int SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required ExecutableTextRegion TextRegion { get; init; }
    public required uint EntryPoint { get; init; }
    public required IReadOnlyList<DiscoveredFunction> Functions { get; init; }

    public string ToCanonicalJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        var json = JsonSerializer.Serialize(this, options);
        return json.ReplaceLineEndings("\n") + "\n";
    }

    public string Sha256()
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())))
            .ToLowerInvariant();
    }
}

/// <summary>Builds function candidates and reachable CFGs from existing analysis output.</summary>
[Domain]
public static class FunctionDiscovery
{
    public static FunctionDiscoveryArtifact Build(
        DiscImageAnalysisReport report,
        IReadOnlyList<uint>? explicitEntries = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Build(report.EntryPoint, report.TextStart, report.TextSize,
            report.DecodedInstructions, report.BasicBlocks, report.CfgEdges, explicitEntries);
    }

    public static FunctionDiscoveryArtifact Build(
        uint entryPoint,
        uint textStart,
        uint textSize,
        IReadOnlyList<DecodedInstruction> instructions,
        IReadOnlyList<BasicBlock> blocks,
        IReadOnlyList<CfgEdge> edges,
        IReadOnlyList<uint>? explicitEntries = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(edges);

        var orderedInstructions = instructions.OrderBy(static i => i.Address).ToArray();
        var rawByAddress = orderedInstructions.ToDictionary(i => i.Address,
            static i => R3000aDecoder.Decode(i.RawWord));
        var blockByStart = blocks.OrderBy(static b => b.StartAddress)
            .ThenBy(static b => b.EndAddress).ToDictionary(static b => b.StartAddress);
        var blockForAddress = blocks.ToDictionaryMany(static b =>
            Enumerable.Range(0, b.InstructionCount).Select(i => unchecked(b.StartAddress + (uint)(i * 4))));
        var edgeBySource = edges.GroupBy(static e => e.SourceAddress)
            .ToDictionary(static g => g.Key, static g => g.OrderBy(e => e.TargetAddress)
                .ThenBy(e => e.Kind ?? string.Empty, StringComparer.Ordinal).ToArray());

        var seeds = new SortedSet<uint> { entryPoint };
        if (explicitEntries is not null)
        {
            foreach (var address in explicitEntries) seeds.Add(address);
        }

        var functions = new List<DiscoveredFunction>();
        var pendingSeeds = new SortedSet<uint>(seeds);
        while (pendingSeeds.Count > 0)
        {
            var seed = pendingSeeds.Min;
            pendingSeeds.Remove(seed);
            var reachable = new SortedSet<uint>();
            var queue = new Queue<uint>();
            if (blockByStart.ContainsKey(seed)) queue.Enqueue(seed);
            var calls = new SortedSet<uint>();
            var returns = new SortedSet<uint>();
            var unresolved = new SortedSet<uint>();

            while (queue.Count > 0)
            {
                var blockStart = queue.Dequeue();
                if (!reachable.Add(blockStart) || !blockByStart.TryGetValue(blockStart, out var block)) continue;
                var blockEnd = unchecked(block.StartAddress + (uint)((block.InstructionCount - 1) * 4));
                for (var address = block.StartAddress; address <= blockEnd; address += 4)
                {
                    if (!rawByAddress.TryGetValue(address, out var raw)) continue;
                    var isReturn = raw.Opcode == R3000aOpcode.Jr && IsRegister(raw.Operand0, 31);
                    if (isReturn) returns.Add(address);
                    if (IsCall(raw) && TryDirectTarget(raw, address, out var callTarget))
                    {
                        calls.Add(callTarget);
                        if (seeds.Add(callTarget)) pendingSeeds.Add(callTarget);
                    }
                    if (raw.ControlFlow == R3000aControlFlowKind.JumpRegister && !isReturn) unresolved.Add(address);

                    if (edgeBySource.TryGetValue(address, out var outgoing))
                    {
                        foreach (var edge in outgoing)
                        {
                            var callEdge = IsCall(raw) && (edge.Kind is "jump" or "branch");
                            if (callEdge || isReturn) continue;
                            if (blockByStart.ContainsKey(edge.TargetAddress)) queue.Enqueue(edge.TargetAddress);
                        }
                    }
                }
            }

            var functionBlocks = reachable.Select(address => blockByStart[address]).OrderBy(static b => b.StartAddress).ToArray();
            var functionEdges = edges.Where(e => blockForAddress.TryGetValue(e.SourceAddress, out var owner) && reachable.Contains(owner.StartAddress))
                .OrderBy(static e => e.SourceAddress).ThenBy(static e => e.TargetAddress)
                .ThenBy(e => e.Kind ?? string.Empty, StringComparer.Ordinal).ToArray();
            functions.Add(new DiscoveredFunction
            {
                FunctionId = $"function_{seed.ToString("X8", CultureInfo.InvariantCulture)}",
                EntryAddress = seed,
                BasicBlocks = functionBlocks,
                Edges = functionEdges,
                DirectCallTargets = calls.ToArray(),
                ReturnAddresses = returns.ToArray(),
                UnresolvedIndirectSources = unresolved.ToArray(),
            });
        }

        return new FunctionDiscoveryArtifact
        {
            SchemaVersion = FunctionDiscoveryArtifact.CurrentSchemaVersion,
            Kind = FunctionDiscoveryArtifact.ArtifactKind,
            TextRegion = new ExecutableTextRegion(textStart, textSize),
            EntryPoint = entryPoint,
            Functions = functions.OrderBy(static f => f.EntryAddress).ToArray(),
        };
    }

    private static bool IsCall(R3000aInstruction instruction) => instruction.LinkInfo.WritesLink;

    private static bool TryDirectTarget(R3000aInstruction instruction, uint address, out uint target) =>
        R3000aBranchSemantics.TryGetBranchTarget(instruction, address, out target)
        || R3000aJumpSemantics.TryGetJumpTarget(instruction, address, out target);

    private static bool IsRegister(R3000aOperand operand, byte register) =>
        operand.Kind == R3000aOperandKind.Register && operand.Register == register;
}

[Domain]
internal static class FunctionDiscoveryDictionaryExtensions
{
    public static Dictionary<uint, BasicBlock> ToDictionaryMany(
        this IEnumerable<BasicBlock> blocks,
        Func<BasicBlock, IEnumerable<uint>> keys)
    {
        var result = new Dictionary<uint, BasicBlock>();
        foreach (var block in blocks)
            foreach (var key in keys(block)) result.TryAdd(key, block);
        return result;
    }
}

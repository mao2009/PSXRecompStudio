using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Deterministic analysis report produced by DiscImageAnalyzer.
/// Contains disc metadata, PS-X EXE details, decoded instructions, and
/// basic-block / control-flow information.
/// </summary>
[Domain]
public sealed record DiscImageAnalysisReport
{
    public required string DiscImageSha256 { get; init; }
    public required string SystemCnfBootPath { get; init; }
    public required string ExecutableFileName { get; init; }
    public required uint EntryPoint { get; init; }
    public required uint TextStart { get; init; }
    public required uint TextSize { get; init; }
    public required uint SpInitial { get; init; }
    public required uint GpInitial { get; init; }
    public required uint ExecutableFileSize { get; init; }
    public required string ExecutableFileHash { get; init; }
    public required uint DecodeStartAddress { get; init; }
    public required int DecodedInstructionCount { get; init; }
    public required IReadOnlyList<DecodedInstruction> DecodedInstructions { get; init; }
    public required IReadOnlyList<DecodeFailure> DecodeFailures { get; init; }
    public required IReadOnlyList<BasicBlock> BasicBlocks { get; init; }
    public required IReadOnlyList<CfgEdge> CfgEdges { get; init; }
    public required int CallCandidateCount { get; init; }
    public required int ReturnCandidateCount { get; init; }

    /// <summary>
    /// Function/CFG projection built from this report's existing decoded instructions,
    /// blocks and edges. It is optional for backward-compatible report construction.
    /// </summary>
    public FunctionDiscoveryArtifact? FunctionDiscovery { get; init; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    public string ToTokenString()
    {
        var builder = new StringBuilder();
        AppendField(builder, "discImageSha256", DiscImageSha256);
        AppendField(builder, "systemCnfBootPath", SystemCnfBootPath);
        AppendField(builder, "executableFileName", ExecutableFileName);
        AppendField(builder, "entryPoint", EntryPoint.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "textStart", TextStart.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "textSize", TextSize.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "spInitial", SpInitial.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "gpInitial", GpInitial.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "executableFileSize", ExecutableFileSize.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "executableFileHash", ExecutableFileHash);
        AppendField(builder, "decodeStartAddress", DecodeStartAddress.ToString("X8", CultureInfo.InvariantCulture));
        AppendField(builder, "decodedInstructionCount", DecodedInstructionCount.ToString(CultureInfo.InvariantCulture));

        for (int i = 0; i < DecodedInstructions.Count; i++)
        {
            var inst = DecodedInstructions[i];
            var token = $"address={inst.Address.ToString("X8", CultureInfo.InvariantCulture)};mnemonic={inst.Mnemonic};operands={inst.Operands};raw=0x{inst.RawWord:X8}";
            AppendField(builder, $"instruction.{i}", token);
        }

        for (int i = 0; i < DecodeFailures.Count; i++)
        {
            var fail = DecodeFailures[i];
            var token = $"address={fail.Address.ToString("X8", CultureInfo.InvariantCulture)};reason={fail.Reason}";
            AppendField(builder, $"failure.{i}", token);
        }

        AppendField(builder, "basicBlockCount", BasicBlocks.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < BasicBlocks.Count; i++)
        {
            AppendField(builder, $"block.{i}", BasicBlocks[i].ToTokenString());
        }

        AppendField(builder, "cfgEdgeCount", CfgEdges.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < CfgEdges.Count; i++)
        {
            AppendField(builder, $"edge.{i}", CfgEdges[i].ToTokenString());
        }

        AppendField(builder, "callCandidateCount", CallCandidateCount.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "returnCandidateCount", ReturnCandidateCount.ToString(CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string key, string? value)
    {
        if (builder.Length > 0)
        {
            builder.Append(';');
        }
        builder.Append(key).Append('=').Append(value ?? string.Empty);
    }
}

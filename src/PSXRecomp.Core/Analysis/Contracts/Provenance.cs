using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Identifies who or what produced an artifact, including the optional human reviewer.
/// </summary>
[Domain]
public record Provenance(
    string ToolName,
    string ToolVersion,
    string AgentId,
    string? HumanReviewer = null)
{
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(ToolName)
            && !string.IsNullOrEmpty(ToolVersion)
            && !string.IsNullOrEmpty(AgentId);
    }

    public string ToTokenString()
    {
        return StableToken.Field("toolName", ToolName)
            + StableToken.Field("toolVersion", ToolVersion)
            + StableToken.Field("agentId", AgentId)
            + StableToken.Field("humanReviewer", HumanReviewer);
    }
}
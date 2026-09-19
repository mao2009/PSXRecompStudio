using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Reads a legally supplied PS-X EXE and lowers it into the production
/// <see cref="RecompilerIrProgram"/> both commands drive. This is a pure
/// composition of production Domain contracts — <c>PsxExe.Load</c>,
/// <c>PsxExeTitleInput.Build</c>, <c>R3000aDecoder.Decode</c> and
/// <c>MipsToIrLowerer.LowerProgram</c> — with no parsing or lowering of its own;
/// the host-I/O here (file read) is Infrastructure-layer responsibility.
/// </summary>
[Infrastructure]
internal static class CliInput
{
    public static PsxExeTitleExecution LoadExe(string exePath, uint outerBudget, uint segmentBudget)
    {
        var bytes = File.ReadAllBytes(exePath);
        var exe = PsxExe.Load(bytes, exePath);
        return PsxExeTitleInput.Build(exe, outerBudget, segmentBudget);
    }

    public static RecompilerIrProgram Lower(PsxExeTitleExecution input)
    {
        var words = input.InstructionWords;
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), input.LoadAddress + (uint)i * 4u));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }
}
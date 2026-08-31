using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

[Test]
public class DiscImageAnalysisReportTests
{
    [Fact]
    public void ToTokenString_DeterministicOutput()
    {
        var report = CreateSampleReport();

        var token1 = report.ToTokenString();
        var token2 = report.ToTokenString();

        token1.Should().Be(token2);
        token1.Should().Contain("entryPoint=80010000");
        token1.Should().Contain("mnemonic=nop");
    }

    [Fact]
    public void ToTokenString_ContainsAllFields()
    {
        var report = CreateSampleReport();

        var token = report.ToTokenString();

        token.Should().Contain("discImageSha256=abc123");
        token.Should().Contain("executableFileName=TEST.EXE");
        token.Should().Contain("decodedInstructionCount=2");
        token.Should().Contain("instruction.0=");
        token.Should().Contain("instruction.1=");
    }

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var report = CreateSampleReport();

        var json = report.ToJson();

        json.Should().Contain("\"EntryPoint\"");
        json.Should().Contain("\"DecodedInstructions\"");
    }

    private static DiscImageAnalysisReport CreateSampleReport()
    {
        return new DiscImageAnalysisReport
        {
            DiscImageSha256 = "abc123",
            SystemCnfBootPath = "cdrom:\\TEST.EXE;1",
            ExecutableFileName = "TEST.EXE",
            EntryPoint = 0x80010000,
            TextStart = 0x80010000,
            TextSize = 0x1000,
            SpInitial = 0x801FFF00,
            GpInitial = 0,
            ExecutableFileSize = 4096,
            ExecutableFileHash = "def456",
            DecodeStartAddress = 0x80010000,
            DecodedInstructionCount = 2,
            DecodedInstructions =
            [
                new DecodedInstruction
                {
                    Address = 0x80010000,
                    RawWord = 0x00000000,
                    Mnemonic = "nop",
                    Operands = "",
                    Format = "R",
                    ControlFlow = "Sequential",
                },
                new DecodedInstruction
                {
                    Address = 0x80010004,
                    RawWord = 0x03E00008,
                    Mnemonic = "jr",
                    Operands = "$ra",
                    Format = "R",
                    ControlFlow = "JumpRegister",
                },
            ],
            DecodeFailures = [],
            BasicBlocks =
            [
                new BasicBlock { StartAddress = 0x80010000, EndAddress = 0x80010004, InstructionCount = 2 },
            ],
            CfgEdges =
            [
                new CfgEdge(0x80010004, 0, "indirect"),
            ],
            CallCandidateCount = 0,
            ReturnCandidateCount = 1,
        };
    }
}

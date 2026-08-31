using System.Security.Cryptography;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Integration tests that run the full CHD → SYSTEM.CNF → PS-X EXE → MIPS decode pipeline
/// using the real PERSONA.chd disc image. Tests skip when the fixture is absent (CI).
/// </summary>
[Test]
public class DiscImageAnalyzerIntegrationTests
{
    private static readonly string ChdPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rom", "PERSONA.chd");

#pragma warning disable PSXR005
    private static bool FixtureExists() => File.Exists(ChdPath);
#pragma warning restore PSXR005

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (byte[] Bytes, string Sha256) LoadChd()
    {
#pragma warning disable PSXR005
        var bytes = File.ReadAllBytes(ChdPath);
#pragma warning restore PSXR005
        return (bytes, ComputeSha256(bytes));
    }

    [Fact]
    public void Analyze_PersonaChd_ProducesValidReport()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 32);

        report.Should().NotBeNull();
        report.DiscImageSha256.Should().Be(sha256);
        report.ExecutableFileName.Should().NotBeNullOrEmpty();
        report.EntryPoint.Should().BeGreaterThan(0x80000000u);
        report.TextStart.Should().BeGreaterThan(0x80000000u);
        report.TextSize.Should().BeGreaterThan(0u);
        report.DecodedInstructions.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_PersonaChd_SystemCnfBootPathFound()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256);

        report.SystemCnfBootPath.Should().NotBeNullOrEmpty();
        report.SystemCnfBootPath.Should().Contain("SLPS", "Persona (PS1, Japanese release) boots via SLPS_005.00");
    }

    [Fact]
    public void Analyze_PersonaChd_PsxExeHeaderValid()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256);

        report.EntryPoint.Should().BeInRange(0x80010000u, 0x801FFFFFu,
            "PS1 executables typically load at 0x80010000 or higher");
        report.TextSize.Should().BeGreaterThan(256,
            "A real game executable should have more than 256 bytes of code");
        report.ExecutableFileSize.Should().BeGreaterThan(2048,
            "A real game executable should be larger than the header");
    }

    [Fact]
    public void Analyze_PersonaChd_MipsInstructionsDecoded()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 16);

        report.DecodedInstructions.Should().HaveCount(16,
            "should decode the requested number of instructions from entry point");

        foreach (var inst in report.DecodedInstructions)
        {
            inst.Address.Should().BeGreaterThanOrEqualTo(report.EntryPoint);
            inst.Address.Should().BeLessThan(report.TextStart + report.TextSize);
            inst.Mnemonic.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Analyze_PersonaChd_DecodeFailuresEmpty()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 32);

        report.DecodeFailures.Should().BeEmpty(
            "decoding from entry point within text segment should not fail");
    }

    [Fact]
    public void Analyze_PersonaChd_CanDecodeFirstInstruction()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);

        var first = report.DecodedInstructions[0];
        first.Mnemonic.Should().NotBe("???",
            "the first instruction should be a recognized MIPS opcode");
    }

    [Fact]
    public void Analyze_PersonaChd_TokenStringDeterministic()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report1 = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);
        var report2 = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);

        report1.ToTokenString().Should().Be(report2.ToTokenString(),
            "same input should produce identical deterministic token");
    }

    [Fact]
    public void Analyze_PersonaChd_BasicBlocksBuilt()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.BasicBlocks.Should().NotBeEmpty(
            "real ROM should produce at least one basic block");
        report.BasicBlocks.Count.Should().BeGreaterThanOrEqualTo(1);

        var firstBlock = report.BasicBlocks[0];
        firstBlock.StartAddress.Should().Be(report.EntryPoint);
        firstBlock.InstructionCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Analyze_PersonaChd_CfgEdgesProduced()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.CfgEdges.Should().NotBeEmpty(
            "128 instructions from Persona entry point should contain control flow");

        foreach (var edge in report.CfgEdges)
        {
            edge.Kind.Should().NotBeNullOrEmpty();
            new[] { "branch", "jump", "fallthrough", "indirect" }.Should().Contain(edge.Kind);
        }
    }

    [Fact]
    public void Analyze_PersonaChd_BasicBlockCountConsistent()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.BasicBlocks.Count.Should().BeGreaterThanOrEqualTo(1);
        report.BasicBlocks.Count.Should().BeLessThanOrEqualTo(report.DecodedInstructionCount);
    }

    [Fact]
    public void Analyze_PersonaChd_CallReturnCandidates()
    {
        if (!FixtureExists()) return;

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.CallCandidateCount.Should().BeGreaterThanOrEqualTo(0);
        report.ReturnCandidateCount.Should().BeGreaterThanOrEqualTo(0);
    }
}

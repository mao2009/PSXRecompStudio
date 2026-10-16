using System.Security.Cryptography;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Integration tests that run the full CHD ↁESYSTEM.CNF ↁEPS-X EXE ↁEMIPS decode pipeline
/// using the real PERSONA.chd disc image. Tests skip when the fixture is absent (CI).
/// </summary>
[Test]
public class DiscImageAnalyzerIntegrationTests
{
    private static readonly string ChdPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rom", "PERSONA.chd");

    private const string MissingFixtureSkipReason =
        "skipped: rom/PERSONA.chd not found (disc images are never committed)";

#pragma warning disable AARC003
    private static bool FixtureExists() => File.Exists(ChdPath);
#pragma warning restore AARC003

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (byte[] Bytes, string Sha256) LoadChd()
    {
#pragma warning disable AARC003
        var bytes = File.ReadAllBytes(ChdPath);
#pragma warning restore AARC003
        return (bytes, ComputeSha256(bytes));
    }

    [SkippableFact]
    public void Analyze_PersonaChd_ProducesValidReport()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

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

    [SkippableFact]
    public void Analyze_PersonaChd_SystemCnfBootPathFound()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256);

        report.SystemCnfBootPath.Should().NotBeNullOrEmpty();
        report.SystemCnfBootPath.Should().Contain("SLPS", "Persona (PS1, Japanese release) boots via SLPS_005.00");
    }

    [SkippableFact]
    public void Analyze_PersonaChd_PsxExeHeaderValid()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256);

        report.EntryPoint.Should().BeInRange(0x80010000u, 0x801FFFFFu,
            "PS1 executables typically load at 0x80010000 or higher");
        report.TextSize.Should().BeGreaterThan(256,
            "A real game executable should have more than 256 bytes of code");
        report.ExecutableFileSize.Should().BeGreaterThan(2048,
            "A real game executable should be larger than the header");
    }

    [SkippableFact]
    public void Analyze_PersonaChd_MipsInstructionsDecoded()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

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

    [SkippableFact]
    public void Analyze_PersonaChd_DecodeFailuresEmpty()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 32);

        report.DecodeFailures.Should().BeEmpty(
            "decoding from entry point within text segment should not fail");
    }

    [SkippableFact]
    public void Analyze_PersonaChd_CanDecodeFirstInstruction()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);

        var first = report.DecodedInstructions[0];
        first.Mnemonic.Should().NotBe("???",
            "the first instruction should be a recognized MIPS opcode");
    }

    [SkippableFact]
    public void Analyze_PersonaChd_TokenStringDeterministic()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report1 = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);
        var report2 = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 8);

        report1.ToTokenString().Should().Be(report2.ToTokenString(),
            "same input should produce identical deterministic token");
    }

    [SkippableFact]
    public void Analyze_PersonaChd_BasicBlocksBuilt()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.BasicBlocks.Should().NotBeEmpty(
            "real ROM should produce at least one basic block");
        report.BasicBlocks.Count.Should().BeGreaterThanOrEqualTo(1);

        var firstBlock = report.BasicBlocks[0];
        firstBlock.StartAddress.Should().Be(report.EntryPoint);
        firstBlock.InstructionCount.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public void Analyze_PersonaChd_CfgEdgesProduced()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

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

    [SkippableFact]
    public void Analyze_PersonaChd_BasicBlockCountConsistent()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.BasicBlocks.Count.Should().BeGreaterThanOrEqualTo(1);
        report.BasicBlocks.Count.Should().BeLessThanOrEqualTo(report.DecodedInstructionCount);
    }

    [SkippableFact]
    public void Analyze_PersonaChd_CallReturnCandidates()
    {
        Skip.IfNot(FixtureExists(), MissingFixtureSkipReason);

        var (chdBytes, sha256) = LoadChd();
        var report = DiscImageAnalyzer.Analyze(chdBytes, sha256, instructionCount: 128);

        report.CallCandidateCount.Should().BeGreaterThanOrEqualTo(0);
        report.ReturnCandidateCount.Should().BeGreaterThanOrEqualTo(0);
    }
}

using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

/// <summary>
/// Contract tests for <see cref="DiagnosticCode"/>: the registered codes are
/// stable literals (identity must never come from the clock or the file
/// system), the SCREAMING_SNAKE shape is enforced so typos and locale text
/// cannot enter the machine contract, and unknown-but-well-formed codes
/// remain first-class (fail-safe against newer contract versions).
/// </summary>
[Test]
public sealed class DiagnosticCodeTests
{
    [Fact]
    public void Registry_CodesAreStableLiterals()
    {
        DiagnosticCodes.DiscInputInvalid.Value.Should().Be("DISC_INPUT_INVALID");
        DiagnosticCodes.DiscChdOpenFailed.Value.Should().Be("DISC_CHD_OPEN_FAILED");
        DiagnosticCodes.DiscInvalidImage.Value.Should().Be("DISC_INVALID_IMAGE");
        DiagnosticCodes.DiscFilesystemFailed.Value.Should().Be("DISC_FILESYSTEM_FAILED");
        DiagnosticCodes.DiscSystemCnfInvalid.Value.Should().Be("DISC_SYSTEM_CNF_INVALID");
        DiagnosticCodes.DiscBootExecutableUnreadable.Value.Should().Be("DISC_BOOT_EXECUTABLE_UNREADABLE");
        DiagnosticCodes.DiscInvalidExecutable.Value.Should().Be("DISC_INVALID_EXECUTABLE");
        DiagnosticCodes.DiscAnalysisFailed.Value.Should().Be("DISC_ANALYSIS_FAILED");
        DiagnosticCodes.AnalyzerDecodeFailed.Value.Should().Be("ANALYZER_DECODE_FAILED");
        DiagnosticCodes.AnalyzerAnalysisFailed.Value.Should().Be("ANALYZER_ANALYSIS_FAILED");
        DiagnosticCodes.AnalyzerReportFailed.Value.Should().Be("ANALYZER_REPORT_FAILED");
        DiagnosticCodes.RecompUnsupportedInstruction.Value.Should().Be("RECOMP_UNSUPPORTED_INSTRUCTION");
        DiagnosticCodes.RecompUnsupportedOperation.Value.Should().Be("RECOMP_UNSUPPORTED_OPERATION");
        DiagnosticCodes.RecompUnsupportedMemory.Value.Should().Be("RECOMP_UNSUPPORTED_MEMORY");
        DiagnosticCodes.RecompStateMismatch.Value.Should().Be("RECOMP_STATE_MISMATCH");
        DiagnosticCodes.RuntimeUnsupportedMmio.Value.Should().Be("RUNTIME_UNSUPPORTED_MMIO");
        DiagnosticCodes.RuntimeExecutionFailed.Value.Should().Be("RUNTIME_EXECUTION_FAILED");
        DiagnosticCodes.BuildCompilerFailed.Value.Should().Be("BUILD_COMPILER_FAILED");
        DiagnosticCodes.InputConfigurationInvalid.Value.Should().Be("INPUT_CONFIGURATION_INVALID");
        DiagnosticCodes.InputFileMissing.Value.Should().Be("INPUT_FILE_MISSING");
    }

    [Theory]
    [InlineData("DISC_INVALID_IMAGE")]
    [InlineData("BIOS_HLE_UNSUPPORTED_CALL")]
    [InlineData("OUTER_BUDGET_EXHAUSTED")]
    [InlineData("RECOMP_S2")]
    public void TryCreate_AcceptsWellFormedScreamingSnake(string value)
    {
        DiagnosticCode.TryCreate(value, out var code).Should().BeTrue();
        code.Value.Should().Be(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lower_snake")]
    [InlineData("PascalCase")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("TRAILING_")]
    [InlineData("DOUBLE__UNDERSCORE")]
    [InlineData("WITH SPACE")]
    [InlineData("with.dots")]
    public void TryCreate_RejectsInvalidShapes(string? value)
    {
        DiagnosticCode.TryCreate(value, out var code).Should().BeFalse();
        code.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Create_ThrowsOnInvalidShape()
    {
        var act = () => DiagnosticCode.Create("not-a-code");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnknownButWellFormedCode_IsFirstClass()
    {
        DiagnosticCode.TryCreate("NEWER_SUBSYSTEM_CONDITION", out var code).Should().BeTrue();

        var diagnostic = new Diagnostic
        {
            Code = code,
            Category = DiagnosticCategory.Infrastructure,
        };

        diagnostic.Code.Value.Should().Be("NEWER_SUBSYSTEM_CONDITION");
        diagnostic.IsValid().Should().BeTrue();
    }

    [Fact]
    public void Diagnostic_IsValid_WithoutAnyMessage()
    {
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCodes.DiscInvalidImage,
            Category = DiagnosticCategory.Disc,
        };

        diagnostic.Message.Should().BeNull();
        diagnostic.MessageKey.Should().BeNull();
        diagnostic.IsValid().Should().BeTrue();
    }
}
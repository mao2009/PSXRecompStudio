using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionDiagnosticLogTests
{
    [Fact]
    public void FormatTail_EmitsNewestEntriesOnly_InOriginalDiagnosticOrder()
    {
        var diagnostics = new[]
        {
            Make("FIRST_FAILURE", guestPc: 0x80000010u),
            Make("SECOND_FAILURE", guestPc: 0x80000020u),
            Make("THIRD_FAILURE", guestPc: 0x80000030u),
        };

        var log = ExecutionDiagnosticLog.FormatTail(diagnostics, maxEntries: 2);

        log.Should().NotContain("FIRST_FAILURE");
        log.IndexOf("SECOND_FAILURE", StringComparison.Ordinal)
            .Should().BeLessThan(log.IndexOf("THIRD_FAILURE", StringComparison.Ordinal));
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
        log.Should().EndWith("\n");
    }

    [Fact]
    public void FormatTail_IsDeterministic_AndFormatsAllowedContextStably()
    {
        var diagnostic = Make(
            "RUNTIME_UNSUPPORTED_MMIO",
            guestPc: 0x80001234u,
            extraContext:
            [
                new(DiagnosticContextKeys.Count, UIntValue: 7),
                new(DiagnosticContextKeys.FailureKind, StringValue: "Unsupported\nMMIO"),
                new(DiagnosticContextKeys.InstructionOpcode, UIntValue: 0xDEADBEEFu),
                new(DiagnosticContextKeys.BiosCallKey, StringValue: "B0:3D"),
            ]);

        var first = ExecutionDiagnosticLog.FormatTail([diagnostic]);
        var second = ExecutionDiagnosticLog.FormatTail([diagnostic]);

        first.Should().Be(second);
        first.Should().Be(
            "severity=Error category=Runtime stage=Execute code=RUNTIME_UNSUPPORTED_MMIO " +
            "biosCall=\"B0:3D\" count=7 failureKind=\"Unsupported MMIO\" " +
            "guestPc=0x80001234 opcode=0xDEADBEEF\n");
    }

    [Fact]
    public void FormatTail_ExcludesFreeFormAndNonAllowlistedSensitiveFields()
    {
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCode.Create("CPU_EXCEPTION"),
            Category = DiagnosticCategory.Runtime,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Execute,
            Message = "message with C:\\Users\\alice\\secret.bin",
            MessageKey = "runtime.secret",
            Context =
            [
                new(DiagnosticContextKeys.GuestPc, UIntValue: 0x80000080u),
                new(DiagnosticContextKeys.FileIdentity, StringValue: "C:\\Users\\alice\\game.exe"),
                new(DiagnosticContextKeys.ExceptionType, StringValue: "SecretException"),
                new(DiagnosticContextKeys.ToolName, StringValue: "C:\\tool\\compiler.exe"),
            ],
            Evidence =
            [
                new(
                    DiagnosticEvidenceKind.SourceFile,
                    "C:\\Users\\alice\\generated.c",
                    "private evidence description"),
            ],
            Recovery = DiagnosticRecovery.ReportBug(),
        };

        var log = ExecutionDiagnosticLog.FormatTail([diagnostic]);

        log.Should().Contain("code=CPU_EXCEPTION");
        log.Should().Contain("guestPc=0x80000080");
        log.Should().NotContain("message with");
        log.Should().NotContain("runtime.secret");
        log.Should().NotContain("Users");
        log.Should().NotContain("game.exe");
        log.Should().NotContain("SecretException");
        log.Should().NotContain("compiler.exe");
        log.Should().NotContain("generated.c");
        log.Should().NotContain("private evidence");
    }

    [Fact]
    public void FormatTail_EmptySequence_IsEmpty()
    {
        ExecutionDiagnosticLog.FormatTail([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void FormatTail_RejectsTailLengthOutsideHardBound(int maxEntries)
    {
        var act = () => ExecutionDiagnosticLog.FormatTail([], maxEntries);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static Diagnostic Make(
        string code,
        uint? guestPc = null,
        IReadOnlyList<DiagnosticContextEntry>? extraContext = null)
    {
        var context = new List<DiagnosticContextEntry>();
        if (guestPc is uint pc)
        {
            context.Add(new(DiagnosticContextKeys.GuestPc, UIntValue: pc));
        }

        if (extraContext is not null)
        {
            context.AddRange(extraContext);
        }

        return new Diagnostic
        {
            Code = DiagnosticCode.Create(code),
            Category = DiagnosticCategory.Runtime,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Execute,
            Context = context,
            Recovery = DiagnosticRecovery.ReportBug(),
        };
    }
}

using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class NamespaceLayerMismatchTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task DomainAttributeInApplicationNamespace_ReportsPsxr003()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace PSXRecompStudio.ViewModels;

            [Domain]
            internal sealed class Misplaced
            {
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR003",
            6,
            23,
            "Domain",
            "PSXRecompStudio.ViewModels",
            "Application");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task AttributeMatchingNamespace_ProducesNoDiagnostic()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace PSXRecomp.Core;

            [Domain]
            internal sealed class Consistent
            {
            }
            """;

        await VerifyAsync(source);
    }

    [Fact]
    public async Task InfrastructureAttributeInInfrastructureNamespace_ProducesNoDiagnostic()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace PSXRecomp.Infrastructure;

            [Infrastructure]
            internal sealed class NativeAdapter
            {
            }
            """;

        await VerifyAsync(source);
    }

    private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new Test { TestState = { Sources = { source } } };
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    private sealed class Test : ArchitectureAnalyzerTest
    {
    }
}

using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class MultipleArchitectureAttributesTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task TwoLayerAttributes_ReportsPsxr002()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            [Application]
            internal sealed class Conflicted
            {
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR002",
            7,
            23,
            "Scenario.Conflicted",
            "Domain, Application");

        await VerifyAsync(source, expected);
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

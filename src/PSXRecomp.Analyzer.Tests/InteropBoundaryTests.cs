using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class InteropBoundaryTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task DllImportOutsideCore_ReportsPsxr006()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Infrastructure]
            public static class NativeCalls
            {
                [DllImport("psx")]
                internal static extern void DoWork();
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR006",
            10,
            33,
            "Scenario.NativeCalls.DoWork()");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task DllImportInsideCore_IsAllowed()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using PSXRecomp.Architecture;

            namespace PSXRecomp.Core;

            [Domain]
            internal static class InteropProbe
            {
                [DllImport("psx")]
                internal static extern void DoWork();
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

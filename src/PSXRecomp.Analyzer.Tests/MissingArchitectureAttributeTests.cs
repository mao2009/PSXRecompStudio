using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class MissingArchitectureAttributeTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task AttributedClass_ProducesNoDiagnostic()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            internal sealed class Tagged
            {
            }
            """;

        await VerifyAsync(source);
    }

    [Fact]
    public async Task UntaggedClass_ReportsPsxr001()
    {
        const string source = """
            namespace Scenario;

            internal sealed class Untagged
            {
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR001",
            3,
            23,
            "Scenario.Untagged",
            "Domain, Application, Infrastructure, Analyzer, Test, Generated");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task PartialClassWithAttributeOnOnePart_ProducesNoDiagnostic()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed partial class Split
            {
            }

            public sealed partial class Split
            {
            }
            """;

        await VerifyAsync(source);
    }

    [Fact]
    public async Task NestedClassInsideAttributedOuter_InheritsLayer()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Outer
            {
                public sealed class Inner
                {
                }
            }
            """;

        await VerifyAsync(source);
    }

    [Fact]
    public async Task UntaggedFamilyInUnmappedNamespace_ReportsPsxr001OnEveryClass()
    {
        const string source = """
            namespace Scenario;

            public class OuterUntagged
            {
                public class InnerUntagged
                {
                }
            }
            """;

        var expected = new[]
        {
            ExpectedDiagnostic(
                "PSXR001",
                3,
                14,
                "Scenario.OuterUntagged",
                "Domain, Application, Infrastructure, Analyzer, Test, Generated"),
            ExpectedDiagnostic(
                "PSXR001",
                5,
                18,
                "Scenario.OuterUntagged.InnerUntagged",
                "Domain, Application, Infrastructure, Analyzer, Test, Generated"),
        };

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task GeneratedFileWithoutAttribute_IsExempt()
    {
        const string generatedSource = """
            namespace Scenario;

            public class GeneratedThing
            {
            }
            """;

        var test = new Test();
        test.TestState.Sources.Add(("Generated.g.cs", generatedSource));

        await test.RunAsync();
    }

    [Fact]
    public async Task MarkerNamespaceTypes_AreExempt()
    {
        const string source = """
            namespace PSXRecomp.Architecture;

            public class NotAMarker
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

using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class DependencyDirectionTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task DomainReferencingApplication_ReportsPsxr004()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Application]
            public sealed class AppService
            {
            }

            [Domain]
            public sealed class DomainService
            {
                internal readonly AppService Dependency = new();
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR004",
            13,
            23,
            "Scenario.DomainService",
            "Domain",
            "Scenario.AppService",
            "Application",
            "the Domain layer must not depend on the outer Application layer");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task ProductionReferencingTest_ReportsPsxr004()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Test]
            public sealed class TestHelper
            {
            }

            [Domain]
            public sealed class ProductionType
            {
                public TestHelper Helper { get; set; } = null!;
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR004",
            13,
            12,
            "Scenario.ProductionType",
            "Domain",
            "Scenario.TestHelper",
            "Test",
            "production code must not depend on test code");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task ApplicationReferencingDomain_IsAllowed()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class DomainEntity
            {
            }

            [Application]
            public sealed class AppFacade
            {
                public DomainEntity Create() => new DomainEntity();
            }
            """;

        await VerifyAsync(source);
    }

    [Fact]
    public async Task TestReferencingDomain_IsAllowed()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class DomainEntity
            {
            }

            [Test]
            public sealed class DomainEntityTests
            {
                public DomainEntity Create() => new DomainEntity();
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

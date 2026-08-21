using Microsoft.CodeAnalysis.Testing;
using PSXRecomp.Analyzer.Tests.TestInfrastructure;

namespace PSXRecomp.Analyzer.Tests;

public class ForbiddenApiTests : ArchitectureAnalyzerTest
{
    [Fact]
    public async Task ConsoleWriteLineInDomain_ReportsPsxr005()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Greeter
            {
                public void Greet() => Console.WriteLine("hello");
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            28,
            "Console.WriteLine",
            "Domain",
            "standard output must be abstracted behind an Infrastructure adapter");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task DateTimeNowInDomain_ReportsPsxr005()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Clock
            {
                public long Stamp() => DateTime.Now.Ticks;
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            28,
            "DateTime.Now",
            "Domain",
            "non-deterministic time sources break determinism");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task RandomUsageChainInDomain_ReportsPsxr005()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Picker
            {
                public int Pick() => Random.Shared.Next();
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            26,
            "Random.Next",
            "Domain",
            "non-deterministic randomness is forbidden");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task ExistingRandomInstanceInDomain_ReportsPsxr005()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Roller
            {
                private readonly Random _random = new Random(42);

                public int Roll() => _random.Next();
            }
            """;

        var expected = new[]
        {
            ExpectedDiagnostic(
                "PSXR005",
                9,
                39,
                "new Random()",
                "Domain",
                "non-deterministic randomness is forbidden"),
            ExpectedDiagnostic(
                "PSXR005",
                11,
                26,
                "Random.Next",
                "Domain",
                "non-deterministic randomness is forbidden"),
        };

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task GuidNewGuidInDomain_ReportsPsxr005()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class IdFactory
            {
                public Guid NewId() => Guid.NewGuid();
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            28,
            "Guid.NewGuid",
            "Domain",
            "non-deterministic randomness is forbidden");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task FileExistsInApplication_ReportsPsxr005()
    {
        const string source = """
            using System.IO;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Application]
            public sealed class StorageProbe
            {
                public bool Exists(string path) => File.Exists(path);
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            40,
            "File.Exists",
            "Application",
            "external I/O is an Infrastructure responsibility");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task HttpClientCreationInDomain_ReportsPsxr005()
    {
        const string source = """
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class NetworkProbe
            {
                internal readonly System.Net.Http.HttpClient Client =
                    new System.Net.Http.HttpClient();
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            9,
            "new HttpClient()",
            "Domain",
            "network access is an Infrastructure responsibility");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task TaskDelayInTest_ReportsPsxr005()
    {
        const string source = """
            using System.Threading.Tasks;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Test]
            public sealed class AsyncProbe
            {
                public void Wait() => Task.Delay(1);
            }
            """;

        var expected = ExpectedDiagnostic(
            "PSXR005",
            9,
            27,
            "Task.Delay",
            "Test",
            "asynchronous timing must use controlled schedulers");

        await VerifyAsync(source, expected);
    }

    [Fact]
    public async Task DeterministicApisInDomain_AreAllowed()
    {
        const string source = """
            using System;
            using PSXRecomp.Architecture;

            namespace Scenario;

            [Domain]
            public sealed class Calculator
            {
                public int Max(int left, int right) => Math.Max(left, right);

                public long Elapsed(long before, long after) => after - before;
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

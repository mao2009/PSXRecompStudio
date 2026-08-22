using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace PSXRecomp.Analyzer.Tests.TestInfrastructure;

public abstract class ArchitectureAnalyzerTest : CSharpAnalyzerTest<PSXRecompArchitectureAnalyzer, XUnit29Verifier>
{
    protected ArchitectureAnalyzerTest()
    {
        ReferenceAssemblies = ReferenceAssemblies.Net.Net90;
        TestState.Sources.Add((AttributesFileName, File.ReadAllText(AttributesSourcePath)));
    }

    protected static DiagnosticResult ExpectedDiagnostic(string id, int line, int column, params object[] arguments)
    {
        // The attributes source occupies index 0, so unnamed scenario sources resolve to /0/Test1.cs.
        return new DiagnosticResult(id, DiagnosticSeverity.Error)
            .WithLocation(ScenarioFilePath, line, column)
            .WithArguments(arguments);
    }

    protected const string AttributesFileName = "PSXRecompArchitectureAttributes.cs";

    protected const string ScenarioFilePath = "/0/Test1.cs";

    private static string AttributesSourcePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "PSXRecomp.Analyzer", "Architecture", "PSXRecompArchitectureAttributes.cs");
}

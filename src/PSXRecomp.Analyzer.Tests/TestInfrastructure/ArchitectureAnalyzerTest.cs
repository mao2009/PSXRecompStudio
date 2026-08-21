using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;

namespace PSXRecomp.Analyzer.Tests.TestInfrastructure;

public abstract class ArchitectureAnalyzerTest : CSharpAnalyzerTest<PSXRecompArchitectureAnalyzer, XUnitVerifier>
{
    protected ArchitectureAnalyzerTest()
    {
        ReferenceAssemblies = ReferenceAssemblies.Net.Net90;
        TestState.Sources.Add((AttributesFileName, AttributesSourceText));
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
    protected const string AttributesSourceText = """
        using System;

        namespace PSXRecomp.Architecture
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class DomainAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class ApplicationAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class InfrastructureAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class AnalyzerAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class TestAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, AllowMultiple = false, Inherited = false)]
            internal sealed class GeneratedAttribute : Attribute
            {
            }
        }
        """;
}

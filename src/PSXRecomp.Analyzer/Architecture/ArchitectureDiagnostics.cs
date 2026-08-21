using Microsoft.CodeAnalysis;

namespace PSXRecomp.Analyzer.Architecture;

internal static class ArchitectureDiagnostics
{
    private const string Category = "Architecture";
    private const string HelpLinkUri = "https://github.com/mao2009/PSXRecompStudio/blob/main/docs/architecture-matrix.md";

    public static readonly DiagnosticDescriptor MissingArchitectureAttribute = new(
        id: "PSXR001",
        title: "Missing architecture attribute",
        messageFormat: "Type '{0}' must declare exactly one architecture attribute ({1})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every class must be tagged with exactly one architecture attribute so its layer can be verified mechanically against the architecture matrix.",
        helpLinkUri: HelpLinkUri);

    public static readonly DiagnosticDescriptor MultipleArchitectureAttributes = new(
        id: "PSXR002",
        title: "Multiple architecture attributes",
        messageFormat: "Type '{0}' declares multiple architecture attributes ({1}); exactly one is required",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type belongs to exactly one architecture layer; declaring several layer attributes is contradictory.",
        helpLinkUri: HelpLinkUri);

    public static readonly DiagnosticDescriptor NamespaceLayerMismatch = new(
        id: "PSXR003",
        title: "Architecture attribute contradicts namespace layer",
        messageFormat: "Architecture attribute '{0}' contradicts namespace '{1}', which belongs to layer '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The namespace matrix in docs/architecture-matrix.md maps namespaces to layers; an attribute that disagrees with that mapping indicates a misplaced type.",
        helpLinkUri: HelpLinkUri);

    public static readonly DiagnosticDescriptor ForbiddenDependency = new(
        id: "PSXR004",
        title: "Forbidden architecture dependency direction",
        messageFormat: "'{0}' ({1}) must not depend on '{2}' ({3}): {4}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The dependency matrix in docs/architecture-matrix.md defines which layer may reference which; this reference violates it.",
        helpLinkUri: HelpLinkUri);

    public static readonly DiagnosticDescriptor ForbiddenApi = new(
        id: "PSXR005",
        title: "Forbidden API usage in architecture layer",
        messageFormat: "'{0}' is forbidden in the {1} layer: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The forbidden API matrix in docs/architecture-matrix.md restricts non-deterministic and I/O APIs per layer.",
        helpLinkUri: HelpLinkUri);

    public static readonly DiagnosticDescriptor InteropBoundaryViolation = new(
        id: "PSXR006",
        title: "P/Invoke outside the interop boundary",
        messageFormat: "P/Invoke declaration '{0}' must be declared inside the 'PSXRecomp.Core' interop boundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All P/Invoke declarations ([DllImport] / [LibraryImport]) must live in PSXRecomp.Core so the C ABI boundary stays in one place.",
        helpLinkUri: HelpLinkUri);
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using PSXRecomp.Analyzer.Architecture;

namespace PSXRecomp.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PSXRecompArchitectureAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ArchitectureDiagnostics.MissingArchitectureAttribute,
        ArchitectureDiagnostics.MultipleArchitectureAttributes,
        ArchitectureDiagnostics.NamespaceLayerMismatch,
        ArchitectureDiagnostics.ForbiddenDependency,
        ArchitectureDiagnostics.ForbiddenApi,
        ArchitectureDiagnostics.InteropBoundaryViolation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterCompilationStartAction(startContext =>
        {
            var reported = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeDependencyDirection(nodeContext, reported),
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
        });
        context.RegisterOperationBlockAction(AnalyzeForbiddenApiUsage);
        context.RegisterSyntaxNodeAction(AnalyzePInvokeDeclaration, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsImplicitlyDeclared)
        {
            return;
        }

        if (ArchitectureFacts.IsMarkerNamespace(type.ContainingNamespace))
        {
            return;
        }

        var location = GetPrimaryDeclarationLocation(type, context.CancellationToken);
        if (location is null)
        {
            return;
        }

        var applied = ArchitectureFacts.GetAppliedAttributes(type);
        var typeName = type.ToDisplayString();

        if (applied.Count == 0)
        {
            if (type.ContainingType is not null
                && ArchitectureFacts.ResolveLayer(type.ContainingType) != ArchitectureLayer.Unknown)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.MissingArchitectureAttribute,
                location,
                typeName,
                ArchitectureFacts.AllAttributeNames));
            return;
        }

        var distinctLayers = applied.Select(static entry => entry.Layer).Distinct().ToList();
        if (distinctLayers.Count > 1)
        {
            var names = string.Join(", ", distinctLayers.Select(static layer => layer.ToString()));
            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.MultipleArchitectureAttributes,
                location,
                typeName,
                names));
            return;
        }

        var declaredLayer = distinctLayers[0];
        var namespaceName = ArchitectureFacts.GetNamespaceName(type.ContainingNamespace);
        var impliedLayer = ArchitectureFacts.FromNamespace(namespaceName);
        if (impliedLayer != ArchitectureLayer.Unknown && impliedLayer != declaredLayer)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.NamespaceLayerMismatch,
                location,
                declaredLayer.ToString(),
                namespaceName,
                impliedLayer.ToString()));
        }
    }

    private static void AnalyzeDependencyDirection(SyntaxNodeAnalysisContext context, ConcurrentDictionary<string, byte> reported)
    {
        if (ArchitectureFacts.IsGeneratedPath(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        var name = (SimpleNameSyntax)context.Node;
        if (name.FirstAncestorOrSelf<AttributeSyntax>() is not null)
        {
            return;
        }

        var semanticModel = context.SemanticModel;
        var cancellationToken = context.CancellationToken;

        var targetType = ResolveReferencedType(name, semanticModel, cancellationToken);
        if (targetType is null || ArchitectureFacts.IsMarkerNamespace(targetType.ContainingNamespace))
        {
            return;
        }

        var targetLayer = ArchitectureFacts.ResolveLayer(targetType);
        if (targetLayer == ArchitectureLayer.Unknown)
        {
            return;
        }

        var (sourceType, sourceLayer) = ResolveEnclosingType(name, semanticModel, cancellationToken);
        if (sourceType is null
            || sourceLayer == ArchitectureLayer.Unknown
            || sourceLayer == targetLayer)
        {
            return;
        }

        if (!ArchitectureFacts.IsForbiddenDependency(sourceLayer, targetLayer, out var reason))
        {
            return;
        }

        var key = sourceType.ToDisplayString() + "->" + targetType.ToDisplayString();
        if (!reported.TryAdd(key, 0))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            ArchitectureDiagnostics.ForbiddenDependency,
            name.GetLocation(),
            sourceType.ToDisplayString(),
            sourceLayer.ToString(),
            targetType.ToDisplayString(),
            targetLayer.ToString(),
            reason));
    }

    private static void AnalyzeForbiddenApiUsage(OperationBlockAnalysisContext context)
    {
        var sourceType = context.OwningSymbol switch
        {
            IMethodSymbol method => method.ContainingType,
            IFieldSymbol field => field.ContainingType,
            IPropertySymbol property => property.ContainingType,
            IEventSymbol @event => @event.ContainingType,
            _ => null,
        };

        if (sourceType is null)
        {
            return;
        }

        if (ArchitectureFacts.IsMarkerNamespace(sourceType.ContainingNamespace))
        {
            return;
        }

        var sourceLayer = ArchitectureFacts.ResolveLayer(sourceType);
        if (sourceLayer == ArchitectureLayer.Unknown)
        {
            return;
        }

        var rules = ForbiddenApiCatalog.GetRules(sourceLayer);
        if (rules.IsEmpty)
        {
            return;
        }

        foreach (var block in context.OperationBlocks)
        {
            foreach (var operation in EnumerateOperations(block))
            {
                ISymbol? member;
                Location location;
                switch (operation)
                {
                    case IInvocationOperation invocation:
                        member = invocation.TargetMethod;
                        location = invocation.Syntax.GetLocation();
                        break;
                    case IObjectCreationOperation creation:
                        member = creation.Constructor;
                        location = creation.Syntax.GetLocation();
                        break;
                    case IMemberReferenceOperation reference:
                        member = reference.Member;
                        location = reference.Syntax.GetLocation();
                        break;
                    default:
                        continue;
                }

                if (member?.ContainingType is not { } owner)
                {
                    continue;
                }

                var matchName = GetMatchName(member, out var isConstructor);
                var ownerFullName = owner.OriginalDefinition.ToDisplayString();

                foreach (var rule in rules)
                {
                    if (!rule.Matches(ownerFullName, matchName, isConstructor))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(
                        ArchitectureDiagnostics.ForbiddenApi,
                        location,
                        FormatApi(owner, member),
                        sourceLayer.ToString(),
                        rule.Reason));
                }
            }
        }
    }

    private static IEnumerable<IOperation> EnumerateOperations(IOperation root)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is null || current.Kind == OperationKind.None)
            {
                continue;
            }

            yield return current;
            foreach (var child in current.ChildOperations)
            {
                stack.Push(child);
            }
        }
    }

    private static void AnalyzePInvokeDeclaration(SyntaxNodeAnalysisContext context)
    {
        if (ArchitectureFacts.IsGeneratedPath(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        if (context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        var isInteropDeclaration = method.GetAttributes().Any(static attribute =>
        {
            var fullName = attribute.AttributeClass?.OriginalDefinition.ToDisplayString();
            return fullName is "System.Runtime.InteropServices.DllImportAttribute"
                or "System.Runtime.InteropServices.LibraryImportAttribute";
        });

        if (!isInteropDeclaration)
        {
            return;
        }

        var namespaceName = ArchitectureFacts.GetNamespaceName(method.ContainingNamespace);
        if (ArchitectureFacts.IsWithin(namespaceName, ArchitectureFacts.InteropNamespaceRoot))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            ArchitectureDiagnostics.InteropBoundaryViolation,
            method.Locations.FirstOrDefault() ?? context.Node.GetLocation(),
            method.ToDisplayString()));
    }

    private static Location? GetPrimaryDeclarationLocation(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
                && !ArchitectureFacts.IsGeneratedPath(declaration.SyntaxTree.FilePath))
            {
                return declaration.Identifier.GetLocation();
            }
        }

        return null;
    }

    private static INamedTypeSymbol? ResolveReferencedType(SyntaxNode name, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol
            ?? semanticModel.GetDeclaredSymbol(name, cancellationToken);

        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.OriginalDefinition,
            IMethodSymbol method => method.ContainingType?.OriginalDefinition,
            IPropertySymbol property => property.ContainingType?.OriginalDefinition,
            IFieldSymbol field => field.ContainingType?.OriginalDefinition,
            IEventSymbol @event => @event.ContainingType?.OriginalDefinition,
            _ => null,
        };
    }

    private static (INamedTypeSymbol? Type, ArchitectureLayer Layer) ResolveEnclosingType(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax typeDeclaration)
            {
                if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is { } type)
                {
                    return (type, ArchitectureFacts.ResolveLayer(type));
                }

                return (null, ArchitectureLayer.Unknown);
            }
        }

        return (null, ArchitectureLayer.Unknown);
    }

    private static string GetMatchName(ISymbol member, out bool isConstructor)
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }:
                isConstructor = true;
                return member.Name;
            case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor:
                isConstructor = false;
                return accessor.AssociatedSymbol?.Name ?? accessor.Name;
            default:
                isConstructor = false;
                return member.Name;
        }
    }

    private static string FormatApi(INamedTypeSymbol owner, ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }:
                return $"new {owner.Name}()";
            case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor:
                return $"{owner.Name}.{accessor.AssociatedSymbol?.Name ?? accessor.Name}";
            default:
                return $"{owner.Name}.{member.Name}";
        }
    }
}

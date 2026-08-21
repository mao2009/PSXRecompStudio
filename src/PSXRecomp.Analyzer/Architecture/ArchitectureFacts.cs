using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace PSXRecomp.Analyzer.Architecture;

internal enum ArchitectureLayer
{
    Unknown = 0,
    Domain,
    Application,
    Infrastructure,
    Analyzer,
    Test,
    Generated,
}

internal static class ArchitectureFacts
{
    public const string MarkerNamespace = "PSXRecomp.Architecture";

    public const string InteropNamespaceRoot = "PSXRecomp.Core";

    public const string AllAttributeNames = "Domain, Application, Infrastructure, Analyzer, Test, Generated";

    private static readonly Dictionary<string, ArchitectureLayer> AttributeFullNameToLayer = new(StringComparer.Ordinal)
    {
        ["PSXRecomp.Architecture.DomainAttribute"] = ArchitectureLayer.Domain,
        ["PSXRecomp.Architecture.ApplicationAttribute"] = ArchitectureLayer.Application,
        ["PSXRecomp.Architecture.InfrastructureAttribute"] = ArchitectureLayer.Infrastructure,
        ["PSXRecomp.Architecture.AnalyzerAttribute"] = ArchitectureLayer.Analyzer,
        ["PSXRecomp.Architecture.TestAttribute"] = ArchitectureLayer.Test,
        ["PSXRecomp.Architecture.GeneratedAttribute"] = ArchitectureLayer.Generated,
    };

    public static IReadOnlyList<(AttributeData Attribute, ArchitectureLayer Layer)> GetAppliedAttributes(INamedTypeSymbol type)
    {
        var results = new List<(AttributeData, ArchitectureLayer)>();
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass
                && AttributeFullNameToLayer.TryGetValue(attributeClass.OriginalDefinition.ToDisplayString(), out var layer))
            {
                results.Add((attribute, layer));
            }
        }

        return results;
    }

    public static ArchitectureLayer ResolveLayer(INamedTypeSymbol type)
    {
        for (var current = type.OriginalDefinition; current is not null; current = current.ContainingType)
        {
            var applied = GetAppliedAttributes(current);
            if (applied.Count > 0)
            {
                return applied[0].Layer;
            }
        }

        return FromNamespace(GetNamespaceName(type.ContainingNamespace));
    }

    public static string GetNamespaceName(INamespaceSymbol? @namespace)
    {
        return @namespace is null || @namespace.IsGlobalNamespace ? string.Empty : @namespace.ToDisplayString();
    }

    public static ArchitectureLayer FromNamespace(string namespaceName)
    {
        if (IsWithin(namespaceName, InteropNamespaceRoot))
        {
            return ArchitectureLayer.Domain;
        }

        if (IsWithin(namespaceName, "PSXRecompStudio"))
        {
            return ArchitectureLayer.Application;
        }

        if (IsWithin(namespaceName, "PSXRecomp.Infrastructure"))
        {
            return ArchitectureLayer.Infrastructure;
        }

        if (IsWithin(namespaceName, "PSXRecomp.Tests"))
        {
            return ArchitectureLayer.Test;
        }

        if (IsWithin(namespaceName, "PSXRecomp.Analyzer"))
        {
            return ArchitectureLayer.Analyzer;
        }

        if (IsWithin(namespaceName, "PSXRecomp.Generated"))
        {
            return ArchitectureLayer.Generated;
        }

        return ArchitectureLayer.Unknown;
    }

    public static bool IsMarkerNamespace(INamespaceSymbol? @namespace)
    {
        var name = GetNamespaceName(@namespace);
        return IsWithin(name, MarkerNamespace);
    }

    public static bool IsGeneratedPath(string? path)
    {
        if (path is null || path.Length == 0)
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalized);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("TemporaryGeneratedFile", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.IndexOf("/obj/", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("/bin/", StringComparison.Ordinal) >= 0;
    }

    public static bool IsForbiddenDependency(ArchitectureLayer source, ArchitectureLayer target, out string reason)
    {
        switch (source)
        {
            case ArchitectureLayer.Domain when target == ArchitectureLayer.Application:
                reason = "the Domain layer must not depend on the outer Application layer";
                return true;
            case ArchitectureLayer.Application when target == ArchitectureLayer.Infrastructure:
                reason = "the Application layer must reach Infrastructure only through the Domain interop boundary";
                return true;
            case ArchitectureLayer.Infrastructure when target == ArchitectureLayer.Application:
                reason = "the Infrastructure layer must not depend on the Application layer";
                return true;
            case ArchitectureLayer.Domain or ArchitectureLayer.Application or ArchitectureLayer.Infrastructure
                when target == ArchitectureLayer.Test:
                reason = "production code must not depend on test code";
                return true;
            default:
                reason = string.Empty;
                return false;
        }
    }

    public static bool IsWithin(string namespaceName, string root)
    {
        return namespaceName == root || namespaceName.StartsWith(root + ".", StringComparison.Ordinal);
    }
}

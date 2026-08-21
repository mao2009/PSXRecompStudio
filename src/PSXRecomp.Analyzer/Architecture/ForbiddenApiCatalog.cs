using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace PSXRecomp.Analyzer.Architecture;

internal sealed class ForbiddenApiRule
{
    public ForbiddenApiRule(string typeFullName, string? memberName, bool wholeType, string reason)
        : this(typeFullName, memberName, wholeType, false, reason)
    {
    }

    internal ForbiddenApiRule(string typeFullName, string? memberName, bool wholeType, bool constructorsOnly, string reason)
    {
        TypeFullName = typeFullName;
        MemberName = memberName;
        WholeType = wholeType;
        ConstructorsOnly = constructorsOnly;
        Reason = reason;
    }

    public string TypeFullName { get; }

    public string? MemberName { get; }

    public bool WholeType { get; }

    public bool ConstructorsOnly { get; }

    public string Reason { get; }

    public bool Matches(string typeFullName, string memberName, bool isConstructor)
    {
        if (!string.Equals(TypeFullName, typeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        if (isConstructor)
        {
            return WholeType || ConstructorsOnly;
        }

        if (ConstructorsOnly)
        {
            return false;
        }

        return WholeType
            || MemberName is null
            || string.Equals(MemberName, memberName, StringComparison.Ordinal);
    }
}

internal static class ForbiddenApiCatalog
{
    private static readonly ImmutableDictionary<ArchitectureLayer, ImmutableArray<ForbiddenApiRule>> RulesByLayer = CreateRules();

    public static ImmutableArray<ForbiddenApiRule> GetRules(ArchitectureLayer layer)
    {
        return RulesByLayer.TryGetValue(layer, out var rules) ? rules : ImmutableArray<ForbiddenApiRule>.Empty;
    }

    private static ImmutableDictionary<ArchitectureLayer, ImmutableArray<ForbiddenApiRule>> CreateRules()
    {
        const string ConsoleReason = "standard output must be abstracted behind an Infrastructure adapter";
        const string IoReason = "external I/O is an Infrastructure responsibility";
        const string EnvironmentReason = "execution environment dependencies break determinism";
        const string ProcessReason = "process control is an Infrastructure responsibility";
        const string TimeReason = "non-deterministic time sources break determinism";
        const string RandomnessReason = "non-deterministic randomness is forbidden";
        const string NetworkReason = "network access is an Infrastructure responsibility";

        var domain = ImmutableArray.Create(
            AnyMember("System.Console", ConsoleReason),
            AnyMember("System.IO.File", IoReason),
            AnyMember("System.IO.Directory", IoReason),
            AnyMember("System.Environment", EnvironmentReason),
            AnyMember("System.Diagnostics.Process", ProcessReason),
            AnyMember("System.DateTimeOffset", TimeReason),
            NamedMember("System.DateTime", "Now", TimeReason),
            NamedMember("System.DateTime", "UtcNow", TimeReason),
            NamedMember("System.Guid", "NewGuid", RandomnessReason),
            NamedMember("System.Random", "Shared", RandomnessReason),
            Constructor("System.Random", RandomnessReason),
            WholeType("System.Net.Http.HttpClient", NetworkReason),
            WholeType("System.Net.Sockets.Socket", NetworkReason));

        var application = ImmutableArray.Create(
            AnyMember("System.Console", ConsoleReason),
            AnyMember("System.IO.File", IoReason),
            AnyMember("System.IO.Directory", IoReason));

        const string AdapterReason = "external I/O must be abstracted behind an adapter interface";

        var infrastructure = ImmutableArray.Create(
            AnyMember("System.Console", "standard output must be abstracted behind an adapter interface"),
            AnyMember("System.IO.File", AdapterReason),
            AnyMember("System.IO.Directory", AdapterReason));

        var special = ImmutableArray.Create(
            AnyMember("System.Console", ConsoleReason),
            AnyMember("System.IO.File", IoReason),
            AnyMember("System.IO.Directory", IoReason),
            AnyMember("System.Environment", EnvironmentReason),
            AnyMember("System.Diagnostics.Process", ProcessReason),
            AnyMember("System.Threading.Thread", "manual thread management breaks deterministic execution"),
            NamedMember("System.DateTime", "Now", TimeReason),
            NamedMember("System.DateTime", "UtcNow", TimeReason),
            NamedMember("System.Guid", "NewGuid", RandomnessReason),
            WholeType("System.Random", RandomnessReason),
            NamedMember("System.Threading.Tasks.Task", "Delay", "asynchronous timing must use controlled schedulers"));

        return ImmutableDictionary.CreateRange(new System.Collections.Generic.Dictionary<ArchitectureLayer, ImmutableArray<ForbiddenApiRule>>
        {
            [ArchitectureLayer.Domain] = domain,
            [ArchitectureLayer.Application] = application,
            [ArchitectureLayer.Infrastructure] = infrastructure,
            [ArchitectureLayer.Test] = special,
            [ArchitectureLayer.Analyzer] = special,
            [ArchitectureLayer.Generated] = special,
        });
    }

    private static ForbiddenApiRule AnyMember(string typeFullName, string reason)
    {
        return new ForbiddenApiRule(typeFullName, null, false, reason);
    }

    private static ForbiddenApiRule NamedMember(string typeFullName, string memberName, string reason)
    {
        return new ForbiddenApiRule(typeFullName, memberName, false, reason);
    }

    private static ForbiddenApiRule WholeType(string typeFullName, string reason)
    {
        return new ForbiddenApiRule(typeFullName, null, true, reason);
    }

    private static ForbiddenApiRule Constructor(string typeFullName, string reason)
    {
        return new ForbiddenApiRule(typeFullName, null, false, true, reason);
    }
}

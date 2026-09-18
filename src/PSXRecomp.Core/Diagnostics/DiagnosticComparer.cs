using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Deterministic ordering for a set of <see cref="Diagnostic"/> instances, so
/// any code that emits multiple diagnostics produces a stable order regardless
/// of collection ordering or dictionary behavior. The order is by category,
/// then stage, then code, then the number of context entries, with canonical
/// JSON as the final locale-independent tie-breaker so distinct serialized
/// diagnostics never depend on their source iteration order.
/// </summary>
[Domain]
public sealed class DiagnosticComparer : IComparer<Diagnostic>
{
    /// <summary>Shared instance; the comparer is stateless.</summary>
    public static readonly DiagnosticComparer Instance = new();

    /// <summary>Orders nulls last, then by the stable key of each diagnostic.</summary>
    public int Compare(Diagnostic? x, Diagnostic? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var result = ((int)x.Category).CompareTo((int)y.Category);
        if (result != 0)
        {
            return result;
        }

        result = ((int)x.Stage).CompareTo((int)y.Stage);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x.Code.Value, y.Code.Value);
        if (result != 0)
        {
            return result;
        }

        result = x.Context.Count.CompareTo(y.Context.Count);
        if (result != 0)
        {
            return result;
        }

        // The primary keys intentionally keep common diagnostics grouped. For
        // distinct values that share them, compare the canonical serialized
        // representation so ordering cannot fall back to source iteration.
        return string.CompareOrdinal(DiagnosticJson.Serialize(x), DiagnosticJson.Serialize(y));
    }
}
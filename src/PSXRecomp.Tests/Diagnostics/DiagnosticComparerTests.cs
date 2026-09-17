using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

/// <summary>
/// Contract tests for <see cref="DiagnosticComparer"/>: ordering is a pure
/// function of category, stage, code, and context-entry count, so a set of
/// diagnostics always sorts the same way regardless of insertion order.
/// </summary>
[Test]
public sealed class DiagnosticComparerTests
{
    private static Diagnostic Make(
        DiagnosticCategory category, DiagnosticStage stage, string code, int contextEntries = 0)
    {
        return new Diagnostic
        {
            Code = DiagnosticCode.Create(code),
            Category = category,
            Stage = stage,
            Context = Enumerable.Range(0, contextEntries)
                .Select(i => new DiagnosticContextEntry($"key{i}", UIntValue: (uint)i))
                .ToArray(),
        };
    }

    [Fact]
    public void Sort_OrdersByCategoryThenStageThenCodeThenContextCount()
    {
        var a = Make(DiagnosticCategory.Disc, DiagnosticStage.Input, "DISC_A");
        var b = Make(DiagnosticCategory.Disc, DiagnosticStage.Analysis, "DISC_B");
        var c = Make(DiagnosticCategory.Recompiler, DiagnosticStage.Recompile, "RECOMP_A");
        var d = Make(DiagnosticCategory.Disc, DiagnosticStage.Input, "DISC_C");
        var e = Make(DiagnosticCategory.Disc, DiagnosticStage.Input, "DISC_A", contextEntries: 2);

        var shuffled = new[] { c, e, a, d, b };
        Array.Sort(shuffled, DiagnosticComparer.Instance);

        shuffled.Should().Equal(a, e, d, b, c);
    }

    [Fact]
    public void Sort_IsStableAcrossRepeatedRuns()
    {
        var diagnostics = new[]
        {
            Make(DiagnosticCategory.Runtime, DiagnosticStage.Execute, "RUNTIME_B"),
            Make(DiagnosticCategory.Analysis, DiagnosticStage.Analysis, "ANALYZER_A"),
            Make(DiagnosticCategory.Runtime, DiagnosticStage.Execute, "RUNTIME_A"),
        };

        var firstOrder = diagnostics.OrderBy(d => d, DiagnosticComparer.Instance).Select(d => d.Code.Value).ToArray();
        var secondOrder = diagnostics.OrderBy(d => d, DiagnosticComparer.Instance).Select(d => d.Code.Value).ToArray();

        firstOrder.Should().Equal(secondOrder);
        firstOrder.Should().Equal("ANALYZER_A", "RUNTIME_A", "RUNTIME_B");
    }

    [Fact]
    public void Compare_OrdersNullsLast()
    {
        var diagnostic = Make(DiagnosticCategory.Disc, DiagnosticStage.Input, "DISC_A");

        DiagnosticComparer.Instance.Compare(diagnostic, null).Should().BeNegative();
        DiagnosticComparer.Instance.Compare(null, diagnostic).Should().BePositive();
        DiagnosticComparer.Instance.Compare(null, null).Should().Be(0);
    }
}

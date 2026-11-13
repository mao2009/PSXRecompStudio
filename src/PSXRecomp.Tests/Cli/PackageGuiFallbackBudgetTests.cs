using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using Xunit;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// #820 W09 review: <c>package --gui</c> is a window that runs until it is closed, so the default fallback transition cap must not end
/// it on its own. Headless packages keep the finite defaults, and explicit <c>--fallback-*</c> values are always kept as given.
/// </summary>
[Test]
public sealed class PackageGuiFallbackBudgetTests
{
    [Fact]
    public void Gui_WithoutExplicitBudgets_IsNotEndedByTheDefaultTransitionCap()
    {
        var options = PackageCommand.ResolveMixedFallback(gui: true, segmentBudget: null, maxTransitions: null);

        options.MaxTransitions.Should().Be(uint.MaxValue, "a window must not stop at the 100,000-handoff default");
        options.SegmentInstructionBudget.Should().Be(uint.MaxValue, "a window's fallback segment must not stop on the headless default");
    }

    [Fact]
    public void Headless_WithoutExplicitBudgets_KeepsTheFiniteDefaults()
    {
        var options = PackageCommand.ResolveMixedFallback(gui: false, segmentBudget: null, maxTransitions: null);

        options.MaxTransitions.Should().Be(MixedFallbackOptions.DefaultMaxTransitions);
        options.SegmentInstructionBudget.Should().Be(MixedFallbackOptions.DefaultSegmentInstructionBudget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitBudgets_AreKeptAsGiven(bool gui)
    {
        var options = PackageCommand.ResolveMixedFallback(gui, segmentBudget: 4000u, maxTransitions: 40u);

        options.SegmentInstructionBudget.Should().Be(4000u);
        options.MaxTransitions.Should().Be(40u);
    }
}

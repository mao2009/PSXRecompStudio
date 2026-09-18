using PSXRecomp.Core.Execution;
using Xunit;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class RecompiledArtifactExitCodeTests
{
    [Theory]
    [InlineData(TitleExecutionState.Completed, RecompiledArtifactOutcome.Success, 0)]
    [InlineData(TitleExecutionState.Returned, RecompiledArtifactOutcome.Success, 0)]
    [InlineData(TitleExecutionState.RuntimeHandoff, RecompiledArtifactOutcome.Blocked, 2)]
    [InlineData(TitleExecutionState.BudgetExhausted, RecompiledArtifactOutcome.Blocked, 2)]
    [InlineData(TitleExecutionState.UnsupportedTransfer, RecompiledArtifactOutcome.Blocked, 2)]
    [InlineData(TitleExecutionState.RuntimeFailure, RecompiledArtifactOutcome.Failure, 1)]
    [InlineData(TitleExecutionState.InvalidState, RecompiledArtifactOutcome.Failure, 1)]
    public void Classify_MapsEveryTitleExecutionStateToItsStableOutcomeAndExitCode(
        TitleExecutionState state, RecompiledArtifactOutcome expectedOutcome, int expectedExitCode)
    {
        RecompiledArtifactExitCode.Classify(state).Should().Be(expectedOutcome);
        RecompiledArtifactExitCode.ExitCodeFor(state).Should().Be(expectedExitCode);
    }

    [Fact]
    public void ExitCodes_AreThreeDistinctStableValues()
    {
        var values = Enum.GetValues<TitleExecutionState>().Select(RecompiledArtifactExitCode.ExitCodeFor).Distinct();
        values.Should().BeEquivalentTo([0, 1, 2]);
    }
}

using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Contract tests for <see cref="RomAnalysisStageRecorder"/>: strict stage ordering,
/// no recording past a failure, and lossless preservation of the failure reason.
/// These are the invariants that make "last successful stage" meaningful.
/// </summary>
[Test]
public class RomAnalysisStageRecorderTests
{
    [Fact]
    public void Pass_TracksTheMostRecentSuccessfulStage()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Start, "started");
        recorder.Pass(RomAnalysisStage.Input, "input accepted");

        recorder.LastSuccessfulStage.Should().Be(RomAnalysisStage.Input);
        recorder.HasFailed.Should().BeFalse();
    }

    [Fact]
    public void Skip_DoesNotAdvanceTheLastSuccessfulStage()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Input, "input accepted");
        recorder.Skip(RomAnalysisStage.ChdOpen, "not a CHD");

        recorder.LastSuccessfulStage.Should().Be(RomAnalysisStage.Input,
            "a skipped stage did not succeed, it did not run");
    }

    [Fact]
    public void Fail_RecordsStageKindAndReasonAndKeepsTheLastSuccessfulStage()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Start, "started");
        recorder.Pass(RomAnalysisStage.Input, "input accepted");
        recorder.Fail(RomAnalysisStage.ChdOpen, "ChdOpenFailure", "bad header");

        recorder.HasFailed.Should().BeTrue();
        recorder.FailedStage.Should().Be(RomAnalysisStage.ChdOpen);
        recorder.FailureKind.Should().Be("ChdOpenFailure");
        recorder.FailureReason.Should().Be("bad header");
        recorder.LastSuccessfulStage.Should().Be(RomAnalysisStage.Input);
    }

    [Fact]
    public void Fail_FromException_PreservesTypeMessageAndTheExceptionItself()
    {
        var recorder = new RomAnalysisStageRecorder();
        var exception = new InvalidDataException("CHD magic mismatch");

        recorder.Pass(RomAnalysisStage.Start, "started");
        recorder.Fail(RomAnalysisStage.Input, "EmptyInput", exception);

        recorder.FailureReason.Should().Be("InvalidDataException: CHD magic mismatch");
        recorder.FailureException.Should().BeSameAs(exception,
            "the originating error is preserved rather than swallowed");
    }

    [Fact]
    public void RecordingAStageOutOfOrder_Throws()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Filesystem, "filesystem read");

        var act = () => recorder.Pass(RomAnalysisStage.Input, "input accepted");

        act.Should().Throw<InvalidOperationException>().WithMessage("*strictly ordered*");
    }

    [Fact]
    public void RecordingTheSameStageTwice_Throws()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Input, "input accepted");

        var act = () => recorder.Pass(RomAnalysisStage.Input, "again");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RecordingAfterAFailure_Throws()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Fail(RomAnalysisStage.Input, "EmptyInput", "empty");

        var act = () => recorder.Pass(RomAnalysisStage.ChdOpen, "opened");

        act.Should().Throw<InvalidOperationException>().WithMessage("*already failed*");
    }

    [Fact]
    public void Outcome_FromRecorder_ClassifiesPassAndFail()
    {
        var passing = new RomAnalysisStageRecorder();
        passing.Pass(RomAnalysisStage.Start, "started");
        RomAnalysisOutcome.From(passing).Status.Should().Be(RomAnalysisStatus.Pass);

        var failing = new RomAnalysisStageRecorder();
        failing.Pass(RomAnalysisStage.Start, "started");
        failing.Fail(RomAnalysisStage.Input, "EmptyInput", "empty");
        RomAnalysisOutcome.From(failing).Status.Should().Be(RomAnalysisStatus.Fail);
    }

    [Fact]
    public void Outcome_Skipped_ReportsSkipWithoutASuccessfulStage()
    {
        var outcome = RomAnalysisOutcome.Skipped("no fixture present");

        outcome.Status.Should().Be(RomAnalysisStatus.Skip);
        outcome.LastSuccessfulStage.Should().BeNull();
        outcome.FailedStage.Should().BeNull();
        outcome.Stages.Should().ContainSingle()
            .Which.Status.Should().Be(RomAnalysisStageStatus.Skipped);
    }
}

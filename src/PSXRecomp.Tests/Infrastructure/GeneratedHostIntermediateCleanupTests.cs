using FluentAssertions;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review L5, MAJOR-2): the intermediate object of a firmware distribution's generated host carries generated code. Its
/// removal is part of that build: when it cannot be removed the build is not reported as a success. A game build (the default
/// request) keeps its object and its link arguments as before.
/// </summary>
[Test]
public sealed class GeneratedHostIntermediateCleanupTests : IDisposable
{
    private const string TrivialSource = "int main(void) { return 0; }\n";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [SkippableFact]
    public void AFailedIntermediateRemoval_IsNotAReportedSuccess()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var output = _dir.CreateSubdirectory("failing");
        var service = new GeneratedHostBuildService(TimeSpan.FromMinutes(1), _ => throw new IOException("the object is locked"));

        var result = service.Build(new GeneratedHostBuildRequest(TrivialSource, output, "probe.exe", FirmwareDistribution: true));

        result.Status.Should().Be(GeneratedHostBuildStatus.OutputFailed);
        result.DiagnosticMessage.Should().Contain("could not be removed");
        result.DiagnosticMessage.Should().Contain("the object is locked");
        result.Artifact.Should().BeNull("a build whose intermediate remains is not an artifact");
    }

    [SkippableFact]
    public void TheDefaultRemoval_LeavesNoObject_AndReportsSuccess()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var output = _dir.CreateSubdirectory("succeeding");

        var result = new GeneratedHostBuildService(TimeSpan.FromMinutes(1)).Build(
            new GeneratedHostBuildRequest(TrivialSource, output, "probe.exe", FirmwareDistribution: true));

        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded, result.DiagnosticMessage);
#pragma warning disable AARC003 // Test-layer inspection of the produced output directory
        Directory.EnumerateFiles(output, "*.o").Should().BeEmpty("the intermediate object is not left in the output");
#pragma warning restore AARC003
    }

    [Fact]
    public void GameLinkArguments_AreTheArgumentsBeforeIssueB2()
    {
        // The game build's link command is [object, extra..., -o, binary]: exactly the arguments before Issue #B2 (no -s).
        GeneratedHostBuildService.LinkArguments("game.o", "game.exe", null, firmwareDistribution: false)
            .Should().Equal("game.o", "-o", "game.exe");
        GeneratedHostBuildService.LinkArguments("game.o", "game.exe", ["-lm"], firmwareDistribution: false)
            .Should().Equal("game.o", "-lm", "-o", "game.exe");
    }

    [Fact]
    public void FirmwareLinkArguments_StripWithDashS_AfterTheObject()
    {
        GeneratedHostBuildService.LinkArguments("fw.o", "fw.exe", null, firmwareDistribution: true)
            .Should().Equal("fw.o", "-s", "-o", "fw.exe");
        GeneratedHostBuildService.LinkArguments("fw.o", "fw.exe", ["-lm"], firmwareDistribution: true)
            .Should().Equal("fw.o", "-s", "-lm", "-o", "fw.exe");
    }

    [SkippableFact]
    public void AGameBuild_KeepsItsObject_AndDoesNotRemoveIt()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var output = _dir.CreateSubdirectory("game");
        var removals = 0;
        var service = new GeneratedHostBuildService(TimeSpan.FromMinutes(1), _ => removals++);

        var result = service.Build(new GeneratedHostBuildRequest(TrivialSource, output, "game.exe"));

        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded, result.DiagnosticMessage);
        removals.Should().Be(0, "a game build does not remove its intermediate, as before Issue #B2");
    }
}

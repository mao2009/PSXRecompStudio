using FluentAssertions;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review L5): the intermediate object of a generated host carries generated code. Its removal is part of the build:
/// when it cannot be removed the build is not reported as a success, so no distribution is committed with it inside.
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

        var result = service.Build(new GeneratedHostBuildRequest(TrivialSource, output, "probe.exe"));

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
            new GeneratedHostBuildRequest(TrivialSource, output, "probe.exe"));

        result.Status.Should().Be(GeneratedHostBuildStatus.Succeeded, result.DiagnosticMessage);
#pragma warning disable AARC003 // Test-layer inspection of the produced output directory
        Directory.EnumerateFiles(output, "*.o").Should().BeEmpty("the intermediate object is not left in the output");
#pragma warning restore AARC003
    }
}

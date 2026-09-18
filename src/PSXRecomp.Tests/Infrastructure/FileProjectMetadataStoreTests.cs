using FluentAssertions;
using PSXRecomp.Core.Project;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Exercises the production <see cref="FileProjectMetadataStore"/> (Issue #42) directly:
/// caller-selected directory writes, save/load round-trip, and rejection of a
/// project.json produced by a different format version.
/// </summary>
[Test]
public sealed class FileProjectMetadataStoreTests
{
    private static ProjectManifestDocument SampleDocument() => new()
    {
        SchemaVersion = ProjectSchema.ProjectFormatVersion,
        ArtifactKind = ProjectSchema.ProjectArtifactKind,
        ProjectId = "castlevania-sotn",
        InputSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
    };

    [Fact]
    public void SaveThenLoad_FromCallerSelectedDirectory_RoundTripsSemantically()
    {
        using var dir = new TempDirectory();
        var store = new FileProjectMetadataStore();
        var original = SampleDocument();

        store.Save(original, dir.FullPath);
        var reloaded = store.Load(dir.FullPath);

        reloaded.Should().Be(original);
    }

    [Fact]
    public void Save_WritesOnlyTheManifestFileDirectlyUnderTheCallerDirectory()
    {
        using var dir = new TempDirectory();
        new FileProjectMetadataStore().Save(SampleDocument(), dir.FullPath);

#pragma warning disable AARC003
        var written = Directory.GetFiles(dir.FullPath);
#pragma warning restore AARC003

        written.Should().ContainSingle()
            .Which.Should().Be(dir.Combine(ProjectSchema.ProjectManifestFileName));
    }

    [Fact]
    public void Save_CreatesTheCallerSelectedDirectoryWhenMissing()
    {
        using var dir = new TempDirectory();
        var target = dir.Combine("nested", "project-root");

        new FileProjectMetadataStore().Save(SampleDocument(), target);
        var reloaded = new FileProjectMetadataStore().Load(target);

        reloaded.Should().Be(SampleDocument());
    }

    [Fact]
    public void Load_RejectsAManifestWrittenByAnUnsupportedFormatVersion()
    {
        using var dir = new TempDirectory();
        var futureVersionJson =
            $$"""
            {
              "schemaVersion": {{ProjectSchema.ProjectFormatVersion + 1}},
              "artifactKind": "{{ProjectSchema.ProjectArtifactKind}}",
              "projectId": "castlevania-sotn",
              "inputSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
            }
            """;
        dir.WriteFile(ProjectSchema.ProjectManifestFileName, System.Text.Encoding.UTF8.GetBytes(futureVersionJson));

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Load_RejectsMalformedManifestJson()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(ProjectSchema.ProjectManifestFileName, "not json at all"u8.ToArray());

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<System.Text.Json.JsonException>();
    }
}

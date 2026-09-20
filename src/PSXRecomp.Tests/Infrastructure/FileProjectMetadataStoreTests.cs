using System.Text;
using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.Project;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Exercises the production <see cref="FileProjectMetadataStore"/> (Issue #42) directly:
/// caller-selected directory writes, the save/load reopen contract (which project,
/// which input identity), deterministic save → load → save round-trips, the explicit
/// missing-project / malformed / unsupported failure modes, and the safe presence-only
/// <see cref="IProjectMetadataStore.IsProjectDirectory"/> probe.
/// </summary>
[Test]
public sealed class FileProjectMetadataStoreTests
{
    private const string Sha256Sample = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static ProjectManifestDocument SampleDocument() => new()
    {
        SchemaVersion = ProjectSchema.ProjectFormatVersion,
        ArtifactKind = ProjectSchema.ProjectArtifactKind,
        ProjectId = "castlevania-sotn",
        InputSha256 = Sha256Sample,
    };

    private static readonly byte[] CanonicalManifestBytes = ProjectMetadataSerializer.Serialize(SampleDocument());

    private static void WriteRawManifest(TempDirectory dir, string projectId, string inputSha256, int schemaVersion = ProjectSchema.ProjectFormatVersion)
    {
        var json = $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "artifactKind": "{{ProjectSchema.ProjectArtifactKind}}",
              "projectId": "{{projectId}}",
              "inputSha256": "{{inputSha256}}"
            }
            """;
        dir.WriteFile(ProjectSchema.ProjectManifestFileName, Encoding.UTF8.GetBytes(json));
    }

    private static byte[] ReadManifestBytes(string directoryPath)
    {
#pragma warning disable AARC003
        return File.ReadAllBytes(Path.Combine(directoryPath, ProjectSchema.ProjectManifestFileName));
#pragma warning restore AARC003
    }

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
    public void Reopen_ReturnsTheIdenticalProjectIdAndFormatIdentity()
    {
        using var dir = new TempDirectory();
        var store = new FileProjectMetadataStore();
        store.Save(SampleDocument(), dir.FullPath);

        var reopened = store.Load(dir.FullPath);

        reopened.ProjectId.Should().Be(SampleDocument().ProjectId);
        reopened.SchemaVersion.Should().Be(ProjectSchema.ProjectFormatVersion);
        reopened.ArtifactKind.Should().Be(ProjectSchema.ProjectArtifactKind);
    }

    [Fact]
    public void Reopen_NormalizesAnUppercaseStoredSha256_WithoutRewritingTheManifest()
    {
        using var dir = new TempDirectory();
        var original = SampleDocument() with { InputSha256 = Sha256Sample.ToUpperInvariant() };
        new FileProjectMetadataStore().Save(original, dir.FullPath);

        var bytesBeforeLoad = ReadManifestBytes(dir.FullPath);
        var reopened = new FileProjectMetadataStore().Load(dir.FullPath);

        reopened.InputSha256.Should().Be(Sha256Sample);
        reopened.ProjectId.Should().Be(original.ProjectId);
        ReadManifestBytes(dir.FullPath).Should().Equal(bytesBeforeLoad);
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
    public void OnDiskManifest_IsExactlyTheCanonicalManagementData_WithNoPathTimestampOrMachineData()
    {
        using var dir = new TempDirectory();
        new FileProjectMetadataStore().Save(SampleDocument(), dir.FullPath);

        var onDisk = ReadManifestBytes(dir.FullPath);

        // What reaches disk is byte-for-byte the deterministic serializer output, which
        // the Domain serializer pins as free of absolute paths, timestamps, and machine
        // data — so the store's caller-selected directory can never leak into project.json.
        onDisk.Should().Equal(CanonicalManifestBytes);
    }

    [Fact]
    public void SaveLoadSave_IsDeterministic_AcrossCallerDirectories()
    {
        using var firstDir = new TempDirectory();
        using var secondDir = new TempDirectory();
        var store = new FileProjectMetadataStore();
        var original = SampleDocument();

        store.Save(original, firstDir.FullPath);
        var reopened = store.Load(firstDir.FullPath);
        store.Save(reopened, secondDir.FullPath);

        var firstBytes = ReadManifestBytes(firstDir.FullPath);
        var secondBytes = ReadManifestBytes(secondDir.FullPath);

        secondBytes.Should().Equal(firstBytes);
        store.Load(secondDir.FullPath).Should().Be(original);
    }

    [Fact]
    public void Load_ThrowsDirectoryNotFoundException_AndNeverCreatesTheDirectory()
    {
        using var dir = new TempDirectory();
        var missing = dir.Combine("nested", "missing-project");

        var act = () => new FileProjectMetadataStore().Load(missing);

        act.Should().Throw<DirectoryNotFoundException>()
            .And.Message.Should().Contain(missing);

#pragma warning disable AARC003
        Directory.Exists(missing).Should().BeFalse();
#pragma warning restore AARC003
    }

    [Fact]
    public void Load_ThrowsFileNotFoundException_WhenProjectJsonIsMissing()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory("build");

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        // Generated-artifact-only content (build/, ...) without a project.json is not a
        // reopenable project; it is reported as such rather than silently constructing one.
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_ThrowsFormatException_ForAnInvalidPersistedProjectId()
    {
        using var dir = new TempDirectory();
        WriteRawManifest(dir, "Not A Valid Id!", Sha256Sample);

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Load_ThrowsFormatException_ForAnInvalidPersistedSha256()
    {
        using var dir = new TempDirectory();
        WriteRawManifest(dir, "castlevania-sotn", new string('z', 64));

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Load_RejectsAManifestWrittenByAnUnsupportedFormatVersion()
    {
        using var dir = new TempDirectory();
        WriteRawManifest(dir, "castlevania-sotn", Sha256Sample, schemaVersion: ProjectSchema.ProjectFormatVersion + 1);

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Load_RejectsMalformedManifestJson()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(ProjectSchema.ProjectManifestFileName, "not json at all"u8.ToArray());

        var act = () => new FileProjectMetadataStore().Load(dir.FullPath);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void IsProjectDirectory_ReturnsTrue_ForAnExistingProjectDirectory()
    {
        using var dir = new TempDirectory();
        new FileProjectMetadataStore().Save(SampleDocument(), dir.FullPath);

        new FileProjectMetadataStore().IsProjectDirectory(dir.FullPath).Should().BeTrue();
    }

    [Fact]
    public void IsProjectDirectory_IsPresenceOnly_AndNeverValidatesContent()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(ProjectSchema.ProjectManifestFileName, "not valid json"u8.ToArray());

        // Content validity is Load's job; the probe only answers "is project.json present?".
        new FileProjectMetadataStore().IsProjectDirectory(dir.FullPath).Should().BeTrue();
    }

    [Fact]
    public void IsProjectDirectory_ReturnsFalse_WhenTheDirectoryDoesNotExist()
    {
        using var dir = new TempDirectory();
        var missing = dir.Combine("does-not-exist");

        new FileProjectMetadataStore().IsProjectDirectory(missing).Should().BeFalse();
    }

    [Fact]
    public void IsProjectDirectory_ReturnsFalse_WhenTheDirectoryHoldsOnlyGeneratedArtifacts()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory("build");
        dir.CreateSubdirectory("generated");
        dir.WriteFile("bin/out.bin", [1, 2, 3]);

        new FileProjectMetadataStore().IsProjectDirectory(dir.FullPath).Should().BeFalse();
    }

    [Fact]
    public void IsProjectDirectory_ReturnsFalse_ForAFilePath()
    {
        using var dir = new TempDirectory();
        var file = dir.WriteFile("some-file.txt", "content"u8.ToArray());

        new FileProjectMetadataStore().IsProjectDirectory(file).Should().BeFalse();
    }
}

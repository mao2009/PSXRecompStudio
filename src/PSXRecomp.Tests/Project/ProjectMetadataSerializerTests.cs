using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.Project;
using Xunit;

namespace PSXRecomp.Tests.Project;

/// <summary>
/// Locks the deterministic save/load contract for <see cref="ProjectManifestDocument"/>
/// (Issue #42, first persistence slice): semantic round-trip, byte-for-byte determinism,
/// SHA-256 normalization/validation, explicit failure on malformed/unsupported input, and
/// the Management Data / Generated Artifact boundary.
/// </summary>
[Test]
public sealed class ProjectMetadataSerializerTests
{
    private static ProjectManifestDocument SampleDocument(string inputSha256 = Sha256Sample) => new()
    {
        SchemaVersion = ProjectSchema.ProjectFormatVersion,
        ArtifactKind = ProjectSchema.ProjectArtifactKind,
        ProjectId = "castlevania-sotn",
        InputSha256 = inputSha256,
    };

    private const string Sha256Sample = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <summary>
    /// A complete, otherwise-valid manifest whose <c>artifactKind</c> is the raw JSON
    /// value <paramref name="artifactKindJson"/> (a quoted string, or <c>null</c>).
    /// </summary>
    private static string ManifestJson(string artifactKindJson) => $$"""
        {
          "schemaVersion": {{ProjectSchema.ProjectFormatVersion}},
          "artifactKind": {{artifactKindJson}},
          "projectId": "castlevania-sotn",
          "inputSha256": "{{Sha256Sample}}"
        }
        """;

    [Fact]
    public void SaveThenLoad_IsSemanticallyEqual()
    {
        var original = SampleDocument();

        var bytes = ProjectMetadataSerializer.Serialize(original);
        var reloaded = ProjectMetadataSerializer.Deserialize(bytes);

        reloaded.Should().Be(original);
    }

    [Fact]
    public void SaveLoadSave_IsByteForByteDeterministic()
    {
        var original = SampleDocument();

        var firstBytes = ProjectMetadataSerializer.Serialize(original);
        var reloaded = ProjectMetadataSerializer.Deserialize(firstBytes);
        var secondBytes = ProjectMetadataSerializer.Serialize(reloaded);

        secondBytes.Should().Equal(firstBytes);
    }

    [Fact]
    public void Serialize_SameDocumentTwice_ProducesIdenticalBytes()
    {
        var first = ProjectMetadataSerializer.Serialize(SampleDocument());
        var second = ProjectMetadataSerializer.Serialize(SampleDocument());

        second.Should().Equal(first);
    }

    [Theory]
    [InlineData(Sha256Sample)]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("9f86D081884C7d659a2FEAA0c55ad015A3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void Serialize_NormalizesSha256CasingToTheSameCanonicalBytes(string inputCasing)
    {
        var canonical = ProjectMetadataSerializer.Serialize(SampleDocument(Sha256Sample));
        var fromOtherCasing = ProjectMetadataSerializer.Serialize(SampleDocument(inputCasing));

        fromOtherCasing.Should().Equal(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")] // 63 chars
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a088")] // 65 chars
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // non-hex, 64 chars
    public void Serialize_RejectsInvalidSha256(string invalidHash)
    {
        var document = SampleDocument(invalidHash);

        var act = () => ProjectMetadataSerializer.Serialize(document);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Serialize_RejectsInvalidProjectId()
    {
        var document = SampleDocument() with { ProjectId = "Not A Valid Id!" };

        var act = () => ProjectMetadataSerializer.Serialize(document);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Serialize_RejectsUnsupportedFormatVersion()
    {
        var document = SampleDocument() with { SchemaVersion = ProjectSchema.ProjectFormatVersion + 1 };

        var act = () => ProjectMetadataSerializer.Serialize(document);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedFormatVersion()
    {
        var json = $$"""
            {
              "schemaVersion": {{ProjectSchema.ProjectFormatVersion + 1}},
              "artifactKind": "{{ProjectSchema.ProjectArtifactKind}}",
              "projectId": "castlevania-sotn",
              "inputSha256": "{{Sha256Sample}}"
            }
            """;

        var act = () => ProjectMetadataSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Deserialize_AcceptsTheCanonicalArtifactKind()
    {
        var bytes = Encoding.UTF8.GetBytes(ManifestJson($"\"{ProjectSchema.ProjectArtifactKind}\""));

        var document = ProjectMetadataSerializer.Deserialize(bytes);

        document.ArtifactKind.Should().Be(ProjectSchema.ProjectArtifactKind);
    }

    /// <summary>
    /// The artifact kind is the persisted discriminator, so it is matched exactly
    /// (casing included) and a foreign value is rejected — never silently rewritten
    /// into a project manifest.
    /// </summary>
    [Theory]
    [InlineData("\"other\"")]
    [InlineData("\"psxrecomp.real-rom-analysis.manifest\"")]
    [InlineData("\"PSXRecomp.Project.Manifest\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Deserialize_RejectsForeignArtifactKindInsteadOfRepairingIt(string artifactKindJson)
    {
        var bytes = Encoding.UTF8.GetBytes(ManifestJson(artifactKindJson));

        var act = () => ProjectMetadataSerializer.Deserialize(bytes);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Serialize_RejectsForeignArtifactKindInsteadOfRepairingIt()
    {
        var document = SampleDocument() with { ArtifactKind = "other" };

        var act = () => ProjectMetadataSerializer.Serialize(document);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Deserialize_RejectsMalformedJson()
    {
        var act = () => ProjectMetadataSerializer.Deserialize("{ not valid json"u8.ToArray());

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserialize_RejectsMissingRequiredField()
    {
        var json = $$"""
            {
              "schemaVersion": {{ProjectSchema.ProjectFormatVersion}},
              "artifactKind": "{{ProjectSchema.ProjectArtifactKind}}",
              "projectId": "castlevania-sotn"
            }
            """;

        var act = () => ProjectMetadataSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Serialize_ProducesExactlyTheCanonicalManifestShape()
    {
        var bytes = ProjectMetadataSerializer.Serialize(SampleDocument());
        var text = Encoding.UTF8.GetString(bytes);

        var expected = "{\n"
            + $"  \"schemaVersion\": {ProjectSchema.ProjectFormatVersion},\n"
            + $"  \"artifactKind\": \"{ProjectSchema.ProjectArtifactKind}\",\n"
            + "  \"projectId\": \"castlevania-sotn\",\n"
            + $"  \"inputSha256\": \"{Sha256Sample}\"\n"
            + "}\n";

        text.Should().Be(expected);
    }

    [Fact]
    public void Serialize_NeverContainsAbsolutePathTimestampOrMachineData()
    {
        var text = Encoding.UTF8.GetString(ProjectMetadataSerializer.Serialize(SampleDocument()));

        text.Should().NotContainAny("C:\\", "/home/", "/Users/", ":\\\\");
        text.Should().NotMatchRegex("\\d{4}-\\d{2}-\\d{2}T");
    }

    /// <summary>
    /// Pins the Management Data / Generated Artifact boundary at the type level: the
    /// persisted manifest exposes exactly these four fields and nothing that could carry
    /// a build output path, binary, object file, or other generated-artifact reference.
    /// Adding a property here is a deliberate, reviewed format change (bump
    /// <see cref="ProjectSchema.ProjectFormatVersion"/>), never an incidental one.
    /// </summary>
    [Fact]
    public void ProjectManifestDocument_ExposesOnlyTheApprovedManagementDataFields()
    {
        var propertyNames = typeof(ProjectManifestDocument)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        propertyNames.Should().BeEquivalentTo(
            ["ArtifactKind", "InputSha256", "ProjectId", "SchemaVersion"]);

        var forbiddenTerms = new[] { "path", "binary", "executable", "object", "build", "dir", "output" };
        foreach (var name in propertyNames)
        {
            var lower = name.ToLowerInvariant();
            forbiddenTerms.Should().NotContain(term => lower.Contains(term));
        }
    }
}

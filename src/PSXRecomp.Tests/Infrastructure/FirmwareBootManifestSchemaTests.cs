using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #10 (MEDIUM-1): a firmware bundle packaged before the disc sector mapping change is schema 3. Schema 4 is the
/// current version, so a schema-3 bundle is refused at load with an instruction to re-package it. Synthetic values only.
/// </summary>
[Test]
public sealed class FirmwareBootManifestSchemaTests
{
    private static readonly string Sha = new('a', 64);

    private static string Json(int schemaVersion) => FirmwareBootManifestJson.Serialize(new FirmwareBootManifest(
        schemaVersion,
        "openbios",
        64,
        "firmware/rom.roots",
        "firmware/images.txt",
        "disc/disc.bin",
        Sha,
        [new FirmwareAotInput("firmware/rom.roots", Sha), new FirmwareAotInput("firmware/images.txt", Sha)]));

    [Fact]
    public void CurrentSchemaVersion_IsFourForTheSectorMappingChange()
    {
        FirmwareBootManifest.CurrentSchemaVersion.Should().Be(4);
    }

    [Fact]
    public void Parse_SchemaVersionThree_IsRefusedWithARepackageInstruction()
    {
        var act = () => FirmwareBootManifestJson.Parse(Json(3));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Unsupported firmware boot manifest schema 3*")
            .WithMessage("*re-package the firmware bundle*");
    }

    [Fact]
    public void Parse_CurrentSchemaVersion_IsAccepted()
    {
        var parsed = FirmwareBootManifestJson.Parse(Json(FirmwareBootManifest.CurrentSchemaVersion));

        parsed.SchemaVersion.Should().Be(FirmwareBootManifest.CurrentSchemaVersion);
    }

    [Fact]
    public void Parse_NewerSchemaVersion_IsRefusedWithoutTheRepackageInstruction()
    {
        var act = () => FirmwareBootManifestJson.Parse(Json(5));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Unsupported firmware boot manifest schema 5*")
            .Where(e => !e.Message.Contains("re-package", StringComparison.Ordinal));
    }
}

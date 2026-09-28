using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.E2E;

/// <summary>Contract tests for the reusable source-generated PS-X EXE fixtures.</summary>
[Test]
public sealed class GeneratedPsxExeFixtureTests
{
    [Fact]
    public void Catalog_HasAtLeastTwoUniqueInspectableFixtures()
    {
        GeneratedPsxExeFixtures.All.Should().HaveCountGreaterThanOrEqualTo(2);
        GeneratedPsxExeFixtures.All.Select(fixture => fixture.Id)
            .Should().OnlyHaveUniqueItems();
        GeneratedPsxExeFixtures.All.Should().OnlyContain(
            fixture => !string.IsNullOrWhiteSpace(fixture.Description)
                       && fixture.InstructionWords.Count > 0
                       && fixture.ExpectedSha256.Length == 64);
    }

    [Fact]
    public void Regeneration_IsByteIdenticalAndMatchesPinnedSha256()
    {
        foreach (var fixture in GeneratedPsxExeFixtures.All)
        {
            var first = fixture.Generate();
            var second = fixture.Generate();

            first.Should().Equal(second, $"{fixture.Id} must regenerate byte-for-byte");
            Sha256Hex(first).Should().Be(
                fixture.ExpectedSha256,
                $"{fixture.Id} byte changes are an explicit fixture-contract change");
        }
    }

    [Fact]
    public void GeneratedImages_RoundTripThroughProductionPsxExeParser()
    {
        foreach (var fixture in GeneratedPsxExeFixtures.All)
        {
            var bytes = fixture.Generate();
            var exe = PsxExe.Load(bytes, $"{fixture.Id}.exe");

            exe.Header.EntryPoint.Should().Be(GeneratedPsxExeFixtures.EntryPc);
            exe.Header.TextStart.Should().Be(GeneratedPsxExeFixtures.EntryPc);
            exe.Header.TextSize.Should().Be((uint)(fixture.InstructionWords.Count * sizeof(uint)));
            exe.DecodedInstructionCount.Should().Be(fixture.InstructionWords.Count);

            for (var index = 0; index < fixture.InstructionWords.Count; index++)
            {
                exe.GetInstructionWord(GeneratedPsxExeFixtures.EntryPc + (uint)(index * sizeof(uint)))
                    .Should().Be(fixture.InstructionWords[index]);
            }
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

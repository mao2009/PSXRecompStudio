using System.Globalization;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #21: machine-readable CLI JSON must not depend on the thread's culture. The
/// culture is set on <see cref="CultureInfo.CurrentCulture"/> inside the test (not via the
/// environment) and the emitted bytes are compared between ja-JP and en-US.
/// </summary>
[Test]
public sealed class CliJsonLocaleIndependenceTests
{
    private const string JapaneseCulture = "ja-JP";
    private const string EnglishCulture = "en-US";

    private static T UnderCulture<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed record Document(string Kind, bool Success, ulong BigCount, uint Small, double Ratio, string? Artifact);

    [Fact]
    public void DoctorJson_IsByteIdentical_AcrossJaJpAndEnUs()
    {
        // Stubbed probes: no real toolchain or native library, so the output is a pure function of the table.
        var japanese = UnderCulture(JapaneseCulture, RunDoctorJson);
        var english = UnderCulture(EnglishCulture, RunDoctorJson);

        english.Should().Be(japanese);
        japanese.Should().Contain("\"kind\": \"doctor\"");
    }

    private static string RunDoctorJson()
    {
        var output = new StringWriter();
        var probes = new DoctorCommand.Probes("Windows", "X64", ".NET 10", "gcc", () => true, () => false);
        DoctorCommand.Run(json: true, output, probes);
        return output.ToString();
    }

    [Fact]
    public void EnvelopeWithEntryRootsAndMixedFallback_IsByteIdentical_AcrossJaJpAndEnUs()
    {
        var document = new Document("run", true, BigCount: 9_876_543_210UL, Small: 1_234_567, Ratio: 1234.5, Artifact: null);
        var roots = new uint[] { 0x80010000u, 0x80001000u };
        var evidence = new MixedFallbackEvidence(
            Transitions: 1_234_567,
            Returns: 1_234_000,
            FallbackInstructions: 9_876_543_210UL,
            PagesToInterpreter: 4096,
            PagesToArtifact: 8192,
            Targets: [new MixedFallbackTarget(0x80020000u, 1_234, 56_789, 0x80030000u)],
            LastEntryPc: 0x80020000u,
            LastReturnPc: 0x80030000u);

        string Envelope() => CliJson.Serialize(roots, evidence, document);

        var japanese = UnderCulture(JapaneseCulture, Envelope);
        var english = UnderCulture(EnglishCulture, Envelope);

        english.Should().Be(japanese);
        // Non-vacuous: the numbers and hex strings are really in the bytes, as plain invariant text.
        japanese.Should().Contain("\"transitions\": 1234567,");
        japanese.Should().Contain("\"bigCount\": 9876543210,");
        japanese.Should().Contain("\"ratio\": 1234.5,");
        japanese.Should().Contain("\"entryRoots\"");
        japanese.Should().Contain("\"0x80001000\"");
    }
}

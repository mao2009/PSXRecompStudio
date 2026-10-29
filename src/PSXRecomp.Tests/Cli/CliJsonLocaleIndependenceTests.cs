using System.Globalization;
using System.Text;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #21: machine-readable CLI JSON must not depend on the thread's culture. The culture is set on
/// <see cref="CultureInfo.CurrentCulture"/> inside the test (not via the environment). The output of each culture is
/// compared, as UTF-8 bytes, with the en-US baseline. de-DE is the case that matters: its decimal (',') and group ('.')
/// separators differ from en-US, so a culture-sensitive number format changes its bytes. ja-JP is kept as a second
/// case. Each test first checks the culture switch took effect, so a no-op switch cannot make the comparison pass.
/// </summary>
[Test]
public sealed class CliJsonLocaleIndependenceTests
{
    private const string Baseline = "en-US";

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

    /// <summary>
    /// Control: under <paramref name="culture"/> a culture-sensitive double must format with that culture's separators.
    /// Without this, the comparison below could pass even if the culture switch had no effect.
    /// </summary>
    private static string ControlledDouble(string culture) =>
        UnderCulture(culture, () => (1234.5).ToString(CultureInfo.CurrentCulture));

    private sealed record Document(string Kind, bool Success, ulong BigCount, uint Small, double Ratio, string? Artifact);

    [Fact]
    public void Control_DeDeFormatsWithCommaDecimalAndDotGroupSeparators()
    {
        ControlledDouble("de-DE").Should().Be("1234,5");
        UnderCulture("de-DE", () => (9_876_543_210UL).ToString("N0", CultureInfo.CurrentCulture)).Should().Be("9.876.543.210");
        ControlledDouble(Baseline).Should().Be("1234.5");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void DoctorJson_IsIdentical_ToEnUs(string culture)
    {
        // Stubbed probes: no real toolchain or native library, so the output is a pure function of the table.
        var baseline = UnderCulture(Baseline, RunDoctorJson);
        var other = UnderCulture(culture, RunDoctorJson);

        Encoding.UTF8.GetBytes(other).Should().Equal(Encoding.UTF8.GetBytes(baseline), "culture {0} must not change doctor --json", culture);
        baseline.Should().Contain("\"kind\": \"doctor\"");
    }

    private static string RunDoctorJson()
    {
        var output = new StringWriter();
        var probes = new DoctorCommand.Probes("Windows", "X64", ".NET 10", "gcc", () => true, () => false);
        DoctorCommand.Run(json: true, output, probes);
        return output.ToString();
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void EnvelopeWithEntryRootsAndMixedFallback_IsIdentical_ToEnUs(string culture)
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

        var baseline = UnderCulture(Baseline, Envelope);
        var other = UnderCulture(culture, Envelope);

        Encoding.UTF8.GetBytes(other).Should().Equal(Encoding.UTF8.GetBytes(baseline), "culture {0} must not change the envelope", culture);
        // Non-vacuous: every numeric field is really in the bytes as plain invariant text.
        baseline.Should().Contain("\"small\": 1234567,");
        baseline.Should().Contain("\"transitions\": 1234567,");
        baseline.Should().Contain("\"bigCount\": 9876543210,");
        baseline.Should().Contain("\"ratio\": 1234.5,");
        baseline.Should().Contain("\"entryRoots\"");
        baseline.Should().Contain("\"0x80001000\"");
    }
}

using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// PR 3 (the public-log boundary). A line a CI log can show is built from an allowlist of keys and fixed codes, and it
/// is refused when it carries a path, a digest-shaped run or an unknown key. The digests below are synthetic test values,
/// not from any ROM; the exception messages are sentinels that must never reach a public line.
/// </summary>
[Test]
public sealed class PublicLogPolicyTests
{
    private const string SyntheticDigest = "3f9a1c0e7b5d2a84f6c1e9b07d3a5c28e4f1b6a9d2c7e0f5a8b3c6d9e2f1a4b7";
    private const string PcSentinel = "0x80031DF0";
    private const string MessageSentinel = "SENTINEL-DETAIL-9d41";

    private static FirmwareRunSummary Summary() => new(
        Bios: "openbios",
        BiosMode: "openbios-unverified",
        DeclaredProvenance: null,
        RomSha256: SyntheticDigest,
        AotBiosBlocks: 3,
        AotLoadedBlocks: 7,
        State: "RuntimeFailure",
        GuestPc: PcSentinel,
        DiagnosticCode: "ARTIFACT_FALLBACK_BUDGET_EXHAUSTED",
        DiagnosticMessage: $"fallback budget exhausted at pc {PcSentinel} {MessageSentinel}",
        MixedFallback: true,
        FallbackSegmentInstructionBudget: 100_000_000u,
        FallbackMaxTransitions: 1000u,
        NativeInstructions: 42ul,
        FallbackInstructions: 99_999_999ul,
        FallbackTransitions: 2u,
        FinalGpr: ["0x00000000", "0x00000001"]);

    private static IReadOnlyCollection<string> Keys => PublicLog.FirmwareRunKeys;

    [Fact]
    public void PublicLine_KeysAreExactlyTheAllowlist()
    {
        using var document = JsonDocument.Parse(Summary().ToPublicJson());
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        keys.Should().Equal(PublicLog.FirmwareRunKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void PublicLine_PassesThePolicy()
    {
        PublicLog.Refusal(Summary().ToPublicJson(), Keys).Should().BeNull();
    }

    [Fact]
    public void PublicLine_OmitsRunDetail_RomDigestPcCountsAndMessage()
    {
        var line = Summary().ToPublicJson();

        line.Should().NotContain(SyntheticDigest, "the ROM digest is never published");
        line.Should().NotContain(PcSentinel, "the guest PC is run detail");
        line.Should().NotContain(MessageSentinel, "the diagnostic message is run detail");
        foreach (var key in new[] { "guestPc", "finalGpr", "aotBiosBlocks", "aotLoadedBlocks", "diagnosticMessage", "nativeInstructions", "fallbackInstructions", "fallbackTransitions" })
        {
            line.Should().NotContain($"\"{key}\"", $"{key} is not on the public allowlist");
        }
    }

    [Fact]
    public void PublicLine_KeepsTheCompatibilityFieldsWithTheirValues()
    {
        using var document = JsonDocument.Parse(Summary().ToPublicJson());
        var root = document.RootElement;
        root.GetProperty("kind").GetString().Should().Be("firmware-run");
        root.GetProperty("bios").GetString().Should().Be("openbios");
        root.GetProperty("biosMode").GetString().Should().Be("openbios-unverified");
        root.GetProperty("state").GetString().Should().Be("RuntimeFailure");
        root.GetProperty("diagnosticCode").GetString().Should().Be("ARTIFACT_FALLBACK_BUDGET_EXHAUSTED");
        root.GetProperty("mixedFallback").GetBoolean().Should().BeTrue();
        root.GetProperty("fallbackSegmentInstructionBudget").GetUInt32().Should().Be(100_000_000u);
        root.GetProperty("fallbackMaxTransitions").GetUInt32().Should().Be(1000u);
    }

    [Fact]
    public void UnknownKey_IsRefused_AndTheValueIsNotEchoed()
    {
        const string line = "{\"kind\":\"firmware-run\",\"guestPc\":\"0x1234\"}";
        var refusal = PublicLog.Refusal(line, Keys);
        refusal.Should().Be("key outside the allowlist");
        refusal.Should().NotContain("0x1234");
    }

    [Fact]
    public void MalformedJsonLine_IsRefused()
    {
        PublicLog.Refusal("{\"kind\":", Keys).Should().Be("malformed JSON line");
    }

    [Theory]
    [InlineData(@"runtime host: wrote D:\a\_temp\psx\run\frame.png")]
    [InlineData(@"runtime host: wrote zzzC:/Users/runner/work/frame.png")]
    [InlineData(@"runtime host: wrote zzzC:\Users\runner\work\frame.png")]
    [InlineData(@"runtime host: wrote C:/Users/runner/work/frame.png")]
    [InlineData("runtime host: wrote /home/runner/work/frame.png")]
    [InlineData(@"runtime host: wrote \\server\share\frame.png")]
    public void AbsolutePath_IsRefused(string line)
    {
        PublicLog.Refusal(line, Keys).Should().Be("absolute machine path");
    }

    [Fact]
    public void DigestShapedRun_IsRefused()
    {
        PublicLog.Refusal("runtime host: id " + SyntheticDigest, Keys).Should().Be("digest-shaped hex run");
    }

    [Fact]
    public void ExceptionMessage_IsNotPublished_OnlyTheFixedCodeAndTheTypeName()
    {
        var line = PublicLog.FailureLine("psxrecomp package", new InvalidDataException($"rom bytes {PcSentinel} {MessageSentinel}"));

        line.Should().NotContain(MessageSentinel);
        line.Should().NotContain(PcSentinel);
        line.Should().Contain("INVALID_DATA").And.Contain("InvalidDataException");
        PublicLog.Refusal(line, Keys).Should().BeNull("a failure line is itself public-safe");
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), "FILE_NOT_FOUND")]
    [InlineData(typeof(DirectoryNotFoundException), "FILE_NOT_FOUND")]
    [InlineData(typeof(UnauthorizedAccessException), "ACCESS_DENIED")]
    [InlineData(typeof(IOException), "IO_ERROR")]
    [InlineData(typeof(InvalidDataException), "INVALID_DATA")]
    [InlineData(typeof(FormatException), "INVALID_FORMAT")]
    [InlineData(typeof(ArgumentException), "INVALID_ARGUMENT")]
    [InlineData(typeof(InvalidOperationException), "INVALID_STATE")]
    [InlineData(typeof(NotSupportedException), "INTERNAL_ERROR")]
    public void FixedCodes_CoverTheExceptionTypes(Type type, string code)
    {
        var exception = (Exception)Activator.CreateInstance(type, "detail")!;
        PublicLog.CodeFor(exception).Should().Be(code);
    }

    [Fact]
    public void FrameWrittenLine_ShowsTheFileNameNotTheDirectory()
    {
        var line = PublicLog.FrameWrittenLine(640, 480, @"D:\a\_temp\psx\run\frame.png");

        line.Should().Be("runtime host: frame 640x480 written (frame.png)");
        line.Should().NotContain(@"D:\a");
        PublicLog.Refusal(line, Keys).Should().BeNull();
    }

    [Fact]
    public void CodedRefusal_PublishesItsCodeOnly()
    {
        var exception = PublicRefusal.Create(PublicCodes.LinkRefused, "secret detail under /home/runner/x");
        PublicLog.CodeFor(exception).Should().Be(PublicCodes.LinkRefused);

        var line = PublicLog.FailureLine("runtime host", exception);
        line.Should().Contain(PublicCodes.LinkRefused).And.NotContain("secret").And.NotContain("/home");
        PublicLog.Refusal(line, Keys).Should().BeNull();
    }

    [Fact]
    public void DifferentCauses_AreNotConflated()
    {
        var link = PublicLog.CodeFor(PublicRefusal.Create(PublicCodes.LinkRefused, "a"));
        var artifact = PublicLog.CodeFor(PublicRefusal.Create(PublicCodes.ArtifactPathInvalid, "b"));
        var generic = PublicLog.CodeFor(new InvalidDataException("c"));

        link.Should().NotBe(artifact);
        link.Should().NotBe(generic, "a symbolic-link refusal is not the generic invalid-data code");
        artifact.Should().NotBe(generic);
    }

    [Fact]
    public void UnlistedCode_FallsBackToTheGenericCodeOfItsType()
    {
        // Fail closed: a code that is not in PublicCodes.All is never published.
        PublicLog.CodeFor(PublicRefusal.Create("NOT_A_LISTED_CODE", "x")).Should().Be("INVALID_DATA");
    }

    [Fact]
    public void EveryListedCode_IsPublishedAsItself()
    {
        foreach (var code in PublicCodes.All)
        {
            PublicLog.CodeFor(PublicRefusal.Create(code, "x")).Should().Be(code);
        }
    }

    [Fact]
    public void IdentityCodes_AreOnTheFailClosedList()
    {
        PublicCodes.All.Should().Contain(PublicCodes.IdentityStatNotExported).And.Contain(PublicCodes.IdentityUnavailable);
        PublicLog.CodeFor(PublicRefusal.Create(PublicCodes.IdentityUnavailable, "x")).Should().Be(PublicCodes.IdentityUnavailable);
    }

    [Fact]
    public void RefusedLine_NamesTheRuleNotTheContent()
    {
        var line = PublicLog.RefusedLine("runtime host: firmware-run", "key outside the allowlist");
        line.Should().Contain("key outside the allowlist").And.NotContain("{");
    }
}

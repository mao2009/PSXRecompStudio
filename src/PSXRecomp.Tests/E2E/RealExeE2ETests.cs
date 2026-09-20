using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// The Issue #461 real-input half: whatever legally user-supplied PS-X EXEs exist
/// locally under <c>rom/*.exe</c> are driven through the same production path as the
/// synthetic proof in <see cref="RecompiledArtifactE2ETests"/>, via the user-facing
/// <c>psxrecomp run</c> entrypoint (<see cref="Program.Execute"/>). When no fixture
/// exists — the normal CI case — these tests skip explicitly; a fixture's presence is
/// never assumed and no title is named in code.
///
/// The gate is deliberately classification-honest (Issue #461, scope 6): every
/// fixture must end in a <em>classified</em> outcome. Generated code that runs to a
/// natural end or a classified blocked boundary must report the production recompiled
/// engine; a genuine unsupported boundary (an instruction/BIOS/MMIO edge the current
/// coverage cannot service) must fail closed with a stable diagnostic. No crash, no
/// silent success, and no fabricated success JSON is ever accepted.
/// </summary>
[Test]
public sealed class RealExeE2ETests
{
    /// <summary>
    /// The real-EXE tests consume user-supplied PS-X EXEs, so the repository must
    /// exclude them by design before any such fixture can ever be created: the
    /// gitignore covers <c>rom/*.exe</c> and the CI Artifact Contamination Gate's
    /// policy forbids the <c>.exe</c> extension and the <c>rom</c> path segment.
    /// Asserting that contract still holds keeps the E2E proof honest — a passing
    /// run can never smuggle a user's executable into Git.
    /// </summary>
    [Fact]
    public void ExeFixturesAreExcludedFromTheRepositoryByDesign()
    {
        var gitignore = RealExeFixtures.ReadRepositoryFile(".gitignore")
            .Split('\n')
            .Select(static line => line.Trim())
            .ToList();

        gitignore.Should().Contain("rom/*.exe").And.Contain("rom/**/*.exe");
        gitignore.Should().Contain("rom/*.EXE", "case variants are ignored as well");

        using var policy = JsonDocument.Parse(
            RealExeFixtures.ReadRepositoryFile(Path.Combine("config", "artifact-policy.json")));
        var extensions = policy.RootElement.GetProperty("forbiddenExtensions")
            .EnumerateArray().Select(static e => e.GetString()).ToList();
        var segments = policy.RootElement.GetProperty("forbiddenPathSegments")
            .EnumerateArray().Select(static e => e.GetString()).ToList();

        extensions.Should().Contain(".exe", "a user-supplied PS-X EXE must be rejected by the contamination gate");
        segments.Should().Contain("rom");
    }

    [SkippableFact]
    public void RealExe_LocalFixtures_DriveTheFullProductionRun_ClassifiedAndHonest()
    {
        var fixtures = RealExeFixtures.Discover();
        Skip.If(fixtures.Count == 0, RealExeFixtures.NoFixtureSkipReason);

        foreach (var fixture in fixtures)
        {
            EvaluateFixture(fixture);
        }
    }

    private static void EvaluateFixture(RealExeFixture fixture)
    {
        new FileInfo(fixture.ExePath).Exists.Should().BeTrue(
            $"the discovered fixture {fixture.ExePath} must still exist when the test executes");
        fixture.FixtureId.Should().NotBeNullOrWhiteSpace();

        using var dir = new TempDirectory();
        var outDir = dir.CreateSubdirectory("out");
        var (exit, output, error) = Invoke("run", fixture.ExePath, "--output", outDir, "--json");

        if (exit == RecompiledArtifactExitCode.Success)
        {
            // Generated/recompiled code executed for this real-exe. The production
            // recompiled engine must be the one that ran — the CLI composes #459, so a
            // non-recompiled backend would be a regression, not a success.
            using var json = JsonDocument.Parse(output);
            var result = json.RootElement.GetProperty("result");
            result.GetProperty("engineName").GetString().Should().Be(RecompiledHostExecutionEngine.EngineName);
            result.GetProperty("outcome").GetInt32().Should().Be((int)RecompiledArtifactOutcome.Success);
            return;
        }

        if (exit == RecompiledArtifactExitCode.Blocked)
        {
            // The guest stopped at a classified blocked boundary (an explicit
            // Runtime/BIOS stop, an unresolved transfer, or an exhausted budget)
            // at a real guest PC: still the production recompiled engine with a
            // machine-readable result — never a crash or an ambiguous success.
            using var json = JsonDocument.Parse(output);
            var result = json.RootElement.GetProperty("result");
            result.GetProperty("engineName").GetString().Should().Be(RecompiledHostExecutionEngine.EngineName);
            result.GetProperty("outcome").GetInt32().Should().Be((int)RecompiledArtifactOutcome.Blocked);
            result.GetProperty("guestPc").ValueKind.Should().NotBe(JsonValueKind.Null);
            return;
        }

        // Exit-code-1 class: a tooling/input/build/lowering failure must fail closed
        // with a stable diagnostic — never a silent exit and never a thrown exception
        // escaping as a test crash. Either the machine-readable JSON identifies the
        // failure or a human diagnostic reaches stderr, and never both being absent.
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        if (TryParseJson(output, out var failed))
        {
            using var failedJson = failed;
            failedJson.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            var hasErrorCode = failedJson.RootElement.TryGetProperty("errorCode", out var code)
                && code.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(code.GetString());
            var hasMessage = failedJson.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString());
            (hasErrorCode || hasMessage).Should().BeTrue(
                "a classified failure must carry a stable diagnostic in the JSON");
        }
        else
        {
            error.Should().Contain("psxrecomp run:",
                "a classified failure must carry a diagnostic on stderr when no JSON is emitted");
        }
    }

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static bool TryParseJson(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
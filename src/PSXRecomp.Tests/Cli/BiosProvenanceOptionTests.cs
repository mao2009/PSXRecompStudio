using FluentAssertions;
using PSXRecomp.Infrastructure.Cli;
using Xunit;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #B2 (review MEDIUM-2): <c>--bios-provenance</c> is validated. An unknown value fails the command and names the
/// value; it is never silently replaced by a default that would label a synthetic image as a real OpenBIOS run.
/// </summary>
[Test]
public sealed class BiosProvenanceOptionTests
{
    [Theory]
    [InlineData("syntetic")]
    [InlineData("user_supplied")]
    [InlineData("")]
    [InlineData("USER-SUPPLIED ")]
    public void AnUnknownProvenance_FailsTheCommand_AndNamesTheValue(string value)
    {
        var error = new StringWriter();
        var exit = Program.Execute(
            ["package", "disc.bin", "--bios", "openbios", "--bios-rom", "openbios.bin", "--aot-dir", "aot",
             "--bios-provenance", value, "--output", "out"],
            new StringWriter(), error);

        exit.Should().NotBe(0, "a misspelled provenance must not package a distribution");
        error.ToString().Should().Contain("--bios-provenance", "the failure names the option");
        error.ToString().Should().Contain($"'{value}'", "the failure names the rejected value");
    }
}

using FluentAssertions;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Runtime;

// Guards ADR-014: all execution paths consume one structured BIOS Runtime
// contract, and unsupported calls remain explicit.
[Test]
public sealed class BiosHleContractTests
{
    [Fact]
    public void Identity_Represents_A0_B0_And_C0_Deterministically()
    {
        new BiosCallIdentity(BiosCallFamily.A0, 0x09).StableKey.Should().Be("A0:09");
        new BiosCallIdentity(BiosCallFamily.B0, 0x17).StableKey.Should().Be("B0:17");
        new BiosCallIdentity(BiosCallFamily.C0, 0x03).StableKey.Should().Be("C0:03");
    }

    [Fact]
    public void SupportedDispatch_Uses_The_Generic_Runtime_Boundary()
    {
        IBiosRuntime runtime = new BiosHleRuntime();

        BiosHleRuntime.PutCharFunction.Should().Be(0x3C);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, 0x80001234, new[] { 0x141u }));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x41u);
        result.Diagnostic.Should().BeNull();
    }

    [Fact]
    public void A0_09_Remains_Unsupported_In_Phase_1()
    {
        var identity = new BiosCallIdentity(BiosCallFamily.A0, 0x09);

        var result = new BiosHleRuntime().Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.Diagnostic.Identity.Should().BeSameAs(identity);
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, 0x7F)]
    [InlineData(BiosCallFamily.B0, 0x09)]
    [InlineData(BiosCallFamily.C0, 0x01)]
    public void UnsupportedDispatch_Is_Explicit_And_Never_Silent(
        BiosCallFamily family, byte functionNumber)
    {
        var identity = new BiosCallIdentity(family, functionNumber, 0x80005678);
        var result = new BiosHleRuntime().Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.Diagnostic.Identity.Should().BeSameAs(identity);
    }

    [Fact]
    public void Diagnostics_Are_Deterministic_For_Identical_Input()
    {
        var first = new BiosHleRuntime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));
        var second = new BiosHleRuntime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));

        first.Diagnostic!.ToStableString().Should().Be(second.Diagnostic!.ToStableString());
    }

    [Fact]
    public void InvalidPutCharCall_Remains_An_Explicit_Diagnostic()
    {
        var result = new BiosHleRuntime().Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
    }
}

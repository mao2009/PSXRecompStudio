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

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidPutCharArgumentCounts_Are_Rejected_Explicitly(int argumentCount)
    {
        var arguments = Enumerable.Range(0, argumentCount).Select(static value => (uint)value).ToArray();
        var result = new BiosHleRuntime().Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: arguments));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        result.Diagnostic.Message.Should().Be("A0:3C putchar requires one character argument.");
    }

    [Fact]
    public void Puts_Dispatches_On_Its_Documented_A0_Identity()
    {
        IBiosRuntime runtime = new BiosHleRuntime();

        BiosHleRuntime.PutsFunction.Should().Be(0x3E);

        var identity = new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, 0x80002000, new[] { 0x80010000u });
        var result = runtime.Invoke(identity);

        // Documented ABI: R2 returns the incoming R4 string pointer unchanged.
        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x80010000u);
        result.Diagnostic.Should().BeNull();
    }

    [Fact]
    public void Puts_Is_Pure_And_Deterministic_Across_Repeated_Invocations()
    {
        var runtime = new BiosHleRuntime();
        var identity = new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, 0x80002000, new[] { 0x80010000u });

        var first = runtime.Invoke(identity);
        var second = runtime.Invoke(identity);

        // No host output sink exists yet, so repeated calls on the same instance
        // must be indistinguishable: no accumulated state, no side effect.
        second.Should().Be(first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidPutsArgumentCounts_Are_Rejected_Explicitly(int argumentCount)
    {
        var arguments = Enumerable.Range(0, argumentCount).Select(static value => (uint)value).ToArray();
        var result = new BiosHleRuntime().Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutsFunction, arguments: arguments));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        result.Diagnostic.Message.Should().Be("A0:3E puts requires one string-pointer argument.");
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, 0x3B)] // getchar, adjacent to putchar
    [InlineData(BiosCallFamily.A0, 0x3D)] // gets, between putchar and puts
    [InlineData(BiosCallFamily.A0, 0x3F)] // the next A0 slot above puts
    [InlineData(BiosCallFamily.B0, 0x3D)] // the B0 putchar alias, deliberately unregistered
    [InlineData(BiosCallFamily.B0, 0x3F)] // the B0 puts alias, deliberately unregistered
    public void NeighbouringFunctionNumbers_Are_Not_Caught_By_The_Registry(
        BiosCallFamily family, byte functionNumber)
    {
        var identity = new BiosCallIdentity(family, functionNumber, 0x80002000, new[] { 0x80010000u });

        var result = new BiosHleRuntime().Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }
}

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
    public void Constructor_Rejects_A_Missing_Output_Sink()
    {
        var act = static () => new BiosHleRuntime(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("outputSink");
    }

    // A0:3C putchar's documented behavior is writing the character to the TTY
    // *and* returning it (ADR-014). Both halves are asserted here: the low byte
    // reaches the sink exactly once, and the return value is unchanged.
    [Fact]
    public void SupportedDispatch_Uses_The_Generic_Runtime_Boundary()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = new BiosHleRuntime(sink);

        BiosHleRuntime.PutCharFunction.Should().Be(0x3C);

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, 0x80001234, new[] { 0x141u }));

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x41u);
        result.Diagnostic.Should().BeNull();

        // Exactly one byte, truncated to the low 8 bits, and no duplicate write.
        sink.Bytes.Should().BeEquivalentTo(new byte[] { 0x41 }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void PutChar_Writes_Every_Invocation_To_The_Sink_In_Call_Order()
    {
        var sink = new CapturedOutputSink();
        IBiosRuntime runtime = new BiosHleRuntime(sink);

        foreach (uint character in new[] { 'P', 'S', 'X' })
        {
            runtime.Invoke(new BiosCallIdentity(
                BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [character]))
                .ReturnValue.Should().Be(character);
        }

        sink.Bytes.Should().BeEquivalentTo(
            new byte[] { (byte)'P', (byte)'S', (byte)'X' }, static o => o.WithStrictOrdering());
    }

    [Fact]
    public void PutChar_Output_Is_Deterministic_Across_Fresh_Runtimes()
    {
        static IReadOnlyList<byte> Emit()
        {
            var sink = new CapturedOutputSink();
            IBiosRuntime runtime = new BiosHleRuntime(sink);

            foreach (var argument in new[] { 0x141u, 0x0Au, 0xFFu })
            {
                runtime.Invoke(new BiosCallIdentity(
                    BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: [argument]));
            }

            return sink.Bytes;
        }

        // Compared against the expected bytes as well as against each other, so
        // the assertion cannot pass vacuously if nothing were ever written.
        var expected = new byte[] { 0x41, 0x0A, 0xFF };
        Emit().Should().BeEquivalentTo(expected, static o => o.WithStrictOrdering());
        Emit().Should().BeEquivalentTo(Emit(), static o => o.WithStrictOrdering());
    }

    [Fact]
    public void A0_09_Remains_Unsupported_In_Phase_1()
    {
        var identity = new BiosCallIdentity(BiosCallFamily.A0, 0x09);

        var result = new BiosHleRuntime(new CapturedOutputSink()).Invoke(identity);

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
        var result = new BiosHleRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.Diagnostic.Identity.Should().BeSameAs(identity);
    }

    [Fact]
    public void Diagnostics_Are_Deterministic_For_Identical_Input()
    {
        var first = new BiosHleRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));
        var second = new BiosHleRuntime(new CapturedOutputSink())
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, 0x7E, 0x80000000));

        first.Diagnostic!.ToStableString().Should().Be(second.Diagnostic!.ToStableString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidPutCharArgumentCounts_Are_Rejected_Explicitly(int argumentCount)
    {
        var arguments = Enumerable.Range(0, argumentCount).Select(static value => (uint)value).ToArray();
        var sink = new CapturedOutputSink();
        var result = new BiosHleRuntime(sink).Invoke(new BiosCallIdentity(
            BiosCallFamily.A0, BiosHleRuntime.PutCharFunction, arguments: arguments));

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        result.Diagnostic.Message.Should().Be("A0:3C putchar requires one character argument.");

        // A rejected call must have no side effect: nothing reaches the sink.
        sink.Bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(BiosCallFamily.A0, 0x3B)] // getchar, adjacent to putchar
    [InlineData(BiosCallFamily.A0, 0x3D)] // gets, between putchar and puts
    [InlineData(BiosCallFamily.A0, 0x3E)] // puts: identity verified (ADR-014, docs/REFERENCES.md) but
                                           // deliberately NOT registered — echoing its string-pointer
                                           // argument would satisfy the ABI's return convention while
                                           // implementing none of the documented behavior (reading guest
                                           // memory, writing to TTY), so it must not be Supported until
                                           // guest-memory access and an output sink exist.
    [InlineData(BiosCallFamily.A0, 0x3F)] // the next A0 slot above puts
    [InlineData(BiosCallFamily.B0, 0x3D)] // the B0 putchar alias, deliberately unregistered
    [InlineData(BiosCallFamily.B0, 0x3F)] // the B0 puts alias, deliberately unregistered
    public void NeighbouringFunctionNumbers_Are_Not_Caught_By_The_Registry(
        BiosCallFamily family, byte functionNumber)
    {
        var identity = new BiosCallIdentity(family, functionNumber, 0x80002000, new[] { 0x80010000u });

        var result = new BiosHleRuntime(new CapturedOutputSink()).Invoke(identity);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }
}

using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// PutsService is tested directly for service semantics, while
// BiosHleContractTests covers A0:3E registration and dispatch through
// BiosHleRuntime.
[Test]
public sealed class PutsServiceTests
{
    private const byte PutsFunction = 0x3E;

    [Fact]
    public void Invoke_ValidString_WritesEveryByteAndReturnsPointer()
    {
        var ram = new RecompilerGuestMemory();
        WriteCString(ram, 0x00000100, "hi");
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0x00000100), new GuestMemoryReader(ram.Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x00000100, "puts returns its incoming string pointer");
        result.Diagnostic.Should().BeNull();
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Invoke_MultiByteString_PreservesOutputOrder()
    {
        var ram = new RecompilerGuestMemory();
        WriteCString(ram, 0x00000200, "PSXRecompStudio");
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0x00000200), new GuestMemoryReader(ram.Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        sink.Bytes.Should().BeEquivalentTo("PSXRecompStudio"u8.ToArray(), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Invoke_EmptyString_SucceedsWithoutWritingBytes()
    {
        var ram = new RecompilerGuestMemory();
        ram.Write8(0x00000100, 0);
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0x00000100), new GuestMemoryReader(ram.Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0x00000100);
        sink.Bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x00000100u)]
    [InlineData(0x80000100u)]
    [InlineData(0xA0000100u)]
    public void Invoke_KusegKseg0Kseg1Aliases_ProduceIdenticalOutput(uint address)
    {
        var ram = new RecompilerGuestMemory();
        WriteCString(ram, 0x00000100, "hi");
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(address), new GuestMemoryReader(ram.Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(address, "the returned pointer is the incoming one, untranslated");
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Invoke_UntranslatableAddress_IsUnsupportedStateWithoutOutput()
    {
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(
            Identity(0xC0000000), new GuestMemoryReader(new RecompilerGuestMemory().Read8), sink);

        AssertUnsupportedState(result);
        sink.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void Invoke_UnmappedKusegAddress_IsUnsupportedStateWithoutOutput()
    {
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(
            Identity(0x00200000), new GuestMemoryReader(new RecompilerGuestMemory().Read8), sink);

        AssertUnsupportedState(result);
        sink.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void Invoke_UnreadableByteMidString_WritesNothingAtAll()
    {
        // Three readable bytes at the very end of RAM, then an unmapped byte
        // where the terminator would be: the atomicity guarantee means the
        // readable prefix must never reach the sink.
        var ram = new RecompilerGuestMemory();
        ram.Write8(RecompilerGuestMemory.RamSize - 3, (byte)'a');
        ram.Write8(RecompilerGuestMemory.RamSize - 2, (byte)'b');
        ram.Write8(RecompilerGuestMemory.RamSize - 1, (byte)'c');
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(
            Identity(RecompilerGuestMemory.RamSize - 3), new GuestMemoryReader(ram.Read8), sink);

        AssertUnsupportedState(result);
        sink.Bytes.Should().BeEmpty("a partially readable string must produce no partial TTY output");
    }

    [Fact]
    public void Invoke_UnterminatedStringFillingTheBound_IsUnsupportedStateWithoutOutput()
    {
        var ram = new RecompilerGuestMemory();
        for (var i = 0u; i < PutsService.MaxStringLength; i++)
        {
            ram.Write8(i, (byte)'A');
        }

        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0x00000000), new GuestMemoryReader(ram.Read8), sink);

        AssertUnsupportedState(result);
        result.Diagnostic!.Message.Should().Contain("NUL terminator");
        sink.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void Invoke_TerminatorAtTheLastScannedByte_Succeeds()
    {
        var ram = new RecompilerGuestMemory();
        for (var i = 0u; i < PutsService.MaxStringLength - 1; i++)
        {
            ram.Write8(i, (byte)'A');
        }

        ram.Write8(PutsService.MaxStringLength - 1, 0);
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0x00000000), new GuestMemoryReader(ram.Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        sink.Bytes.Should().HaveCount(PutsService.MaxStringLength - 1);
    }

    [Fact]
    public void Invoke_AddressPlusBoundOverflows_IsRejectedBeforeAnyRead()
    {
        var physicalReads = 0;
        var reader = new GuestMemoryReader(_ =>
        {
            physicalReads++;
            return 0;
        });
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(Identity(0xFFFFFFFF), reader, sink);

        AssertUnsupportedState(result);
        physicalReads.Should().Be(0, "the wraparound must be rejected before the scan starts");
        sink.Bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Invoke_WrongArgumentCount_IsInvalidArguments(int argumentCount)
    {
        var identity = new BiosCallIdentity(
            BiosCallFamily.A0,
            PutsFunction,
            arguments: Enumerable.Repeat(0x00000100u, argumentCount));
        var sink = new CapturedOutputSink();

        var result = PutsService.Invoke(identity, new GuestMemoryReader(new RecompilerGuestMemory().Read8), sink);

        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        sink.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void Invoke_RepeatedRunsWithFreshDependencies_AreDeterministic()
    {
        static (BiosServiceResult Result, byte[] Output) Run()
        {
            var ram = new RecompilerGuestMemory();
            WriteCString(ram, 0x00000100, "hi");
            var sink = new CapturedOutputSink();
            var result = PutsService.Invoke(Identity(0x00000100), new GuestMemoryReader(ram.Read8), sink);
            return (result, [.. sink.Bytes]);
        }

        var first = Run();
        var second = Run();

        first.Output.Should().BeEquivalentTo(second.Output, o => o.WithStrictOrdering());
        first.Result.Should().Be(second.Result);
    }

    [Fact]
    public void Invoke_NullArguments_Throw()
    {
        var reader = new GuestMemoryReader(new RecompilerGuestMemory().Read8);
        var sink = new CapturedOutputSink();

        ((Action)(() => PutsService.Invoke(null!, reader, sink))).Should().Throw<ArgumentNullException>();
        ((Action)(() => PutsService.Invoke(Identity(0), null!, sink))).Should().Throw<ArgumentNullException>();
        ((Action)(() => PutsService.Invoke(Identity(0), reader, null!))).Should().Throw<ArgumentNullException>();
    }

    private static BiosCallIdentity Identity(uint address) =>
        new(BiosCallFamily.A0, PutsFunction, arguments: [address]);

    private static void WriteCString(RecompilerGuestMemory ram, uint address, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            ram.Write8(address + (uint)i, (byte)value[i]);
        }

        ram.Write8(address + (uint)value.Length, 0);
    }

    private static void AssertUnsupportedState(BiosServiceResult result)
    {
        result.Status.Should().Be(BiosServiceStatus.Unsupported);
        result.ReturnValue.Should().BeNull();
        result.Diagnostic.Should().NotBeNull();
        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }
}

using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Runtime;

[Test]
public sealed class OutputSinkTests
{
    [Fact]
    public void WriteByte_SingleByte_CapturedExactly()
    {
        var sink = new CapturedOutputSink();

        sink.WriteByte(0x41);

        sink.Bytes.Should().HaveCount(1);
        sink.Bytes[0].Should().Be(0x41);
    }

    [Fact]
    public void WriteByte_MultipleBytes_PreservesOrder()
    {
        var sink = new CapturedOutputSink();

        sink.WriteByte(1);
        sink.WriteByte(2);
        sink.WriteByte(3);

        sink.Bytes.Should().BeEquivalentTo(new byte[] { 1, 2, 3 }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void WriteByte_EmptySink_CapturesNothing()
    {
        var sink = new CapturedOutputSink();

        sink.Bytes.Should().BeEmpty();
    }

    [Fact]
    public void WriteByte_DeterministicAcrossFreshInstances()
    {
        static byte[] EmitSequence(CapturedOutputSink target)
        {
            target.WriteByte(0xAA);
            target.WriteByte(0xBB);
            target.WriteByte(0xCC);
            return [.. target.Bytes];
        }

        var a = EmitSequence(new CapturedOutputSink());
        var b = EmitSequence(new CapturedOutputSink());

        a.Should().BeEquivalentTo(b, o => o.WithStrictOrdering());
    }

    [Fact]
    public void WriteByte_IndependentInstances_DoNotShareState()
    {
        var a = new CapturedOutputSink();
        var b = new CapturedOutputSink();

        a.WriteByte(0x10);
        a.WriteByte(0x20);

        b.WriteByte(0x30);

        a.Bytes.Should().BeEquivalentTo(new byte[] { 0x10, 0x20 }, o => o.WithStrictOrdering());
        b.Bytes.Should().HaveCount(1);
        b.Bytes[0].Should().Be(0x30);
    }

    [Fact]
    public void InjectedSink_ReceivesExactly_WhenConsumedThroughInterface()
    {
        var sink = new CapturedOutputSink();
        ReadOnlySpan<byte> payload = [0xDE, 0xAD, 0xBE, 0xEF];

        Emit(sink, payload);

        sink.Bytes.Should().BeEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Clear_ResetBuffer()
    {
        var sink = new CapturedOutputSink();
        sink.WriteByte(0xFF);
        sink.Clear();

        sink.Bytes.Should().BeEmpty();
    }

    private static void Emit(IRuntimeOutputSink sink, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            sink.WriteByte(b);
        }
    }
}

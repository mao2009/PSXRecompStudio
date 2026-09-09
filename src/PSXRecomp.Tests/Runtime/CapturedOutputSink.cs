using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Runtime;

/// <summary>Test double that captures every byte written through the sink.</summary>
[Test]
public sealed class CapturedOutputSink : IRuntimeOutputSink
{
    private readonly List<byte> _bytes = [];

    /// <summary>All bytes written to this sink so far.</summary>
    public IReadOnlyList<byte> Bytes => _bytes;

    /// <summary>Resets the capture buffer.</summary>
    public void Clear() => _bytes.Clear();

    /// <inheritdoc />
    public void WriteByte(byte value) => _bytes.Add(value);
}

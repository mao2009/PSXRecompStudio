using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// Generic Runtime output boundary. A single byte at a time so the receiver owns
/// buffering and encoding. This is explicitly not a logging interface — TTY
/// output is a separate responsibility.
/// </summary>
[Domain]
public interface IRuntimeOutputSink
{
    /// <summary>Writes one byte to the output sink. Implementations must be
    /// deterministic: identical call sequences produce identical output.</summary>
    void WriteByte(byte value);
}

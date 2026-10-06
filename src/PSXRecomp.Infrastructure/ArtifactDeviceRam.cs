using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The only route by which a device writes into (or reads from) guest RAM on the
/// generated-host path (Issue #679): the artifact's own <c>artifact_ram</c>, reached
/// with the protocol's byte R/W requests. It stands in for the <see cref="IMemoryBus"/>
/// the interpreter's devices use, whose RAM is the native core's — a second, private
/// RAM that must never receive a generated-host device's write.
/// </summary>
/// <remarks>
/// The artifact answers those requests only while it waits for the reply to a guest-time
/// report, which is the only time a device runs (the scheduler is advanced from nowhere
/// else). A request at any other time cannot be served, so it fails closed instead of
/// reaching a RAM that is not the guest's.
/// </remarks>
[Infrastructure]
internal sealed class ArtifactDeviceRam(Func<uint, byte> readByte, Action<uint, byte> writeByte) : IMemoryBus
{
    private bool _serving;
    private IMemoryBus? _redirect;

    /// <summary>
    /// While a mixed-execution fallback segment runs (Issue #693) the interpreter works on the graph core's RAM, a
    /// copy that is only written back to <c>artifact_ram</c> at the segment's end. A device that moves data into
    /// guest RAM during that time must reach the copy the CPU is executing on, or the write-back would erase it:
    /// the fallback owner redirects here for the segment and clears the redirect (null) afterwards.
    /// </summary>
    public void RedirectTo(IMemoryBus? coreBus) => _redirect = coreBus;

    /// <summary>A device accessed guest RAM while the artifact was not serving it.</summary>
    public sealed class UnroutableException(string message) : InvalidOperationException(message);

    public void BeginServing() => _serving = true;

    public void EndServing() => _serving = false;

    public uint Read(uint address)
    {
        if (_redirect is not null)
        {
            return _redirect.Read(address);
        }

        Require(address);
        return readByte(address)
            | (uint)readByte(address + 1) << 8
            | (uint)readByte(address + 2) << 16
            | (uint)readByte(address + 3) << 24;
    }

    public void Write(uint address, uint value)
    {
        if (_redirect is not null)
        {
            _redirect.Write(address, value);
            return;
        }

        Require(address);
        for (var i = 0u; i < sizeof(uint); i++)
        {
            writeByte(address + i, (byte)(value >> (int)(8 * i)));
        }
    }

    private void Require(uint address)
    {
        if (!_serving)
        {
            throw new UnroutableException(
                $"A device accessed guest RAM at physical 0x{address:X8} while the artifact was not serving its RAM.");
        }
    }
}

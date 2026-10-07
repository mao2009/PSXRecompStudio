using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// A0:49 <c>GPU_cw(cmd)</c>: wait for the GPU (<c>GPU_sync</c>), then write one word to GP0 (<c>GPU_DATA</c>,
/// <c>0x1F801810</c>); returns <c>GPU_sync</c>'s result, 0 on success (PCSX-Redux OpenBIOS <c>openbios/gpu/gpu.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>GPU_sync</c> as OpenBIOS states it: with GPUSTAT bits 29-30 (DMA direction) both 0 it waits for bit 28
/// (ready to receive a DMA block); otherwise it waits for DMA channel 2's CHCR busy bit (24) to clear, waits for
/// GPUSTAT bit 26, and writes GP1(04h) (<c>GPU_STATUS = 0x04000000</c>, DMA direction off). The retail loops
/// spin up to 0x10000000 times and then print a timeout and abort. A BIOS service cannot advance device time, so
/// a wait that is not already satisfied fails closed (<see cref="BiosServiceResult.UnsupportedState"/>); the
/// retail timeout/abort path is not modelled and nothing is written to GP0 in that case.
/// </para>
/// </remarks>
[Domain]
public static class BiosGpuCommandService
{
    public const uint Gp0Address = 0x1F801810;
    public const uint GpuStatusAddress = 0x1F801814;
    public const uint Dma2ChcrAddress = 0x1F8010A8;

    private const uint DmaDirectionMask = 0x60000000;
    private const uint ReadyForDmaBlock = 0x10000000;
    private const uint ReadyForCommand = 0x04000000;
    private const uint DmaBusy = 0x01000000;
    private const uint Gp1DmaDirectionOff = 0x04000000;

    public static BiosServiceResult Invoke(BiosCallIdentity identity, IGuestDeviceAccess? devices)
    {
        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} GPU_cw requires one command-word argument.");
        }

        if (devices is null)
        {
            return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} GPU_cw needs the device registers, but none are attached to this BIOS runtime.");
        }

        if (!devices.TryRead32(GpuStatusAddress, out var status))
        {
            return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} GPU_cw could not read GPUSTAT.");
        }

        if ((status & DmaDirectionMask) == 0)
        {
            if ((status & ReadyForDmaBlock) == 0)
            {
                return NotReady(identity, "GPU_sync(FG): GPUSTAT bit 28 (ready for DMA block) is clear", status);
            }
        }
        else
        {
            if (!devices.TryRead32(Dma2ChcrAddress, out var chcr))
            {
                return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} GPU_cw could not read DMA2 CHCR.");
            }

            if ((chcr & DmaBusy) != 0)
            {
                return NotReady(identity, "GPU_sync(BG): DMA channel 2 is busy", status);
            }

            if (!devices.TryRead32(GpuStatusAddress, out status) || (status & ReadyForCommand) == 0)
            {
                return NotReady(identity, "GPU_sync(BG): GPUSTAT bit 26 (ready for command) is clear", status);
            }

            if (!devices.TryWrite32(GpuStatusAddress, Gp1DmaDirectionOff))
            {
                return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} GPU_cw could not write GP1.");
            }
        }

        return devices.TryWrite32(Gp0Address, identity.Arguments[0])
            ? BiosServiceResult.Supported(identity, 0)
            : BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} GPU_cw could not write GP0.");
    }

    private static BiosServiceResult NotReady(BiosCallIdentity identity, string what, uint status) =>
        BiosServiceResult.UnsupportedState(
            identity,
            $"{identity.StableKey} GPU_cw: {what} (GPUSTAT=0x{status:X8}); a BIOS service cannot advance device time, so the retail wait/timeout is not modelled.");
}

using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>Outcome of one <see cref="PsxDeviceGraph"/> physical-address access.</summary>
[Domain]
public enum PsxDeviceAccessStatus
{
    /// <summary>The access reached a Runtime device or backing store.</summary>
    Completed = 0,

    /// <summary>The address is one the Runtime cannot answer for: main RAM (the caller's) or the BIOS-ROM window (no image).</summary>
    Unsupported,
}

/// <summary>
/// The Runtime's native core plus the managed device implementations wired to it
/// (MemoryBus, DMA, Timer, interrupt controller, GPU, CD-ROM; SIO0 and SPU stay
/// native-owned). One object graph shared by every execution backend, so the
/// interpreter and the generated-host artifact reach the same device code
/// (Issue #678) instead of each assembling their own.
/// </summary>
/// <remarks>
/// The graph never advances device time or raises interrupts: that is
/// <see cref="DeviceScheduler"/>'s job, built by the owner over these members.
/// <see cref="TryRead"/>/<see cref="TryWrite"/> give a width-aware physical-address
/// entry that routes through the native core exactly as the interpreter's guest
/// LB/LH/LW/SB/SH/SW do, so width-sensitive registers see one access of the guest's
/// width, never a byte-wise decomposition.
/// </remarks>
[Domain]
public sealed class PsxDeviceGraph : IDisposable
{
    private bool _disposed;

    public PsxDeviceGraph()
    {
        Core = new PSXCoreWrapper();
        Bus = new MemoryBus(Core);
        DmaAdapter = new DmaMmioAdapter(Core);
        TimerAdapter = new TimerMmioAdapter(Core);
        InterruptControllerAdapter = new InterruptControllerMmioAdapter(Core);
        GpuDevice = new GpuDevice();
        GpuAdapter = new GpuMmioAdapter(GpuDevice);
        // A licensed disc is always "present" so ReadN/ReadS/GetID reach their
        // success path and DMA3/IRQ2 stay production-reachable; disc-image
        // content/format and swap UX remain out of scope (#587 non-goals).
        // LoadData still supplies sector bytes as a separate, format-independent
        // boundary (Issue #586/#587), same as every focused CD-ROM test.
        CdRomDevice = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        CdRomAdapter = new CdRomMmioAdapter(CdRomDevice);
        CdRomDmaTransfer = new CdRomDmaTransfer(CdRomDevice, DmaAdapter, Bus);
        Bus.AttachDmaAdapter(DmaAdapter);
        Bus.AttachTimerAdapter(TimerAdapter);
        Bus.AttachInterruptControllerAdapter(InterruptControllerAdapter);
        Bus.AttachGpuAdapter(GpuAdapter);
        Bus.AttachCdRomAdapter(CdRomAdapter);

        // Guest LW/SW executes inside the native core, which bypasses the managed
        // MemoryBus object itself. Route only the GPU's 32-bit register window back
        // to the same managed adapter; the native side owns no GPU semantics
        // (Issue #572).
        Core.AttachGpuMmio(GpuAdapter.ReadRegister, GpuAdapter.WriteRegister);

        // Same bridge as the GPU above, but for the CD-ROM controller's 8-bit
        // port window: the native side owns no CD-ROM semantics either
        // (Issue #587).
        Core.AttachCdRomMmio(
            address => (byte)CdRomAdapter.ReadRegister(address),
            (address, value) => CdRomAdapter.WriteRegister(address, value));

        // SIO0 register model (Issue #542): native/Rust-owned inside
        // PSXMemory (see MemoryBus.ReadMmio/WriteMmio's Sio0 case and
        // crate::sio0's module documentation), so no adapter is attached
        // here — the owner's Core.Reset() already resets it.
    }

    public PSXCoreWrapper Core { get; }
    public MemoryBus Bus { get; }
    public DmaMmioAdapter DmaAdapter { get; }
    public TimerMmioAdapter TimerAdapter { get; }
    public InterruptControllerMmioAdapter InterruptControllerAdapter { get; }
    public GpuDevice GpuDevice { get; }
    public GpuMmioAdapter GpuAdapter { get; }
    public CdRomDevice CdRomDevice { get; }
    public CdRomMmioAdapter CdRomAdapter { get; }
    public CdRomDmaTransfer CdRomDmaTransfer { get; }

    /// <summary>
    /// Whether the Runtime defines the behaviour of a guest access at <paramref name="physicalAddress"/>
    /// itself: the scratchpad, the hardware-register window (a routed device, or the native
    /// core's flat register store for an address no device claims — what the interpreter
    /// does), and an address outside every mapped region (open bus: reads 0, writes
    /// ignored; <c>docs/runtime/architecture.md</c>). Not included: main RAM, because the
    /// caller owns its own backing store and answering from the core's would be a second,
    /// unsynchronized copy; and the BIOS-ROM window, because no ROM image is loaded here and
    /// answering 0 would hide that.
    /// </summary>
    public static bool IsSupported(uint physicalAddress) =>
        Ps1MemoryMap.ClassifyRegion(physicalAddress) is not (MemoryRegionClass.Ram or MemoryRegionClass.Bios);

    /// <summary>Reads <paramref name="width"/> (1, 2 or 4) bytes at <paramref name="physicalAddress"/> as one access.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not 1, 2 or 4.</exception>
    public PsxDeviceAccessStatus TryRead(uint physicalAddress, int width, out uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWidth(width);
        if (!IsSupported(physicalAddress))
        {
            value = 0;
            return PsxDeviceAccessStatus.Unsupported;
        }

        value = width switch
        {
            1 => Core.ReadMemory8(physicalAddress),
            2 => Core.ReadMemory16(physicalAddress),
            _ => Core.ReadMemory32(physicalAddress),
        };
        return PsxDeviceAccessStatus.Completed;
    }

    /// <summary>Writes the low <paramref name="width"/> (1, 2 or 4) bytes of <paramref name="value"/> at <paramref name="physicalAddress"/> as one access.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not 1, 2 or 4.</exception>
    public PsxDeviceAccessStatus TryWrite(uint physicalAddress, int width, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWidth(width);
        if (!IsSupported(physicalAddress))
        {
            return PsxDeviceAccessStatus.Unsupported;
        }

        switch (width)
        {
            case 1: Core.WriteMemory8(physicalAddress, (byte)value); break;
            case 2: Core.WriteMemory16(physicalAddress, (ushort)value); break;
            default: Core.WriteMemory32(physicalAddress, value); break;
        }
        return PsxDeviceAccessStatus.Completed;
    }

    /// <summary>Releases the native core, memory bus and MMIO adapters this graph owns.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // The native CPU may call back into the managed GPU/CD-ROM adapters
        // while stepping, so sever both edges before either side of either
        // bridge is disposed (Issues #572/#587).
        Core.DetachGpuMmio();
        Core.DetachCdRomMmio();
        Bus.Dispose();
        GpuAdapter.Dispose();
        GpuDevice.Dispose();
        CdRomAdapter.Dispose();

        // The native-owned adapters unregister their callbacks in Dispose();
        // MemoryBus.Dispose() only clears its own references to them, so they
        // must be disposed before Core.Dispose().
        DmaAdapter.Dispose();
        TimerAdapter.Dispose();
        InterruptControllerAdapter.Dispose();
        Core.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void RequireWidth(int width)
    {
        if (width is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Access width must be 1, 2 or 4 bytes.");
        }
    }
}

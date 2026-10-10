using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.MemoryCard;
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
public sealed class PsxDeviceGraph : IGuestDeviceAccess, IDisposable
{
    private bool _disposed;

    /// <param name="deviceRam">Where RAM traffic a device originates itself (today: CD-ROM DMA3 writing a
    /// sector into guest RAM) goes. Null routes it to <see cref="Bus"/>, i.e. this graph's native core RAM,
    /// which is the guest RAM of the interpreter. A backend whose guest RAM lives elsewhere (the generated-host
    /// artifact, Issue #679) passes its own seam, so a device never writes a second, private RAM.</param>
    /// <param name="memoryCardSlots">Which card is in each slot for this graph's whole life (Issue #715); null is
    /// <see cref="MemoryCardSlotConfiguration.Empty"/>, the production default.</param>
    /// <param name="disc">The disc in the CD-ROM drive (Issue #732): the controller reads its sectors with hardware
    /// timing. Null keeps the legacy BIOS-less model (a licensed disc identity with no sector source).</param>
    public PsxDeviceGraph(
        IMemoryBus? deviceRam = null, MemoryCardSlotConfiguration? memoryCardSlots = null, ICdSectorSource? disc = null)
    {
        MemoryCardSlots = memoryCardSlots ?? MemoryCardSlotConfiguration.Empty;
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
        CdRomDevice = new CdRomDevice(CdRomDiscIdentity.LicensedMode2(), disc);
        CdRomAdapter = new CdRomMmioAdapter(CdRomDevice);
        CdRomDmaTransfer = new CdRomDmaTransfer(CdRomDevice, DmaAdapter, deviceRam ?? Bus);
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

    /// <summary>
    /// The memory-card slots as the host configured them (Issue #715): immutable for the graph's life, read-only to
    /// every consumer. Host configuration, not guest-visible state.
    /// </summary>
    public MemoryCardSlotConfiguration MemoryCardSlots { get; }

    /// <summary>
    /// Whether a card is inserted at SIO0 port <paramref name="port"/> (0 = slot 1, 1 = slot 2): the read-only query the
    /// SIO0 bridge asks before answering a card-select byte. An empty slot is the no-ACK case.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not 0 or 1.</exception>
    public bool HasMemoryCard(int port) => MemoryCardSlots.HasCard(port switch
    {
        0 => MemoryCardSlot.Slot1,
        1 => MemoryCardSlot.Slot2,
        _ => throw new ArgumentOutOfRangeException(nameof(port), port, "SIO0 has memory-card ports 0 and 1 only."),
    });

    /// <summary>Diagnostic rendering of the slot state, e.g. <c>slot0=Empty slot1=Empty</c>. Card paths are not disclosed.</summary>
    public string MemoryCardSlotSummary =>
        $"slot0={(HasMemoryCard(0) ? "Inserted" : "Empty")} slot1={(HasMemoryCard(1) ? "Inserted" : "Empty")}";
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

    bool IGuestDeviceAccess.TryRead32(uint physicalAddress, out uint value) =>
        TryRead(physicalAddress, 4, out value) == PsxDeviceAccessStatus.Completed;

    bool IGuestDeviceAccess.TryWrite32(uint physicalAddress, uint value) =>
        TryWrite(physicalAddress, 4, value) == PsxDeviceAccessStatus.Completed;

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

# PS1 Hardware Runtime Architecture

Issue #44: Runtime/Hardware Abstraction Architecture for allowing Recompiled Code to use PS1 hardware other than the R3000A CPU.

## Overview

The PSXRecompStudio Runtime provides an abstraction layer through which Recompiled Code accesses PS1 hardware. Hardware implementations are not embedded directly into game-specific code; access goes through Domain interfaces.

### Prerequisites

- Issue #39 (R3000A CPU Domain) is complete.
- CPU Domain Models such as `R3000aDecoder` / `R3000aInstruction` exist.
- The C ABI boundary between `PSXRecomp.Core` (Domain layer) and `PSXRecomp.Native` (Infrastructure layer) is established.

## Architecture Policy

```text
Recompiled Code (generated C# code)
    ↓ Domain interface calls
PSXRecomp.Core (Domain layer: Hardware interface definitions)
    ↓ P/Invoke / C ABI
PSXRecomp.Native (Infrastructure layer: actual hardware implementations)
```

### Boundary Principles

1. **Recompiled Code references only IHardwareComponent interfaces.**
2. **The Domain layer is Pure.** (No File I/O, Console, DateTime.Now, or Environment.)
3. **The Infrastructure layer handles actual state changes and I/O.**
4. **Opaque pointers (IntPtr) are used across the C ABI boundary.**

## Hardware Component Model

All PS1 hardware components implement `IHardwareComponent`.

```csharp
[Domain]
public interface IHardwareComponent
{
    string Name { get; }
    void Reset();
    uint Read32(uint offset);
    void Write32(uint offset, uint value);
    ushort Read16(uint offset);
    void Write16(uint offset, ushort value);
    byte Read8(uint offset);
    void Write8(uint offset, byte value);
}
```

### Component List

| Component | Interface | Address Range | Interrupt |
|-----------|-----------|---------------|-----------|
| RAM | IMemoryBus (direct) | 0x00000000-0x007FFFFF (2MB, 8MB mirror) | None |
| Scratchpad | IMemoryBus | 0x1F800000-0x1F8003FF (1KB) | None |
| BIOS | IBios | 0x1FC00000-0x1FC7FFFF (512KB) | None |
| Interrupt Controller | IInterruptController | 0x1F801070-0x1F801074 | Central |
| DMA Controller | IDmaController | 0x1F801080-0x1F8010FF | IRQ3 |
| Timer 0-2 | ITimer | 0x1F801100-0x1F801128 | IRQ4-6 |
| Controller/MemCard | (future) | 0x1F801040-0x1F80105E | IRQ7 |
| CD-ROM | ICdRom | 0x1F801800-0x1F801803 | IRQ2 |
| GPU | IGpu | 0x1F801810-0x1F801814 | IRQ0 (VBlank), IRQ1 (GPU cmd) |
| MDEC | IMdec | 0x1F801820-0x1F801824 | None |
| SPU | ISpu | 0x1F801C00-0x1F801DFF | IRQ9 |
| GTE | IGte (COP2) | Coprocessor | None |
| Cache Control | IMemoryBus | 0xFFFE0130 | None |

## Memory / Bus Model

`IMemoryBus` routes physical addresses to the appropriate component.

```text
Physical Address
    ↓ Address Decode
┌─────────────────────────────────────────────────────┐
│ 0x00000000-0x007FFFFF: RAM (2MB, 8MB mirror)       │
│ 0x1F000000-0x1F07FFFF: Expansion Region 1          │
│ 0x1F800000-0x1F8003FF: Scratchpad (1KB Fast RAM)  │
│ 0x1F801000-0x1F801FFF: I/O Ports                   │
│   0x1F801070-0x1F801074: Interrupt                 │
│   0x1F801080-0x1F8010FF: DMA                      │
│   0x1F801100-0x1F801128: Timers                   │
│   0x1F801800-0x1F801803: CD-ROM                   │
│   0x1F801810-0x1F801814: GPU                      │
│   0x1F801820-0x1F801824: MDEC                     │
│   0x1F801C00-0x1F801DFF: SPU                      │
│ 0x1FC00000-0x1FC7FFFF: BIOS ROM (512KB)            │
│ 0xFFFE0130: Cache Control Register                  │
└─────────────────────────────────────────────────────┘
```

### Access Rules

- **Unmapped addresses**: reads return 0; writes are ignored (open bus).
- **RAM 16-bit/8-bit access**: direct byte/halfword access is allowed.
- **HW Register 16-bit/8-bit access**: partial writes to 32-bit registers.
- **BIOS**: read-only (ROM).
- **GetRamPointer()**: used for fast memory access from Recompiled Code.

## MMIO Model

Each hardware component owns its own register-offset address space.

1. `IMemoryBus.Read32(address)` receives a physical address.
2. Static checks in `Ps1MemoryMap` identify the component.
3. The corresponding `IHardwareComponent.Read32(offset)` is called.
4. offset = `address - component_base_address`.

## DMA Model

Manages seven DMA channels.

| Channel | Purpose | Direction | Sync Mode |
|---------|---------|-----------|-----------|
| 0: MDECin | MDEC input | FromRam (RAM→MDEC) | Slice (CHCR sync=1) |
| 1: MDECout | MDEC output | ToRam (MDEC→RAM) | Slice (CHCR sync=1) |
| 2: GPU | Rendering | Bidirectional | Burst/Slice/LinkedList |
| 3: CD-ROM | Sector read | FromRam (CD→RAM) | Burst (CHCR sync=0) |
| 4: SPU | Audio data | Bidirectional | Slice (CHCR sync=1) |
| 5: PIO | Expansion port | Bidirectional | Burst (CHCR sync=0) |
| 6: OTC | Reverse clear | ToRam (OTC→RAM) | Burst (CHCR sync=0) |

- On DMA completion, call `IInterruptController.Raise(IRQ3)`.
- OTC is dedicated to reverse clearing linked lists (for GPU OT).
- Channel 2 (GPU) supports linked-list mode.

## GTE (Geometry Transformation Engine) Model

Implemented as the COP2 coprocessor.

- **Data registers (32)**: vectors (V0-V2), intermediate values (IR0-3), screen coordinates (SXY0-2), Z values (SZ0-3), MAC accumulators (MAC0-3), colors (RGBC, RGB0-2).
- **Control registers (32)**: rotation matrix, light vector/color, projection-plane distance, clipping values.
- **Commands**: issued through COP2 instructions (`sf=shift fraction`, `lm=saturate`).
- **Major commands**: RTPS, NCLIP, AVSZ3, AVSZ4, SQR, NCCT, NCS, NCT, NCDS, NCDT, DPCL, DPCT, DPCS, DCT, INTPL, MVMVA, DCPL, DPCS, GPF, GPL, NCCT.

## GPU Model

Controlled through the two GP0/GP1 registers.

- **GP0 (0x1F801810)**: drawing commands, VRAM transfers, display-area configuration.
- **GP1 (0x1F801814)**: display control, reset, DMA-direction configuration.
- **GPUREAD (0x1F801810)**: reads GP0/GP1 results.
- **GPUSTAT (0x1F801814)**: GPU status register (read-only).
- **VBlank**: raises IRQ0 on vertical blank.
- **GPU IRQ1**: requested by GP0(1Fh), acknowledged by GP1(02h).

## SPU Model

A 24-voice audio synthesis engine.

- **Register space**: 0x1F801C00-0x1F801DFF.
- **Voices**: ADPCM decoding, ADSR envelope, pitch control.
- **Main volume/reverb**: stereo output control.
- **CD audio input**: receives audio data directly from the CD-ROM.
- **IRQ9**: raised when the sound buffer crosses the IRQ address.

## CD-ROM Model

Controls the CD-ROM controller.

- **Registers**: 0x1F801800-0x1F801803 (indexes 0-3).
- **Commands**: sector reads, seek, packet reads, CD audio.
- **IRQ2**: raised on command completion, data ready, or errors.
- **Modes**: Normal/Double speed, DMA/PIO.

## BIOS Model

A 512KB ROM BIOS.

- **Address**: 0x1FC00000-0x1FC7FFFF.
- **System calls**: GPU, SPU, CD-ROM, memory card, controller I/O.
- **Overlay**: functions are placed in memory.
- **Event handling**: callbacks for timers, DMA, and interrupts.

## MDEC (Motion Decoder)

JPEG decoding and motion-video decoding.

- **Registers**: 0x1F801820-0x1F801824.
- **DMA**: MDECin (ch0), MDECout (ch1).
- **State**: Busy, FIFO word count.

## Interrupt Model

Central interrupt controller.

```text
IRQ0: VBlank      (GPU vertical blank)
IRQ1: GPU command  (GPU command completion, GP1(02h) acknowledges)
IRQ2: CD-ROM       (CD-ROM)
IRQ3: DMA          (DMA Controller)
IRQ4: Timer 0      (Timer)
IRQ5: Timer 1      (Timer)
IRQ6: Timer 2      (Timer)
IRQ7: Controller   (Controller/Memory Card byte received)
IRQ8: SIO          (Serial Interface)
IRQ9: SPU          (Sound Processing)
IRQ10: PIO         (Expansion / Controller lightpen)
```

- **I_STAT (0x1F801070)**: interrupt status (write-0-to-clear).
- **I_MASK (0x1F801074)**: interrupt mask.
- **Condition**: `(I_STAT & I_MASK) != 0` → interrupt asserted.
- **Edge-triggered**: each bit is set when its interrupt source transitions from false to true.
- **IRQ Acknowledge**: clear the corresponding bit by writing 0 to I_STAT.

## Timer Model

Three hardware timers.

| Timer | Base | Clock Source | Primary Use |
|-------|------|--------------|-------------|
| Timer 0 | 0x1F801100 | Dot clock/scanline | GPU VBlank detection |
| Timer 1 | 0x1F801110 | Horizontal retrace | Display synchronization |
| Timer 2 | 0x1F801120 | System clock/8 | General-purpose timer |

- **MODE register**:
  - Bit 0: synchronization enable (0=Free Run, 1=Synchronize).
  - Bit 1-2: synchronization mode (counter reset/pause condition).
  - Bit 3: reset condition (0=when FFFFh is reached, 1=when Target is reached).
  - Bit 4: IRQ on Target reached (0=disabled, 1=enabled).
  - Bit 5: IRQ on FFFFh reached (0=disabled, 1=enabled).
  - Bit 6: One-shot/Repeat (0=One-shot, 1=Repeat).
  - Bit 7: Pulse/Toggle (0=Pulse, 1=Toggle).
  - Bit 8-9: clock source.
- **COUNT register**: 16-bit counter value (0-FFFFh).
- **TARGET register**: 16-bit target value.

## Timing Model

- **CPU clock**: 33.8688 MHz.
- **1 CPU cycle**: 1 clock (approximately 29.5ns).
- **HBlank**: 15.734 kHz (63.5μs per scanline).
- **VBlank**: 59.94 Hz (16.68ms per frame).
- **DMA transfer**: consumes 1-2 cycles per word.
- **GTE commands**: generally several clocks per iteration.
- **SIO (Controller/Memory Card)**: baud-rate dependent.

### Synchronization Policy

- CPU execution is synchronized by cycle count.
- Hardware events are processed at cycle boundaries.
- DMA runs in parallel with CPU execution but stalls on bus contention.
- Timers count in synchronization with CPU cycles.

## Recompiled Code ↔ Runtime ABI

```text
Recompiled Code
    │
    ├── Load/Store → IMemoryBus.Read32/Write32
    ├── COP2 (GTE) → IGte.ExecuteCommand
    ├── System Call → IBios (via BIOS)
    └── I/O Check → address check on memory access
                      │
                      ├── RAM → direct pointer access
                      └── MMIO → IHardwareComponent.Read/Write
```

### Performance Optimization

- **Direct RAM access**: obtain a pointer with `GetRamPointer()` and access memory directly using Unsafe code.
- **MMIO delayed check**: execute the RAM address-range check on the shortest path.
- **Hardware Inlining**: inline frequently accessed registers.

## Multi-Platform Runtime Extension Policy

- Interfaces such as `IHardwareComponent` / `IMemoryBus` are platform-independent.
- The Infrastructure layer (`PSXRecomp.Native`) provides platform-specific implementations.
- The C# side depends only on Domain interfaces.
- Future extensions:
  - GPU backends (Vulkan, OpenGL, DirectX).
  - Audio backends (SDL2, CoreAudio).
  - Timing backends (high-precision timers, frame pacing).
  - Networking (multiplayer).

## Acceptance Criteria Status

| Criteria | Status |
|----------|--------|
| Define the hardware component model | ✅ IHardwareComponent + all interfaces |
| Define the boundary between Recompiled Code and Runtime | ✅ Section: Recompiled Code ↔ Runtime ABI |
| Define the MMIO / memory-access policy | ✅ Section: MMIO Model, Memory / Bus Model |
| Define the timing / synchronization policy | ✅ Section: Timing Model |
| Define the BIOS interaction policy | ✅ Section: BIOS Model |
| Organize responsibilities for GTE/GPU/SPU/CD/DMA, etc. | ✅ Defined in each section |
| Consider future multi-platform Runtime support | ✅ Section: Multi-Platform Runtime Extension Policy |

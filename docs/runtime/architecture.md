# PS1 Hardware Runtime Architecture

Issue #44: Runtime/Hardware Abstraction Architecture for allowing Recompiled Code to use PS1 hardware other than the R3000A CPU.

## Overview

The PSXRecompStudio Runtime keeps PS1 hardware semantics out of game-specific code. There is no single common hardware-component interface: production guest memory/MMIO is decoded by the native/Rust memory path, while focused Domain contracts and managed adapters are used where they are the correct boundary (for example BIOS HLE orchestration and the managed GPU model).

### Prerequisites

- Issue #39 (R3000A CPU Domain) is complete.
- CPU Domain Models such as `R3000aDecoder` / `R3000aInstruction` exist.
- The C ABI boundary between `PSXRecomp.Core` (Domain layer) and `PSXRecomp.Native` (Infrastructure layer) is established.

## Architecture Policy

```text
Application / generated-host composition
    ↓ explicit Domain / runtime contracts
PSXRecomp.Core
    ├── BIOS HLE / orchestration / managed GPU contracts
    └── PSXCoreWrapper
            ↓ P/Invoke / stable C ABI
PSXRecomp.Native
    └── PSXCpu → PsxMemory → Rust-owned memory/MMIO devices
```

### Boundary Principles

1. **No title-specific hardware implementation is embedded in generated code.**
2. **The Domain layer is Pure.** (No File I/O, Console, DateTime.Now, or Environment.)
3. **Each device has one authoritative semantic owner.** Managed seams either own the device explicitly (GPU) or forward to the native/Rust SSOT (for example SIO0/SPU).
4. **The native boundary remains the stable C ABI.** Rust is linked inside the same native library; Rust/C++ implementation details do not leak into managed callers.
5. **Opaque native handles are used only where stateful native ownership requires them.**

## Hardware Component Model

The old common `IHardwareComponent` abstraction was removed because it had no production consumers. Current hardware boundaries are intentionally device-specific.

- Production CPU loads/stores execute through native `PSXCpu` → `PsxMemory`.
- RAM, scratchpad, BIOS bytes and Rust-owned MMIO devices are decoded by `PsxMemory`.
- Interrupt, DMA, Timer, SIO0 and SPU semantics are owned by native/Rust state reached through that production memory boundary.
- The GPU remains a managed Domain model (`IGpu` / `GpuDevice`). Production interpreter 32-bit GPU MMIO is forwarded through a narrow native callback seam to the same `GpuMmioAdapter` / `GpuDevice` state (#572); native/Rust does not duplicate GPU semantics.
- `IMemoryBus` / `MemoryBus` is a managed routing seam used by BIOS HLE/tests and focused adapters; it is not a universal base interface implemented by every device.
- BIOS-less execution is modeled through `IBiosRuntime` / `BiosHleRuntime`; there is no live `IBios` hardware-component interface.

### Component List

| Component | Current boundary / implementation | Address Range | Interrupt |
|-----------|-----------------------------------|---------------|-----------|
| RAM | native/Rust `PsxMemory`; managed `IMemoryBus` seam | 0x00000000-0x007FFFFF (2MB, 8MB mirror) | None |
| Scratchpad | native/Rust `PsxMemory`; managed `IMemoryBus` seam | 0x1F800000-0x1F8003FF (1KB) | None |
| BIOS address space | `PsxMemory` backing bytes when supplied; normal execution uses `IBiosRuntime` HLE instead of requiring a BIOS ROM | 0x1FC00000-0x1FC7FFFF (512KB) | None |
| Interrupt Controller | IInterruptController | 0x1F801070-0x1F801074 | Central |
| DMA Controller | IDmaController | 0x1F801080-0x1F8010FF | IRQ3 |
| Timer 0-2 | ITimer | 0x1F801100-0x1F801128 | IRQ4-6 |
| Controller/MemCard | native/Rust `crate::sio0` (register model, Issue #542; minimal disconnected-pad protocol, Issue #543) | 0x1F801040-0x1F80105E | IRQ7 (byte received; driven for the disconnected-slot protocol only) |
| CD-ROM | ICdRom | 0x1F801800-0x1F801803 | IRQ2 |
| GPU | IGpu | 0x1F801810-0x1F801814 | IRQ0 (VBlank), IRQ1 (GPU cmd) |
| MDEC | IMdec | 0x1F801820-0x1F801824 | None |
| SPU | native/Rust `crate::spu` register store (Issue #445; `ISpu` remains the higher-level contract) | 0x1F801C00-0x1F801DFF | IRQ9 (not yet driven) |
| GTE | IGte (COP2) | Coprocessor | None |
| Cache Control | IMemoryBus | 0xFFFE0130 | None |

The Controller/MemCard row is SIO0. `crate::sio0` (native Rust, owned inline
by `PsxMemory`; see `docs/development/rust-ffi-contract.md`'s "Guest memory
(#492)" section) implements the guest-visible register file: deterministic
SIO_DATA/SIO_STAT/SIO_MODE/SIO_CTRL/SIO_BAUD read/write semantics and a fixed
idle value for reserved addresses in the window.

On top of that register file, Issue #543 adds a minimal controller serial
protocol: while a port is selected (`SIO_CTRL.1`), every transaction byte
written to SIO_DATA gets a deterministic disconnected-slot response
(`0xFF` — no device's `/ACK` ever drives the line low, matching real SIO0
hardware with nothing connected) queued into the RX FIFO, and signals IRQ7
("byte received"). This is not real hardware ACK timing (out of scope): the
model treats each transfer as instantaneous and always signals completion,
so IRQ7 fires for every transaction byte while selected, not only when a
real device would pull `/ACK` low. There is still no controller/memory-card
*transaction* protocol (DualShock/analog, memory-card read/write, multitap)
and no real host controller input reaches this component — both remain
tracked as a child of #443. `PSXRecomp.Core.Runtime.DeviceScheduler.Advance`
delivers IRQ7 the same way it delivers Timer/DMA interrupts: it polls
`PSXCoreWrapper.GetSio0InterruptPending`, clears it, and raises IRQ7 on the
interrupt controller.

SIO0 was originally a pure managed model (`Sio0Device`/`Sio0State`/
`Sio0MmioAdapter`, the GPU precedent, ADR-022), reachable only through the
managed `MemoryBus` test/BIOS-HLE seam — not from a real guest CPU
`LW/SW/LH/SH/LB/SB`, which executes through the native interpreter
(`InterpreterTitleExecutionEngine` → `PSXCoreWrapper` → native `PSXCpu` →
native `PSXMemory`) without ever going through `MemoryBus`. A CodeRabbit
review on PR #548 caught this gap; the fix moved the register semantics into
native Rust. At Issue #542 this component had no controller protocol or IRQ7
in scope. Issue #543 adds the minimal disconnected-pad protocol and delivers
IRQ7 through a poll/clear pair on the `PsxMemory` handle, so SIO0 still needs
no `PSXCore`-owned state or `AttachControllers`-style pointer — see the
FFI contract doc and the Issue #543 paragraph above. `MemoryBus`'s
SIO0 case now calls `PSXCoreWrapper.ReadMemory32`/`WriteMemory32` (the same
native entry point the CPU step path uses) instead of a managed adapter, so
the managed test/BIOS-HLE seam and the production CPU path observe one SSOT.

Memory-card **storage** — the 128 KiB card file, its format, and the slot
configuration that selects it — is a separate, implemented subsystem documented
in [Memory Card Format and Storage Policy](memory-card.md). It supplies the card
content this component would carry; it is not hardware emulation.

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
│   0x1F801040-0x1F80105E: SIO0 (Controller/MemCard) │
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

There is no common `IHardwareComponent.Read/Write` dispatch layer.

- **Production guest CPU path:** `InterpreterTitleExecutionEngine` → `PSXCoreWrapper` → native `PSXCpu` → `PsxMemory`. `PsxMemory` decodes RAM/scratchpad/BIOS/MMIO and forwards Rust-owned device windows to their device modules.
- **Managed routing seam:** `MemoryBus` uses `Ps1MemoryMap` / `MmioRoute` to classify addresses for BIOS-HLE/tests and managed-owned devices. SIO0/SPU forwarding reaches the same native memory SSOT instead of maintaining duplicate semantics.
- **GPU bridge:** the GPU register/VRAM model remains managed and routed through `GpuMmioAdapter` + `MemoryBus`. Production native-interpreter 32-bit accesses to the GPU port window are forwarded to that same adapter through the #572 callback seam; DMA2 and GPU IRQ1 integration remain #440 items.

Subword semantics are defined by the owning device/memory implementation; they are not inherited from a deleted common base interface.

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

### Runtime implementation (Issue #440)

The register/VRAM contract is implemented as a pure managed Domain model in
`PSXRecomp.Core.Runtime.Gpu` (`GpuDevice`, `GpuVram`, `GpuState`,
`Gp0CommandDecoder`) and reaches the live `0x1F801810-0x1F80181C` ports through
`GpuMmioAdapter` + `MemoryBus`, following the Timer/Interrupt adapter pattern.
GPUSTAT is derived from named state (see ADR-022), VRAM is 1024x512x16b, and
unimplemented GP0 opcodes are reported explicitly rather than silently ignored.

### Rasterization and frame snapshot (Issue #441, partial)

`GpuRasterizer` renders flat and Gouraud-shaded triangles and flat rectangles
(non-textured, non-quad) into `GpuVram` synchronously when `GpuDevice`
completes a GP0 drawing-primitive command; texture mapping and quads are
recognized but explicitly reported as `GpuRasterOutcome.UnsupportedFeature`
rather than mis-rendered. `FrameSnapshot.Capture` (also reachable via
`GpuDevice.CaptureFrame`) is a pure function of the current VRAM and the
GP1(05h)/display-resolution state that returns a deterministic,
presentation-agnostic capture of the configured display region, plus a
SHA-256 stable-content hash — independent of VBlank/scheduler timing.

VBlank IRQ0 is raised by the device scheduler (see
[Device Scheduling](#device-scheduling-issue-442)), not by `GpuDevice`;
`IGpu.HasVblank` stays false. Production guest 32-bit GPU MMIO reaches this
same managed device through #572, and Issue #574 wires the existing GP0(1Fh)
command-interrupt source to scheduler-delivered IRQ1 without duplicating GPU
state.

Not yet implemented: texture mapping, quads, line primitives,
semi-transparency blending, dithering, mask-bit checking, VRAM→VRAM blit, and
DMA channel 2 (GPU) consumption of the DMA controller. `InterpreterTitleExecutionEngine.CaptureFrame()` exposes that same production
GPU/VRAM state, and `psxrecomp run --frame-evidence` emits deterministic
width/height/SHA-256 headless evidence (#575).

## SPU Model

The first SPU slice (Issue #445) is deliberately **register/MMIO only**. The
guest-visible `0x1F801C00-0x1F801DFF` window is backed by native Rust
`crate::spu::SpuState`, owned inline by `PsxMemory` so real CPU load/store
instructions and the managed `MemoryBus` seam observe the same state.

- **Register space**: 0x1F801C00-0x1F801DFF, 256 deterministic 16-bit registers.
- **Voice range**: 0x1F801C00-0x1F801D7F.
- **Global/control range**: 0x1F801D80-0x1F801DBF.
- **Reverb-register range**: 0x1F801DC0-0x1F801DFF.
- **Access widths**: byte lanes, aligned 16-bit registers, and aligned 32-bit pairs are preserved little-endian.
- **Reset**: all modeled register storage returns to zero.
- **No audio semantics yet**: ADPCM decode, ADSR, pitch stepping, mixing,
  reverb DSP, sound RAM transfer behavior, CD audio, and host audio output are
  not implemented or claimed.
- **IRQ9**: not modeled in this slice; the `ISpu` interrupt surface remains a
  future behavior seam.

This is intentionally a storage/observability contract rather than a fake
audio implementation: BIOS/game initialization writes no longer disappear into
the generic hardware-register fallback, while unsupported sound behavior is
still absent rather than guessed.

## CD-ROM Model

Controls the CD-ROM controller.

- **Registers**: 0x1F801800-0x1F801803 (indexes 0-3).
- **Commands**: sector reads, seek, packet reads, CD audio.
- **IRQ2**: raised on command completion, data ready, or errors.
- **Modes**: Normal/Double speed, DMA/PIO.

## BIOS Model

Normal user execution is BIOS-less by default. BIOS calls are represented by
`IBiosRuntime` / `BiosHleRuntime` and identified by A0/B0/C0 family plus
function number; supported services implement their guest-visible behavior and
unsupported services fail explicitly with `BIOS_HLE_UNSUPPORTED_CALL`.

`PsxMemory` can still expose the 0x1FC00000-0x1FC7FFFF BIOS address range when
backing bytes are supplied, but a dumped Sony BIOS ROM is not a mandatory
runtime dependency and is never distributed by this repository. Guest-visible
jump-table state used by the HLE contract lives in ordinary guest RAM, not in a
deleted `IBios` component.

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

### Device Scheduling (Issue #442)

`PSXRecomp.Core.Runtime.DeviceScheduler` is what advances devices during a real
run. Responsibilities are split so no device behavior is duplicated:

- **Elapsed time comes from the engine.** `InterpreterTitleExecutionEngine`
  calls `Advance(CyclesPerInstruction)` after every retired `Step()`. The native
  interpreter has no cycle model, so its retired-instruction count is the time
  source (1 instruction = 1 cycle). The scheduler keeps no clock of its own —
  only the phase inside the current VBlank interval and the DMA IRQ line's last
  level. BIOS HLE vector dispatch retires no instruction and advances nothing.
- **Device semantics stay native/Rust.** Timers advance via `PSXCore_TickTimers`
  and DMA via `PSXCore_TickDma`; IRQs are raised through
  `IInterruptController.Raise` onto the Rust interrupt controller.
- **IRQs are taken as CPU interrupts (Issue #499).** The engine steps with
  `PSXCore_Step`, so a guest that unmasks I_MASK and sets SR IEc/IM2 takes an
  INT exception and its handler at 0x80000080 runs, acknowledges I_STAT and
  returns through `MFC0 EPC` / `JR` / `RFE`. With IEc clear the IRQ only
  latches in I_STAT, where the guest can poll it. Only a hardware INT
  continues; other exceptions (SYSCALL, BREAK, faults, software interrupts)
  still end the segment as `CPU_EXCEPTION`. See `docs/cpu/exceptions.md`.
- **Fixed order per `Advance`:** Timers (a latched timer IRQ is consumed and
  raised as IRQ4-6) → DMA (IRQ3 on a rising edge of DICR bit 31) → SIO0 (IRQ7)
  → GPU command IRQ (IRQ1 on the rising edge of GP0(1Fh)'s source) → VBlank
  (IRQ0 every `VblankIntervalCycles` = 33,868,800 / 60 = 564,480 cycles).
  GP1(02h) deasserts the GPU source; the already-latched I_STAT bit remains
  independently guest-acknowledged (#574).
- **DMA completion model:** a started channel (CHCR bit 24, DPCR enable, and bit
  28 for sync mode 0) completes after one cycle per word (sync 0: BCR[15:0];
  sync 1: size × count; linked list: one word, since its length lives in guest
  RAM). Completion clears CHCR bits 24/28 and sets the channel's DICR flag when
  enabled. No data is transferred; device-backed transfers (GPU DMA2, OTC
  clearing, CD-ROM, SPU, MDEC) remain device work.

Not modelled: cycle-exact timing, HBlank, and Timer 0/1 blank sync lines.
The generated-host engine (`HostTitleExecutionEngine`, test-only) carries
guest MMIO as a flat register window across processes and has no native device
state to schedule, so it is not wired.

## Recompiled Code ↔ Runtime ABI

```text
Execution / generated-host composition
    │
    ├── BIOS service → IBiosRuntime / BiosHleRuntime
    ├── production CPU load/store
    │       → PSXCoreWrapper → stable C ABI
    │       → PSXCpu → PsxMemory
    │             ├── RAM / scratchpad / BIOS backing
    │             └── Rust-owned MMIO devices
    ├── managed BIOS-HLE/test memory seam → IMemoryBus / MemoryBus
    └── managed GPU seam → IGpu / GpuDevice
            ├── production guest MMIO bridge (#572)
            ├── scheduler IRQ1 delivery (#574)
            └── production FrameSnapshot / headless evidence (#575)
```

The exact generated-host execution contract is documented separately; this diagram records ownership boundaries rather than claiming that every execution backend uses the same memory-call shape.

### Performance Optimization

- **Direct RAM access**: obtain a pointer with `GetRamPointer()` and access memory directly using Unsafe code.
- **MMIO delayed check**: execute the RAM address-range check on the shortest path.
- **Hardware Inlining**: inline frequently accessed registers.

## Multi-Platform Runtime Extension Policy

- Device-specific Domain contracts and `IMemoryBus` are platform-independent where they are used.
- The native library hides C++/Rust implementation choices behind the stable C ABI.
- Managed code depends on explicit Domain/runtime contracts rather than a universal hardware base interface.
- Future extensions:
  - GPU backends (Vulkan, OpenGL, DirectX).
  - Audio backends (SDL2, CoreAudio).
  - Timing backends (high-precision timers, frame pacing).
  - Networking (multiplayer).

## Acceptance Criteria Status

| Criteria | Status |
|----------|--------|
| Define the hardware component model | ✅ Device-specific ownership/boundaries; obsolete common `IHardwareComponent` removed |
| Define the boundary between Recompiled Code and Runtime | ✅ Section: Recompiled Code ↔ Runtime ABI |
| Define the MMIO / memory-access policy | ✅ Section: MMIO Model, Memory / Bus Model |
| Define the timing / synchronization policy | ✅ Section: Timing Model |
| Define the BIOS interaction policy | ✅ Section: BIOS Model |
| Organize responsibilities for GTE/GPU/SPU/CD/DMA, etc. | ✅ Defined in each section |
| Consider future multi-platform Runtime support | ✅ Section: Multi-Platform Runtime Extension Policy |

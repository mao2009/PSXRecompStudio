# ADR-022: GPU runtime is a pure managed model with a psx-spx GPUSTAT layout

- Status: Accepted
- Date: 2026-09-18
- Issue: #440

## Context

`IGpu` existed as an interface with no implementation. The first implementable
unit for Issue #440 is the GP0/GP1/GPUREAD/GPUSTAT register contract, a VRAM
buffer, and CPU↔VRAM transfers, without rasterization.

Two decisions are not forced by the interface and would otherwise be re-litigated
by every later GPU change:

1. **Where GPU state lives.** The DMA/Timer devices already have managed
   adapters over the native core (`PSXRecomp.Core.Dma`). The GPU has no native
   counterpart (`src/PSXRecomp.Native/src` has no `psx_gpu`), and its state
   (1 MiB VRAM, a handful of registers, a packet state machine) is ordinary
   managed data. Adding a native GPU core now would create a C ABI surface and a
   second source of truth before any rasterizer exists.

2. **Which GPUSTAT bit layout to derive.** Two layouts describe the same
   hardware: the DuckStation/Mednafen-style layout (with a v1-only screen-flip
   bit and version bit) and the psx-spx/nocash layout. They place the vertical
   interlace and display-enable bits differently past bit 19.

## Decision

1. **The GPU is a pure managed Domain model** under
   `PSXRecomp.Core.Runtime.Gpu` (`GpuDevice`, `GpuVram`, `GpuState`,
   `Gp0CommandDecoder`). `PSXRecomp.Core.Dma.GpuMmioAdapter` bridges
   address → `IMemoryBus` → MMIO route → device, following the existing
   Timer/Interrupt adapter pattern. No native GPU core is introduced.

2. **GPUSTAT uses the psx-spx/nocash bit layout.** Bits 0-15 are draw settings,
   16 hres2, 17-18 hres1, 19 vres, 20 video mode, 21 color depth, 22 interlace,
   23 display-enable (1 = disabled), 24 IRQ1, 25 data-request, 26 cmd-ready,
   27 VRAM→CPU ready, 28 DMA-block-ready, 29-30 DMA direction, 31 drawing
   even/odd. Every bit is derived from named `GpuState` fields (plus transient
   packet flags), never from a magic status word. The documented power-on value
   `0x14802000` (bits 13, 23, 26, 28) is reproduced at reset.

3. **VRAM is not cleared by GPU reset.** `GpuVram` is deterministically zeroed
   at construction; `GpuDevice.Reset()` (GP1(00h)) resets registers and the
   command buffer but preserves VRAM contents, matching real hardware. `Clear()`
   exists for explicit, deterministic re-initialization.

4. **Unimplemented commands are explicit.** GP0 decode returns one of
   `Executed`, `DecodedPendingRasterization`, `RecognizedNotImplemented`, or
   `Unsupported` on `LastResult`/`LastResultOpcode`. Nothing silently no-ops
   (consistent with the fail-closed policy from #279/#351/#377).
   Two packet shapes publish their result later than the command word:
   a variable-length polyline (GP0 48h/58h family) reports `Unsupported`
   immediately and then *consumes* its payload words — they are never decoded
   as fresh commands — until the `0x50005000` terminator rule fires; a
   CPU→VRAM transfer publishes `Executed` only once its data phase is fully
   consumed, so an in-progress transfer is never reported as completed.

5. **Deferred by design.** Primitive rasterization and display output, the
   VRAM→VRAM blit (GP0 80h) body, VBlank/IRQ0 scheduling, and DMA channel 2
   consumption of the DMA controller are out of scope and tracked as dependent
   follow-ups (`LastPrimitive`, `HasVblank`, `GpuCommandResult` are the seams).

## Consequences

### Positive

- No C ABI churn for a component with no rasterizer yet; state is testable
  without the native core.
- One documented GPUSTAT layout, internally consistent with the reset value, so
  later rasterization/scheduling work derives from the same named state.
- Fail-closed command classification makes unsupported-opcode regressions
  visible in tests instead of silent.

### Costs / constraints

- A future native or accelerated backend must interoperate with, or replace,
  the managed model; the `IGpu` port is the substitution point.
- Until VBlank/IRQ0 and DMA2 wiring land, GPU interrupts do not reach the
  Interrupt Controller and title code that polls GPUSTAT bit 31 (drawing
  even/odd) sees 0.
- GPUSTAT bit 14 (screen flip, v1-only) is always 0 on the modeled v2 GPU.

## Rejected alternatives

### DuckStation/Mednafen GPUSTAT layout

Rejected because its bit placement is not internally consistent with the
documented reset value `0x14802000` used by this repository, and it carries
v1-only bits (screen flip, version) that the modeled v2 GPU does not have.
Adopting it would require either faking those bits or documenting a reset value
that does not match.

### Native `psx_gpu` C core mirroring the DMA/Timer pattern

Rejected as premature: it adds a C ABI and duplicated state before any
rasterizer exists, and the interface lets a native backend be substituted later
without changing callers.

### Clear VRAM on GPU reset

Rejected because real hardware does not; deterministic tests get a zeroed VRAM
from construction and an explicit `Clear()` instead.

### Treat every decoded-but-unrendered primitive as unsupported

Rejected because the packets are valid hardware commands that simply await the
rasterizer (#441). They are accumulated and exposed via `LastPrimitive` rather
than misreported as errors.

# OpenBIOS: firmware backend (initial execution slice)

Tracking: [#732](https://github.com/mao2009/PSXRecompStudio/issues/732), [#730](https://github.com/mao2009/PSXRecompStudio/issues/730).

## Chosen architecture

**OpenBIOS owns BIOS behavior now.** A distinct `IBiosBootBackend` allows a future
independent HLE backend to replace it; it is **not** a fallback from unsupported
OpenBIOS instructions to incomplete HLE stubs.

Nugget OpenBIOS: https://github.com/pcsx-redux/nugget at pinned revision
`c950e18a168944ec2d4e6d3c408fc224317483a7`. Build its ROM locally using
the upstream toolchain and instructions from `openbios/README.md`. Do not
commit `openbios.bin`, `openbios.elf`, any commercial BIOS or game data. A
future source/binary distribution must complete #730's **file-by-file** license,
dependencies and NOTICE audit (an MIT repo badge alone is insufficient).

### Local OpenBIOS build

```bash
# Checkout Nugget at the exact pinned revision, once, outside this repository:
git clone https://github.com/pcsx-redux/nugget.git /your/nugget
git -C /your/nugget checkout c950e18a168944ec2d4e6d3c408fc224317483a7
bash scripts/openbios/build.sh /your/nugget
psxrecomp openbios-probe out/openbios/openbios.bin --json
```

The helper does not vendor code, checks the revision and tracked-worktree state,
builds using upstream's Docker wrapper, verifies the 512 KiB output, copies the
observed MIT LICENSE beside the local ROM, and records its SHA-256 and Docker
image digest under the git-ignored `out/` folder. Upstream's Docker wrapper
uses an unpinned `:latest` image: source is pinned, **builder is not**; an
independent toolchain pin and end-to-end compatibility proof are still required.
Local building does not itself approve distribution.

### Executable slice

`psxrecomp openbios-probe /path/to/openbios.bin --segment-budget 100000 --segments 10 --json`

This command:
- validates the full 512 KiB ROM; loads all MIPS words at the PS1 BIOS address
  `0xBFC00000` (physical `0x1FC00000`) in the **existing native memory**;
- executes from reset vector using the same CPU/GPU/CD-ROM/SIO0/IRQ/scheduler;
- allows the OpenBIOS kernel to run dynamically written low RAM, not just ROM;
- does **not** construct `BiosHleRuntime`, synthesize A0/B0/C0 service
  results or install proprietary BIOS data;
- reports observable milestones (`OpenBiosBootMonitor`): reset vector fetched, kernel
  running from low RAM, 0x80/A0/B0/C0 stubs installed, a guest exception (SYSCALL / IRQ
  counted separately) delivered to 0x80, shell entered at 0x80030000. Exit 0 and
  `kernelBooted:true` only when all hold; `bootVerified:true` additionally needs
  `titleStarted` (control left the shell for user RAM outside the shell image). Running a
  fixed number of instructions is never evidence. `--capture-at <pc>` snapshots registers
  and recent transfers when a PC is first fetched; a stop prints PC, word, COP0, GPRs,
  recent fetches/transfers, and the first non-IRQ/non-SYSCALL exception (ExcCode/EPC).

Measured with the pinned build (`openbios.bin` sha256
`96889cfc16e3637e58e3b709a8de4f47725a4c9f2cafde57e0c24dc075f5cfb7`, GCC 16.2.0; a clean
rebuild reproduced the hash): without a disc the kernel reaches the shell after 709,142
fetches (1 SYSCALL, 0 IRQ) and the shell idles waiting for a disc.

First stops found and fixed on the way (each at the shared owner, no faked BIOS result):
- PC 0xFFFFFFD0 (A0 vector word zeroed): the native CPU lacked `SR.IsC`, so `flushCache`
  stores of zero landed in RAM over the freshly installed vectors.
- shell `waitVSync` / disc sequence: see the disc section below.

Persona (user-provided CHD, not in the repo), real OpenBIOS: kernel boots, the shell accepts
the disc, the firmware loads the executable through its own CD code and enters it at
0x80011930 (`titleStarted:true`, 566 IRQ and 31 SYSCALL entries delivered). First stop:
CpU (ExcCode 11, CE=2 = COP2/GTE) at EPC 0x800861DC; OpenBIOS reports it as an unresolved
exception (A0:40) and halts. The native CPU raised CpU for every COP2 instruction by
design. With COP2 execution (Issue #447) that word (`CTC2 $t0,$29`, the start of the title's
GTE initialisation: ZSF3/ZSF4/H/DQA/DQB/OFX/OFY) and the rest of it execute; the run no longer
faults (`FirstUnexpectedException` null). After 4000 x 1M instructions the GTE control registers
hold exactly what that code wrote (ZSF3 0x155, ZSF4 0x100, H 1000, DQA -4194, DQB 0x01400000,
OFX/OFY 0). The next stop is not GTE: the title spins in a bounded retry loop at
0x800812E8 -> 0x8008A014 polling the status halfword of a CD sector-buffer slot that its
DMA3/CD-ROM interrupt state machine (state word 0x800A77E0, DMA3 CHCR 0x11000000) never advances;
23,489 IRQs are delivered, 0 GTE commands have executed, and the displayed frame (probe `frame`)
is 320x240 with no non-zero pixel. No title screen was reached.

That wait and the stops after it were Runtime hardware gaps (Issue #732), traced through the
guest's CD-ROM/DMA/GPU/MDEC register traffic:

1. The loop is the movie player's stream ring (SetLoc 26:05:31, SetMode C0h, ReadS). Every 8th
   sector is XA audio (submode 64h); with SetMode bit 6 the drive must route it to the SPU,
   but the model raised INT1 for it, so the stream library parsed ADPCM as a video sector
   header and its ring stalled after 9 sectors. Fixed in `CdRomDevice` (no audio decoder).
2. Next, DrawSync at 0x80084B58 waited on GPUSTAT bit 26: each decoded frame is uploaded with
   GP0(A0h) + DMA2 block mode, and the DMA model moved no data. Fixed by `GpuDmaTransfer`.
3. Next, DecDCTinSync at 0x80081BC8 polled MDEC status 0x1F801824, a flat register with no
   device behind it. Fixed by `MdecDevice` + `MdecDmaTransfer`.
4. The DICR layout was also corrected to psx-spx (the title enables channels 1/3/4 with
   DICR 009A0000h and now sees its MDEC-out flag and IRQ3).

After these the intro movie plays: VRAM holds the decoded 24-bit ATLUS logo frame and the probe
frame reports 29,609 non-zero pixels (the probe's frame evidence reads VRAM as 15-bit, so a
24-bit display looks striped there). This is a VRAM/probe observation, not a verified window presentation or a title-screen result. The run at 600 x 1M instructions is mid-movie, waiting on
the stream at 0x800812E8 as designed between frames; no title screen yet.

### Disc in the drive (`--disc`)

`psxrecomp openbios-probe <rom> --segment-budget 1000000 --segments 600 --disc <image.chd|image.bin> --json`

The firmware reads the disc only through the existing `CdRomDevice` / DMA3 /
IRQ2 / `DeviceScheduler` graph; no BIOS function is implemented or answered on
its behalf. A disc is an `ICdSectorSource` (TOC plus raw 2352-byte sectors by
LBA; an absent sector is reported, never zero-filled): `ChdCdSectorSource`
over the existing `ChdReader`, or `RawCdSectorSource` over a single-track
`.bin`. With a disc the controller is hardware-timed: responses arrive after the
acknowledge/second-response delays (psx-spx timing table, DuckStation's
acknowledge delay), only after the previous interrupt was acknowledged, and
ReadN/ReadS stream one INT1 per sector at 75/150 sectors/s; a sector enters the
data FIFO on the request register's BFRD write. Without a disc the legacy
BIOS-less model is unchanged. GPUSTAT bits 13/31 alternate per VBlank field,
which the shell's `waitVSync` polls.

Observed with the pinned build and a synthetic Mode 2 disc (ISO 9660,
`SYSTEM.CNF`, a 6-instruction PS-X EXE; no commercial data): the shell accepts
the disc, schedules its boot, returns; the kernel runs `initCDRom`, opens
`cdrom:SYSTEM.CNF;1`, loads `\TEST.EXE;1` through CD reads and DMA3, and
`gameMainThunk` → `exec` enters the executable's entry `0x80010000` (about 192M
instructions), which then writes its marker to RAM. Known gaps: the CHD adapter
reports a single data track (CHT2 metadata not parsed); seek time is a fixed
constant; no XA/CD-DA audio (XA sectors are routed away from the CPU and dropped).

### Generated host (`--engine generated-host`)

`psxrecomp openbios-probe <rom> --segment-budget 1000000 --segments 300 --disc <image> --engine generated-host --roots <file> [--code-bytes <n>] [--differential] [--json]`

The ROM's code (its first `--code-bytes`, e.g. `.text` + `.text_memcpy` =
37160 bytes for the pinned build) is built by
`ReachableProgramBuilder.BuildFirmwareImage` from the reset vector plus the
explicit `--roots` (one hex PC per line; for example the ELF's `T`/`t` symbols
in the ROM code range, extracted with `nm` — the CLI does not parse ELF),
compiled with `gcc` and run by `RecompiledHostExecutionEngine` in firmware mode
(see [host codegen](../development/recompiler-host-codegen.md)). Kernel code
copied to RAM, the `0x80`/A0/B0/C0 vectors, the shell and the loaded executable
have no block and run in the mixed-execution interpreter over the same device
graph; the report separates `nativeInstructions` from `fallbackInstructions`.
Milestones come from the fallback interpreter's fetches (native blocks are not
observed per instruction; the reset vector is the build's first dispatch unit).
`--differential` first runs the interpreter backend and compares both at the
first fetch of `0x80030000` (shell) and `0x80010000` (the executable entry):
GPRs, SR/CAUSE/EPC/BadVAddr and all 2 MiB of RAM (SHA-256, first mismatching
address, mismatching pages).

Observed with the pinned build and the synthetic disc: the generated host boots
the kernel, enters the shell and reaches `0x80010000` with the interpreter's
milestones (111 INT and 9 SYSCALL vector entries). 9228 of 9290 code words are
native; about 13.8M instructions retire natively and 178M in the fallback
before the executable entry, over 712k artifact/interpreter transitions (mostly
the shell's B0 calls). The page-granular RAM copy-sync costs about 0.9 ms per
transition and dominates the ~15 minute run. `--differential` reports an
exact match at both boundaries: identical GPRs, SR/CAUSE/EPC/BadVAddr and RAM
SHA-256 (no differing range). The executable's final `b .` loop runs in the
fallback until the budget, which ends the run as
`ARTIFACT_FALLBACK_BUDGET_EXHAUSTED`; that is the probe's end, not a failure
of the boot.

The existing `psxrecomp run` pipeline still uses its legacy HLE path until the
OpenBIOS firmware can actually load a game via the same guest memory and handle
all required hardware and generated-host transitions. **Do not change the default
by making an unverified OpenBIOS path appear to pass.** The purpose here is an
executable full-ROM integration base, not another one-at-a-time HLE function.

### Remaining acceptance gates

1. Run this against the audited pinned Nugget build on Windows/Linux/macOS.
2. Observe real reset/boot initialization (including A0/B0/C0 RAM vectors and
   exception state) and fix missing device semantics at the correct shared owner.
3. Load the user-supplied game executable **without resetting BIOS RAM/kernel
   state**, and verify startup, callbacks, disc-sector reads, input and IRQs.
4. Add generated-host ROM/guest RAM transfer and mixed-execution parity. A MIPS
   interpreter executing OpenBIOS is not itself a native recompilation.
5. Only after real Persona E2E evidence (#351) promote OpenBIOS to the default
   game-running backend. Keep the backend interface for a future HLE replacement.

### Native recompilation status

The IR pipeline lowers COP0 (MFC0/MTC0/RFE), SYSCALL/BREAK as firmware traps and honours
`SR.IsC`; `ReachableProgramBuilder.BuildFirmwareImage` builds the ROM text image. Measured on the
pinned ROM: 2,956 of the 9,280 `.text` words are reachable from the reset vector alone; with the
257 ELF function symbols as explicit roots 9,228 are native. Code that exists only at run time
(kernel `.data`/ramtext copied to 0x500+, the A0/B0/C0 stubs, the shell, a loaded executable) has no
generated block and can only run through the mixed-execution interpreter fallback, which is
reported as fallback. The interpreter remains the only verified way to run OpenBIOS end to end;
generated-host execution of OpenBIOS is **not** verified (see the status line).

**Status: the real OpenBIOS boots on the interpreter, loads and enters an executable from a disc
through its own CD driver (synthetic disc and Persona); COP2/GTE executes (#447: transfers,
LWC2/SWC2, RTPS/NCLIP/AVSZ3/AVSZ4). Persona's intro movie decodes and displays (CD XA routing, GPU DMA2, MDEC, #732). Not done: a
Persona title screen; further GTE
commands, added when a run reaches them; generated-host execution/parity; OpenBIOS as the default `run`
backend; redistribution approval (#730).**

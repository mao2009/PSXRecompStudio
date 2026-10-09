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
- always reports `bootVerified:false` / exit code 2 until a real boot
  completion criterion and title handoff are implemented. Running instructions
  for 1,000,000 steps is **not** evidence of a fully booted game.

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
constant; no XA/CD-DA audio; the native DMA model's DICR bit layout differs
from psx-spx (enables/flags swapped), so the kernel's DMA3 IRQ bookkeeping does
not see its flag.

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

**Status: initial executable slice; entire firmware port is not complete.**

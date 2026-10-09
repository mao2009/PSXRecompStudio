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
exception (A0:40) and halts. The native CPU raises CpU for every COP2 instruction by
design; GTE execution is Issue #447. No title screen was reached.

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

### Ahead-of-time RAM-placed code (`--load-images`, ADR-026)

PSXRecompStudio is AOT-only: nothing is generated or compiled at run time. Code the firmware
places in RAM is compiled before the run from explicit images listed in a manifest
(`LoadImageManifest`; one directive per line, hex numbers, paths relative to the manifest):

```text
image kernel-data 0x00000500 rom 0xBFC1DF6C 0x45A0 kernel.roots  # ELF LOAD 0x500 <- ROM 0xBFC1DF6C (.data, RWE)
image vector-80   0x80000080 rom 0xBFC06DE8 0x10 vector80.roots # exceptionVector (0x80000084)
image vector-a0   0x000000A0 rom 0xBFC06DF8 0x10                # A0Vector; likewise B0Vector/C0Vector
image shell       0x80030000 rom 0xBFC0A1F4 0x13D78 shell.roots  # _binary_shell_bin_start
exe   test-exe    test.exe exe.roots                             # or: boot-exe <name> (SYSTEM.CNF on --disc)
interpret 0x80000080   # observation points stay interpreted: exception vector, exceptionHandler
interpret 0x000026A4   # (0x26A4, KernelRunningFromRam), shell entry 0x80030000, EXE entry 0x80010000
```

Addresses come from the pinned build's `openbios.elf` (program headers, `exceptionVector`/`A0Vector`/
`B0Vector`/`C0Vector`, `_binary_shell_bin_start`/`_size`) and `shell/shell.elf`. Roots are the ELF `T`/`t`
symbols inside each image plus the aligned words of read-only data that point into the image's code
(switch tables: `.rodata`/`.data` of the shell, the kernel `.data` and the ROM `.rodata` for the
kernel). A root that is not really an entry is harmless: its block holds the correct code for those
bytes and runs only if control reaches it with those bytes in RAM. Each image is compiled at its
destination (`BuildLoadedImage`), the builds are linked into one `LoadedCodeTable`, and the artifact
runs a version only while RAM holds exactly its words (page generations, ADR-026); anything else is
the counted fallback. The report lists every image (`loadedImages`), `loadedCodeVersions`,
`precompileMilliseconds`, `atBoundary` (native instructions and seconds when each differential
boundary was reached) and, per fallback target, its `cause` (`observation-point`, `version-mismatch`,
`aot-coverage-gap:loaded-image|rom`, `unknown-code`).

Measured (pinned build, synthetic disc, `--segment-budget 1000000 --segments 300 --differential`):

| | baseline (ROM only) | AOT images |
|---|---|---|
| native / fallback before `0x80010000` | 13.8M / 178.1M (7.2 %) | 191,927,114 / 5,197 (99.997 %) |
| native / fallback, whole run | 13.8M / 478.1M (2.8 %) | 476,966,271 / 5,198 |
| artifact ↔ interpreter transitions | 712,411 | 368 |
| copy-sync time | 808 s | 0.29 s |
| loaded-code versions / pre-compile (gcc) | — / — | 7,211 / 68 s |

Both runs match the interpreter at `0x80030000` and `0x80010000` (GPRs, SR/CAUSE/EPC/BadVAddr, RAM
SHA-256) with the same milestones (111 INT, 9 SYSCALL). The remaining fallback is the four observation
points (each exception: `0x80000080` and `0x26A4`), a kernel coverage gap after the observation point
(`0x26E4`, 120 entries) and a ROM coverage gap (`0xBFC05734`, 6 entries). With only symbol roots the shell's
`MOD_UpdateEffect` switch target `0x80032BD8` was a coverage gap entered ~1.46M times (each transition
~1 ms of copy-sync); the code-pointer roots close it. The whole run is no faster yet: the executable's
final `b .` loop now runs natively until the dispatch budget, and every native MMIO access and every
1024 retired instructions is one protocol round trip to the host's devices.

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
(kernel `.data`/ramtext copied to 0x500+, the A0/B0/C0 stubs, the shell, a loaded executable) is
compiled ahead of time from explicit images with `--load-images` (see above, ADR-026) and selected by
content at run time; without it, or where RAM holds no pre-generated version, it runs in the
mixed-execution interpreter fallback, which is reported as fallback. The interpreter remains the only verified way to run OpenBIOS end to end;
generated-host execution of OpenBIOS is **not** verified (see the status line).

**Status: the real OpenBIOS boots on the interpreter, loads and enters an executable from a disc
through its own CD driver (synthetic disc and Persona). Not done: GTE (#447) so Persona cannot get
past its first COP2 instruction; generated-host execution/parity; OpenBIOS as the default `run`
backend; redistribution approval (#730).**

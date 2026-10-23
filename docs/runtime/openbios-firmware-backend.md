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
defaults to the recorded native GCC 16.2.0 toolchain (override its directory with
`OPENBIOS_TOOLCHAIN_BIN`, which takes precedence over PATH). It rejects other
reported GCC versions and defaults to two make jobs (`OPENBIOS_BUILD_JOBS` can
override). An explicit `--builder docker` uses the optional digest-pinned image,
verifies the 512 KiB output, copies the observed MIT LICENSE beside the local ROM, and records its SHA-256 and Docker
image/toolchain details under the git-ignored `out/` folder.
`scripts/openbios/build.sh` pins the Docker digest and records the expected
native reference toolchain archive hash. Version validation is not verification
of an installed compiler or archive identity; each builder still requires independent ROM hash
and end-to-end compatibility verification.
Local building does not itself approve distribution.
The recorded ROM hash uses the SHA-pinned Windows GCC 16.2.0 archive listed in
`scripts/openbios/build.sh`. On Linux that archive can run in a separate Wine
prefix with Windows `make` and the toolchain's `bin` on its Windows PATH. Keep
ROM/ELF outputs outside tracked files and compare the ROM hash before reusing
any prior result.

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
  Local diagnostics for the interpreter engine (output is never committed): `--dump-ram <file>`
  writes the 2 MiB of main RAM at the `--capture-at` PC, `--dump-frame <file.ppm>` the displayed
  frame at the end (24-bit aware), `--pc-histogram <file.json>` the guest-RAM fetch counts (top PCs and
  64-byte buckets) so a wait loop can be told from a stall, and `--list-disc <file.json>` every
  ISO 9660 file with the PS-X EXE header of the executables (load address, entry, size).

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
`cdrom:SYSTEM.CNF;1`, loads the boot EXE named by `SYSTEM.CNF` through CD reads and DMA3, and
`gameMainThunk` → `exec` enters the executable's entry `0x80010000` (about 192M
instructions), which then writes its marker to RAM. Known gaps: the CHD adapter
reports a single data track (CHT2 metadata not parsed); seek time is a fixed
constant; no XA/CD-DA audio (XA sectors are routed away from the CPU and dropped).

What OpenBIOS's own CD driver needs from the disc and the drive (diagnosed with a
CD command/sector trace for #732): `dev_cd_open` reads the path table only when
the `GetStat` stat byte has bit 4 (ShellOpen) set, so a drive with a disc reports
it on the first `GetStat` after power-on and clears it there; it then reads the
PVD (LBA 16), the L path table (PVD offsets 132 size / 140 LBA) and the root
directory. The synthetic ISO therefore carries a descriptor-set terminator
(sector 17) and a one-entry L path table (sector 19); without them `open`
returns -1 for `SYSTEM.CNF`, the default `PSX.EXE` is not found, `loadExe` fails
and `fatal(0x38A)` halts in `unimplemented` at `0xBFC05E48` (`b .`, its delay-slot
NOP is `0xBFC05E4C`). With both, the probe reaches `0x80010000` with
`bootVerified`/`titleStarted` true (`PSXRECOMP.EXE`, 8.3 names are not required).
The optional M path table is not emitted; the optional/extra descriptors are
not modelled.

### Generated host (`--engine generated-host`)

The probe compiles the ROM and explicit RAM images before execution, then runs
`RecompiledHostExecutionEngine` over the common device graph. Unknown or changed
code uses the counted interpreter fallback. No code is compiled during the run.

```bash
python3 scripts/openbios/prepare-aot.py /local/openbios.elf /local/shell.elf /local/aot
pwsh scripts/demo/synthetic-disc.ps1 -WorkDir /local/fixtures -OpenBiosCompatible
psxrecomp openbios-probe /local/openbios.bin --disc /local/fixtures/synthetic-disc-openbios.bin \
  --engine generated-host --roots /local/aot/rom.roots --load-images /local/aot/images.txt \
  --code-bytes 37160 --segment-budget 1000000 --segments 230 \
  --stop-at 0x80010014 --differential --json
```

`psxrecomp openbios-probe <rom> --segment-budget 1000000 --segments 300 --disc <image> --engine generated-host --roots <file> [--code-bytes <n>] [--differential] [--compare-at <hex-pc>]... [--stop-at <hex-pc>] [--symbols <nm-output>] [--load-images <manifest>] [--standalone] [--json]`

The ROM's code (its first `--code-bytes`, e.g. `.text` + `.text_memcpy` =
37160 bytes for the pinned build) is built by
`ReachableProgramBuilder.BuildFirmwareImage` from the reset vector plus the
explicit `--roots` (one hex PC per line; for example the ELF's `T`/`t` symbols
in the ROM code range, extracted with `nm` — the CLI does not parse ELF),
compiled with `gcc` and run by `RecompiledHostExecutionEngine` in firmware mode
(see [host codegen](../development/recompiler-host-codegen.md)). Without
`--load-images`, RAM-placed code runs through interpreter fallback. With an
explicit manifest, matching guarded versions run natively and unknown or
changed code uses fallback over the same device graph; the report separates `nativeInstructions` from `fallbackInstructions`.
Milestones come from the fallback interpreter's fetches (native blocks are not
observed per instruction; the reset vector is the build's first dispatch unit).
`--differential` first runs the interpreter backend and compares both at the
first fetch of `0x80030000` (shell), `0x80010000` (the executable entry) and
every `--compare-at` PC (repeatable). A host boundary is captured by the
fallback interpreter, so a PC inside a compiled block is reported as not
reached. `--stop-at <pc>` ends both runs before they first execute that PC
(the host sees fallback PCs only; a stop the fallback observes ends that segment
as `ARTIFACT_FALLBACK_STOP_REQUESTED` — a stop on request, not an exhausted
instruction budget — with `stopAt.reachedByHost:true`), so a
check need not spin the executable's final loop; without it the full run is
unchanged. `--symbols` takes `nm -n` output (the CLI never parses ELF).
Without `--json` a compact summary precedes the indented document; progress
goes to stderr every 2^25 fetches.

#### Standalone embedded-image mode (`--standalone`)

`--standalone` (Phase 2 Step B, Issue #739) delivers the same firmware run
through the standalone embedded-image artifact decided in
[ADR-027](../adr/027-standalone-artifact-runtime-integration.md): the ROM image
and the run's initial state are compiled into the generated C source and the
artifact attaches the shared Runtime host bridge over the existing
`--host-transfer` path, so no `artifact-input.txt`/`artifact-image.bin` are
written and no Studio is involved. It is a delivery change, not a second
execution path: the accounting, milestones and differential comparison are
unchanged. It requires `--engine generated-host` (the interpreter backend
rejects it) and is reported as `standalone:true` so no reader can mistake which
path produced a number. The BIOS → RAM executable handoff (a pre-generated
`--load-images` executable the firmware jumps to runs natively) is verified
against the firmware interpreter.

#### Report schema (generated host)

Besides the fields above (`execution` keeps the session's raw evidence):

- `accounting` — what was observed, never an estimate:
  - `native.instructions`: the sum of the artifact's guest-time (`R`) reports,
    i.e. instructions compiled blocks retired. It carries no PC, so
    `native.region` is set only when every compiled block lies in one region
    (otherwise null and per-region native counts are null).
  - `fallback.fetches`: PCs the fallback interpreter was about to execute;
    `retiredInstructions` is the session's retired count. An instruction that
    takes an exception (IRQ, SYSCALL, fault) is fetched, not retired:
    `fetchesNotRetired` is the difference.
  - `regions[]` (`rom` = physical 0x1FC00000–0x1FC7FFFF, `kernel-ram` = RAM
    < 0x10000, `shell` = 0x30000–0x450C0, `user-ram` = other RAM incl.
    mirrors, `other`): `nativeInstructions`, `fallbackFetches` and
    `transitionsEntered` with their shares.
  - `transitions`: artifact→interpreter handoffs. `byReason[]` classifies the
    entry PC using the AOT-aware `observation-point`, `version-mismatch`,
    `aot-coverage-gap:loaded-image|rom` and `unknown-code` reasons. The generic
    accounting helper retains address-based reasons for older callers. Each
    reason carries transitions, how many came
    from JR/JALR (`indirect`), retired instructions and shares. `byAotClass[]`
    (`MixedFallbackAotClass`, AOT-only project: no run-time codegen) says why
    no AOT code ran: `known-not-yet-aot` (in a known image, no block),
    `not-in-any-aot-image` (statically unknown to the build), `image-stale`
    (content hash/generation differs), `region-overwritten` (another image
    now occupies it), `runtime-generated` (class C: run-time generated or
    unanalysable self-modifying, `preDeterminable:false`). The producer of a
    handoff sets `MixedFallbackTransition.AotClass` when it knows (an AOT image
    table); otherwise the probe derives it from the address: ROM →
    `known-not-yet-aot`, anything else → `not-in-any-aot-image`. This
    address heuristic is separate from the probe's image-aware `byReason` and
    does not establish runtime code provenance. `byExit[]`: `returned-to-block`,
    `budget-exhausted`, `stopped:<code>`. Each region names its `codeImage`
    (`rom`, `kernel-ram-image`, `shell`, `ps-x-exe` — the executable and its
    overlays, i.e. other RAM — or `unknown`) and the instructions retired in
    segments entered there (`retiredInSegmentsEntered`).
  - `hotEntries[]` (top 20 entry PCs by transitions), `hotPcs[]` (top 20
    fallback PCs by fetches), `hotSymbols[]` (with `--symbols`: fetches per
    `region:function`; a PC maps to the nearest text symbol at or below it in
    the same region, by physical address).
  - `unsupportedOpcodes[]`: the first RI (10) / CpU (11) exception per
    (ExcCode, major opcode) seen at the vector: faulting PC (EPC, +4 in a
    delay slot), word, fallback fetch index.
- `timings` (wall clock, not deterministic): `buildMs` (image analysis),
  `compileMs` = `codegenMs` + `gccMs`, `referenceMs` (interpreter run),
  `runMs`, `fallbackMs`, `transferMs` (copy-sync), `nativeAndHostMs` = run −
  fallback − transfer (compiled blocks, MMIO/device relay, process start),
  `totalMs` (the whole command). `aot`: `generatedCBytes` (UTF-8 bytes of the
  generated C) and `artifactBytes` (the compiled binary).
- `consistency.differentialPass`: additionally requires every explicitly
  requested boundary and the first hardware IRQ comparison to match; true only when at least one boundary was
  compared, every boundary the interpreter reached matched in the host, and
  the milestones match; null without `--differential`.
- `differential.<pc>`: `match`, `firstMismatch` (`kind` cpu/device/ram, `name`,
  both values; CPU before devices before RAM), `cpuMismatches[]` (PC, r1–r31,
  HI, LO, SR, CAUSE, EPC, BadVAddr), `deviceMismatches[]` (I_STAT, I_MASK, IRQ
  line, DMA DPCR/DICR/MADR/BCR/CHCR and IRQ, timer counter/target/IRQ, SIO0
  IRQ, GPUSTAT, VRAM SHA-256, CD-ROM index/status/IF/IE/FIFO counts/last
  command/mode), `ramFirstMismatch` (address, 4 KiB page, 16 bytes either side
  in both runs), `ramMismatchBytes`, `ramMismatchPages`, both RAM SHA-256.
  Only side-effect-free reads are used (no timer mode, no FIFO pops).
- `milestoneComparison`: whether both runs reached the same milestones
  (fetch indices excluded: they count different fetches) and the
  interpreter's report. `stopAt`: the PC and whether each run reached it.

`prepare-aot.py` reads ELF32 MIPS symbols and code-pointer roots from the locally
built ELF pair. Manifest root paths are relative to the manifest. Issue #773 adds the observed-roots directive
`roots <image> <roots-file> <source-sha256> <load-hex> <words-sha256>`: it names exactly one EXE-based image (`exe`,
`boot-exe` or `disc-exes`), and the load is refused unless the image's source-file SHA-256, load address and code-word
SHA-256 all match, and every root is a unique, aligned, in-image, structurally safe PC that is not already a root. The
roots file is read strictly (a duplicate is an error), and one image takes at most one `roots` directive (the per-image limit is checked per file). `psxrecomp fallback-roots <openbios.bin> --load-images <manifest>
--fallback-targets <file.json> --out <manifest> [--disc <image>]` plans those roots from a probe run's full fallback
export (`openbios-probe --engine generated-host --fallback-targets <file.json>`, which writes every distinct fallback target
and leaves the top-12 summary unchanged). Only `unknown-code` and `aot-coverage-gap:loaded-image` targets inside one identified EXE image are candidates. The
plan is ordered by observed instructions and bounded by `ObservedRootBudget.Default` (64 roots per image, 256 in total, and
added reachable code of at most max(1024 instructions, 20 % of the entry-driven reachable set)). The export (`psxrecomp.fallback-targets/v2`)
also records the identity of the run that produced it (`run`: firmware SHA-256, the manifest's interpret points, and per
image its name, load address, code-word SHA-256, source-file SHA-256 and roots), and `fallback-roots` refuses an export
unless that identity equals what the supplied firmware and manifest describe now. This proves which firmware, which EXE
bytes, which load addresses and which root set the trace was taken over; it does **not** prove the disc image beyond the EXE
files it supplied, the ROM `--roots`/`--code-bytes` options, the run's inputs, or that a PC was reached by the same
execution that a different export would show. A v1 export carries no identity and is refused. The export also records the run's
`outcome` (its terminal `TitleExecutionState` and `complete`). Only a run that reached a classified end (`Completed`,
`Returned`, `RuntimeHandoff`) is planned from; an export of a run that exhausted its budget, failed, or hit an unsupported
transfer is partial and is refused, and an export without `outcome` is malformed. The probe still writes such an export
(with its outcome) and reports it on stderr. Each target also carries `ramWord`: the instruction word the interpreter read from guest
RAM or ROM at that PC, observed at every fallback fetch there. It is `null` when the PC is outside RAM and ROM (the probe reads no
word there, so a zero is never recorded for it), when a fetch could not be read, or when two fetches at the same PC read different
words. The planner rejects a `null` `ramWord` as `ram-word-mismatch`. The planner reads a target as a candidate root only when its `ramWord`
equals the word the EXE file places at that PC; any other target is rejected with the reason `ram-word-mismatch` and is not reported
as a native route. This check compares what the run fetched with the file, so it can refuse a root whose native block would not run,
but it does not prove that RAM matched the file at every block entry; the loaded-image guard does that at run time. A `v2` export
without `ramWord` is malformed and refused. This note records what this repository does not show. Earlier commit
messages on the observed-roots branch (d164590 and 366b0c1) cite an observation from a Persona real run and loaded-code counts from
the same work. Neither is recorded in this repository (no run log, export, or measurement is committed), so both are unverified and
are not evidence for any result here. The `ramWord` check is a code path; its effect on any real run has not been measured. Output safety: the
output manifest must sit beside the input; no existing file or directory is overwritten; every output is first written
under a run-unique `.<name>.<guid>.tmp` name in the same directory (exclusive create), the staged manifest is read back
through the manifest parser, then the roots files are renamed into place (no overwrite) and the manifest last (the commit
point), and the final manifest is read back again. On any failure the files this run published are removed, staged files
are removed, and a file or directory that was already there is never touched. Two concurrent runs onto the same names
cannot both publish: the loser fails and removes only its own files. Not atomic across files: if the process is killed
between publishing a roots file and the manifest, `observed-roots-NNN.txt` and `.*.tmp` files remain with no manifest
naming them. Recovery: delete the `.*.tmp` files and the `observed-roots-NNN.txt` files that no manifest references, then
re-run (the next run refuses to start while those names exist, by design). `boot-exe <name>`
reads the executable the disc's SYSTEM.CNF `BOOT` names (the real one on a real disc; `<name>` is only a
label) with its entry as the sole root. `disc-exes <prefix>` (Issue #734) adds every PS-X EXE file on the
disc, one guarded image each named `<prefix>:<ISO path>`: a game's later stages are executables its boot
program stages into RAM, and overlays may share a load address, which the loaded-code guard resolves by
the code actually in RAM. Files too large to be an executable are not read; nothing is title-specific.
`prepare-aot.py --disc-exes` emits it; it is **opt-in** because, measured on Persona, entry-only roots leave every
function-pointer target to the interpreter fallback and the mixed execution then ping-pongs (Issue #734, below). Observation
points must remain interpreted: the generated manifest includes `C0Handler`
(kernel RAM execution), exception vector/handler, shell and EXE entries.

`indirect-roots` (Issue #773, **opt-in**) adds the functions an EXE calls only through a function-pointer table to
that EXE image's roots. A reachable `lui`/`addiu|ori` pair names a table; the table words that are in-image,
function-entry-shaped (an `addiu $sp,$sp,-N` prologue, or the word after `jr $ra` and its delay slot), and not
reserved encodings become roots. Discovery repeats to a fixed point under fixed caps (8 rounds, 256 roots, 32
words per table, 1024 candidate checks). The table base itself is never a root. Limits: a table reached through a
pointer variable or a computed index is not found, and a data word that looks like a prologue can still be accepted.
Every root is a guarded block, so an unused root costs build time only. Without the directive the roots are unchanged.

`--compare-at <hex-pc>` adds comparison boundaries; `--stop-at <hex-pc>` stops
both engines before that instruction. These PCs are excluded from RAM native
blocks. ROM-only compiled interior PCs may be unobservable; a missing boundary
fails the verdict. `--capture-at` is the legacy interpreter register capture.
`--symbols <nm-output>` labels fallback accounting.

Differential snapshots include PC, GPR, HI/LO, COP0 SR/CAUSE/EPC/BadVAddr,
2 MiB RAM SHA-256 and mismatch context, scratchpad SHA-256, total scheduler
cycles, IRQ controller, DMA channels, timers, SIO0, GPU status/VRAM and CD state.
The reads avoid clear-on-read registers. Fetch indices differ (all reference
fetches versus fallback-only host fetches); they are not clocks. Native retired
instructions at each boundary, fallback retired instructions, transitions and
protocol round trips are reported separately. `consistency.differentialPass`
requires matching states, all required boundaries, the first hardware IRQ state
when reached, and matching milestones;
a mismatch returns exit 2. `--stop-at 0x80030000` deliberately measures a shorter
Shell gate; otherwise the EXE boundary is required. Kernel boot and EXE parity
are separate claims. The first hardware IRQ snapshot is reported independently
because endpoint CPU/RAM can converge after an earlier timing divergence.
[ADR-025](../adr/025-generated-host-guest-time-and-device-ram.md), amended by
[#744](https://github.com/mao2009/PSXRecompStudio/issues/744), now requires device-owned
conservative deadlines and per-instruction retirement points inside fused units.
Deadline credits travel in existing host replies; branch delay slots defer CPU
acceptance while device delivery remains exact. Timer MODE comparisons use a
read-only peek, so capturing a boundary does not clear status flags.
The probe allows a bounded three minutes per compiler
step; ordinary builds retain their 30-second default.

Historical CPU/RAM-only comparisons did not establish device-time parity. A
Shell state match also does not prove a successful executable payload load.
The original deterministic disc (`4f75a05f…ab27`) declares only 20 text bytes;
pinned OpenBIOS `dev_cd_read` rejects reads not aligned to 2048 bytes and
`loadExe` ignores that failure. It can jump to zero RAM at the entry. Preserve
that fixture for baseline comparison, but use `-OpenBiosCompatible` for actual
load verification: padded 2048-byte text and a terminal loop, SHA-256
`bbae905d1caf8d01cb166224197e6c82ce326a71c7ac34502f8f38f0854d3f03`.
C# and PowerShell builders share both pins. Verify the first loaded instruction
and the marker as well as PC/state parity.

The minimal ISO retains the historical endian-field omissions (including the
big-endian Path Table Size half) and lacks a complete ISO compliance proof
([#743](https://github.com/mao2009/PSXRecompStudio/issues/743)).
Disc insertion/removal is not modelled; the sector source is fixed at device
construction. Reset rearms ShellOpen and GetStat consumes it, covered by tests;
this is model evidence rather than an actual-hardware validation.

Baseline with the accounting above (same command plus `--symbols`, before any
RAM AOT work): native 13,802,380 (all ROM); fallback 478,090,664 retired
(fetched +115); 712,411 transitions, 712,394 of them at the A0/B0/C0 vectors
(B0 alone 712,330; `B0Handler` 5.7M fetches), all `not-in-any-aot-image`
except 6 `known-not-yet-aot` ROM entries (`flushCache`). Fallback fetches:
user RAM 63% (the executable's `b .` spin, 300M — `--stop-at` removes it),
shell 35% (one 163M-instruction segment from `0x80030C44`), kernel RAM 1.9%.
Time: run 1024 s, of which transfer (copy-sync) 863 s, fallback 88 s,
native+host 72 s; gcc 6.2 s for 5.5 MB of C (2.2 MB binary). Differential:
the shell entry matches in full; at `0x80010000` CPU and RAM match but the
three timer counters differ (interpreter `0xBB56`, host `0xBF4D`), and the
host's native + fallback count to that point exceeds the interpreter's
fetches by 1,880, so `differentialPass` is false: device time diverges
(the earlier comparison did not read device state).

### Ahead-of-time RAM-placed code (`--load-images`, [ADR-026](../adr/026-aot-ram-placed-code.md))

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
`B0Vector`/`C0Vector`, `_binary_shell_bin_start`/`_size`) and `shell/shell.elf`. The portable helper selects ELF function symbols inside each image plus the aligned words of read-only data that point into the image's code
(switch tables: `.rodata`/`.data` of the shell, the kernel `.data` and the ROM `.rodata` for the
kernel). A root that is not really an entry is harmless: its block holds the correct code for those
bytes and runs only if control reaches it with those bytes in RAM. Each image is compiled at its
destination (`BuildLoadedImage`), the builds are linked into one `LoadedCodeTable`, and the artifact
runs a version only while RAM holds exactly its words (page generations, ADR-026); anything else is
the counted fallback. The report lists every image (`loadedImages`), `loadedCodeVersions`,
`precompileMilliseconds`, `atBoundary` (native instructions and seconds when each differential
boundary was reached) and, per fallback target, its `cause` (`observation-point`, `version-mismatch`,
`aot-coverage-gap:loaded-image|rom`, `unknown-code`).

Historical report (not current-PC verification; original fixture and CPU/RAM-only comparison):

Measured (pinned build, synthetic disc, `--segment-budget 1000000 --segments 300 --differential`):

| | baseline (ROM only) | AOT images |
|---|---|---|
| native / fallback before `0x80010000` | 13.8M / 178.1M (7.2 %) | 191,927,114 / 5,197 (99.997 %) |
| native / fallback, whole run | 13.8M / 478.1M (2.8 %) | 476,966,271 / 5,198 |
| artifact ↔ interpreter transitions | 712,411 | 368 |
| copy-sync time | 808 s | 0.29 s |
| loaded-code versions / pre-compile (gcc) | — / — | 7,211 / 68 s |

The historical report claimed both runs match the interpreter at `0x80030000` and `0x80010000` (GPRs, SR/CAUSE/EPC/BadVAddr, RAM
SHA-256) with the same milestones (111 INT, 9 SYSCALL). The remaining fallback is the four observation
The historical AOT comparison matched CPU and RAM at `0x80030000` and `0x80010000` (GPRs, SR/CAUSE/EPC/BadVAddr, RAM
SHA-256); it did not establish strict device/time parity. These earlier measurements
used a different comparison contract. Both runs had the same milestones
(111 INT, 9 SYSCALL). The remaining fallback is the four observation
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

### Standalone OpenBIOS distribution (`package --bios openbios`)

Issue #732 / #739. `psxrecomp package <game.exe|game.chd>` (no `--bios`) still builds the legacy HLE-bridge
distribution. The OpenBIOS-booted distribution is selected only by the explicit `--bios` family, because the
default must not change until the real-game gate below passes:

```bash
python3 scripts/openbios/prepare-aot.py /local/openbios.elf /local/shell.elf /local/aot
psxrecomp package /local/game.chd --bios openbios --bios-rom /local/openbios.bin   --aot-dir /local/aot --output ./persona [--code-bytes 37160] [--instruction-budget <n>] [--run-timeout-seconds 1800] [--build-timeout-seconds <n>] [--mixed-fallback|--no-mixed-fallback] [--fallback-segment-budget <n>] [--fallback-max-transitions <n>]
./persona/persona          # persona.exe on Windows
```

`--bios` is `openbios` (default when any firmware option is given), `custom-hle` or `genuine`. The last two are
reserved names: they stop with `BIOS backend '<name>' is not implemented` and nothing is produced or substituted
(`BiosBackendSelection`).

`--build-timeout-seconds <n>` sets the bounded per-step native-toolchain (compile/link) timeout for the firmware
artifact build; it is independent of `--run-timeout-seconds` (the guest run bound). Omit it to keep the historical
three-minute per-step default (`FirmwareDistributionBuilder.DefaultBuildTimeout`); it is validated to `1..86400` and
is never unbounded. A slower build host (for example the ~515,783-line ROM + AOT dispatch on a low-core CPU) can be
given a longer compile budget without extending the game's own run time. The `openbios-probe` generated-host build
keeps its own fixed three-minute per-step cap.

**Execution structure (one guest state).** The native artifact *is* the OpenBIOS ROM compiled ahead of time
(`ReachableProgramBuilder.BuildFirmwareImage`) plus the guarded RAM-placed versions (`LoadedCodeTable`: kernel data,
vectors, shell and the disc's `boot-exe`, ADR-026). The game is never a second CPU session: OpenBIOS starts at the
reset vector, its own CD driver reads the disc through the Runtime host's `CdRomDevice`/DMA3/IRQ graph, and `exec`
jumps into the game in the same RAM, COP0 and device state. The game's calls back into A0/B0/C0 reach OpenBIOS code
(native if it has a block, counted interpreter fallback otherwise); no `IBiosRuntime` is attached to a guest firmware.
Unknown or changed RAM code is the explicit, counted, bounded fallback (ADR-025/026); it is reported, never silent.
The distribution's AOT compiles the probe's post-shell milestones too: `0x80030000` and `0x80010000` are lowered as
guarded blocks, never kept interpreted (`FirmwareAotPlan.FixedExceptionObservationPcs`); only OpenBIOS's own exception
vectors (`0x80000080`, `0x26A4`) stay observed/interpreted, and a guarded game block runs only while RAM holds its
compiled words.

| Item | Where |
|---|---|
| BIOS ROM | user-supplied `--bios-rom`, exactly 512 KiB; copied to `runtime/artifact-image.bin`; SHA-256 re-verified on every run |
| Disc (CD sector source) | user-supplied, copied to `runtime/disc/disc.{chd,bin}`; SHA-256 re-verified on every run |
| AOT inputs | `--aot-dir` files (`rom.roots`, `images.txt` and every `image`/`exe` reference, resolved fail-closed against the AOT directory) copied to `runtime/firmware/` preserving their layout; per-file SHA-256 pinned in the boot manifest (`aotInputs`, schema 2) and re-verified before every plan rebuild; the host rebuilds the identical `LoadedCodeTable` (analysis only, no code generation) |
| Backend / identities | `runtime/firmware-boot.json` (`FirmwareBootManifest`, schema 2), selects the firmware run in the shared RuntimeHost |
| Run settings | `runtime/runtime-host.json` (`guestFirmware:true`; mixed fallback on by default — `--mixed-fallback`/`--no-mixed-fallback` with `--fallback-segment-budget`/`--fallback-max-transitions` propagate through the manifest, recorded as 0 budgets when off) |

`FirmwareRuntimeLauncher` builds the engine through `ForPrebuiltArtifact` with the disc and the loaded-code table; a missing or wrong-size BIOS named by `PSXRECOMP_BIOS_ROM`, a tampered disc, backend, AOT input or manifest path stops the run with exit 78 before anything executes. The BIOS is not in the distribution, so packaging pins no digest of it: the run reports the SHA-256 of the image it loaded, and a change to that image during the run fails the run.
The run prints a `firmware-run` JSON line on stderr with native and fallback instruction counts and the final register file, and `RuntimeHost --diagnostics <path>` additionally writes the Issue #763 stop diagnostic (best-effort; the classified exit code is unchanged).

**Distribution boundary.** The ROM and disc are copied into the *local* output only. Nothing is downloaded, and no
commercial game or genuine BIOS is bundled. Redistributing OpenBIOS (even the compiled artifact embeds the ROM) waits
for the #730 file-by-file license audit; the generated `README.txt` says so. This is a technical verification path,
not distribution approval.

**Tests.** `OpenBiosStandaloneSelectionTests` (selection, manifest, ROM/disc rejection, CLI fail-closed — including a malformed `rom.roots` returning exit 1 with no partial output),
`FirmwareGameRoundTripTests` (ROM code → RAM game → ROM routine → game → ROM, COP0/RAM preserved, interpreter parity,
native vs counted-fallback), `FirmwareAotPlanTests` (the game entry is compiled to a guarded native block whose
flipped RAM word voids the match; the exception vectors stay observed; the shared roots parser accepts comments/blank
lines and rejects invalid lines as `InvalidDataException`), `FirmwareAotBundleTests` (input containment,
copy layout, digest pinning and tamper/missing/escape/link rejection — including a linked `rom.roots`/`images.txt` or
a linked AOT root — schema-2 manifest negatives), `FirmwareDistributionBuilderTests` (a build whose AOT input
escapes the root or has a malformed roots file fails before any output, preserves a pre-existing distribution and
leaves no staging directory), `FirmwareRuntimeLauncherTests`/`FirmwareRuntimeLauncherDiagnosticsTests` (fallback-policy propagation and
`--diagnostics` honoring) and the packaging E2E `OpenBiosStandalonePackageE2E` (a `boot-exe` AOT image runs in the
one packaged guest state with zero fallback instructions and ROM → game → ROM register continuity). **The packaging
E2E uses a synthetic ROM that stores the game's words into RAM itself; it proves packaging → guarded game AOT → ROM
continuity, not OpenBIOS's run-time CD-sector load.** The latter is the separate runtime disc-load gate, verified by
real OpenBIOS over the shared device graph with the synthetic Mode 2 disc (see "Disc in the drive" above and the
measured table below) and by the real Persona runs. The game block's own contribution is attributed inside the
aggregate native count by the game-only final register (`$t0`) plus zero fallback instructions, never by the BIOS's
native count alone. The same E2E asserts the explicit fallback budgets reach the packaged manifest and that the host
writes the diagnostics document; the existing `FirmwareStandaloneHandoffTests` cover the pre-packaged handoff.

**Measured (pinned OpenBIOS build, `--instruction-budget`/timeout bounded, local only).**

| Run | Result |
|---|---|
| Synthetic Mode 2 disc, `openbios-probe --standalone --stop-at 0x80010014` | `bootVerified`; 195,060,431 native / 2,338 fallback instructions; 327 transitions |
| Same disc, packaged, `./synth` launched directly (200M budget) | **Runtime disc load proven by real OpenBIOS's own CD driver** (separate gate, synthetic disc + probe above) **not by this packaging E2E**: the packaging E2E's synthetic ROM stores the EXE words into RAM itself. The packaged direct launch (200M budget) reached the synthetic EXE's `b .` loop at `0x80010014`; 278,673,285 native / 2,338 fallback / 326 transitions; ends `OUTER_BUDGET_EXHAUSTED` (exit 2 = bounded by design) |
| Persona CHD, 600 s timeout, Windows, `PSXRecomp.RuntimeHost --diagnostics`, before/after `disc-exes` (all executables compiled, entry-only roots) | Before: 270,127,434 native / 2,221,246,107 fallback instructions / 4,698 transitions, fallback time 269 s of 601 s; last fallback entry `0x8008A484`. After: 315,766,486 native / 11,374,179 fallback / 254,095 transitions, fallback **transfer** time 175 s of 600 s (249,567 entries at `0x80084C88`, returning at `0x8008AECC`). The guest advanced ~7x *less* in the same wall time: AOT coverage rose, progress fell. Both end `ARTIFACT_TIMEOUT`; no title evidence. |
| Persona CHD, packaged with the synthetic-disc AOT inputs (`boot-exe` has only the entry as a root), 55 min timeout | OpenBIOS boots from real MIPS code and hands control to the game; **no title/frame evidence**: 357,153,833 native / 23,541,800,409 fallback instructions / 24,625 transitions, ends `ARTIFACT_TIMEOUT`. The game body ran as counted interpreter fallback because it has no AOT roots. **Not a pass.** |

**Transition attribution (PR #792 measurement; opt-in, off by default).** Setting `PSXRECOMP_TRANSITION_ATTRIBUTION=<path>` for a
firmware `PSXRecomp.RuntimeHost` run installs a recorder that classifies every artifact-to-interpreter entry on entry, from the
loaded-code table and the RAM the artifact held: `unregistered` (no version at the PC), `guard-mismatch` (versions registered, none
equals RAM) or `guard-matched-anomaly` (a version matches RAM; must be 0, the artifact only offers a PC no version matched). It
aggregates by (entry, cause, previous return, exit) with at most 4096 keys and 64 detailed events, reports what it dropped, writes
once at the end, and never fails the run. Not observable host-side: native arrivals at a registered PC and the native block that
made a transfer (the wire protocol carries neither). The 254,095-transition row above was measured with `disc-exes` and
entry-only roots, i.e. **without** `indirect-roots`. Persona CHD, #792 head 28b1b11 (`disc-exes` + `indirect-roots`), 600 s,
Windows, one configuration: 367,675,344 native / 142,792 fallback instructions / 4,494 transitions (1,176 guard-mismatch,
3,318 unregistered, 0 anomalies); no fallback entry at `0x80084C78`, `0x80084C88` or `0x8008AECC`; fallback transfer 3.1 s and
interpreter 0.06 s of 600 s; 12.2 M MMIO accesses and 23.0 M time reports (the IPC bound of #768 remains the limiter). Ends
`ARTIFACT_TIMEOUT`, black frame: **no title evidence**. Precompile (package) 227 s, separate from the 600 s guest run.

**Host IPC profile and the fused report + MMIO request (Issue #768).** `PSXRECOMP_IPC_PROFILE=<path>` (opt-in; absent, the
bridge holds no profile and the hot path pays a null check) writes per message kind (time report, fused MMIO, MMIO read/write,
transfer, GTE, COP0, other): count, lines written, bytes, wait (previous handler end to this line read: native execution plus
pipe wake-up), handler time (parse, device work, reply writes), device time, write time and p50/p95/p99 (power-of-two
microsecond buckets). Every counted message is one synchronous round trip; `mmioAccesses`/`timeReports` therefore equal message
counts (no double counting) except that a fused request is counted in both. Measured on Persona, #792 head, Windows, 60 s,
machine shared with other agents (CPU 4-83 %): about 63,000 messages/s, ~16 us each; 66 % are time reports, 32 % MMIO reads;
wait is 73 % of wall time (the artifact and the pipe wake-up, which the host cannot shorten), host handler 27 %, of which the
reply write syscalls are about 20 % of wall and device advance about 2.5 %. Each MMIO access was preceded by its own time
report (reports ~ 2 x MMIO: one before the access, one after its unit). The access and the report that must precede it now
travel as one line `RHOST_RETIRED count R|W width physical [value]`; the host handles the report, then the access, and the
artifact reads the same replies in the same order, so no device observes a different order or time. The report after the unit
is unchanged (it carries the credit and the interrupt line the next dispatch boundary needs). Result, same host build, 3
interleaved 60 s runs each: round trips per guest instruction 0.106 -> 0.071 (-33 %, deterministic); native instructions in 60 s
29.4 M / 21.9 M / 36.4 M -> 46.8 M / 26.6 M / 37.6 M (median +28 %, run-to-run noise about +-25 %, so the throughput figure is
indicative only). Not adopted: coalescing the host's reply lines into one write (AutoFlush off, flush before every blocking
read): two interleaved 40 s runs each showed no difference beyond noise. Not attempted: skipping the report after a read whose
access did not change the scheduler (it would defer a device advance past dispatch boundaries; needs its own proof).

**Native guard counters and the integrated Persona run (Issue #773; measurement only).** `PSXRECOMP_NATIVE_GUARD_STATS=1` at
package time builds the artifact with counters per RAM-placed entry PC (arrivals, hits per version, misses, and budget refusals) and, for each entry's
first miss, the entry PC of the previous dispatch-loop iteration (not necessarily the guest source of the transfer), the RAM word and
the CPU registers (at most 16 such records in all, `psx_gs_snap[16][36]`; a first miss beyond them is only counted in `snapdropped` and has no CPU registers); per-version tables are flat (no padding to the widest slot). At run time `PSX_GUARD_STATS_FILE=<path>` receives
them (every 4 M dispatch units and at exit), written whole to `<path>.tmp` and then replaced over `<path>` in one step; the previous
report is not removed before the replacement, so a refused replacement keeps the report that was there and deletes the `.tmp`. Platform
difference: POSIX uses `rename()` (atomic replace); Windows uses `MoveFileExA(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` (declared by hand, not via `<windows.h>`),
a same-volume rename in practice but not documented as atomic and not guaranteed to be one step (a reader may see no `<path>` for an
instant); it is refused (keeping the old report) while the destination is open without `FILE_SHARE_DELETE`. Each slot line satisfies `arrive = hit + miss + refuse`: `refuse` counts an arrival whose budget check refused it (the unit did not run). Each report starts with
a `run <id>` line (line 2) that names the run which wrote it. Guarantee on a killed or timed-out run: `<path>` holds nothing, or a
complete report, either of this run's last dump or of an earlier run not yet replaced (told apart by `run`). Its content is never a
truncated dump, because the content is complete before the rename. A `<path>.tmp` of the dump in progress may remain (the next dump
overwrites it). Not promised: a single-step replacement on Windows and survival across power loss (no `fsync`). The last line is `end final 1` only for the exit dump; a file without it comes from a
run that did not exit normally and must not be read as complete. Tests select the counters with `GuardStatsScope.Begin(path)`
(a per-async-context switch handed to the child process only) instead of process-wide environment variables. The generated text is byte-for-byte unchanged without the option. Values are produced
inside the generated code, so they are native-side observations, unlike the host-side attribution of fallback entries.
Persona CHD, `disc-exes` + `indirect-roots` (#792) on main with the fused report (#796), Windows, 600 s, one run (CPU 20-53 %, other
agents' processes present): `0x80084C88` had **1,655,795 native arrivals, all guard hits on version 0 (the ATLUS.EXE version),
0 misses, 0 fallback entries**; `0x80084C78` (1,655,795 hits) and `0x8008AECC` (1,727,352 hits) likewise. Misses overall: 1,190 at 30
entries (kernel and loader code that is rewritten), matching the 1,190 host-side `guard-mismatch` entries. Executed native
instructions are spread over many regions (OpenBIOS shell 0x80030000-0x80032FFF about 42 %, 0x80084xxx 19 %, 0x8008Axxx 10 %,
0x80081xxx 9 %, 0x80025xxx 8 %), not one loop. Same host and ROM, 600 s each (no title evidence in any, black frame):

| Configuration | native instr | fallback instr | fallback transitions | native instr/s | last fallback entry |
|---|---:|---:|---:|---:|---|
| main before #796, no `indirect-roots` | 311,988,244 | 4,834,914 | 108,778 | 519,914 | `0x80084C88` |
| + #792 `indirect-roots` | 379,787,396 | 143,672 | 4,513 | 632,897 | `0x800828B0` |
| + #796 fused report | 420,132,321 | 143,672 | 4,513 | 700,148 | `0x800828B0` |
| + #795 attribution, IPC profile, guard counters ON | 428,675,041 | 143,672 | 4,513 | 714,340 | `0x800828B0` |

The 254,095-transition figure above therefore came from the missing registration: without #792, `0x80084C88` is a fallback
entry (108,778 transitions here); with it, the entry is native. The four runs cover different amounts of AOT code, so the
rates are not a pure IPC comparison, and instruction counts at 600 s are not the same guest position. Diagnostics ON vs OFF
differ by +2 % (within run-to-run noise). IPC remains the limit: 31.3 M messages in 600 s (52,000/s), wait 73 % of wall time.

Profile of the same Persona disc with `openbios-probe --standalone` on the merged #764 main (30 min bound): kernel
booted and `titleStarted:true`, but 275,017,697 native vs 11,066,526,413 fallback instructions, 9,118 transitions,
`ARTIFACT_TIMEOUT`. 99.9% of the fallback is `user-ram`/`ps-x-exe` code classified `unknown-code` (in no AOT image):
entry `0x8008A484` (1,127 entries, 10.9 G instructions) spinning in the loop `0x8008AECC..0x8008AF04` (about 154 M
iterations per PC), with the stream code at `0x80081BC8..0x80081C18` next. The disc's boot EXE does not cover this
code (it is loaded or placed at run time), so the fix is more game AOT images supplied through `--aot-dir`
(`exe`/`image` lines, no title constants in the tool) and, separately, finding out why that wait never completes
under the generated host. Until then Persona is **not** verified on this path.

#### Measured divergence between the generated host and the interpreter (Issue #774)

Two runs of **one** build (pinned `openbios.bin`, the Persona CHD, `openbios-probe --standalone
--engine generated-host --differential`, synthetic-disc AOT inputs only, so every byte of the game is counted
fallback) compared the host against the interpreter at the first fetch of each checkpoint PC. Never mix
measurements from different builds or the packaged `persona` distribution directories: the host binary must be the
same one that ran the interpreter reference.

| Checkpoint PC | interpreter | generated host | verdict |
|---|---|---|---|
| `0x80030000` (shell) | fetch 709,142 | fallback fetch 1,688 | identical |
| first hardware interrupt | guest cycle 169,936,299, EPC `0x000015BC` | same PC | identical |
| `0x8008A484` | guest cycle 268,664,967 (fetch 268,665,641 = cycles + 674 interrupt entries) | 268,576,640 native + 88,327 fallback = 268,664,967 | identical |
| `0x800812E8`, `0x8008A014` (movie stream ring) | first fetched 73.5 s / 73.6 s into the reference run; 9,274,208 fetches each in the standalone 3G run | never fetched as fallback | **interpreter-only** |
| `0x80081BC8` (`DecDCTinSync`, MDEC status `0x1F801824`) | **never fetched in 3,000,000,000 fetches (= 88.6 s at the PSX's 33.87 MHz)** | reached at 268,652,329 native + 3,827,708 fallback = **272,480,037 guest cycles** (≈8.0 s of guest CPU time) | **host-only** |

"identical" is `ProbeGuestState.Compare`: PC, `r1`..`r31`, HI/LO, SR/CAUSE/EPC/BadVAddr, all of the 2 MiB of main
RAM (SHA-256), VRAM SHA-256, `gpustat`, and every captured device register including `guest_cycles`. That equality
is a comparison of the **state at the checkpoint instant** (the PC's first fetch); the cycles *between* checkpoints
are not observed, so it proves "no divergence was observed at this checkpoint", not "no divergence exists at any
earlier cycle". At that checkpoint the two engines are bit-identical and then separate: the host is at the movie
player's MDEC sync after ≈272.5M guest cycles, while the interpreter at 3,000,000,000 fetches has not reached it
and ends at `0x8008A248`. The two runs also occupy different loops: the standalone interpreter spends its 3G
budget in one hot function around `0x8009284C` (46.1 M calls; `--pc-histogram`), while the host's dominant fallback
targets are `0x8008AECC`..`0x8008AF18` and `0x80088378` (626,572 / 584,779 retired instructions). That is a
distribution comparison, not a checkpoint comparison, but it is the shape a path divergence has.

Classification of the hypotheses:

- **H1 (a mere phase/progress offset): not observed up to the last shared checkpoint; INCONCLUSIVE after it.**
  At 268,664,967 guest cycles the two engines are bit-identical, not merely close, in CPU, RAM, device time and
  VRAM, so no phase offset exists at that instant. That single-instant equality does not bound the phase or
  scheduler difference *after* the checkpoint, and nothing is observed between it and the separation, so
  post-checkpoint phase divergence is neither confirmed nor rejected.
- **H2 (CPU execution path): a control-flow difference is observed after the checkpoint.** After that checkpoint
  the same instruction semantics (the generated host's fallback *is* the interpreter engine) follow a different
  guest control flow, so the difference lies in the instructions executed between `0x8008A484` and `0x80081BC8`
  and in the native/fallback handoff around them. The observed locus does not identify the specific defect.
- **H3 (GPU/VRAM mismatch): no mismatch observed at the compared checkpoints.** VRAM SHA-256 and `gpustat` are
  equal at every compared checkpoint, but no checkpoint after the separation is available, so a later GPU/VRAM
  difference is not excluded.
- **H4 (frame/PNG conversion): INCONCLUSIVE — the probe's frame evidence cannot settle it.** The probe reads VRAM
  as 15-bit, so a 24-bit display looks striped and a black frame is not evidence of black VRAM (see the Persona
  section above). Equally, VRAM/`gpustat` checkpoint equality is a *GPU-state* equality, not a rendered-image
  equality, so it neither confirms nor denies the frame/PNG conversion path. Settling H4 needs a correct 24-bit
  display capture.
- **H5 (another device or scheduling difference): no mismatch observed at the compared checkpoints.** Every
  captured device register, `guest_cycles` and the first interrupt entry are equal at the checkpoint, and nothing
  is captured after it, so a later device or scheduling difference is not excluded.

**The specific defect is not identified.** Which engine deviates is not decided by this evidence (the interpreter
is the project's reference, so the presumption is the host), and no comparison point exists after the divergence
because of the tooling limits below. This is a measured locus, not a root cause.

Tooling limits that bound the above (they are properties of the differential, not of the engines):

- the host captures a checkpoint only when the **counted fallback** fetches that PC; a PC inside a compiled native
  block is unobservable ("not reached by the generated host"), so `0x80010000` and `0x80084B58` cannot be compared
  on the host at all, and the tool cannot say whether a missing host boundary means "executed natively" or "never
  reached";
- the host's `atFetch`, `milestones`, `InterruptEntries` and `TitleEntryPc` count **fallback fetches only**, so they
  must never be compared with the interpreter's fetch counts or milestones (compare state at the same PC, or
  `guest_cycles`, instead);
- `interpretedEntries` is the sorted set of observed fallback PCs, not an execution order.

One `--stop-at` run also reported the `0x80081BC8` stop as `ARTIFACT_FALLBACK_BUDGET_EXHAUSTED` ("retired its
3000000000-instruction budget") although the whole run retired 3,827,708 fallback instructions: a probe
`StopRequested` ended the segment exactly as exhaustion does and the report kept the exhaustion message. That
false diagnostic is fixed on this branch (`ARTIFACT_FALLBACK_STOP_REQUESTED`); it was reporting noise, not a
retired budget.

**Relation to #764.** #764 (host-resolved indirect resume, the `J` protocol, `--diagnostics`) is merged and this
branch builds on it. `RuntimeHost` now honors `--diagnostics` for a firmware distribution as well: the launcher
writes the same Issue #763 stop diagnostic (`RuntimeHostDiagnostic.From`/`FromFailure`, best-effort — the run's
classified exit code and `firmware-run` line are unchanged), off by default. `ForPrebuiltArtifact` takes the `disc`
as part of the one guest state.

#### BIOS: never an AOT input; run-time provenance (Issue #B2)

**Primary guarantee: the BIOS is never an input of ahead-of-time generation.** `BiosAddressGuard` refuses BIOS code at four
stages, and each stage is wired through `BiosGuardWiring.Run` in `FirmwareDistribution`:

| Stage | Refuses | Negative test (production path, `BiosWiringIntegrationTests`) |
|---|---|---|
| manifest | a `rom` image (BIOS bytes copied to RAM), an image overlapping the BIOS range, a source kind other than `boot-exe`/`disc-exe`/`exe-file`, AOT inputs that together hold more than 224 bytes of BIOS code (cumulative, Issue #B2) | `ProductionBuild_RefusesTheBiosRangeImage_AtTheManifestStage` |
| plan | a loaded block starting in or overlapping the BIOS range | `WithTheManifestStageDisabled_ThePlanStageRefuses` |
| dispatch | the same check on the dispatch the artifact is generated from | `WithTheManifestAndPlanStagesDisabled_TheDispatchStageRefuses` |
| source | a generated `recompiler_block_0x…` name at a BIOS address | `WithThreeStagesDisabled_TheSourceStageRefuses_OnTheGeneratedBlockName` |

Address aliases (KUSEG, KSEG0, KSEG1, physical) are reduced by masking `0x1FFFFFFF` before the range test. The load-bearing
proof: with all four stages disabled, the production path does generate a block at `0xBFC00000`
(`WithAllFourStagesDisabled_TheBiosBlockReachesTheGeneratedSource`), so the refusals are what keep BIOS code out.

**Disc code and the BIOS: cumulative copy rule (Issue #B2, owner ruling).** Disc code is AOT input (`boot-exe`,
`disc-exe`, `exe-file`) and is not refused for sharing bytes with the BIOS by chance. The BIOS ROM itself, and RAM copies of
BIOS code, are never AOT input. The copy test is cumulative over all AOT inputs of one packaging:

- A **match run** is a run of words that equal a run of the BIOS, at least `MinRunWords` = 16 words (64 bytes) long. Uniform
  runs (padding, such as zero words) are never evidence. Shorter matches are coincidences and are not counted.
- The measure is the **union** of the BIOS words covered by match runs, over all inputs together. A BIOS word covered by
  several runs, in one input or in several, is counted once. The measure is not a sum.
- The inputs together may hold at most `CopyThresholdBytes` = **224 bytes** (56 words) of BIOS code. Above that the packaging
  is refused, with the total named (`the AOT inputs together hold N bytes of BIOS code`).

**Why 16 words and 224 bytes (measured).** The accidental BIOS coverage of non-BIOS binaries, per file, measured with
`BiosCopyMeasurementTests` (a skipped measurement, run with `PSXRECOMP_MEASURE_DIR`):

| Minimum run | System32 `*.exe`/`*.dll` (400 files): p50 / p90 / max, bytes | Project's own built binaries (37 files): max, bytes |
|---|---|---|
| 4 words | 580 / 1084 / 6184 | 1084 |
| 8 words | 0 / 0 / 224 | 56 |
| 16 words | 0 / 0 / **192** | 0 |
| 32 words | 0 / 0 / 0 | 0 |

- At 8 words the accidental maximum (224 bytes) reaches the threshold, so 8 is too short. At 4 words, short runs are
  common in any structured data, so 4 is far too short. At 32 words a split 127-byte copy (31 words per half) is not counted,
  so 32 is too long. 16 words is the shortest run whose accidental maximum stays under the detection requirement.
- The threshold 224 bytes is above the measured accidental maximum (192 bytes, one system DLL) and below the required
  detections: a 254-byte copy (63 whole words, 252 bytes) and a 127-byte copy split in two (62 words, 248 bytes) are refused.
- **Limits of the measurement.** No real PS1 game disc is available locally. The measured corpus is x86 system binaries and the
  project's own binaries, not PS1 disc code. The threshold is therefore justified by a proxy distribution. A disc whose code
  shares many 16-word runs with the BIOS would be refused, and that is intended; a disc sharing fewer does pass. The measure
  does not establish that any accepted code is not BIOS-derived.

**Binary window scan (supporting only, `DistributionLeakScanner`).** 8-byte windows of the BIOS at every offset are matched
against every file of the packaged distribution. A match is excused when the identical window is also held by a non-BIOS
baseline (the same packaging with a synthetic image, and the game disc). The excuse is content identity, for any window shape.
Limits, stated plainly:

- A BIOS fragment shorter than 8 bytes is never matched.
- A BIOS fragment built only from windows the baseline also holds is not detected (`ASharedOnlyBiosFragment_IsNotDetectedByTheBinaryCheck_ThePinnedLimit`).
- Windows that are mostly `0x00`/`0xFF` are excused when the baseline holds them, which is almost always: the real-image
  run found 104,590 such occurrences in the packaged output, all excused. This is a measured false-negative path.
- The scan does not prove zero leakage. It does not establish the origin of code; only the generation guard is a guarantee.

**Provenance, decided at run time (MAJOR-1).** The run classifies the image it loads by SHA-256 (`BiosProvenance`). The
packaging-time declaration (`--bios-provenance`) is recorded for comparison only and is never evidence.

- `openbios-rom`: the digest is in `BiosProvenance.VerifiedOpenBiosSha256`, an allowlist of independently verified OpenBIOS
  builds. **The allowlist is empty.** No build has an independently verified, reproducible procedure from a pinned upstream
  commit (Issues #794, #732, #730). The local build used for the real-image gate is NOT registered.
- `openbios-synthetic`: the digest is in `BiosProvenance.KnownSyntheticSha256`, the digests of this project's deterministic
  test stubs (SyntheticRom, the reproducible BIOS-shaped random image, RomWithGame). A stub is not BIOS code.
- `openbios-unverified`: any other image. It may run locally. It is never a verified OpenBIOS run, and nothing in the docs or
  the logs calls such a run a success.
- An unregistered image is never classified synthetic automatically. A declaration that the run does not confirm produces a
  `runtime host: warning:` line.

**Run evidence (real image, classified `openbios-unverified`, code of `826eb4f`, HEAD `c06c0c2`):**

- Generation stage: `aotBiosBlocks` = 0 and `aotLoadedBlocks` = 0 on the `firmware-run` line.
- Unbounded run: `RuntimeFailure` with `ARTIFACT_TIMEOUT` at the 300-second bound. The fallback count is null: the interpreter's
  single fallback segment never ends, so its count is never collected. Nothing about the BIOS's progress can be read from it.
- Bounded run (fallback budget 2,000,000): `RuntimeFailure` with `ARTIFACT_FALLBACK_BUDGET_EXHAUSTED`, a classified stop.
  `fallbackInstructions` = 1,999,999, `nativeInstructions` = 0, `fallbackTransitions` = 1. The `firmware-run` line carries the
  interpreter's own stop message (`diagnosticMessage`): "it stopped at pc 0x800327FC". That is the interpreter's final PC for
  the run, read from the run's output (the message is written by `InterpreterTitleExecutionEngine` at the budget stop).
  The `guestPc` field (`0xBFC00000`) is not that PC; see below.
- Progress of the BIOS, from a separate probe: `psxrecomp openbios-probe <image> --segment-budget 2000000 --segments 1 --json`
  (its own run of `InterpreterTitleExecutionEngine`) ends with `guestPc` = 2147685912 = `0x80031618`, `KernelRunningFromRam`,
  `VectorsInstalled` and `ShellEntered` all true. The record is `scratchpad/probe-2m.json`. The BIOS leaves the reset vector.

**Why the `guestPc` field of the bounded run is `0xBFC00000`.** It is the artifact process's own snapshot, parsed from its stdout
(`RecompiledHostExecutionEngine`, `ArtifactSnapshotParser`). The interpreter's fallback runs in the host, and its final state is
not written back into that snapshot, so the field is the segment's entry state. The run's stop message above is the value to
read. Mixed-fallback progress is therefore verified by the stop message and by the probe, not by `guestPc`.

**Packaging and exceptions.**

- The packaged executable loads the BIOS at run time from the path named by `PSXRECOMP_BIOS_ROM` (512 KiB exactly; a direct run
  exits with **95** on another size, the Runtime host with **78**). The packaging pins no digest of the BIOS.
- Path exemption (one only): `runtime/PSXRecomp.RuntimeHost.exe` contains the .NET SDK apphost template path
  `D:\a\_work\1\s\src\runtime\artifacts\obj\win-x64.Release\corehost\apphost\standalone\`, a Microsoft CI build path,
  not a developer path. Matched exactly (case-insensitive) in that relative file only. Removing it needs `UseAppHost=false` (owner decision pending).
- The generated executable and the native library are stripped (`-s`). The Runtime host is published with `DebugType=none`.
  The intermediate object is removed after linking; a failed removal fails the build.

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
mixed-execution interpreter fallback, which is reported as fallback. The corrected
synthetic EXE has been loaded and its marker executed on both engines. The #744
implementation verifies strict equality at Shell, first SYSCALL, first hardware
IRQ, EXE entry and marker: CPU/COP0/RAM/scratchpad/device registers/timers and
modeled guest cycles match. The first IRQ is accepted at cycle 169936497,
EPC 0x15BC; EXE entry is cycle 195062240 and marker is cycle 195062475 on both
engines. This remains a synthetic fixture gate, not commercial-game verification
or physical PS1 timing accuracy.

Historical #745 measurements showed first IRQ +604 and EXE +46894 cycles. Merely
reducing its report interval left residual mismatches. #744 fixes the common
Runtime deadline and fused-retirement semantics; the reporting cap remains 1024,
with no timer offsets. Durable evidence and resource measurements are in #744
and its stacked Draft PR.
For multi-image dispatch, generated switches are partitioned by 4 KiB PC pages: Clang 18 at `-O0` otherwise emits
thousands of linear comparisons for the sparse cross-image switch. This partition preserves full-PC lookup,
version selection, unknown-PC fallback and instruction accounting; ROM-only generation retains its existing shape.

**Status: the real OpenBIOS boots on the interpreter, loads and enters an executable from a disc
through its own CD driver (synthetic disc and Persona); COP2/GTE executes (#447: transfers,
LWC2/SWC2, RTPS/NCLIP/AVSZ3/AVSZ4). Persona's intro movie decodes and displays (CD XA routing, GPU DMA2, MDEC, #732). Not done: a
Persona title screen; further GTE
commands, added when a run reaches them; generated-host execution/parity; OpenBIOS as the default `run`
backend; redistribution approval (#730).**

### CD-ROM event ordering

When a guest-time advance crosses multiple pending CD-ROM response or sector
deadlines, the device processes each event in chronological order rather than
processing an entire type of event before the other. This preserves observable
IRQ/response ordering even when host execution advances time in large chunks.

### Generated memory-fault coverage (Issue #749)

Generated firmware now delivers aligned LH/LHU/LW AdEL and SH/SW AdES before
RAM/MMIO effects, with virtual BadVAddr and existing EPC/BD/SR exception entry.
Synthetic regressions compare full CPU/COP0/RAM/device state and guest cycles at
the vector, pending-load commit, branch/link prefixes, IsC, and RFE return.
Partial-word LWL/LWR/SWL/SWR remain legal. These focused results do not replace
strict real-OpenBIOS parity on the integrated stack or establish compatibility
with every firmware exception or real game.

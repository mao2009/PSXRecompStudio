# Issue #807: provenance of guest RAM at 0x80030C44 (OpenBIOS boot)

Tracking: [#807](https://github.com/mao2009/PSXRecompStudio/issues/807). Related: [#773](https://github.com/mao2009/PSXRecompStudio/issues/773) (root discovery), [#792](https://github.com/mao2009/PSXRecompStudio/issues/792) (indirect roots), PR #784.

Status: **analysis only**. No code is changed. Code evidence is from branch `batch2/w03-ram-provenance-analysis`, based on `origin/main` `2e3ecad`. The Persona disc, the pinned OpenBIOS ROM and their bytes are not in the repository and were not read for this document. No BIOS or ROM bytes are recorded here; only addresses and `file:line` references.

Labels:

- **CONFIRMED**: read in the source or in the design docs at the cited location.
- **OBSERVED**: a measurement recorded in a repository document (cited). Not re-run for this analysis.
- **PREDICTED**: an inference from CONFIRMED facts that a run or a disassembly must test.
- **UNVERIFIED**: unknown from the repository. It needs data outside the repository (the disc, the pinned ROM's ELF symbols) or a new run.

## 0. Question and answer in brief

The issue asks which code writes `0x80030C44` during an OpenBIOS boot of a boot EXE, when, and from which PC, and whether the boot EXE load range covers that address.

Short answers:

1. **0x80030C44 is inside the OpenBIOS shell image, not inside the game image.** CONFIRMED (`src/PSXRecomp.Core/Execution/OpenBiosBootMonitor.cs:50`, `:53`; `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:40-48`). The shell occupies `0x80030000` to `0x800450C0` (shell image plus bss, per the comment at `OpenBiosBootMonitor.cs:52`). The offset of the address in the shell is `0xC44`.
2. **Whether the boot EXE covers `0x80030C44` is UNVERIFIED.** The entry is `0x80010000` (OBSERVED, `docs/runtime/openbios-firmware-backend.md:136`, `:196`). A text region starting at `0x80010000` reaches `0x80030C44` only if its declared size is at least `0x20C45` bytes (`0x80030C44 - 0x80010000 + 1`). The Persona header's text size is not in the repository.
3. **The writer is not identified.** Static analysis narrows it to a short list (section 4). Identifying the writing PC and the time needs a run with a watch on that word (section 5), which is design only here.
4. **A "not matched" verdict at this PC does not prove an overwrite.** The accounting labels any RAM PC without an AOT block as `ram-no-code-image` (`src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:55-66`), whether or not a version was ever registered for it. CONFIRMED by code; the consequence is PREDICTED to be the common case here.

## 1. Boot EXE load range

### 1.1 How the boot EXE gets into RAM in the OpenBIOS path

- The host does not load the boot EXE into RAM in the OpenBIOS engine. `OpenBiosBootBackend.CreateEngine` builds an `InterpreterTitleExecutionEngine` over the ROM words at `0xBFC00000` and does not pass `guestFirmware` (`src/PSXRecomp.Core/Execution/OpenBiosFirmware.cs:68-80`). The host-side RAM writes in `InterpreterTitleExecutionEngine` (`src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:186-193` for `guestFirmware`, `:371-377` for the title path) are not reached in this path. CONFIRMED.
- OpenBIOS itself loads the EXE: its CD driver reads the disc through `CdRomDevice`, and DMA3 writes the data into RAM. `exec` then jumps to the entry. OBSERVED (`docs/runtime/openbios-firmware-backend.md:133-137`: "loads the boot EXE named by `SYSTEM.CNF` through CD reads and DMA3, and `gameMainThunk` → `exec` enters the executable's entry `0x80010000`"). The ADR says the same at `docs/adr/026-aot-ram-placed-code.md:10`. CONFIRMED as a design statement.
- The host models the DMA3 transfer in `CdRomDmaTransfer.TryTransfer` (`src/PSXRecomp.Core/Dma/CdRomDmaTransfer.cs:80-137`). It writes `words = BCR` 32-bit words starting at `MADR & AddressMask` through `IMemoryBus.Write`, one word per iteration (`:118-128`), with no PC in the record. CONFIRMED.
- The memory bus routes RAM writes to the native core RAM (`src/PSXRecomp.Core/Dma/MemoryBus.cs:139-152`, `WriteRam` at `:184-190`). CONFIRMED.

### 1.2 The header fields that define the range

| Field | File offset | Parsed at | Meaning |
|---|---|---|---|
| Entry PC | `0x10` | `src/PSXRecomp.Core/DiscImage/PsxExeHeader.cs:61` | Initial PC |
| GP | `0x14` | `PsxExeHeader.cs:44` | Initial GP |
| Text start (load address) | `0x18` | `PsxExeHeader.cs:63` | Destination of the text |
| Text size | `0x1C` | `PsxExeHeader.cs:64` | Declared text length in bytes |
| Data start / size | `0x20` / `0x24` | `PsxExeHeader.cs:65-66` | Data destination and length |
| BSS start / size | `0x28` / `0x2C` | `PsxExeHeader.cs:67-68` | Zero-fill region (the OpenBIOS behaviour is UNVERIFIED, see 4.3) |
| SP base / offset | `0x30` / `0x34` | `PsxExeHeader.cs:50-52` | Initial SP |

CONFIRMED: the fields are parsed at those offsets. The text bytes start at file offset `0x800` (`src/PSXRecomp.Core/DiscImage/PsxExe.cs:37`) and `PsxExe.Load` keeps `min(TextSize, available)` bytes (`PsxExe.cs:39-44`). A zero `TextSize` means "all available bytes" (`PsxExe.cs:39-40`).

The repository checks the text region in two places: `PsxExeTitleInput.Build` rejects a truncated text segment when `TextSize != 0` (`src/PSXRecomp.Core/Execution/PsxExeTitleInput.cs:77-82`) and requires the text to map to a contiguous physical span (`:97-104`). These checks are about the host's own view and do not establish what OpenBIOS's loader writes.

### 1.3 Range for the real disc

- Entry `0x80010000`: OBSERVED (`openbios-firmware-backend.md:136`, `:196`; also `:404`, `:497`).
- Text start: PREDICTED to be `0x80010000` (the usual PS-X layout, and the entry is at the text start for this disc). UNVERIFIED for the Persona EXE.
- Text size: UNVERIFIED. The repository states that the original deterministic fixture declares only 20 text bytes (`openbios-firmware-backend.md:379`) and that the pinned OpenBIOS `dev_cd_read` rejects reads not aligned to 2048 bytes, while `loadExe` ignores the failure (`:379-381`). That is a fixture, not Persona.
- **Threshold.** With text start `0x80010000`, the text reaches the shell only if `TextSize >= 0x20C45` (134,213 bytes). Any larger declared size or any bss/data extent above `0x80010000 + 0x20C44` overlaps `0x80030C44`. CONFIRMED arithmetic.
- **Sector rounding.** PREDICTED: if the loader reads whole 2048-byte sectors (the fixture note at `:379-381` says the pinned reads are 2048-byte aligned), the number of bytes DMA3 writes is rounded up past the declared `TextSize`. A text size just below the threshold would then still write `0x80030C44`. UNVERIFIED for the pinned OpenBIOS.

## 2. RAM ownership between OpenBIOS and the game

Region definitions are CONFIRMED in `src/PSXRecomp.Cli/OpenBiosProbeAccounting.cs:40-48`:

| Region | Physical range (RAM offset) | Owner | Source |
|---|---|---|---|
| kernel-ram | `< 0x10000` | OpenBIOS kernel (copied from ROM to `0x500`+, per `docs/runtime/openbios-firmware-backend.md:987`) | `OpenBiosBootMonitor.cs:56` (`KernelRamStart`/`KernelRamEnd`) |
| shell | `0x30000` to `0x450C0` | OpenBIOS shell image and bss | `OpenBiosBootMonitor.cs:50`, `:53`; `OpenBiosProbeAccounting.cs:47` |
| user-ram | other RAM, including mirrors | Boot EXE and overlays; anything else | `OpenBiosProbeAccounting.cs:47`; `openbios-firmware-backend.md:238` |

Consequences:

- `0x80030C44` is physical `0x00030C44`, offset `0xC44` into the shell image. It is classified **shell** (CONFIRMED by the arithmetic of `OpenBiosProbeAccounting.cs:47`).
- The title detector only counts a PC as a started title when it lies in `0x80010000..0x8002FFFF` or `ShellImageEnd..0x80200000` after the shell is entered (`OpenBiosBootMonitor.cs:86`). A title, or an EXE load, over the shell's own range is **not detected** (the report's own note at `OpenBiosBootMonitor.cs:24`). CONFIRMED.
- The shell is copied into RAM by OpenBIOS code, not by the host (`OpenBiosBootMonitor.cs:49` says "Where OpenBIOS (like the retail BIOS) copies its shell and calls it"; ADR-026 `:10`). CONFIRMED as a design statement; the copy routine is UNVERIFIED (4.1).
- The game image is `0x80010000` to its text end, plus any data and bss, and it is expected to be disjoint from the shell. PREDICTED for normal discs; the threshold in 1.3 is the test.
- **Fallback at this PC.** OBSERVED (`openbios-firmware-backend.md:401`): one fallback segment of 163,124,226 instructions started at `0x80030C44` in the shell region. The accounting said shell = 35 % of fallback fetches. The same document says the segment was the interpreter running shell code, not AOT (`:401`, `:498`: the shell "runs interpreted from the BIOS image"). CONFIRMED that the shell runs interpreted; the segment's loop is UNVERIFIED.
- `0x80030C44` appears in tests only as synthetic input (`src/PSXRecomp.Tests/Recompiler/LoadedCodeMismatchDiagnosticsTests.cs:11`, `src/PSXRecomp.Tests/Infrastructure/RuntimeHostDiagnosticTests.cs:290-291`, `src/PSXRecomp.Tests/Cli/OpenBiosProbeAccountingTests.cs:34`). These fixtures are not evidence about the disc.

## 3. What "overwritten" can and cannot mean in this code

- **Mismatch versus absence.** The loaded-code table keys versions by entry PC and exact words (`src/PSXRecomp.Core/Recompiler/LoadedCodeTable.cs:5-11`; the table keyed by `EntryPc` is in ADR-026 `:27-28`). A PC with no version is `ram-no-code-image` (`OpenBiosProbeAccounting.cs:55-66`). So the handoff report at `0x80030C44` cannot distinguish "RAM changed under a registered version" from "no version was ever registered here". CONFIRMED by code.
- **The expected value.** The issue speaks of an expected value. No artifact in the repository records the expected word at `0x80030C44` for this disc. The shell's words are ROM bytes (not reproduced here) copied by the kernel. UNVERIFIED which source the expectation came from (ROM copy, a manifest `roots`/`images` entry, or an earlier run).
- **Refusal behaviour (must not change).** A version whose words do not match RAM is refused and falls back; `LoadedCodeTable` selection and the guard compare the words (`docs/runtime/loaded-code-identity.md` section 2, rows 4 and 6). Issue #807 forbids adopting roots over a mismatch. This analysis does not touch that guard or the root planner (`src/PSXRecomp.Core/Recompiler/ObservedRootPlanner.cs:179`, owned by PR #849 and #773).

## 4. Candidate writers

Each candidate lists the writer, the PC that would be reported, the time window, and the evidence that would confirm or exclude it. Ranking is by PREDICTED likelihood, not by evidence.

### 4.1 OpenBIOS kernel shell copy (ROM to shell range)

- Mechanism: the kernel copies the shell image into `0x80030000` before it calls the shell (`OpenBiosBootMonitor.cs:49`; ADR-026 `:10`). The copy is CPU stores.
- PC: kernel code (ROM `0xBFC...` or the copied kernel in RAM below `0x10000`). Not in the shell range.
- Time: before the first fetch of `0x80030000` (`OpenBiosBootMonitor.cs:90`, `_shell` set), i.e. before `ShellEntered`.
- Predicts: a write that is complete before shell entry, and no later write from kernel PCs unless the kernel re-copies.
- Status: PREDICTED that it writes `0x80030C44` once. UNVERIFIED the copy routine and its length. A disassembly of the pinned ELF, with symbols only (no bytes in git), is needed.

### 4.2 Boot EXE CD load by DMA3 (CdRomDmaTransfer)

- Mechanism: the EXE text (and possibly more) is read from the CD FIFO and written to RAM by `CdRomDmaTransfer.TryTransfer` (`CdRomDmaTransfer.cs:80-137`), starting at `MADR`.
- PC: the DMA has no PC. The reported PC would be the last CPU fetch before the DMA's completion (the instruction that started it is inside the loader), which is the attribution rule to use (section 5.2).
- Time: during `loadExe`, before the first fetch of `0x80010000` (`openbios-firmware-backend.md:136`).
- Condition: the declared text end, or the sector-rounded transfer end, reaches `0x80030C44`. See 1.3. Without that condition this candidate is excluded.
- Status: PREDICTED to be the most direct overwrite of shell data if the Persona EXE's text is at least `0x20C45` bytes. UNVERIFIED for Persona.

### 4.3 BSS clear by OpenBIOS `exec`

- Mechanism: a loader that follows the PS-X EXE convention clears `BssStart`/`BssSize` (`PsxExeHeader.cs:67-68`). If OpenBIOS does this and the bss range reaches `0x80030C44`, zeros are written over shell data.
- PC: loader code in the ROM or kernel.
- Time: after the EXE load, before the entry `0x80010000`.
- Status: UNVERIFIED whether the pinned OpenBIOS clears bss and to what range. The bss range is in the Persona header (UNVERIFIED).

### 4.4 Shell's own stores

- Mechanism: a store in shell code to its own variable at offset `0xC44`, or a stray pointer in shell code.
- PC: in `0x80030000..0x800450C0`. The shell runs interpreted (`openbios-firmware-backend.md:498`).
- Time: during shell execution, after `ShellEntered`.
- Status: PREDICTED possible; cannot be excluded by static analysis of the repository alone. A store in a shell PC that changes a value the shell itself reads is the signature of a legitimate shell variable.

### 4.5 Game (user RAM) stores

- Mechanism: a store from the executable (for example a stray pointer, or a data write into the wrong range) after `TitleStarted`.
- PC: `0x80010000` and above, outside the shell range.
- Time: after the entry.
- Status: PREDICTED possible. Note that the title detector does not see stores into the shell range (1.3, `OpenBiosBootMonitor.cs:86`), so a title store is only visible through a store-level trace.

### 4.6 Host-side resynchronisation in the artifact path (not OpenBIOS)

- Mechanism: in the generated-host artifact path, `ArtifactFallbackSession.ApplyToCore` (`src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:476-498`) copies the agreed `_synced` page back into core RAM wherever the core differs, then applies incoming pages. A page whose core copy diverged without being reported could be rewritten with an older image. `WriteBack` (`:500`) does the reverse direction.
- PC: none; a host write.
- Time: at each fallback entry and return.
- Status: PREDICTED possible only in the generated-host path. The OpenBIOS probe in interpreter mode (`OpenBiosProbeCommand.cs:301`) does not use this path. It must be checked by comparing shell words between interpreter-only and generated-host runs.

### 4.7 Native artifact stores (AOT)

- Mechanism: generated C writes RAM through `artifact_ram_write8` and the 16/32-bit stores (bumping the per-page generation; `docs/runtime/loaded-code-identity.md` section 2, row 5).
- PC: an AOT block PC. These stores are visible in the native trace only.
- Status: CONFIRMED that such stores bump generations. Whether any AOT block writes this word: UNVERIFIED.

### 4.8 Startup writers (ruled out for mid-run writes)

- `memset(artifact_ram, 0, ...)` at startup in the generated `main` (`docs/runtime/loaded-code-identity.md` section 2, row 5, citing `RecompiledArtifactCodeGen.cs:1023`, `:1836`) is before any dispatch and cannot change `0x80030C44` after the boot starts. CONFIRMED by the cited design doc; the artifact-path initial load of an embedded image (`RecompiledArtifactCodeGen.cs:1031-1045`) is the standalone file image path, not the OpenBIOS path. CONFIRMED that it is excluded from the OpenBIOS path.

## 5. Identifying the writing PC and time (design only)

No code is changed here. The following is the design to be implemented after this analysis is reviewed.

### 5.1 Time base

- The fetch index: `OpenBiosBootMonitor` counts fetches (`_fetches`, `OpenBiosBootMonitor.cs:70`). Its milestones (`ResetVectorFetched`, `KernelRunningFromRam`, `ShellEntered`, `TitleStarted`) are already reported.
- The guest cycle count (`ProbeGuestState`, compared per checkpoint; see `openbios-firmware-backend.md:639`). The fetch index and the cycle count differ by interrupt entries (the doc gives cycles = fetches + interrupt entries, `openbios-firmware-backend.md:634`). Use both. The device capture includes `guest_cycles` (`openbios-firmware-backend.md:639`).

### 5.2 Attributing a CPU store

- The CPU core is native (`src/PSXRecomp.Core/NativeInterop.cs`, `PSXCoreWrapper.ReadMemory32` at `src/PSXRecomp.Core/PSXCoreWrapper.cs:639`). A store hook therefore belongs in the native core's memory write path, not in managed code. The native source location is UNVERIFIED for this repository layout.
- Managed-side alternative (design option A): the interpreter calls `FetchObserver` once per fetch (`src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs:282`, `:679`). Reading the watched word there (`PSXCoreWrapper.cs:639`) at each fetch gives the first fetch after a change. The writer is the instruction retired just before that fetch. Cost: one P/Invoke per fetch, so it must be bounded (for example by a stop PC or a fetch window). Exceptions and delay slots make "the previous PC" ambiguous; record the previous two PCs and the CAUSE/BD bit.
- Design option B (cheaper, DMA only): log in `CdRomDmaTransfer.TryTransfer` (`CdRomDmaTransfer.cs:80`) the `MADR`, `BCR`, the transfer end, and the last fetched PC. This answers 4.2 directly.

### 5.3 Record format (proposed)

For each change of the watched word at `0x80030C44` (physical `0x00030C44`): fetch index, guest cycle, previous PC and word, new word, old word, and the writing source: `cpu-store` (native hook or option A), `dma3` (option B), or `host-sync` (4.6). Dump the shell words `0x80030000..0x800450C0` at `ShellEntered` and at `TitleStarted` as a checksum only, so no BIOS bytes are written to git or logs.

### 5.4 Guardrails

- No BIOS or ROM bytes in any artifact (the existing rule). The record stores addresses, words of the EXE (the user's own disc), and counts. A word of the pinned ROM must not be written out; compare with the ROM through a hash of the region, computed locally, if needed.
- The watch must not change execution: it observes, like `OpenBiosBootMonitor` ("Pure observation", `OpenBiosBootMonitor.cs:44`).

## 6. How dynamic code loading relates

- The contract already separates code identity from RAM words. A loaded overlay or a legitimate switch is a new version at a PC, with its own words (ADR-026 `:27-29`; `LoadedCodeTable`). The guard then selects the version whose words match RAM, and refuses the rest (`docs/runtime/loaded-code-identity.md` section 2). CONFIRMED.
- Therefore a legitimate code switch at `0x80030C44` is not an error by itself. It appears as a RAM mismatch for the old version, and the correct behaviour is a refusal followed by fallback or by the matching version. The mismatch record (`LoadedCodeMismatchDiagnostics`, #773 Phase B) exists to show that.
- To tell a legitimate overlay write from a bug, the record in 5.3 needs the writer's source: a DMA of an overlay sector (`dma3`), a copy routine in a known loader PC, or a stray store. A DMA3 write from a loader that is also the disc's EXE load is the normal case for a boot EXE (4.2), not an overwrite of the shell, unless the range overlaps the shell (the 1.3 threshold).
- No change to the adoption guard is proposed. An overwrite must be explained by its writer; it may not be ignored by adopting roots.

## 7. Unresolved

1. The Persona boot EXE header: text start, text size, bss start and size (1.3). This is the first thing to read, locally, and to record as addresses and sizes only.
2. Whether the pinned OpenBIOS `loadExe`/`exec` writes beyond the declared text size (sector rounding, 1.3) and whether it clears bss (4.3). Needs the pinned ELF symbols and a disassembly kept out of git.
3. The shell copy routine and its length (4.1).
4. The expected word at `0x80030C44` and its source (3). Not in the repository.
5. Whether the 163M-instruction shell segment at `0x80030C44` (OBSERVED, `openbios-firmware-backend.md:401`) is a loop that writes the word, or an idle loop that only reads it.
6. Whether the host-side resynchronisation (4.6) can write this page in the generated-host probe. Needs the comparison in 4.6.
7. The native core's store path for option 5.2 (location and hook point).

## 8. Next steps (not done here)

1. Read the Persona header (local, addresses only) and check the 1.3 threshold.
2. Run the existing probe with a stop PC or fetch window and log the value at `0x80030C44` at `ShellEntered` and `TitleStarted` (no code change: the probe already reports these milestones).
3. If the value changes, add the attribution of 5.2 (option A for a bounded window, option B for DMA) as a diagnostic, after review. It must stay observation only.
4. Only after 1 to 3 pick the candidate of section 4 and decide whether anything in the loaded-code path needs to change. The root planner and the adoption guard are out of scope (PR #849, #773).

# Loaded-code identity for verified AOT dispatch

Tracking: [#734](https://github.com/mao2009/PSXRecompStudio/issues/734). Related: [#732](https://github.com/mao2009/PSXRecompStudio/issues/732), [#773](https://github.com/mao2009/PSXRecompStudio/issues/773), [#840](https://github.com/mao2009/PSXRecompStudio/issues/840), [#249](https://github.com/mao2009/PSXRecompStudio/issues/249).

Status: **design only**. No code is changed by this document. Evidence is taken from `origin/main` at `385c645`.

- **CONFIRMED**: read in the source at the cited location.
- **INFERRED**: reasoned from the source. Not measured.

## 1. Problem

A PS-X EXE loaded by the guest (through OpenBIOS, or through the standalone artifact's file image) may run from RAM. Ahead-of-time (AOT) code generated from the same bytes can be dispatched in its place, but only while RAM still holds exactly the words the code was compiled from. Issue #734 asks for three things at once: an input identity hash, a per-dispatch check that is safe, and no full-region hash recomputation on the hot path.

This document separates the six concepts that the issue owner ruled must not be collapsed into one hash:

1. Input identity hash: which executable or disc the artifact was built from.
2. Compiled code range: which words a version covers.
3. Load address: where the image lives in guest memory.
4. Actual RAM instruction words: what guest memory holds now.
5. RAM write generation: a cheap signal that a write happened.
6. AOT mapping validity: whether a pc-to-version mapping is still trusted.

A hash of the input proves provenance. It does not prove what RAM holds at a given moment, because the guest can overwrite code, and a DMA or a store can change a page after load.

## 2. Current handling of each concept

| # | Concept | Current handling | Status | Where |
|---|---|---|---|---|
| 1 | Input identity hash | SHA-256 of the input is computed at load: whole EXE file (`CliInput.LoadExeWithIdentity`), whole CHD stream (`CliInput.LoadChdWithIdentity`), boot EXE (`RomAnalysisPipeline`), each manifest image (`SourceSha256`), disc image (`DiscImageSha256` into the report). It is stored in the project manifest (`InputSha256`). It is **not** consulted by the dispatch guard, by the fallback session, or by the generated C. The only runtime use is the manifest `roots` line, which must match the image's `SourceSha256` and `CodeWordsSha256` (`LoadImageManifest`, roots check). | CONFIRMED | `src/PSXRecomp.Cli/CliInput.cs:85`, `:105-109`; `src/PSXRecomp.Core/DiscImage/RomAnalysisPipeline.cs:259`, `:413`; `src/PSXRecomp.Core/Recompiler/LoadImageManifest.cs:356`, `:453-469`; `src/PSXRecomp.Core/Project/ProjectManifestDocument.cs:29`. No consumer of `InputSha256` outside `Project/` in production code (grep). |
| 1 | Provenance of the loaded image | `LoadedImageOrigin` is the manifest's declaration (name, kind, load address, word count). The source says it is "not a check of the file's bytes" and "proves no more than the manifest does". Diagnostics only. | CONFIRMED | `src/PSXRecomp.Core/Recompiler/LoadedCodeTable.cs:22-36` |
| 2 | Compiled code range | A `GuardedBlock` holds the block's whole fused unit: `Words` covers `RetiredInstructionCount` words from `EntryPc`. The generated C embeds these words as `codeN[]` and passes the count to the guard. The guard checks exactly that span. | CONFIRMED | `LoadedCodeTable.cs:6-11`; `src/PSXRecomp.Core/Recompiler/RecompilerHostCodeGen.cs:1283-1286`; `RecompiledArtifactCodeGen.cs:1157` (guard), `:1163-1169` (span sum) |
| 2 | Span bounds | `count == 0` and `count > PSX_RAM_SIZE/4` fail closed. The span must lie in RAM (`artifact_ram_offset`); ROM or mirror-overflow spans fail closed. | CONFIRMED | `RecompiledArtifactCodeGen.cs:1158-1162`, `:1116` (`PSX_RAM_SIZE` = 2 MiB) |
| 3 | Load address | The table is keyed by the virtual `EntryPc`, so the address is part of the identity. Two versions at the same pc with different words are kept as separate versions. The manifest checks the load address for roots. `LoadedImageOrigin.LoadAddress` is diagnostics only. The file image is loaded at an embedded address. | CONFIRMED (key); INFERRED (why the key must include the address: the lowering of absolute `j`/`jal` targets and PC-relative branches depends on `EntryPc`) | `LoadedCodeTable.cs:58` (group by `EntryPc`); `LoadImageManifest.cs:453-460` (load check); `RecompiledArtifactCodeGen.cs:453` (`embedded_image_load_address`) |
| 4 | Actual RAM words | Authoritative at dispatch. The C guard compares each word of the span when the span's generation sum differs from the last success. The C# `LoadedCodeTable.Match` does the same comparison, but production code does not call it (only `FirmwareAotPlanTests` does). | CONFIRMED | `RecompiledArtifactCodeGen.cs:1169-1176`; `src/PSXRecomp.Core/Recompiler/LoadedCodeTable.cs:89-104`; `src/PSXRecomp.Tests/Infrastructure/FirmwareAotPlanTests.cs:87-90` |
| 4 | Attribution of fallback entries | `ArtifactFallbackSession.Attribute` compares every version at the pc word by word, one `ReadMemory32` P/Invoke per word. It runs only when a transition observer is set. | CONFIRMED (code); INFERRED (cost only when observer on) | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:143-160`, `:295`; `src/PSXRecomp.Core/PSXCoreWrapper.cs:639-643` |
| 5 | RAM write generation | One `uint64` per 4 KiB page (`artifact_page_gen`). Bumped by the single-byte store, 16/32-bit stores, the startup loader for the file image (added by #840, commit `e821453`), and the fallback commit (K). The span check sums the generations of the pages the block covers and reruns the word compare only when the sum changed. | CONFIRMED | `RecompiledArtifactCodeGen.cs:1147-1155` (macro and loader), `:1205-1209` (`artifact_ram_write8`), `:1434-1460` (stores), `:1641-1642` (K commit) |
| 5 | Writers that do **not** bump | Two `memset(artifact_ram, 0, ...)` calls at startup, emitted inside the generated `main()`, before any dispatch. They are safe only because `main()` runs once per process and the caches start at `UINT64_MAX`. | CONFIRMED (no bump); INFERRED (safe under the single-`main` invariant) | `RecompiledArtifactCodeGen.cs:1023`, `:1836`; caches `RecompilerHostCodeGen.cs:1285` |
| 5 | C# side RAM | The interpreter's RAM is not generation-tracked. It reaches the native artifact only through a fallback commit (`B` stage then `K`), which bumps. Changes are detected on the C# side by a full compare against a shadow copy at every segment return. | CONFIRMED | `src/PSXRecomp.Infrastructure/ArtifactFallbackSession.cs:489-534` (`WriteBack`), `:337` (call) |
| 6 | AOT mapping (pc to versions) | Built at code generation time: versions per `EntryPc` in link order, identical words deduplicated. Emitted as a `switch` on pc, with one `if` per version. The table is fixed in the generated artifact. No runtime check compares it with the input hash. The artifact file hash is recorded for diagnostics at run time. | CONFIRMED | `LoadedCodeTable.cs:52-64`; `RecompilerHostCodeGen.cs:1276-1300`; `src/PSXRecomp.Cli/RunCommand.cs:191`, `:206` |
| 6 | Mismatched artifact | Because each version carries its own words, a stale or foreign version fails the word check and falls back. Safety does not depend on the input hash. Only provenance and diagnostics do. | INFERRED | `RecompiledArtifactCodeGen.cs:1169-1176`; `RecompilerHostCodeGen.cs:1286` |

### Dispatch rule as implemented

At a dispatch point for `pc` (generated code, `RecompilerHostCodeGen.cs:1276-1300`):

1. If `pc` has no version in the table, the generated code takes the unknown-pc boundary (no check).
2. For each version in link order, call `recompiler_code_current(core, pc, codeN, count, &seenN)`.
3. The guard returns 0 for an invalid span. It sums the generations of the covered pages. If the sum equals `*seenN`, it returns 1 without reading any word.
4. Otherwise it compares the span's words with `codeN`. On a match it stores the sum in `*seenN` and returns 1. On a mismatch it returns 0 and leaves `*seenN` unchanged.
5. If no version returns 1, the pc has no native code and the fallback path runs.

This is already the design the issue asks for: identity comes from the words, and a cheap generation check avoids rereading them.

## 3. Where full-region work can occur

"Full-region" means work proportional to the whole 2 MiB of RAM or to the whole input file. Work proportional to the block span, or to the number of pages a transition changed, is listed separately.

| Site | Trigger | Scope | Class | Assessment |
|---|---|---|---|---|
| `CliInput.cs:85`, `:105-109`; `RomAnalysisPipeline.cs:259` | Input load | Whole EXE or disc stream | Startup, once | Linear in input, acceptable for identity. Not on the dispatch path. |
| `LoadImageManifest.cs:356`, `:469` | Manifest load | Whole image text | Startup, once | Acceptable. |
| `OpenBiosProbeCommand.cs:998` (`FileSha256`) | Probe / CLI | Whole disc file | Startup, once | Acceptable. |
| `RunCommand.cs:191`, `:206` | Each diagnostic run | Whole artifact file | Once per run | Diagnostic only. |
| `RecompiledArtifactCodeGen.cs:1023`, `:1836` (`memset` of RAM) | Startup | 2 MiB | Startup, once | Acceptable. |
| `ArtifactFallbackSession.cs:489-502` (`WriteBack`): `core.CopyRamTo` (`PSXCoreWrapper.cs:203-212`), then `SequenceEqual` on all 512 pages, with an FNV `HashPage` of each dirty page inside the same loop | Every fallback segment that returns (`:337`) | **2 MiB copy and a full compare, every time, whatever changed**; hash over dirty pages only | Per transition | **The largest avoidable full-region cost on the mixed path.** INFERRED from code; not measured. |
| `RecompiledArtifactCodeGen.cs:1543` (`fallback_dirty_count`, called from `F` at `:1620`) | Each fallback query | 512 generation compares; a 4 KiB `memcmp` only for pages whose generation moved | Per transition | Cheap in steady state. |
| `RecompiledArtifactCodeGen.cs:1574` (`fallback_send_pages`), `:1519` (byte-wise FNV) | Each `Y` | Dirty pages, hex-encoded (8 KiB text per page) | Per transition | Proportional to dirty pages. |
| `RecompiledArtifactCodeGen.cs:1630-1634` (`fallback_stage_hash`) | Each `K` | Staged pages | Per transition | Proportional to staged pages. |
| `ArtifactFallbackSession.cs:143-160` (`Attribute`) | Each fallback entry, only with an observer | All versions at pc, word by word, one P/Invoke per word | Per transition, diagnostics | Bounded by block span times versions. |
| `LoadedCodeTable.cs:89-104` (`Match`) | Not called in production | Block span times versions | None in production | Must not be placed on the hot path without caching. |
| `RecompiledArtifactCodeGen.cs:1157-1177` (guard) | Each dispatch to a versioned pc | Pages spanned (at most 512 adds), then the block's words **only** if a page in the span moved | **Per dispatch** | No full-region hash. Cost is block-sized. A write into a span page forces one word compare of the block on the next dispatch. |

Judgement from the code:

- **No full-region hash runs per dispatch.** The hot path is an integer sum over the pages a block covers, plus a block-sized compare after a write into one of those pages. The issue's requirement is met on this path today.
- **Full-region work happens at startup and on each fallback transition.** The transition cost is the one to watch. `WriteBack` copies and compares 2 MiB on every fallback return even when one page changed. Replacing that with dirty tracking would remove the cost, but this needs a measurement first (none was taken here).
- **The residual dispatch risk is false invalidation.** Generations are per 4 KiB page. Data writes into a page that also holds a large block's span force a block-sized word compare. Its frequency is unknown without a measurement.

## 4. Proposed design

### Principle

Separate **identity** (what the artifact was built from, decided once, used for provenance and startup gating) from **validity** (is this code still in RAM now, decided at every dispatch). The hash belongs to identity. The per-dispatch decision uses the words and the generation, and never the hash.

### Assignment of concepts

| Concept | Kept as | Used at |
|---|---|---|
| (1) Input hash | SHA-256 of the input file or disc, recorded in the generated artifact's metadata for each image | Startup only, and only where the input file is given to the run (the standalone file image). For the OpenBIOS path, the recorded disc hash is provenance. |
| (2) Code range | `EntryPc` plus `Words` count, as now | Dispatch (span guard) |
| (3) Load address | Table key (`EntryPc`); recorded per image for diagnostics | Table lookup |
| (4) RAM words | Compared against the embedded `codeN` words | Dispatch, on first use and after a span write |
| (5) Write generation | Per-page `uint64`, bumped by every writer | Dispatch, as the cache key that skips the compare |
| (6) Mapping validity | Table fixed at generation time, with a per-version words hash recorded | Startup: a version whose recorded words hash does not match is removed from the table and falls back. Dispatch: no separate check. |

### Dispatch decision

| Condition | Action | Already in code? |
|---|---|---|
| `pc` has no version | Unknown pc: no check, fallback | Yes |
| `count == 0`, `count > PSX_RAM_SIZE/4`, span outside RAM | Refuse (fallback) | Yes |
| Generation sum equal to `*seen` | Accept without reading words (relies on the writer invariant below) | Yes |
| Generation changed, words equal | Accept, update `*seen` | Yes |
| Generation changed, words differ | Refuse this version; try the next version; if none, fallback. `*seen` unchanged. | Yes |
| Recorded words hash of a version does not match its embedded words at startup | Remove the version from the table; log it; keep running | Not yet (proposed) |
| Input file given to the run and its hash differs from the recorded input hash | Refuse to attach the table (fail closed) | Not yet (proposed, see open question 1) |

### Writer invariant

Every write to `artifact_ram` must bump the page generation. Today the rule holds for every writer except the two startup `memset` calls, which are safe only because `main()` runs once and runs them before any dispatch. Proposed:

- Route the startup clears through a bumping helper. This removes the exception.
- Add a test that scans the generated C for writes to `artifact_ram` that do not go through `ARTIFACT_WROTE` or `artifact_ram_load8`/`artifact_ram_write8`. The existing `ArtifactCodeGuardSpanTests` (`src/PSXRecomp.Tests/Recompiler/ArtifactCodeGuardSpanTests.cs`) already compiles the guard with gcc, so the test can reuse that harness.

### Performance rules

- No dispatch path may hash the span or the region. The span compare runs only after a generation change.
- The fallback transition should send only the pages whose generation changed. That replaces the full `CopyRamTo` and compare in `WriteBack`. This is proposed for measurement first. It is out of scope for the dispatch rule above.

## 5. Alternatives

**A. Hash the span on every dispatch (no generations).**
- Advantages: no writer invariant to maintain; correctness follows directly from the bytes.
- Disadvantages: O(span) hashing on every dispatch, including when nothing was written. The generation check exists to avoid exactly that. Rejected for the hot path. Useful as a debug verification mode for differential tests.

**B. Pure write tracking with a dirty bit per version and no word compare.**
- Advantages: O(1) dispatch.
- Disadvantages: correctness depends entirely on writer completeness. A future DMA path or device write that misses the bump silently runs stale code. Keep the word compare as the safety net. Reconsider only after the writer test in section 4 exists and has covered every writer.

**C. Finer generation granularity (for example 256-byte lines).**
- Advantages: fewer false invalidations from data writes beside code. Costs 8192 counters (64 KiB) instead of 512.
- Disadvantages: more counters to sum per dispatch. Worth doing only if a measurement shows false invalidations matter.

## 6. Open questions for the owner

1. **Input hash gating.** Should the run refuse to attach the table when a given input file's hash differs from the recorded one, or is the recorded hash enough for provenance? The safety argument above does not depend on this choice. It only affects whether a mismatched artifact can run at all.
2. **Formal identity for the Persona path.** `ArtifactFixtureIdentity` names the disc image hash as the formal identity and the executable hash as the analysed-code identity. Which hash identifies the main EXE handoff in #734: the disc hash, the EXE hash, or both?
3. **Scope of the `WriteBack` full copy.** Is replacing the 2 MiB copy and compare part of #734, or a separate issue? A measurement is needed first.
4. **Startup clears.** Accept the documented exception for the two startup `memset` calls, or route them through the bumping helper?
5. **Granularity.** Keep 4 KiB pages, or move to finer lines (alternative C), given that the false-invalidation rate is unmeasured?

## 7. Not done here

- No code was changed. No build or test was run. No measurement was taken.
- No ROM or BIOS was read, copied or printed.

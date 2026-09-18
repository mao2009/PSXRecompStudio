# Real-ROM Analysis Artifact Format

**Status:** Stable

**Authority:** SSOT

**Related Issues:** #212, #215, #11, #205, #206, #209, #211, #225, #279, #410

**Related Components:** `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/`, `src/PSXRecomp.Core/Recompiler/RealRomCoverageAnalyzer.cs`, `src/PSXRecomp.Core/Recompiler/RealRomRecompilerBridge.cs`, `src/PSXRecomp.Tests/RealRomAnalysis/`, `docs/development/artifact-policy.md`

## Purpose

Issue #212 established the real-ROM analysis pipeline (CHD → ISO 9660 → SYSTEM.CNF →
PS-X EXE → MIPS decode → basic blocks / CFG). This document is the SSOT for how the
*results* of that pipeline are persisted, so that analyses can be compared across
titles, across disc revisions, and across analyzer revisions.

The pipeline itself is unchanged. `DiscImageAnalysisReport` remains the single runtime
producer of analysis results; the artifact layer only projects that result into a
stable persisted shape:

```text
existing runtime analysis  ->  deterministic serialization  ->  manifest / report / instructions / cfg
   (DiscImageAnalyzer)         (DeterministicArtifactBuilder)
```

## Two kinds of output, and why they must not mix

This is the distinction the whole format rests on.

| | Deterministic artifact | Execution log |
|---|---|---|
| Files | `reports/real-rom/<fixture>/{manifest,report,instructions,cfg}.json` | `logs/real-rom/<fixture>/analysis.log.jsonl` |
| Question it answers | *What did the analyzer conclude?* | *What did this run do, and how long did it take?* |
| Timestamps / elapsed time | Forbidden | Expected |
| Local paths, host name, user name | Forbidden | Permitted |
| Random ids, run ids | Forbidden | Permitted |
| Reproducible byte-for-byte | Required | No |
| Safe to attach to an issue or PR | Yes | Only after review — may contain local paths |

A deterministic artifact answers a question about the *input*; an execution log answers
a question about the *run*. Mixing them destroys the property that makes artifacts
useful: a diff between two artifacts would then always be non-empty, and a real
analysis regression would be indistinguishable from noise.

Both roots (`reports/` and `logs/`) are git-ignored local-only working directories, so
their content is **not scanned by the CI Artifact Contamination Gate** — that gate checks
only the Git-tracked tree (`git ls-files`) for contaminants. The guarantee that neither
directory ever contains ROM, ISO, EXE or CHD content is upheld locally by `.gitignore`
preventing accidental staging, and mechanically by the same gate if such content is ever
committed into the tracked tree — see `docs/development/artifact-policy.md`. Reports and
logs stay out of the repository entirely.

### How determinism is enforced, not just intended

The serialization layer lives in `PSXRecomp.Core.DiscImage.AnalysisArtifacts`, which is a
**Domain** layer namespace. The architecture analyzer's forbidden-API rule (AARC003)
already bans `System.DateTime.Now`/`UtcNow`, `System.DateTimeOffset`, `System.Guid.NewGuid`,
`System.Random`, `System.Environment`, `System.IO.File` and `System.IO.Directory` in that
layer. Contaminating an artifact with a timestamp, a path or a random id is therefore a
**compile error**, not a review finding. All file I/O lives outside that boundary.

Three further rules are pinned by tests in `DeterministicArtifactTests`:

1. **Canonical ordering.** Every array is explicitly sorted; none is left in discovery
   order. Each document records its own ordering contract in a field (`ordering`,
   `blockOrdering`, `edgeOrdering`, `distributionOrdering`), so a consumer never has to
   guess. A property-style test re-runs the build over 25 permutations of the same
   analysis and requires one single output.
2. **Canonical encoding.** camelCase keys, two-space indent, LF line endings, UTF-8
   without BOM, one trailing newline, `null` written explicitly rather than omitted so a
   document's key set depends only on its schema version. LF matters: the .NET indenting
   JSON writer defaults to the platform newline, which would otherwise make Windows and
   Linux runs differ byte-for-byte on identical input.
3. **Culture-invariant scalars.** Addresses and raw instruction words are always
   `0xXXXXXXXX` (uppercase hex, 8 digits), so textual diffs align column-for-column.

## Layout

```text
reports/real-rom/<fixture>/
  manifest.json        compact index: identity, headline counts, hashes of the others
  report.json          per-stage summary and distributions
  instructions.json    detailed instruction artifact
  cfg.json             detailed control-flow artifact
  coverage.json        recompilation coverage (optional; present when measured)
  function-provenance.json  selected-function provenance (optional; standalone)
```

`coverage.json` is the only optional document in the four-document set: it exists when a
coverage measurement was supplied to the artifact builder, and is absent otherwise. Every
other document in that set is always written.

`function-provenance.json` is **optional and standalone**. It is *not* produced by
`DeterministicArtifactBuilder.Build`, is never listed in `manifest.json`'s `documents[]`
set, and adding one implies no manifest schema bump. It is written by whoever selected a
guest range for downstream work (for example the real-ROM recompiler bridge of Issue #225)
so that the selection can be reproduced from an artifact rather than from a rebuilt
analysis. A reader of the four-document set is unaffected by its presence or absence.

`<fixture>` is a **human-facing alias only**, derived mechanically from the disc image's
file name. The transform is pure and title-agnostic:

- ASCII letters are lowercased; digits, `-`, `_` and `.` are kept.
- Every other character collapses to a single `-` (consecutive separators never double).
- Leading and trailing `-` are trimmed, and any leading non-alphanumeric characters are
  removed so the id starts with a letter or a digit.
- The result is truncated to 64 characters.
- If nothing usable remains the id is `"unnamed"`.

Two distinct fixture names can nevertheless normalize to the same id (case differences,
separator collisions, truncation past the 64-character boundary). Before artifact
directories are chosen, `AnalysisArtifactSchema.DisambiguateFixtureIds` detects such
collisions and, **only for the colliding members**, appends a short deterministic
discriminator derived from each original name so no two fixtures share a directory.
Non-colliding fixtures keep their normalized id unchanged, and the whole derivation
depends only on the fixture names — never on paths, timestamps, randomness or the host.

No title is named in code: fixtures are whatever `rom/*.chd` discovery finds. The
**formal identity** of an analysis is the disc image SHA-256, recorded in every document.
Two machines may use different aliases for the same disc; they will still agree on its
hash.

## Schema versioning

Each document carries its own `schemaVersion` and an `artifactKind` discriminator. The
versions are constants in `AnalysisArtifactSchema`: `manifest.json` is at version `2`
(version 2 admitted `coverage.json` into the indexable set), `report.json` is at version
`2` (version 2 added the `biosCalls` section), and `instructions.json`, `cfg.json` and
`coverage.json` are at version `1`. The optional `function-provenance.json` is also at
version `1`; it versions independently of every other document because it is not part of
the manifest's indexable set.

**Any change to the shape or meaning of a field requires bumping that document's
version.** Consumers diff artifacts across analyzer revisions, and must be able to tell
a schema change from an analysis change. Adding a document to the manifest's set is a
manifest schema change; adding a *standalone* optional document (such as
`function-provenance.json`) is not, because no existing consumer enumerates it. Removing
or renaming a field is always a version bump for the document that carries it.

## Documents

### `manifest.json`

The index. Small enough to read at a glance and to diff across titles.

- `schemaVersion`, `artifactKind`
- `fixture` — the shared identity block (below)
- `counts` — `decodedInstructions`, `decodeFailures`, `basicBlocks`, `cfgEdges`,
  `branches`, `jumps`, `callCandidates`, `returnCandidates`
- `documents[]` — one entry per sibling document (`fileName`, `artifactKind`,
  `schemaVersion`, `sizeBytes`, `sha256`), ordered by file name

The manifest hashes its siblings but **never itself**: a self-referential hash is not
computable, and the format does not pretend otherwise. To verify a fixture directory,
hash every file `documents[]` names and compare against the manifest. `coverage.json`
appears there only when it was produced, so the entry count is 3 or 4.

### The `fixture` identity block

Embedded verbatim in every document (`manifest.json`, `report.json`,
`instructions.json`, `cfg.json`, any `coverage.json`, and any
`function-provenance.json`), so each file is independently attributable without reading
its siblings.

`fixtureId`, `discImageFormat`, `discImageSha256`, `discImageSizeBytes`,
`executableFileName`, `executableSerial`, `executableSizeBytes`, `executableSha256`.

`executableSerial` is derived purely from the on-disc executable name: the ISO 9660
`;version` suffix is stripped, the name is uppercased, and a Sony-style `AAAA_NNN.NN`
label is folded to its canonical `AAAA-NNNNN` form. Any other name (homebrew, demo)
passes through uppercased, so the field is always defined and never title-specific.

### `report.json`

The per-fixture summary — everything except per-instruction data, so it stays diffable.

- `chd` — format version, logical bytes, hunk size, total/cdlz/cdzl hunk counts, map and
  data-region sizes
- `iso` — volume identifier, volume space size, root directory location/size,
  SYSTEM.CNF presence, file and directory counts
- `systemCnf` — boot path and resolved boot executable
- `executable` — file name, serial, size, SHA-256, entry point, text start/size/end,
  initial SP and GP
- `decode` — start address, instruction count, failure count, the failure list, and
  three distributions: `mnemonicMix`, `formatMix`, `controlFlowMix`
- `controlFlow` — basic-block and edge counts, branch/jump counts, call/return candidate
  counts, and `edgeKindMix`

- `biosCalls` — recognized BIOS jump-table call sites and their aggregation (schema
  version 2+, see below)

The `*Mix` distributions are what make cross-title comparison practical: they are
histograms in fixed ordinal name order, so instruction distributions and control-flow
shape can be compared between two titles with an ordinary diff.

#### The `biosCalls` section (Issue #11 / #279)

PS1 software reaches the BIOS by jumping to the jump-table vector at physical `0xA0`,
`0xB0` or `0xC0` with the function number in R9 (`$t1`) — not through the MIPS `syscall`
instruction. `BiosCallRecognizer` recognizes those transfers in the decoded stream and
records them here, so the next BIOS HLE service can be chosen from what titles actually
request instead of from a static candidate table.

- `siteOrdering`, `summaryOrdering` — the ordering contracts, recorded in the document
- `siteCount`, `resolvedSiteCount`, `unresolvedSiteCount`
- `sites[]` — `guestPc`, `family` (`A0`/`B0`/`C0`), `functionNumber` (two uppercase hex
  digits, or `null`), `serviceName` (or `null`), `resolution`,
  `basicBlockStartAddress`, `containingFunctionAddress` (or `null`)
- `summary[]` — `family`, `functionNumber`, `serviceName`, `stableKey`, `callSiteCount`

Three rules make this evidence rather than a guess:

1. **What the ROM asks for, not what the Runtime provides.** The section is never
   filtered to the services `BiosHleRuntime` happens to implement. Filtering it would make
   a title's BIOS surface appear to shrink and grow with implementation progress.
2. **An unresolved call is recorded, not dropped.** When the vector is certain but the
   function number is not statically resolvable, the site is emitted with
   `functionNumber: null` and `resolution: "Unresolved"`, and the summary carries a
   per-family unresolved bucket keyed `A0:unresolved`. Silently omitting it would
   understate the surface.
3. **`serviceName` is only set for a verified identity.** Names come from
   `BiosCallNames`, which holds only the identities `docs/REFERENCES.md` verifies. An
   unverified function number is counted and reported without a name rather than with a
   guessed one (ADR-014's no-guessing rule).

`resolution` records how the function number was established: `DelaySlotConstant` (the
canonical stub, where the constant is materialized in the jump's delay slot and therefore
executes before control reaches the vector), `BlockConstant` (earlier in the same basic
block, with nothing in between that could clobber R9), or `Unresolved`.

Constant tracking is deliberately block-local and forward-only, and any instruction that
*might* write a tracked register without materializing a known constant makes it unknown
again. A load into R9 — whose R3000A write is architecturally delayed — therefore yields
`Unresolved` rather than a stale constant.

### `instructions.json`

One entry per decoded instruction, ordered by address ascending
(`ordering: "address-ascending"`).

Each entry: `address`, `rawWord`, `mnemonic`, `operands`, `format`, `controlFlow`.

### `cfg.json`

Basic blocks ordered by start then end address; edges ordered by source address, then
target address, then kind. Both contracts are recorded in `blockOrdering` and
`edgeOrdering`.

- `basicBlocks[]` — `startAddress`, `endAddress` (address of the last instruction,
  inclusive), `instructionCount`
- `edges[]` — `sourceAddress`, `targetAddress`, `kind` (`branch`, `jump`, `fallthrough`,
  `indirect`; an unresolved indirect target is recorded as `0x00000000`)

### `coverage.json` (Issue #410)

How much of the analyzed code corpus is currently recompilable, and — for the remainder —
why not. It answers a different question from the proof path, and the two must never be
conflated:

| | Lowerable coverage (`coverage.json`) | Differentially validated coverage |
|---|---|---|
| Question | *Would this code lower?* | *Did the recompiled code execute identically?* |
| Produced by | `RealRomCoverageAnalyzer` over the whole analyzed corpus | a differential run over one selected window |
| Selection | none — every analyzed unit is measured | `RealRomCandidateSelector`, deliberately conservative (ADR-013) |
| Strength | an **upper bound**: the lowering stage accepts the shape | a **proof**: reference and recompiled state agree |
| Where | `totals` and `classes` | the `differential` section only |

`RealRomCandidateSelector` is untouched by this document. Coverage is descriptive;
differential proof remains the correctness gate. A progress statement reads *"X of Y
analyzed units currently lowerable; N selected candidates differentially validated"* —
never "coverage == proof success rate".

**The unit is one instruction-sized word**, not a function, and the document says so in
its own `unit` and `unitRationale` fields. `FunctionDiscovery` grows each function by
reachability from a seed, so its functions overlap, stop at unresolved indirect transfers,
and do not partition the corpus; whole-program percentages over them would be fabricated
(ADR-012 describes what that projection is actually for). Basic blocks *do* partition the
decoded stream and are reported as a secondary structural breakdown.

- `unit`, `unitRationale`, `classOrdering`, `reasonOrdering`, `differentialWindowOrdering`
  — the contracts, recorded in the document
- `totals` — `textInstructionSlots`, `decodedInstructions`, `decodeFailures`,
  `notAnalyzedInstructions`, `lowerableInstructions`, `rejectedInstructions`,
  `basicBlocks`, `fullyLowerableBasicBlocks`, `partiallyLowerableBasicBlocks`,
  `rejectedBasicBlocks`
- `classes[]` — `class`, `instructionCount`; **every** class is present even at zero, so
  two fixtures diff row-for-row
- `reasons[]` — `class`, `detail`, `instructionCount`: the named, machine-readable reason
  (the decoded opcode for an instruction-level rejection, the BIOS identity such as
  `A0:3C` or `A0:unresolved` for a runtime dependency, the decoder's own reason for an
  undecodable word)
- `differential` — `attemptedWindows`, `matchedWindows`, `mismatchedWindows`,
  `matchedInstructions`, `mismatchedInstructions`, `windows[]`

The classes, and the evidence each one rests on:

| Class | Evidence |
|---|---|
| `Lowerable` | `MipsToIrLowerer.LowerProgram` accepts the instruction's shape |
| `IndirectControlFlow` | `JR`/`JALR` — the same unresolved transfer `cfg.json` records as an `indirect` edge |
| `BiosDependency` | a recognized BIOS jump-table call site (`report.json`'s `biosCalls`) |
| `UnsupportedInstruction` | the lowering stage rejects the shape outright |
| `MalformedOrUndecodable` | a `DecodeFailure` from the existing pipeline |
| `AnalysisUncertainty` | the instruction decoded, but the block partition does not cover it |
| `NotAnalyzed` | a text-region word the bounded decode window never reached |

There is no second "supported instruction" list: support is decided by calling the real
lowerer, exactly as candidate selection does (ADR-013). `NotAnalyzed` is what keeps the
denominator honest — a ratio computed over decoded instructions alone would overstate
whole-title coverage whenever the decode window is smaller than the text region.

**Three classes Issue #410 lists as desirable are deliberately absent**, because nothing
in the repository can currently establish them: MMIO / hardware dependency (no analyzer
computes a static effective address, so a load's target region is unknown), dynamic /
overlay suspicion (`OverlayInfo` is a contract with no producer, and #249 is unstarted),
and self-modifying code. An always-zero bucket would read as "measured and absent" rather
than "not measured", so they are reported nowhere rather than reported falsely. Adding one
later is a `coverage.json` schema bump.

`differential` is populated only by a run that actually executed a differential. An empty
section means *nothing was proven* — never *nothing failed*.

### `function-provenance.json` (Issue #215)

Identifies **which guest range of which executable** a downstream consumer selected, so
that the selection is reproducible from metadata alone. It answers *"what exactly was
handed to the next stage?"* — not *"what did the analyzer conclude?"* (that is
`report.json`) and not *"did the selected code run correctly?"* (that is a differential
run).

The document carries **no raw instruction words and no executable bytes**. A range is
identified by guest addresses plus a deterministic SHA-256 over its basic blocks, and
the executable by `fixture.executableSha256`. This is what makes it safe to attach to an
issue.

- `schemaVersion`, `artifactKind` (`psxrecomp.real-rom-analysis.function-provenance`)
- `fixture` — the shared identity block (above)
- `startAddress`, `endAddress` — first and last selected instruction, canonical
  `0xXXXXXXXX`
- `instructionCount` — decoded instructions across the selected range's blocks
- `selectionRule` — how the range was chosen, e.g. `entry-point-reachability` or
  `self-contained-candidate-window`; the producer names the rule, this schema only records it
- `blockOrdering` — `start-address-ascending,end-address-ascending`, identical to
  `cfg.json`'s contract, so a reader that has already consumed `cfg.json` applies the
  same rule
- `basicBlocks[]` — `startAddress`, `endAddress` (inclusive), `instructionCount`
- `blockIdentitySha256` — lowercase hex SHA-256 over the canonical JSON of
  `basicBlocks[]` **alone**; identifies the range's CFG subset and never depends on the
  document that contains it
- `subsetOrdering`, `requiredInstructionSubset[]` — distinct opcode names the range
  requires (`lui`, `addiu`, `beq`, `jal`, `jr`, …), name-ordinal ascending
- `flagsOrdering`, `unresolvedFlags[]` — stable, explicitly named limitations that were
  *not* resolved for this range (for example `indirect-control-flow`, `bios-call`,
  `decode-failure`), name-ordinal ascending

`FunctionProvenanceBuilder.Build` is a pure projection: it does not discover functions
or select ranges, it sorts blocks by start then end address, de-duplicates and sorts the
subset and flag lists, and validates the boundary conditions (at least one block, an end
address not before the start, a non-empty selection rule). Determinism therefore rests on
the same three rules as the rest of the format, and `SelectedFunctionProvenanceTests`
pins them: required fields and ordering, byte-for-byte repeatability, independence from
block insertion order, range and fixture distinction, culture invariance, and the absence
of any raw instruction data.

`unresolvedFlags` is descriptive, not a coverage claim. An empty array means the producer
reported no unresolved analysis flags for this selection; it never asserts that a category
was validated when it was not analyzed at all. The producer decides which flags exist.

### Relationship to the recompiler-selection issues

`function-provenance.json` is the persisted form of the smaller, in-memory
`RealRomFunctionProvenance` produced by the real-ROM recompiler bridge (Issue #225). The
two are intentionally not the same type:

- **#205 / #206** established the differential execution comparison. This document
  records what was selected, not whether it matched.
- **#209** established the synthetic vertical slice that proved the pipeline end to end.
  `function-provenance.json` uses the same fixture identity and canonical JSON as the
  rest of the format, so a synthetic selection and a real one serialize identically.
- **#211** tracks real-ROM hardening and end-to-end execution. Provenance makes the
  selected window of a hardened run auditable after the fact.
- **#225** selects candidate functions and carries `RealRomFunctionProvenance` internally.
  That value is deliberately minimal and unversioned; this document is the versioned,
  documented, attachable projection of it. When a selected range needs to survive outside
  the process, it is rendered here through `FunctionProvenanceBuilder`.

## Comparing artifacts

- **Same disc, two runs** — expect an empty diff. A non-empty diff is an analyzer
  regression or a nondeterminism bug.
- **Same disc, two analyzer revisions** — diff `manifest.json` first. A change in
  `counts` localizes the behavior change; `report.json`'s distributions narrow it to a
  mnemonic class or edge kind; `instructions.json` / `cfg.json` give the exact addresses.
- **Two titles** — compare `report.json` distributions. Absolute counts differ, but the
  shape of the mnemonic and edge-kind mixes is comparable, and `decode.failures` exposes
  decoder gaps a single title would not reveal.

## Fixtures and CI

Fixtures are user-supplied, legally obtained disc images placed in the git-ignored
`rom/` directory, discovered by `RealRomFixtures.Discover()`. Any number may be present.

- **Fixture present** — the real-ROM tests run; a failure fails CI.
- **No fixture** — the real-ROM tests skip explicitly with a reason. CI runners have no
  fixtures and never will.

Because CI can never run the real-ROM path, every format-level guarantee — schema,
determinism, ordering, identity, multi-fixture support, absence of environment data — is
additionally covered on **synthetic** input by `DeterministicArtifactTests`, which runs
on every build. The real-ROM tests confirm the same guarantees hold on real data.

## What may be shared

| Artifact | Shareable |
|---|---|
| `manifest.json`, `report.json`, `coverage.json` | Yes — metadata and statistics only |
| `function-provenance.json` | Yes — guest addresses, hashes, opcode names and selection metadata only; no instruction words and no executable bytes |
| `instructions.json`, `cfg.json` | Yes in principle (disassembly metadata, no game data blobs); prefer excerpts in issues and PRs given their size |
| `logs/**` | Local only — may contain local filesystem paths |
| `rom/**` | Never. Copyrighted game data |

When reporting real-ROM results in an issue or PR, quote only safe metadata: PASS/FAIL,
stage, counts, SHA-256 values and summary statistics. Never attach disc, ISO or
executable content.

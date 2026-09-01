# Real-ROM Analysis Artifact Format

**Status:** Stable

**Authority:** SSOT

**Related Issues:** #212, #215

**Related Components:** `src/PSXRecomp.Core/DiscImage/Artifacts/`, `src/PSXRecomp.Tests/RealRomAnalysis/`, `docs/development/artifact-policy.md`

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

Both roots (`reports/` and `logs/`) are git-ignored. Neither ever contains ROM, ISO,
EXE or CHD content — see `docs/development/artifact-policy.md`, which is enforced in CI
by the Artifact Contamination Gate.

### How determinism is enforced, not just intended

The serialization layer lives in `PSXRecomp.Core.DiscImage.Artifacts`, which is a
**Domain** layer namespace. The architecture analyzer's forbidden-API rule (PSXR005)
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
```

`<fixture>` is a **human-facing alias only**, derived mechanically from the disc image's
file name by `AnalysisArtifactSchema.NormalizeFixtureId` (lowercase ASCII, `-`, `_`, `.`).
No title is named in code: fixtures are whatever `rom/*.chd` discovery finds. The
**formal identity** of an analysis is the disc image SHA-256, recorded in every document.
Two machines may use different aliases for the same disc; they will still agree on its
hash.

## Schema versioning

Each document carries its own `schemaVersion` and an `artifactKind` discriminator. The
versions are constants in `AnalysisArtifactSchema` and are currently all `1`.

**Any change to the shape or meaning of a field requires bumping that document's
version.** Consumers diff artifacts across analyzer revisions, and must be able to tell
a schema change from an analysis change. Adding a document to the set is a manifest
schema change.

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
hash `report.json`, `instructions.json` and `cfg.json` and compare against the manifest.

### The `fixture` identity block

Embedded verbatim in all four documents, so each file is independently attributable
without reading its siblings.

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

The `*Mix` distributions are what make cross-title comparison practical: they are
histograms in fixed ordinal name order, so instruction distributions and control-flow
shape can be compared between two titles with an ordinary diff.

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
| `manifest.json`, `report.json` | Yes — metadata and statistics only |
| `instructions.json`, `cfg.json` | Yes in principle (disassembly metadata, no game data blobs); prefer excerpts in issues and PRs given their size |
| `logs/**` | Local only — may contain local filesystem paths |
| `rom/**` | Never. Copyrighted game data |

When reporting real-ROM results in an issue or PR, quote only safe metadata: PASS/FAIL,
stage, counts, SHA-256 values and summary statistics. Never attach disc, ISO or
executable content.

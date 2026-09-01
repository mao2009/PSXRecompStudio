# Real-ROM Analysis Flow

**Status:** Stable

**Authority:** Reference

**Related Issues:** #212, #213, #215

Specification of the repeatable analysis flow applied to a user-provided PS1 disc
image: what stages exist, how success and failure are classified, which artifacts
are produced, and why no disc image can reach Git.

Issue #212 established the analysis path itself (CHD → ISO 9660 → SYSTEM.CNF →
PS-X EXE → entry point → text region → MIPS decode → basic blocks). This document
specifies its promotion to a reusable, title-agnostic flow (#213). The persisted
snapshot/manifest *schema* is a separate concern owned by #215; this flow reuses
the existing `DiscImageAnalysisReport` format rather than defining a rival one.

## Stage model

```text
START → INPUT → CHD_OPEN → FILESYSTEM → SYSTEM_CNF → BOOT_EXECUTABLE → PSX_EXE
      → EXE_HEADER → ENTRY_POINT → TEXT_REGION → MIPS_DECODE → BASIC_BLOCK
      → REPORT → MANIFEST → COMPLETE
```

Stages are recorded in strictly increasing order and nothing is recorded after a
failure, so **the last recorded successful stage always identifies how far the run
got**. A stage ends as `Passed`, `Skipped` (deliberately not executed — for
example `CHD_OPEN` for a plain ISO input), or `Failed`.

| Stage | Responsibility |
|---|---|
| `START` | Flow entry |
| `INPUT` | Non-empty image bytes and a SHA-256 input identity |
| `CHD_OPEN` | Open the CHD v5 container |
| `FILESYSTEM` | ISO 9660 volume descriptor and directory tree |
| `SYSTEM_CNF` | Locate and parse `SYSTEM.CNF`, resolve the `BOOT` value |
| `BOOT_EXECUTABLE` | Read the executable named by `SYSTEM.CNF` |
| `PSX_EXE` | Confirm the boot file is a PS-X EXE (size + magic) |
| `EXE_HEADER` | Parse and sanity-check the 2048-byte header |
| `ENTRY_POINT` | Entry point inside the declared text region and 4-byte aligned |
| `TEXT_REGION` | Declared text region actually present in the file |
| `MIPS_DECODE` | Linear R3000A decode from the entry point |
| `BASIC_BLOCK` | Basic blocks and direct control-flow edges |
| `REPORT` | Assemble the deterministic `DiscImageAnalysisReport` |
| `MANIFEST` | Persist the analysis report artifact |
| `COMPLETE` | Flow finished |

`MANIFEST` and `COMPLETE` are recorded by the flow driver, not by the domain
pipeline: persisting artifacts is I/O, which the domain layer must not perform.

## Failure classification

A failure is a result, not an exception to swallow. Every failed run reports the
failing stage, a stable `FailureKind`, the reason text, and the originating
exception (retained in-process for callers that need to rethrow it).

| Stage | `FailureKind` | Typical cause |
|---|---|---|
| `INPUT` | `EmptyInput` | Zero-byte image |
| `INPUT` | `MissingInputIdentity` | No SHA-256 supplied |
| `INPUT` | `FixtureUnreadable` | Image file missing or unreadable on disk |
| `INPUT` | `InvalidInstructionCount` | Non-positive `instructionCount` supplied |
| `CHD_OPEN` | `ChdOpenFailure` | Not a CHD, unsupported version, malformed header/map |
| `FILESYSTEM` | `FilesystemFailure` | No Primary Volume Descriptor, unreadable directory tree |
| `SYSTEM_CNF` | `SystemCnfMissing` | `SYSTEM.CNF` absent from the volume |
| `SYSTEM_CNF` | `SystemCnfInvalid` | Present but has no `BOOT` entry |
| `BOOT_EXECUTABLE` | `BootExecutableMissing` | `BOOT` names a file not on the disc |
| `BOOT_EXECUTABLE` | `BootExecutableUnreadable` | Extent unreadable |
| `PSX_EXE` | `InvalidPsxExe` | Shorter than the header, or wrong magic |
| `EXE_HEADER` | `InvalidExeHeader` | Text start `0x00000000`, or a text region that overflows |
| `ENTRY_POINT` | `InvalidEntryPoint` | Outside the text region, or unaligned |
| `TEXT_REGION` | `TextRegionUnavailable` | Declared text region absent from the file |
| `MIPS_DECODE` | `DecodeFailure` | No instruction decodable from the entry point |
| `BASIC_BLOCK` | `BasicBlockAnalysisFailure` | No block built from decoded instructions |
| `REPORT` | `ReportGenerationFailure` | Report could not be assembled |
| `MANIFEST` | `ArtifactPersistenceFailure` | Artifact could not be written |
| `MANIFEST` | `DiscMetadataUnreadable` | Post-REPORT CHD/ISO metadata read failure |

A run can pass with a non-zero decode-failure count (a partial decode); the count
is reported in the manifest. Zero decoded instructions is a `MIPS_DECODE` failure.

## Inputs

Fixtures are *discovered*, never named in code. `RealRomFixtures.Discover()` (the
single SSOT for fixture discovery) scans `rom/*.chd` at the top level:

```text
rom/<fixture>.chd   → fixture "<fixture>"
```

The fixture name is only a directory-safe alias (disambiguated via
`AnalysisArtifactSchema.DisambiguateFixtureIds` so different fixtures never share
an artifact directory); the formal identity of an input is its SHA-256, recorded
inside the artifacts.

## Artifacts

Analysis is orchestrated by `RealRomAnalyzer` and persisted by
`RealRomArtifactWriter` using the Issue #215 deterministic artifact schema — the
only persisted schema. There is no separate "run-summary" document.

```text
reports/real-rom/<fixture>/manifest.json       invocation identity + counts  (PASS)
reports/real-rom/<fixture>/report.json         CHD/ISO/SYSTEM.CNF/decode data (PASS)
reports/real-rom/<fixture>/instructions.json   decoded instruction listing     (PASS)
reports/real-rom/<fixture>/cfg.json            basic blocks + control-flow     (PASS)
logs/real-rom/<fixture>/analysis.log.jsonl     per-stage detail, JSONL        (best effort)
```

- **`manifest.json`** — the formal identity (disc SHA-256, size, executable SHA-256
  and serial), aggregated counts (decoded instructions, basic blocks, CFG edges),
  and content hashes of the sibling documents. Deterministic and diffable.
- **`report.json` / `instructions.json` / `cfg.json`** — the #215 projection of the
  `DiscImageAnalysisReport` into stable, versioned documents.
- **`analysis.log.jsonl`** — one JSON object per stage with the full detail text.
  It may be written even for a failing analysis (down to the stage that failed),
  but persistence is best-effort. Paths are redacted at this boundary.

### Persistence is best-effort

Artifact persistence is isolated per fixture and never throws out of the
orchestrator. If an artifact cannot be written, the run is classified as
`ArtifactPersistenceFailure` at the `MANIFEST` stage and the affected artifact is
represented as **unavailable** (its path is absent/non-null-but-unwritten). A
`MANIFEST` failure means the run stops before `COMPLETE`. Do not assume a summary
report or log always exists: a fixture that fails before `REPORT` produces no
artifacts, and a disk error can make any artifact unavailable. Each fixture is
handled independently, so one failing title never hides another's result.

## ROM handling policy

- Disc images, extracted executables and analysis artifacts stay local. `rom/`,
  `reports/` and `logs/` are git-ignored, and the artifact contamination gate
  (`scripts/ci/check-artifact-policy.ps1`, SSOT `config/artifact-policy.json`)
  rejects the `rom` path segment, disc-image extensions, and files matching
  CHD/ISO/PS-X EXE content signatures anywhere in the tracked tree.
- Logs record derived metadata only — counts, addresses, hashes, filenames.
  No sector, instruction payload or executable content is written to a log.
- Only safe metadata (PASS/FAIL, stage, counts, SHA-256, summary) may be quoted
  in an Issue or PR.

## CI policy

The flow's CI entry point is `RealRomAnalysisSkillTests`. With no fixture present
it skips explicitly with a reason and CI stays green; with a fixture present each
discovered image must reach `COMPLETE` or the test fails. No existing unit,
integration or native job depends on a fixture.

The stage model and every stage failure are covered by tests that run everywhere,
using synthetic in-memory ISO images built by `SyntheticIsoImageBuilder` — no
copyrighted data is needed to verify the flow's classification logic. Tests that
need a real disc image to reach `REPORT` (the deterministic artifact set,
persistence-failure classification, full-success `COMPLETE`) skip when no fixture
is present, exactly like the CI entry point. Persistence isolation, path
redaction and stream ownership are additionally exercised everywhere with no
disc image required.

## Implementation map

| Concern | Type |
|---|---|
| Stage sequence and failure classification | `PSXRecomp.Core.DiscImage.RomAnalysisPipeline` |
| Stage bookkeeping and ordering invariants | `RomAnalysisStageRecorder` |
| Classified result | `RomAnalysisOutcome` |
| Throwing façade (unchanged public contract) | `DiscImageAnalyzer` |
| CHD→ISO reader (single SSOT) | `DiscImageAnalyzer.CreateIsoReader` |
| Fixture discovery (single SSOT) | `PSXRecomp.Tests.RealRomAnalysis.RealRomFixtures` |
| Orchestration: stream run, MANIFEST/COMPLETE, persistence isolation | `RealRomAnalyzer` |
| Deterministic artifact writer | `RealRomArtifactWriter` |
| Deterministic artifact builder (schema SSOT, #215) | `PSXRecomp.Core.DiscImage.AnalysisArtifacts.DeterministicArtifactBuilder` |
| Detailed execution log | `ExecutionLogWriter` |
| Path redaction (log persistence boundary) | `PathRedactor` (applied in `ExecutionLogWriter`) |

The procedure an agent follows to run and interpret the flow is
`skills/project/psxrecomp-studio/real-rom-analysis/SKILL.md`.

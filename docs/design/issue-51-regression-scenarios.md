# Compatibility & Regression Scenarios: smoke set, evidence, and detection (Issue #51)

Tracking: [#51](https://github.com/mao2009/PSXRecompStudio/issues/51). Related: [#33](https://github.com/mao2009/PSXRecompStudio/issues/33) (Compatibility Registry), [#42](https://github.com/mao2009/PSXRecompStudio/issues/42) (Project/Workspace), [#44](https://github.com/mao2009/PSXRecompStudio/issues/44) (PS1 Hardware Runtime Architecture), [#46](https://github.com/mao2009/PSXRecompStudio/issues/46) (Recomp Artifact & Build Management), [#49](https://github.com/mao2009/PSXRecompStudio/issues/49) (Diagnostics & Recovery).

Status: **design only**. No code, schema file, test, ADR, or shared doc is changed by this document. Base: `origin/main` at `2e3ecad`. The compatibility registry input is `batch2/w13-compat-registry-design` at `e389088` (`docs/design/issue-33-compat-registry.md`). It was read as an input; its review was not awaited, so its decisions are treated as proposals (section 9).

## 0. Labels and constraints

- **CONFIRMED**: read in this worktree during this work, and the text says what is claimed.
- **OBSERVED**: taken from a repository document, a commit, a GitHub page, or the #33 design doc's citations. The underlying behavior was not re-run by this author.
- **PREDICTED**: a consequence of the design. Not implemented, not measured.
- **UNVERIFIED**: not checked. Treated as an open item.

Hard constraints: this design stores and references verification outcomes only. It contains no BIOS, ROM, disc image bytes, extracted title data, or copyrighted asset content. Titles appear as placeholders (`Title A`). Smoke tests use repository-authored synthetic executables only. A user-supplied real input is referenced by a hash and a bundle id, never by path or content.

## 1. Problem and scope

Issue #51 asks for a mechanism that continuously checks per-title Recomp/Runtime compatibility and regressions. [OBSERVED: issue #51 page, fetched via WebFetch; the Japanese overview line is "タイトル単位でRecomp/Runtimeの互換性とRegressionを継続的に検証できる仕組みを設計する。"]

The issue body (as summarized from the fetched page; the full Japanese body was not reproduced verbatim by the fetch) lists these scope areas: Boot, Gameplay, Save/Load, CD/Disc, Audio, Graphics, Input, Runtime stability, Known issues, and Regression tracking. [OBSERVED] Its completion checklist has seven unchecked items: (1) compatibility test model, (2) test result schema, (3) linking tests to title and disc hashes, (4) automated smoke test approach, (5) regression detection approach, (6) integration with the Compatibility Registry (#33), (7) GUI/CLI result-viewing boundary. [OBSERVED]

This document covers items 1 to 6 and sketches item 7. Out of scope: a real-title test corpus, any network service, and the CI wiring details (they belong to a later implementation issue).

## 2. Current state

### 2.1 What exists

- Repository-authored synthetic PS-X EXE fixtures already exist. They are generated in memory from MIPS instruction words, pinned by SHA-256, and consumed by `RecompiledArtifactE2ETests` through the production recompiler/runtime path. The catalog has two entries: `bios-putchar-marker` (completed/success, S1 = `0x7777`, TTY byte `P`) and `unresolved-jump` (blocked/unsupported-transfer, guest PC `0x80020000`, `UNRESOLVED_TRANSFER`). [CONFIRMED: `docs/development/generated-psx-exe-fixtures.md`, the "Current fixtures" and "Verification" sections]
- The fixture doc states that no generated `.exe` is checked in and that the catalog excludes Sony BIOS code and user-provided inputs. [CONFIRMED: same doc, "Legal and ownership boundary"]
- Note: the fixture ID `bios-putchar-marker` names a BIOS-HLE call path, not BIOS code. The name is kept as-is; a rename is out of scope and flagged in section 11. [CONFIRMED wording; PREDICTED impact]
- Existing smoke-style tests: `src/PSXRecompStudio.Tests/MainWindowSmokeTests.cs` (Avalonia headless window and binding checks) and `src/PSXRecomp.Tests/RustAbiSmokeTests.cs` (managed to native to Rust chain, Issue #473). [CONFIRMED: both files exist and their headers were read]
- Capability matrix rules: a Verified row does not imply complete title compatibility, and verification requires an executable test that asserts an observable contract, not just absence of a crash. [OBSERVED: `docs/status/capabilities.md:28`, `:55-60`, as cited by #33]
- Real-input evidence vocabulary exists in the capability matrix. [OBSERVED: `docs/status/capabilities.md:44`, section heading only, not re-read]
- Title and disc identity types, evidence/confidence contracts, execution outcome enums, and `ExecutionEnvironmentReport` exist as described in the #33 doc. [OBSERVED: #33 section 2.1 citations, not re-read in this worktree]

### 2.2 What does not exist

- No compatibility test model, result schema, or regression detector in `src`. [OBSERVED: #33 section 2.2 search; not re-run for #51 scope]
- No GPU field in `ExecutionEnvironmentReport`, no recomp `BuildId`, and no Runtime version identifier. [OBSERVED: #33 section 2.1]
- No title-level smoke run that links results to a disc content hash. `DiscIdentity` has no content hash field. [OBSERVED: #33 citing `DiscIdentity.cs:10-14`]

## 3. Compatibility test model

The test model is a set of **scenarios**. Each scenario is a repository-owned, versioned definition. It is run against a **subject** (a synthetic fixture or a user-supplied input) under a **binding** (section 6). It produces one **result** (section 4).

```
ScenarioDefinition {
  Id                 // "smoke.boot.synthetic-putchar", "smoke.trap.unresolved-jump"
  Facet              // Boot | Gameplay | Save | Audio | Graphics | Input | Runtime | Disc
  Version            // bump when pass criteria change
  Subject            // SyntheticFixture(id) | UserInput(discHash, releaseKey)
  Criteria[]         // observable assertions only
  Tier               // Smoke | Extended | Manual
  Requires[]         // e.g. "Rust native staticlib", "GPU backend present", "input bundle"
}

Subject
  SyntheticFixture { FixtureId, ExpectedSha256 }      // repo-authored, always runnable in CI
  UserInput        { DiscHash?, ExeFingerprint?, ReleaseKey? }   // never checked in; local only
```

Rules:

- A criterion must assert an observable contract: an outcome enum value, a guest PC, a register value, a TTY byte, a frame hash, a diagnostic code. "Did not throw" is not a criterion. [OBSERVED: capability matrix verification threshold, as above]
- A scenario that depends on a facet not yet implemented (for example, Audio output, `Not claimed` in `capabilities.md`) must declare `Tier = Manual` or be absent. It must not be listed as Pass. [OBSERVED: `capabilities.md:78` per #33; PREDICTED for the rule]
- Facet list: the issue lists Input, Runtime stability, CD/Disc, and Save/Load. The #33 draft has five facets (Boot, Gameplay, Save, Audio, Graphics) and folds Load into Save and CD into Gameplay. This document proposes adding `Input`, `Runtime`, and `Disc` as scenario tags rather than facets, so the #33 facet set is not changed. Owner decision: section 11, Q1.

## 4. Smoke-test set (synthetic fixtures)

The smoke set is the subset that runs on every change without any user input. It must pass on a clean checkout, using only repository-authored code.

### 4.1 Initial smoke scenarios

| Scenario ID | Subject | Criteria | Source of truth |
|---|---|---|---|
| `smoke.boot.synthetic-putchar` | `bios-putchar-marker` | outcome `completed/success`; S1 = `0x7777`; TTY byte `P` | fixture catalog (CONFIRMED) |
| `smoke.trap.unresolved-jump` | `unresolved-jump` | outcome `blocked/unsupported-transfer`; guest PC `0x80020000`; diagnostic `UNRESOLVED_TRANSFER` | fixture catalog (CONFIRMED) |
| `smoke.parse.fixture-hash` | every catalog entry | generated bytes match pinned SHA-256 | fixture test (CONFIRMED: doc "Verification") |
| `smoke.native.rust-abi` | native staticlib | `AbiVersion == 1`; round-trip sentinel | `RustAbiSmokeTests.cs` (CONFIRMED that the test exists) |
| `smoke.studio.window` | headless window | DataContext type; binding resolves | `MainWindowSmokeTests.cs` (CONFIRMED that the test exists) |

Mapping to facets: `smoke.boot.*` maps to Boot. `smoke.trap.*` maps to Runtime (a classified stop is a Runtime result, not a Boot Pass). `smoke.parse.*` and `smoke.native.*` are infrastructure checks and produce no Compatibility result. They gate the run but are not counted in any facet.

Important: `smoke.boot.synthetic-putchar` proves that the recompiler/runtime path works on a synthetic program. It does **not** prove that any title boots. Its Pass must never be displayed as a title Boot Pass. Its `Subject.Kind` is `SyntheticFixture`, and the registry (section 7) excludes such results from title levels. [PREDICTED]

### 4.2 Synthetic-fixture growth rules

- New fixtures are added only as repository-authored MIPS instruction words, following the fixture doc generation contract. [CONFIRMED rule in `generated-psx-exe-fixtures.md`]
- Each new fixture pins its SHA-256 and declares its expected evidence. [CONFIRMED rule]
- A fixture must not reproduce a real title's code sequence, tables, or assets. Review checks this. [PREDICTED rule; the fixture doc's boundary section supports it]

### 4.3 Gate

The smoke tier is a required check for changes under `src/PSXRecomp.Core/Recompiler`, `src/PSXRecomp.Core/Runtime`, `src/PSXRecomp.Core/Execution`, and the native crate. The exact path set is UNVERIFIED and listed in section 11. [PREDICTED]

## 5. Result evidence schema

A result is an immutable record. It is written once and never edited. A correction is a new record that supersedes the old one by id.

```
ScenarioResult {
  Schema             // "psxrecomp.compat-result.v1"  (proposed; PREDICTED)
  ResultId           // content hash of the canonical record minus ResultId
  ScenarioId, ScenarioVersion
  Subject            // SyntheticFixture(id, sha256) | UserInput(discHash?, exeFingerprint?)
  ReleaseKey?        // (Serial, Region, Revision); absent for synthetic subjects
  Binding            // section 6
  Outcome            // Pass | Fail | Blocked | Error | NotRun   (section 5.1)
  ObservedFacts[]    // name/value pairs: outcome enum, guest PC, register, TTY byte, diagnostic code
  EvidenceRefs[]     // ids of attached artifacts (section 5.2)
  Producer           // tool name, version, reporter class (section 5.3)
  CapturedUnixSeconds  // input; never read from a clock inside derivation
}
```

### 5.1 Outcome

Outcome values follow the #33 vocabulary so that the two designs do not diverge. [OBSERVED: #33 section 3.5]

| Outcome | Meaning | Maps from |
|---|---|---|
| `Pass` | All criteria met on this binding. | criteria evaluation |
| `Fail` | Evidence produced, a criterion not met. | criteria evaluation |
| `Blocked` | Stopped at a known unsupported boundary. | `RecompiledArtifactOutcome.Blocked` (OBSERVED: #33 citing `RecompiledArtifactExitCode.cs:18-21`) |
| `Error` | Harness or host failure, not a guest result. | `RecompiledArtifactOutcome.Failure` (OBSERVED) |
| `NotRun` | No attempt, or attempt produced no evidence. | run planner |

Rules:

- `Blocked` is a diagnosable stop, not a defect. [OBSERVED: `capabilities.md:49`, per #33]
- `Error` is excluded from the denominator and reported. A run that errors cannot turn a Pass into a Fail. [PREDICTED]
- `NotRun` is never shown as `Pass` or `Fail`. [PREDICTED]
- A timeout is `Error` with a `TimedOut` fact when the harness killed the run, and `Blocked` only when the runtime reports a classified budget stop (`OUTER_BUDGET_EXHAUSTED` pattern from `capabilities.md:87`). [PREDICTED; the distinction must be checked against `RecompilerExecutionStatus.TimedOut`, OBSERVED in #33]

### 5.2 Evidence attachments

- Attachments are referenced by SHA-256 of the captured file (log, frame, report). The attachment itself is stored outside the result record. [PREDICTED; matches #33 `EvidenceRecord.ArtifactHash`]
- For a user input, an attachment must not contain game data (no frame dumps of copyrighted art in a shared bundle). Such attachments stay in the local store and only their hash is shared. [PREDICTED]
- Frame comparison uses a hash of the frame buffer, not the frame. [PREDICTED]

### 5.3 Producer and reporter class

Reporter class follows #33 section 4.1.2: `Maintainer`, `Automated`, `Community`. [OBSERVED: #33] Synthetic smoke runs are `Automated`. `AIPrediction` and `AnalyzerFinding` evidence can never produce a `Pass`. [OBSERVED: #33 section 4.1.1]

## 6. Binding

Every result is bound to the environment that produced it. The #33 binding is `(BuildId, RuntimeVersion, Os, Gpu, DotNet, RuntimeIdentifier)`. [OBSERVED: #33 section 4.4]

For the smoke tier, the binding is taken from the same environment source as the rest of the run. Two items are unresolved for the smoke tier:

- `BuildId` and `RuntimeVersion` do not exist in `src`. [OBSERVED: #33 section 2.1] A smoke run cannot be bound until one of them is defined. The candidate used in this design is the git commit of the worktree plus the staged native artifact hash. This is a proposal (PREDICTED), and the choice is open question Q2.
- GPU is absent from `ExecutionEnvironmentReport`. [OBSERVED: #33]. Synthetic smoke scenarios do not need a GPU. Graphics scenarios are `Tier = Manual` until the GPU field exists. [PREDICTED]

Binding equality is exact (per #33). A result under binding A never counts toward binding B. [OBSERVED design in #33, adopted here]

## 7. Regression detection

Detection is derived from results. It is never typed by hand.

### 7.1 Conditions

Let `R(s, b)` be the latest non-superseded result for scenario `s` on binding `b`.

1. **Regression**: `R(s, A) = Pass` and `R(s, B) in {Fail, Blocked}` for the same `Subject` and the same `ScenarioId` where `B` is a later binding of the same title. [PREDICTED; mirrors #33 section 6.2]
2. **Unverified after change**: `R(s, A) = Pass` and `R(s, B) = NotRun` (or absent) while `B` differs from `A` in any binding field. Reported as `Unverified after change`, not as a regression. [PREDICTED]
3. **Synthetic regression**: the same rule applies to synthetic subjects, but the result is labelled `synthetic` and never rolls up into a title level. A synthetic regression is a recompiler/runtime regression signal, which is the primary purpose of the smoke tier. [PREDICTED]
4. **Error is not a regression**: `Error` on B does not create a regression. It creates a harness alert. [PREDICTED]
5. **Scenario version change**: if `ScenarioVersion` of `s` changes between A and B, the pair is not compared. The result is `Re-baselined`, and the old Pass is kept as history. [PREDICTED]

### 7.2 Derived record

A regression record names: scenario id and version, subject, both binding keys, both result ids, and the first differing `ObservedFacts` entry. It does not copy evidence bodies. [PREDICTED]

### 7.3 Gate behavior

- Synthetic regression on the smoke tier fails the check. [PREDICTED]
- Title-level regression does not fail repository CI in this design. It is reported to the registry and the Studio view. A policy for blocking releases is open (Q5). [PREDICTED]

## 8. Integration with the Compatibility Registry (#33)

This section lists each integration point and the #33 decision it depends on. "Depends on" means: if the #33 decision changes, this section changes.

| Integration point | This design needs | #33 decision it depends on | Status of that decision |
|---|---|---|---|
| Result outcome | the five outcome values | section 3.5 Outcome vocabulary | proposal in e389088 |
| Evidence record | `EvidenceRecord` with `Binding` and `ArtifactHash` | section 4.1 | proposal |
| Reporter class | `Maintainer`, `Automated`, `Community` | section 4.1.2 | proposal |
| Binding | exact-match binding | section 4.4 and open Q5 | proposal; exact match is the default |
| Level derivation | synthetic results excluded from title levels | section 5 (not addressed by #33) | **new requirement from this design**; #33 must add it |
| Facets | five facets (Boot, Gameplay, Save, Audio, Graphics) | section 3.2 and open Q1 | proposal; the issue asks for more |
| Disc binding | a disc content hash | section 10 Q3 and `DiscIdentity` gap | UNVERIFIED |
| Build identity | a `BuildId` | section 10 Q6 | UNVERIFIED |
| Persistence | append-only JSONL plus snapshot (Option D) | section 7.2 | proposal |
| Confidence | ordinal levels, no `Certain`, no numeric Score | section 4.3 | proposal |
| Scenario catalog | a versioned catalog that is part of the derived key | section 5 and Q13 | proposal |

Concrete boundaries:

- This document reuses the #33 record types for results. It does not define a second evidence model. [PREDICTED]
- The smoke tier writes results through `ICompatibilityStore` (#33 section 8). It does not write files directly. [PREDICTED]
- Derivation of levels stays in #33's pure Core. The regression detector (section 7) is also pure: it takes two result sets and returns regression records. It has no clock and no I/O. [PREDICTED, consistent with #33 section 8]

## 9. Decisions this design depends on from #33

If the following #33 decisions change, this design must be revisited:

1. Outcome vocabulary, especially `NotRun` distinct from `Fail`.
2. Exact binding equality, and no implicit carry-forward.
3. Reporter classes without a required agent identity.
4. The five-facet set (Q1 in #33).
5. Persistence as Option D, with the append-only log as v1.
6. Scenario catalog versioning as part of the derived key.

This design adds one requirement to #33: synthetic results must be excluded from title levels, and the registry must carry a subject kind so that exclusion is possible. [PREDICTED]

## 10. GUI and CLI viewing boundary (checklist item 7)

- Studio: a read-only view of the derived read model, one row per binding in use, and a badge for each regression (#33 section 9). [PREDICTED]
- Studio must show the smoke tier separately from title levels. A synthetic Pass shown in the title grid is a defect. [PREDICTED]
- CLI (optional, later): `compat results` and `compat regressions` subcommands that print the same derived model as JSON. No write path in v1. [PREDICTED]
- Neither surface computes outcomes or levels itself. Both call the pure Core derivation. [PREDICTED]

## 11. Open questions

- **Q1. Facets versus scenario tags.** The issue lists Input, Runtime stability, CD/Disc, Save/Load. Should these become facets (changing #33) or scenario tags (this draft)? Owner decision.
- **Q2. Build and binding identity for smoke runs.** Is the worktree commit plus native artifact hash acceptable as `BuildId` until #46 defines one? UNVERIFIED that #46 has a definition.
- **Q3. Synthetic exclusion.** Confirm that #33 will add a subject kind and exclude synthetic subjects from title levels. This is a requirement on #33, not yet agreed.
- **Q4. Disc hash in results.** The disc hash is needed for checklist item 3. Whether it is stored in shared records is the same question as #33 Q3. UNVERIFIED.
- **Q5. Release gating.** Should a title-level regression ever block a release? Not decided. This draft only reports it.
- **Q6. Smoke gate path set.** The exact path list for the required smoke tier (section 4.3) is UNVERIFIED and needs a check against the CI configuration, which was not read.
- **Q7. Fixture naming.** `bios-putchar-marker` contains the word BIOS but describes a HLE call path. Rename or keep? A rename touches the fixture catalog and its pinned hashes. Owner decision.
- **Q8. Public API without auth.** The task brief for this worker states that the public API works without auth. The issue page fetched for this design did not state this, so it is UNVERIFIED here. Its relevance to #51 is also unknown, because this design stores no network state.
- **Q9. Timeout classification.** Whether `RecompilerExecutionStatus.TimedOut` maps to `Error` or `Blocked` (section 5.1) needs a code check. UNVERIFIED.
- **Q10. Where smoke results live.** Local JSONL only, or also committed snapshot for synthetic results? A committed snapshot of synthetic results would create churn. Proposed: local only in v1. UNVERIFIED that CI can persist between runs.
- **Q11. W13 review.** The #33 design has not been reviewed. Section 9 depends on it. Changes from that review must be reconciled here.

## 12. Verification of this document

- Design only. No build, test, or CI run was performed. [CONFIRMED by this change: one docs file]
- The issue summary comes from a WebFetch of the issue page, not from `gh`, which is not authenticated. The full Japanese body was not read verbatim. [CONFIRMED by process]
- Fixture facts come from `docs/development/generated-psx-exe-fixtures.md`, read in this worktree. Other code claims are OBSERVED from #33 and are not re-checked here. [CONFIRMED / OBSERVED as labelled]
- No ROM, BIOS, disc image, or copyrighted content was read or referenced by content. [CONFIRMED by process]

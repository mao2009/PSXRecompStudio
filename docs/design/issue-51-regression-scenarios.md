# Compatibility & Regression Scenarios: smoke set, evidence, and detection (Issue #51)

Tracking: [#51](https://github.com/mao2009/PSXRecompStudio/issues/51). Related: [#33](https://github.com/mao2009/PSXRecompStudio/issues/33) (Compatibility Registry), [#42](https://github.com/mao2009/PSXRecompStudio/issues/42) (Project/Workspace), [#44](https://github.com/mao2009/PSXRecompStudio/issues/44) (PS1 Hardware Runtime Architecture), [#46](https://github.com/mao2009/PSXRecompStudio/issues/46) (Recomp Artifact & Build Management), [#49](https://github.com/mao2009/PSXRecompStudio/issues/49) (Diagnostics & Recovery).

Status: **design only**. No code, schema file, test, ADR, or shared doc is changed by this document. Base: `origin/main` at `2e3ecad`. The compatibility registry input is `origin/batch2/w13-compat-registry-design` at `700633d` (`docs/design/issue-33-compat-registry.md`). It was read as an input. The four #33 changes in section 9 are approved by owner ruling (binding) but are not yet in the #33 text at `700633d`. At the last `ls-remote` check, W13 had not pushed an update, so the pin stays at `700633d` until it does.

## 0. Labels and constraints

- **CONFIRMED**: read in this worktree during this work, and the text says what is claimed.
- **OBSERVED**: taken from a repository document, a commit, a GitHub page, or the #33 design doc's citations. The underlying behavior was not re-run by this author.
- **PREDICTED**: a consequence of the design. Not implemented, not measured.
- **UNVERIFIED**: not checked. Treated as an open item.

Hard constraints: this design stores and references verification outcomes only. It contains no BIOS, ROM, disc image bytes, extracted title data, or copyrighted asset content. Titles appear as placeholders (`Title A`). Smoke tests use repository-authored synthetic executables only. A user-supplied real input is referenced by a hash and a bundle id, never by path or content.

## 1. Problem and scope

Issue #51 asks for a mechanism that continuously checks per-title Recomp/Runtime compatibility and regressions. [OBSERVED: issue #51 page, read through a WebFetch summary, not `gh` and not verbatim. The summary's overview line says the goal is a system for continuously verifying Recomp/Runtime compatibility and Regression per title.]

The issue body, as summarized (not verbatim), lists these scope areas: Boot, Gameplay, Save/Load, CD/Disc, Audio, Graphics, Input, Runtime stability, Known issues, and Regression tracking. [OBSERVED] Its completion checklist has seven unchecked items: (1) compatibility test model, (2) test result schema, (3) linking tests to title and disc hashes, (4) automated smoke test approach, (5) regression detection approach, (6) integration with the Compatibility Registry (#33), (7) GUI/CLI result-viewing boundary. [OBSERVED]

This document covers items 1 to 6 and sketches item 7. Out of scope: a real-title test corpus, any network service, and CI wiring details (a later implementation issue).

## 2. Current state

### 2.1 What exists

- Repository-authored synthetic PS-X EXE fixtures exist. They are generated in memory from MIPS instruction words, pinned by SHA-256, and run through the production recompiler/runtime path. Two of the five (`BiosPutCharMarker`, `UnresolvedJump`) are consumed by `RecompiledArtifactE2ETests`; consumed by name in `RecompiledArtifactE2ETests` (`BiosPutCharMarker`, `UnresolvedJump`) and in `RuntimeHostPublishE2E` (`NaturalEndWithV0`, `GuestFirmwareOpenbios`). All five are enumerated by `GeneratedPsxExeFixtureTests` via `All`. No test asserts the expected outcome fields of `NaturalEndWithTransitions` or `GuestFirmwareOpenbios`. [CONFIRMED: `docs/development/generated-psx-exe-fixtures.md`, "Generation contract" and "Verification"; `src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs:57,112,141`; `src/PSXRecomp.Tests/E2E/RuntimeHostPublishE2E.cs:50,455,493,535,679`; `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtureTests.cs:13-25,40`]
- The catalog `GeneratedPsxExeFixtures.All` contains **five** fixtures: `BiosPutCharMarker`, `UnresolvedJump`, `NaturalEndWithV0`, `NaturalEndWithTransitions`, `GuestFirmwareOpenbios`. [CONFIRMED: `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:202-203`, with IDs at lines 94, 112, 133, 157, 184]
- The fixture doc's "Current fixtures" table lists only two entries, so it is stale for the three added later. [CONFIRMED that the table lists two; OBSERVED that it is stale, since `All` lists five]
- The fixture doc states that no generated `.exe` is checked in and that the catalog excludes Sony BIOS code and user-provided inputs. [CONFIRMED: same doc, "Legal and ownership boundary"]
- The ID `bios-putchar-marker` names a BIOS-HLE call path, not BIOS code. The name is kept as-is; a rename is out of scope and flagged in section 11 (Q7). [CONFIRMED wording]
- Existing smoke-style tests: `src/PSXRecompStudio.Tests/MainWindowSmokeTests.cs` (Avalonia headless window and binding checks) and `src/PSXRecomp.Tests/RustAbiSmokeTests.cs` (managed to native to Rust chain, Issue #473, `AbiVersion == 1`). [CONFIRMED: both files exist and their headers were read]
- Capability matrix rules: a Verified row does not imply complete title compatibility, and verification requires an executable test that asserts an observable contract, not just absence of a crash. [CONFIRMED: `docs/status/capabilities.md:28`, `:55-60`]
- Execution outcomes: `RecompiledArtifactOutcome` (`Success`, `Blocked`, `Failure`) and `TitleExecutionState`. `TitleExecutionState.Cancelled` exists (Issue #820) and maps to `RecompiledArtifactOutcome.Blocked`. [CONFIRMED: `src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:47-49`; `src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:59`]
- `RecompilerExecutionStatus` has `TimedOut` and `Cancelled` (Issue #820). [CONFIRMED: `src/PSXRecomp.Core/Recompiler/RecompilerExecutionStatus.cs:34-35`]
- Other title identity, evidence/confidence, and environment types are as described in the #33 doc (section 2.1 there). [OBSERVED: #33 citations, not re-read in this worktree]

### 2.2 What does not exist

- No compatibility test model, result schema, or regression detector in `src`. [OBSERVED: #33 section 2.2 search; not re-run for #51 scope]
- No GPU field in `ExecutionEnvironmentReport`, no recomp `BuildId`, and no Runtime version identifier. [OBSERVED: #33 section 2.1]
- `DiscIdentity` has no content hash field. [OBSERVED: #33 citing `DiscIdentity.cs:10-14`]

### 2.3 Gap in #33

#33 section 2.1 (at `700633d`) lists `TitleExecutionState` values without `Cancelled`. This design adds the mapping in section 5.1. [CONFIRMED: `700633d` text vs. `TitleExecutionContract.cs:47-49`]

## 3. Compatibility test model

The test model is a set of **scenarios**. Each scenario is a repository-owned, versioned definition. It is run against a **subject** (a synthetic fixture or a user-supplied input) under a **binding** (section 6). It produces one **result** (section 5).

```
ScenarioDefinition {
  Id                 // "smoke.boot.synthetic-putchar", "smoke.runtime.unresolved-jump"
  Facet              // Boot | Gameplay | Save | Audio | Graphics | None   (None is an owner-approved #33 change, section 9)
  Tags[]             // Input | Runtime | Disc | ... (not facets; see below)
  Version            // bump when pass criteria change
  Subject            // SyntheticFixture(id, sha256) | UserInput(discHash?, exeFingerprint?)
  Criteria[]         // observable assertions only
  Tier               // Smoke | Extended | Manual
  Requires[]         // e.g. "Rust native staticlib", "GPU backend present", "input bundle"
}
```

Rules:

- A criterion must assert an observable contract: an outcome enum value, a guest PC, a register value, a TTY byte, a frame hash, a diagnostic code. "Did not throw" is not a criterion. [OBSERVED: capability matrix verification threshold]
- A scenario that depends on a facet not yet implemented (for example, Audio output, `Not claimed` in `capabilities.md:78`) must declare `Tier = Manual` or be absent. It must not be listed as Pass. [PREDICTED]
- **Facets versus tags.** The five #33 facet values are unchanged. The issue's extra areas (Input, Runtime stability, CD/Disc) are carried as `Tags`, and a scenario with no facet of its own (for example a Runtime stop) uses `Facet = None`. `None` is a new #33 value and is listed in section 9. It is excluded from level derivation (section 8). This follows the facet-list question in #33 Q1, and the choice is an owner decision (section 11, Q1). Scripted-input scenarios depend on #33 Q14 for their input evidence. [OBSERVED: #33 Q14 at `700633d`]

## 4. Smoke-test set (synthetic fixtures)

The smoke set is the subset that runs on every change without any user input. It must pass on a clean checkout, using only repository-authored code.

### 4.1 Initial smoke scenarios

| Scenario ID | Subject | Criteria | Source of truth |
|---|---|---|---|
| `smoke.boot.synthetic-putchar` | `bios-putchar-marker` | outcome `completed/success`; S1 = `0x7777`; TTY byte `P` | catalog expected fields (CONFIRMED); consumed by `RecompiledArtifactE2ETests` (CONFIRMED) |
| `smoke.runtime.unresolved-jump` | `unresolved-jump` | outcome `blocked/unsupported-transfer`; guest PC `0x80020000`; diagnostic `UNRESOLVED_TRANSFER` | catalog expected fields (CONFIRMED); consumed by `RecompiledArtifactE2ETests` (CONFIRMED) |
| `smoke.runtime.natural-end-transitions` | `natural-end-transitions` | outcome `completed/success`; result value `0x42`; guest PC `EntryPc + 16` | catalog expected fields (CONFIRMED). No test asserts its expected outcome fields. It is enumerated by `GeneratedPsxExeFixtureTests` via `All`, so it gets the hash check (CONFIRMED) |
| `smoke.parse.fixture-hash` | every catalog entry | generated bytes match pinned SHA-256 | fixture test (CONFIRMED: doc "Verification") |
| `smoke.native.rust-abi` | native staticlib | `AbiVersion == 1`; round-trip sentinel | `RustAbiSmokeTests.cs` (CONFIRMED that the test exists) |
| `smoke.studio.window` | headless window | DataContext type; binding resolves | `MainWindowSmokeTests.cs` (CONFIRMED that the test exists) |

Fixture selection for the smoke tier:

Host mode per fixture, applied to every fixture the same way:

- `bios-putchar-marker` and `unresolved-jump`: run through the production recompiler/runtime path used by `RecompiledArtifactE2ETests`. [CONFIRMED]
- `natural-end-transitions`: its source comment says it verifies "bridge mode" for non-guest-firmware cases. [CONFIRMED: `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:153-154`; OBSERVED: `docs/runtime/runtime-host.md:295-298`] Whether bridge mode needs host setup beyond the E2E path is UNVERIFIED. This is the reason it is in the smoke set only if that check passes.
- `natural-end-v0`: described as a "RuntimeHost-managed natural-end flow" [CONFIRMED description text]. It depends on host launch behavior that this design has not reviewed. Extended tier.
- `guest-firmware-openbios`: exercises the `GuestFirmware=true` mode, with no HLE, at `0x1FC00000`. Its input requirements beyond the repository-authored instruction words are UNVERIFIED. Extended tier, and not enabled in CI until those are checked. [CONFIRMED description text]

Selection:

- **Included:** `bios-putchar-marker`, `unresolved-jump`, and `natural-end-transitions`, all subject to the UNVERIFIED host-mode check above for the third.
- **Excluded from smoke:** `natural-end-v0` and `guest-firmware-openbios`, both in the Extended tier.

Mapping to facets: `smoke.boot.*` uses `Facet = Boot`. `smoke.runtime.*` uses `Facet = None` with `Tags = [Runtime]`, so it does not count toward Boot. A classified stop (`unresolved-jump`) is a Runtime result. `smoke.parse.*` and `smoke.native.*` are infrastructure checks and produce no Compatibility result.

Important: `smoke.boot.synthetic-putchar` proves that the recompiler/runtime path works on a synthetic program. It does **not** prove that any title boots. Its `Subject` is `SyntheticFixture`, and the registry excludes such results from title levels (section 8). [PREDICTED]

### 4.2 Synthetic-fixture growth rules

- New fixtures are added only as repository-authored MIPS instruction words, following the fixture doc generation contract. [CONFIRMED rule]
- Each new fixture pins its SHA-256 and declares its expected evidence. [CONFIRMED rule]
- A fixture must not reproduce a real title's code sequence, tables, or assets. Review checks this. [PREDICTED rule]
- The fixture doc's table must be regenerated when fixtures are added. Its current two-row table is stale against `All`. [OBSERVED]

### 4.3 Gate

The smoke tier is a required check for changes under `src/PSXRecomp.Core/Recompiler`, `src/PSXRecomp.Core/Runtime`, `src/PSXRecomp.Core/Execution`, and the native crate. The exact path set is UNVERIFIED and listed in section 11 (Q6). [PREDICTED]

## 5. Result record

The result record reuses the #33 types. It does not define a second evidence model. Where #33 has no field, this design adds one and says so. Field mapping:

| This design | #33 counterpart | Status |
|---|---|---|
| `ScenarioId`, `ScenarioVersion` | `ScenarioResult.ScenarioId`, `ScenarioVersion` (#33 §3.4) | reused |
| `Binding` | `ScenarioResult.Binding` (§3.4), `Binding` (§4.4) | reused |
| `Outcome` | `ScenarioResult.Outcome` (§3.4, values in §3.5) | reused |
| `EvidenceIds` | `ScenarioResult.EvidenceIds` (§3.4) | reused |
| `TitleKey` | `ScenarioResult.TitleKey` (§3.4), required | **changed** (owner-approved): may be omitted only when `Subject` is `SyntheticFixture` (see 5.4) |
| `Facet` | `ScenarioResult.Facet` (§3.4) | **changed**: adds a `None` value for scenarios with no facet (for example `smoke.runtime.*`) |
| `Producer` (tool, version, reporter class) | `EvidenceRecord.ProducedBy` (§4.1) and reporter class (§4.1.2) | reused |
| `CapturedUnixSeconds` | `EvidenceRecord.CapturedUnixSeconds` (§4.1) | reused |
| `ArtifactHash` | `EvidenceRecord.ArtifactHash` (§4.1) | reused |
| `Subject` (kind plus fixture or user-input reference) | none | **new**; required by 5.4 |
| `ObservedFacts[]` (name/value pairs, e.g. guest PC, TTY byte, diagnostic code, stop reason) | none | **new**; needed for criteria and regression diffs (7.2) |
| `RecordedAt` | `ScenarioResult.RecordedAt` (§3.4 in 700633d text) | reused |

No `Schema` string or separate `ResultId` is introduced. The schema identity is #33's `psxrecomp.compatibility-registry.v1`, and result identity follows #33's `EvidenceRecord.Id` content-hash scheme. [PREDICTED]

### 5.1 Outcome

Outcome values follow #33 section 3.5, plus the owner-approved `Cancelled` value. `Unknown` is defined here and is not yet in #33. [OBSERVED: #33 at `700633d`]

| Outcome | Meaning | Maps from |
|---|---|---|
| `Pass` | All criteria met on this binding. | criteria evaluation |
| `Fail` | Evidence produced, a criterion not met. | criteria evaluation |
| `Blocked` | Stopped at a known unsupported boundary. | `RecompiledArtifactOutcome.Blocked` (OBSERVED: #33 citing `RecompiledArtifactExitCode.cs:18-21`) |
| `Error` | Harness or host failure, not a guest result. | `RecompiledArtifactOutcome.Failure` (OBSERVED) |
| `NotRun` | No attempt, or attempt produced no evidence. | run planner |
| `Cancelled` | Stopped by a cancellation request before a classified guest end. Distinct from `Blocked`, `NotRun`, `Unknown`, `Fail`. | `TitleExecutionState.Cancelled` (`TitleExecutionContract.cs:49`). The registry maps from the title-level state, not from `RecompiledArtifactOutcome`, which folds it into `Blocked` (`RecompiledArtifactExitCode.cs:59`). |
| `Unknown` | An attempt produced a result whose state cannot be determined, for example a missing or unparseable outcome field. Distinct from `NotRun` (no attempt) and `Cancelled`. Never counts toward a level. | Defined here. The owner ruling names the state; its definition is PREDICTED and UNVERIFIED against #33 text. |

Stop reasons, which are carried in `ObservedFacts` and not in `Outcome`:

| Stop reason | Source | Outcome | Notes |
|---|---|---|---|
| Completed | `TitleExecutionState.Completed` | `Pass` only if criteria are met, else `Fail` | `Completed` means a classified end, not gameplay completion (OBSERVED: #33 §2.1) |
| Unsupported transfer | `TitleExecutionState.UnsupportedTransfer` | `Blocked` | Diagnosable stop |
| Budget stop | `BudgetExhausted` / `OUTER_BUDGET_EXHAUSTED` pattern (`capabilities.md:87`) | `Blocked` | Diagnosable stop |
| **User cancel** | `RecompilerExecutionStatus.Cancelled` (`RecompilerExecutionStatus.cs:35`), reaching `TitleExecutionState.Cancelled` (`TitleExecutionContract.cs:49`) | Registry outcome `Cancelled` (owner-approved). The run-level `RecompiledArtifactOutcome` is still `Blocked`, because `RecompiledArtifactExitCode.cs:59` folds `Cancelled` into it, and the registry does not use that run-level value. The engine status becomes `Cancelled` at `ExecutionOrchestrator.cs:156-159` (returns `TitleExecutionState.Cancelled`); `RecompiledArtifactExitCode.cs:59` maps `Cancelled` to `Blocked`. [CONFIRMED] | A cancel is not a guest boundary. It is never Pass or Fail, and section 7.1 rule 6 keeps it out of regression detection. |
| Engine timeout | `RecompilerExecutionStatus.TimedOut` | `Error` with `stopReason = TimedOut`. The orchestrator maps any non-`Completed`, non-`Cancelled` status to `RuntimeFailure` with the engine's diagnostic code or `ENGINE_FAILED` (`ExecutionOrchestrator.cs:162-170`); `RecompiledArtifactExitCode.cs:56` maps `RuntimeFailure` to `Failure`; #33 aligns `Failure` with `Error`. [CONFIRMED for each step] | Closes Q8 for the orchestrator path. A caller that bypasses the orchestrator is UNVERIFIED. |

Rules:

- `Blocked` is a diagnosable stop, not a defect. [OBSERVED: `capabilities.md:49`, per #33]
- `Error` is excluded from the denominator and reported. An errored run cannot turn a Pass into a Fail. [PREDICTED]
- `NotRun` is never shown as `Pass` or `Fail`. [PREDICTED]
- `Cancelled` and `Unknown` are never `Pass` or `Fail`, and each is distinct from `NotRun` and from each other. This is an owner-approved condition for `Cancelled`. [PREDICTED for `Unknown`]

### 5.2 Evidence attachments

- Attachments are referenced by SHA-256 of the captured file (log, frame, report). The attachment is stored outside the result record. [PREDICTED; matches #33 `EvidenceRecord.ArtifactHash`]
- For a user input, an attachment must not contain game data (no frame dumps of copyrighted art in a shared bundle). Such attachments stay local and only their hash is shared. [PREDICTED]
- Frame comparison uses a hash of the frame buffer, not the frame. [PREDICTED]

### 5.3 Producer and reporter class

Reporter class follows #33 section 4.1.2: `Maintainer`, `Automated`, `Community`. [OBSERVED: #33 at `700633d`] Synthetic smoke runs are `Automated`. `AIPrediction` and `AnalyzerFinding` evidence can never produce a `Pass`. [OBSERVED: #33 section 4.1.1]

### 5.4 Synthetic subjects

#33 section 3.1 keys `TitleEntry` by `ReleaseKey`, and section 3.4 requires `TitleKey` on `ScenarioResult` with no subject field. [OBSERVED: #33 at `700633d`] So #33 cannot tell a synthetic result from a title result. Two options:

- (a) Add a `Subject` field to #33 §3.4 (this design's preference).
- (b) Use a reserved pseudo-`TitleKey` such as `synthetic:<fixtureId>`. Rejected: it overloads a key that #33 treats as a real title identity.

Either way, the level derivation in #33 section 5 must exclude `Subject.Kind = SyntheticFixture`. This is a requirement on #33 (Q3). Under option (a), `TitleKey` also becomes optional when `Subject` is `SyntheticFixture`. That is a second #33 change, listed in section 9 and Q3. Scenarios with no facet use the `None` value (section 3), which is a third change, also listed.

Owner-approved conditions (binding ruling) on these changes: synthetic results never enter a real-title compatibility evaluation; `Cancelled` never counts as `Pass` or `Fail`; `Unknown`, `NotRun` and `Cancelled` are distinct; #33's identity (`ReleaseKey`) and binding rules are kept unchanged.

## 6. Binding

Every result is bound to the environment that produced it. The #33 binding is `(BuildId, RuntimeVersion, Os, Gpu, DotNet, RuntimeIdentifier)`. [OBSERVED: #33 section 4.4]

For the smoke tier, the binding comes from the same environment source as the rest of the run. Two items are unresolved:

- `BuildId` and `RuntimeVersion` do not exist in `src`. [OBSERVED: #33 section 2.1] A smoke run cannot be bound until one of them is defined. A candidate is the worktree commit plus the staged native artifact hash. This is a proposal (PREDICTED), and the choice is open question Q2.
- GPU is absent from `ExecutionEnvironmentReport`. [OBSERVED: #33] Synthetic smoke scenarios do not need a GPU. Graphics scenarios are `Tier = Manual` until the GPU field exists. [PREDICTED]

Binding equality is exact (per #33). A result under binding A never counts toward binding B. [OBSERVED design in #33, adopted here]

## 7. Regression detection

Detection is derived from results. It is never typed by hand.

### 7.1 Conditions

Let `R(s, b)` be the latest non-superseded result for scenario `s` on binding `b`.

1. **Regression**: `R(s, A) = Pass` and `R(s, B)` is `Fail` or `Blocked`, for the same `Subject` and `ScenarioId`, where `B` is a later binding of the same title. [PREDICTED; mirrors #33 section 6.2]
2. **Unverified after change**: `R(s, A) = Pass` and `R(s, B) = NotRun` (or absent) while `B` differs from `A` in any binding field. Reported as `Unverified after change`, not as a regression. [PREDICTED]
3. **Synthetic regression**: the same rule applies to synthetic subjects, but the result is labelled `synthetic` and never rolls up into a title level. A synthetic regression is the primary signal of the smoke tier. [PREDICTED]
4. **Error and Unknown are not regressions**: `Error` or `Unknown` on B does not create a regression. `Error` creates a harness alert; `Unknown` is reported as an unclassified result. [PREDICTED]
5. **Scenario version change**: if `ScenarioVersion` differs between A and B, the pair is not compared. The result is `Re-baselined`, and the old Pass is kept as history. [PREDICTED]
6. **Cancel is not a regression**: a `Cancelled` result is never Pass or Fail, so it cannot trigger rule 1 on B and cannot establish Pass on A. It is treated as `NotRun` for rule 2. Without this distinction, an operator cancel mapped to `Blocked` would be reported as a regression under rule 1 and #33 section 6.2. [PREDICTED; the rule is needed so that `Cancelled` is not compared as `Blocked`]

### 7.2 Derived record

A regression record names: scenario id and version, subject, both binding keys, both result ids, and the first differing `ObservedFacts` entry. It does not copy evidence bodies. [PREDICTED]

### 7.3 Gate behavior

- Synthetic regression on the smoke tier fails the check. [PREDICTED]
- Title-level regression does not fail repository CI in this design. It is reported to the registry and the Studio view. A release-blocking policy is open (Q5). [PREDICTED]

## 8. Integration with the Compatibility Registry (#33)

Each row names the #33 decision it depends on. If that decision changes, this section changes. All cited #33 text is at `700633d`.

| Integration point | This design needs | #33 decision it depends on | Status |
|---|---|---|---|
| Result outcome | the five outcome values | §3.5 Outcome vocabulary | proposal |
| Evidence record | `EvidenceRecord` with `Binding`, `ArtifactHash`, `ProducedBy` | §4.1 | proposal |
| Reporter class | `Maintainer`, `Automated`, `Community` | §4.1.2 | proposal |
| Binding | exact-match binding | §4.4 and #33 Q5 | proposal; exact match is the default |
| Level derivation | synthetic results excluded from title levels; `TitleKey` optional for synthetic subjects; `Facet = None` excluded | §3.1 `TitleEntry` keyed by `ReleaseKey`; §3.4 `ScenarioResult.TitleKey` required, no subject field, five facets only. #33 has no subject kind. | **new requirements from this design**; #33 must add them (Q3, section 9) |
| Facets | five facets (Boot, Gameplay, Save, Audio, Graphics) | §3.2 and #33 Q1 | proposal; the issue asks for more (section 3) |
| Disc binding | a disc content hash | #33 Q3 and `DiscIdentity` gap | UNVERIFIED |
| Build identity | a `BuildId` | #33 Q6 | UNVERIFIED |
| Persistence | append-only JSONL plus snapshot (Option D) | §7.2 | proposal |
| Confidence | ordinal levels, no `Certain`, no numeric Score | §4.3 | proposal |
| Scenario catalog | a versioned catalog that is part of the derived key | §5 and #33 Q13 | proposal |

Concrete boundaries:

- This design reuses the #33 record types, as the field table in section 5 shows, and adds only the fields marked **new**. [PREDICTED]
- The smoke tier writes results through `ICompatibilityStore` (#33 section 8). It does not write files directly. [PREDICTED]
- Level derivation stays in #33's pure Core. The regression detector (section 7) is also pure: it takes two result sets and returns regression records, with no clock and no I/O. [PREDICTED, consistent with #33 section 8]

## 9. Decisions this design depends on from #33

If the following #33 decisions change, this design must be revisited. All six were checked against `700633d`, and each is present there:

1. Outcome vocabulary, with `NotRun` distinct from `Fail` (#33 §3.5).
2. Exact binding equality, and no implicit carry-forward (#33 §4.4).
3. Reporter classes without a required agent identity (#33 §4.1.2).
4. The five-facet set (#33 §3.2; #33 Q1).
5. Persistence as Option D, with the append-only log as v1 (#33 §7.2).
6. Scenario catalog versioning as part of the derived key (#33 §5; #33 Q13).

This design relies on these four requirements on #33. The owner has approved them (binding ruling), but #33 at `700633d` does not yet contain them. The wording below is PREDICTED until W13 updates #33.

1. A `Subject` field (kind plus fixture or user-input reference), so synthetic results can be excluded (section 5.4).
2. `TitleKey` optional when `Subject` is `SyntheticFixture` (section 5, section 5.4). #33 §3.4 currently requires it.
3. A `None` value on `Facet` for scenarios with no facet (section 3). #33 §3.2 lists five facets only.
4. A `Cancelled` row in the execution-state table (section 2.3). #33 §2.1 has no `Cancelled` entry, which the `700633d` text confirms.

## 10. GUI and CLI viewing boundary (checklist item 7)

- Studio: a read-only view of the derived read model, one row per binding in use, and a badge for each regression (#33 section 9). [PREDICTED]
- Studio must show the smoke tier separately from title levels. A synthetic Pass shown in the title grid is a defect. [PREDICTED]
- CLI (optional, later): `compat results` and `compat regressions` subcommands that print the same derived model as JSON. No write path in v1. [PREDICTED]
- Neither surface computes outcomes or levels itself. Both call the pure Core derivation. [PREDICTED]

## 11. Open questions

- **Q1. Facets versus tags.** The issue lists Input, Runtime stability, CD/Disc, and Save/Load. This design carries Input, Runtime, and Disc as tags. The overlap with #33 Q1 (facet list) and #33 Q2 (CD-ROM and streaming) is the same question, so the answer is taken from #33. Owner decision. [OBSERVED: the issue list is from the summary in section 1]
- **Q2. Build and binding identity for smoke runs.** Is the worktree commit plus native artifact hash acceptable as `BuildId` until #46 defines one? UNVERIFIED that #46 has a definition.
- **Q3. Synthetic exclusion and related #33 fields.** #33 must add:
  - a `Subject` field (section 5.4), and exclude synthetic subjects from title levels;
  - `TitleKey` optional when `Subject` is `SyntheticFixture` (section 5);
  - a `None` value on `Facet` (section 3);
  - a `Cancelled` row in the execution-state table (section 2.3). #33 §2.1 has no `Cancelled` entry at `700633d`.

  All four are approved by owner ruling. The #33 text that records them is pending. Section 9 lists them, with the conditions.
- **Q4. Disc hash in results.** The disc hash is needed for checklist item 3. Whether it is stored in shared records is the same question as #33 Q3. UNVERIFIED.
- **Q5. Release gating.** Should a title-level regression ever block a release? Not decided. This draft only reports it.
- **Q6. Smoke gate path set.** The exact path list for the required smoke tier (section 4.3) is UNVERIFIED and needs a check against the CI configuration, which was not read.
- **Q7. Fixture ID rename.** The fixture ID `bios-putchar-marker` contains the word BIOS but describes an HLE call path. A rename would change:
  - the ID string at `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:94`, and any test that matches it by ID;
  - the fixture doc and `docs/development/showcase-demo.md:25`, which names the ID in its text.

  It would not change the pinned SHA-256 or the generated bytes. The ID is not written into the EXE: `BuildExe` takes instruction words and header fields only (`GeneratedPsxExeFixtures.cs`, method `BuildExe`). [CONFIRMED for the BuildExe signature; the ID-usage list is OBSERVED from a grep not repeated here]. Owner decision.
- **Q8. Timeout classification. Closed for the orchestrator path.** `RecompilerExecutionStatus.TimedOut` becomes `RuntimeFailure` and then `Failure`, which maps to `Error` (section 5.1). [CONFIRMED: `ExecutionOrchestrator.cs:162-170`, `RecompiledArtifactExitCode.cs:56`] Still UNVERIFIED: whether any caller reads `TimedOut` outside the orchestrator.
- **Q9. Where smoke results live.** Local JSONL only, or also a committed snapshot of synthetic results? A committed snapshot would churn. Proposed: local only in v1. UNVERIFIED that CI can persist between runs.
- **Q10. #33 review.** Section 9 depends on #33 at `700633d`. Any changes from #33's review must be reconciled here. #33 Q10 (feedback intake) is also open and is not used by this design.

## 12. Verification of this document

- Design only. No build, test, or CI run was performed. [CONFIRMED by this change: one docs file]
- The issue summary comes from a WebFetch of the issue page, not from `gh`, which is not authenticated. The full Japanese body was not read verbatim. [CONFIRMED by process]
- Fixture facts come from `GeneratedPsxExeFixtures.cs` and `docs/development/generated-psx-exe-fixtures.md`, read in this worktree. Other code claims are OBSERVED from #33 and are not re-checked here. [CONFIRMED / OBSERVED as labelled]
- No ROM, BIOS, disc image, or copyrighted content was read or referenced by content. [CONFIRMED by process]

# Compatibility & Regression Scenarios: smoke set, evidence, and detection (Issue #51)

Tracking: [#51](https://github.com/mao2009/PSXRecompStudio/issues/51). Related: [#33](https://github.com/mao2009/PSXRecompStudio/issues/33) (Compatibility Registry), [#42](https://github.com/mao2009/PSXRecompStudio/issues/42) (Project/Workspace), [#44](https://github.com/mao2009/PSXRecompStudio/issues/44) (PS1 Hardware Runtime Architecture), [#46](https://github.com/mao2009/PSXRecompStudio/issues/46) (Recomp Artifact & Build Management), [#49](https://github.com/mao2009/PSXRecompStudio/issues/49) (Diagnostics & Recovery).

Status: **design only**. No code, schema file, test, ADR, or shared doc is changed by this document. Base: `origin/main` at `2e3ecad`. The registry input is `origin/batch2/w13-compat-registry-design`, pinned at `441cd6a` (`docs/design/issue-33-compat-registry.md`). At the last `ls-remote` check, W13 had announced a further update (`TitleKey` as `ReleaseKey`, `Cancelled` in the registry Outcome vocabulary, and de-duplication of the `NotRun`/`Cancelled` wording). That update was not on the remote, so this document stays at `441cd6a` and must be re-pinned when it lands (section 9.2).

## 0. Labels and constraints

- **CONFIRMED**: read in this worktree during this work, and the text says what is claimed.
- **OBSERVED**: taken from a repository document, a commit, a GitHub page, or a #33 section at the pinned revision. Not re-run by this author.
- **PREDICTED**: a consequence of the design. Not implemented, not measured.
- **UNVERIFIED**: not checked. Treated as an open item.
- **NEEDS_OWNER_DECISION**: a proposed change to #33 or to the result schema that the owner has not approved. These are listed in section 12 and are not part of the schema in sections 3 to 8.

Hard constraints: this design stores and references verification outcomes only. It contains no BIOS, ROM, disc image bytes, extracted title data, or copyrighted asset content. Titles appear as placeholders (`Title A`). Smoke tests use repository-authored synthetic executables only. A user-supplied real input is referenced by a hash and a bundle id in the evidence store, never by path or content.

## 1. Problem and scope

Issue #51 asks for a mechanism that continuously checks per-title Recomp/Runtime compatibility and regressions. [OBSERVED: issue #51 page, read through a WebFetch summary, not `gh` and not verbatim. The summary's overview line says the goal is a system for continuously verifying Recomp/Runtime compatibility and Regression per title.]

The issue body, as summarized (not verbatim), lists these scope areas: Boot, Gameplay, Save/Load, CD/Disc, Audio, Graphics, Input, Runtime stability, Known issues, and Regression tracking. [OBSERVED] Its completion checklist has seven unchecked items: (1) compatibility test model, (2) test result schema, (3) linking tests to title and disc hashes, (4) automated smoke test approach, (5) regression detection approach, (6) integration with the Compatibility Registry (#33), (7) GUI/CLI result-viewing boundary. [OBSERVED]

This document covers items 1 to 6 and sketches item 7. Out of scope: a real-title test corpus, any network service, and CI wiring details (a later implementation issue).

## 2. Current state

### 2.1 What exists

- Repository-authored synthetic PS-X EXE fixtures exist. They are generated in memory from MIPS instruction words, pinned by SHA-256, and run through the production recompiler/runtime path. [CONFIRMED: `docs/development/generated-psx-exe-fixtures.md`, "Generation contract" and "Verification"]
- The catalog `GeneratedPsxExeFixtures.All` contains **five** fixtures: `BiosPutCharMarker`, `UnresolvedJump`, `NaturalEndWithV0`, `NaturalEndWithTransitions`, `GuestFirmwareOpenbios`. [CONFIRMED: `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:202-203`, with IDs at lines 94, 112, 133, 157, 184]
- Two of the five (`BiosPutCharMarker`, `UnresolvedJump`) are consumed by name in `RecompiledArtifactE2ETests`. Two more (`NaturalEndWithV0`, `GuestFirmwareOpenbios`) are consumed by name in `RuntimeHostPublishE2E`. All five are enumerated by `GeneratedPsxExeFixtureTests` via `All`. No test asserts the expected outcome fields of `NaturalEndWithTransitions` or `GuestFirmwareOpenbios`. [CONFIRMED: `src/PSXRecomp.Tests/E2E/RecompiledArtifactE2ETests.cs:57,112,141`; `src/PSXRecomp.Tests/E2E/RuntimeHostPublishE2E.cs:50,455,493,535,679`; `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtureTests.cs:13-25,40`]
- The fixture doc's "Current fixtures" table lists only two entries, so it is stale for the three added later. [CONFIRMED that the table lists two; OBSERVED that it is stale, since `All` lists five]
- The fixture doc states that no generated `.exe` is checked in and that the catalog excludes Sony BIOS code and user-provided inputs. [CONFIRMED: same doc, "Legal and ownership boundary"]
- The ID `bios-putchar-marker` names a BIOS-HLE call path, not BIOS code. The name is kept as-is; a rename is out of scope and flagged in section 11 (Q6). [CONFIRMED wording]
- Existing smoke-style tests: `src/PSXRecompStudio.Tests/MainWindowSmokeTests.cs` (Avalonia headless window and binding checks) and `src/PSXRecomp.Tests/RustAbiSmokeTests.cs` (managed to native to Rust chain, Issue #473, `AbiVersion == 1`). [CONFIRMED: both files exist and their headers were read]
- Capability matrix rules: a Verified row does not imply complete title compatibility, and verification requires an executable test that asserts an observable contract, not just absence of a crash. [CONFIRMED: `docs/status/capabilities.md:28`, `:55-60`]
- Execution outcomes: `RecompiledArtifactOutcome` (`Success`, `Blocked`, `Failure`) and `TitleExecutionState`. `TitleExecutionState.Cancelled` exists (Issue #820) and `RecompiledArtifactExitCode.Classify` maps it to `RecompiledArtifactOutcome.Blocked`. [CONFIRMED: `src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:47-49`; `src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:59`]
- `RecompilerExecutionStatus` has `TimedOut` (line 29) and `Cancelled` (line 35, Issue #820). [CONFIRMED: `src/PSXRecomp.Core/Recompiler/RecompilerExecutionStatus.cs:29,35`]
- Other title identity, evidence/confidence, and environment types are as described in the #33 doc (section 2.1 there). [OBSERVED: #33 citations, not re-read in this worktree]

### 2.2 What does not exist

- No compatibility test model, result schema, or regression detector in `src`. [OBSERVED: #33 section 2.2 search; not re-run for #51 scope]
- No GPU field in `ExecutionEnvironmentReport`, no recomp `BuildId`, and no Runtime version identifier. [OBSERVED: #33 section 2.1]
- `DiscIdentity` has no content hash field. [OBSERVED: #33 citing `DiscIdentity.cs:10-14`]

### 2.3 Cancelled in #33

At `441cd6a`, #33 records `Cancelled` in three places: the execution-state list (section 2.1), the Outcome table (section 3.5), and the required-change list (section 9.1 item 4). Section 5.1 of this document relies on these. [CONFIRMED: `441cd6a` `docs/design/issue-33-compat-registry.md` lines 60, 185, 382]

## 3. Compatibility test model

The test model is a set of **scenarios**. Each scenario is a repository-owned, versioned definition. It is run against a **subject** under a **binding** (section 6). It produces one **result** (section 5).

`ScenarioDefinition` is W15-local catalog metadata. It is not part of the #33 result schema, and the registry never reads it except through `ScenarioId` and `ScenarioVersion`.

```
ScenarioDefinition {                 // W15-local catalog entry
  Id                 // "smoke.boot.synthetic-putchar", "smoke.runtime.unresolved-jump"
  Facet              // Boot | Gameplay | Save | Audio | Graphics | None   (#33 §3.2, None owner-approved)
  Tags[]             // Input | Runtime | Disc | ... (catalog metadata, not facets)
  Version            // bump when pass criteria change
  SubjectKind        // RealTitle | SyntheticFixture   (#33 §3.4 enum)
  FixtureId?         // present only when SubjectKind = SyntheticFixture; names a catalog fixture
  Criteria[]         // observable assertions only
  Tier               // Smoke | Extended | Manual   (catalog metadata)
  Requires[]         // e.g. "Rust native staticlib", "GPU backend present", "input bundle"
}
```

Rules:

- A criterion must assert an observable contract: an outcome enum value, a guest PC, a register value, a TTY byte, a frame hash, a diagnostic code. "Did not throw" is not a criterion. [OBSERVED: capability matrix verification threshold]
- A scenario that depends on a facet not yet implemented (for example, Audio output, `Not claimed` in `capabilities.md:78`) must declare `Tier = Manual` or be absent. It must not be listed as Pass. [PREDICTED]
- **Facets versus tags.** The five #33 facet values are unchanged, and `None` is added (owner-approved). The issue's extra areas (Input, Runtime stability, CD/Disc) are catalog `Tags`, not facets. A scenario with no facet of its own (for example a Runtime stop) uses `Facet = None`. This follows the facet-list question in #33 Q1, and the choice is an owner decision (section 11, Q1). Scripted-input scenarios depend on #33 Q14 for their input evidence. [OBSERVED: #33 Q14 at `441cd6a`]

## 4. Smoke-test set (synthetic fixtures)

The smoke set is the subset that runs on every change without any user input. It must pass on a clean checkout, using only repository-authored code.

### 4.1 Initial smoke scenarios

| Scenario ID | Subject | Criteria | Source of truth |
|---|---|---|---|
| `smoke.boot.synthetic-putchar` | `bios-putchar-marker` | outcome `completed/success`; S1 = `0x7777`; TTY byte `P` | catalog expected fields (CONFIRMED); consumed by `RecompiledArtifactE2ETests` (CONFIRMED) |
| `smoke.runtime.unresolved-jump` | `unresolved-jump` | outcome `blocked/unsupported-transfer`; guest PC `0x80020000`; diagnostic `UNRESOLVED_TRANSFER` | catalog expected fields (CONFIRMED); consumed by `RecompiledArtifactE2ETests` (CONFIRMED) |
| `smoke.runtime.natural-end-transitions` | `natural-end-transitions` | outcome `completed/success`; result value `0x42`; guest PC `EntryPc + 16` | catalog expected fields (CONFIRMED). No test asserts its expected outcome fields. It is enumerated by `GeneratedPsxExeFixtureTests` via `All`, so it gets the hash check (CONFIRMED). Included only if the host-mode check in section 4.1 (Q8) passes. |
| `smoke.parse.fixture-hash` | every catalog entry | generated bytes match pinned SHA-256 | fixture test (CONFIRMED: doc "Verification") |
| `smoke.native.rust-abi` | native staticlib | `AbiVersion == 1`; round-trip sentinel | `RustAbiSmokeTests.cs` (CONFIRMED that the test exists) |
| `smoke.studio.window` | headless window | DataContext type; binding resolves | `MainWindowSmokeTests.cs` (CONFIRMED that the test exists) |

Host mode per fixture, applied to every fixture the same way:

- `bios-putchar-marker` and `unresolved-jump`: run through the production recompiler/runtime path used by `RecompiledArtifactE2ETests`. [CONFIRMED]
- `natural-end-transitions`: its source comment says it verifies "bridge mode" for non-guest-firmware cases. [CONFIRMED: `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:153-154`; OBSERVED: `docs/runtime/runtime-host.md:295-298`] Whether bridge mode needs host setup beyond the E2E path is UNVERIFIED (Q8). It stays in the smoke set only if that check passes.
- `natural-end-v0`: described as a "RuntimeHost-managed natural-end flow" [CONFIRMED description text]. It depends on host launch behavior that this design has not reviewed. Extended tier.
- `guest-firmware-openbios`: exercises the `GuestFirmware=true` mode, with no HLE, at `0x1FC00000`. Its input requirements beyond the repository-authored instruction words are UNVERIFIED. Extended tier, and not enabled in CI until those are checked. [CONFIRMED description text]

Selection:

- **Included:** `bios-putchar-marker`, `unresolved-jump`, and `natural-end-transitions`, the last subject to the UNVERIFIED host-mode check (Q8).
- **Excluded from smoke:** `natural-end-v0` and `guest-firmware-openbios`, both in the Extended tier.

Mapping to facets: `smoke.boot.*` uses `Facet = Boot`. `smoke.runtime.*` uses `Facet = None` with `Tags = [Runtime]`, so it does not count toward Boot. A classified stop (`unresolved-jump`) is a Runtime result. `smoke.parse.*` and `smoke.native.*` are infrastructure checks and produce no Compatibility result.

Important: `smoke.boot.synthetic-putchar` proves that the recompiler/runtime path works on a synthetic program. It does **not** prove that any title boots. Its `SubjectKind` is `SyntheticFixture`, and it never enters a real-title evaluation (section 5). [PREDICTED]

### 4.2 Synthetic-fixture growth rules

- New fixtures are added only as repository-authored MIPS instruction words, following the fixture doc generation contract. [CONFIRMED rule]
- Each new fixture pins its SHA-256 and declares its expected evidence. [CONFIRMED rule]
- A fixture must not reproduce a real title's code sequence, tables, or assets. Review checks this. [PREDICTED rule]
- The fixture doc's table must be regenerated when fixtures are added. Its current two-row table is stale against `All`. [OBSERVED]

### 4.3 Gate

The smoke tier is a required check for changes under `src/PSXRecomp.Core/Recompiler`, `src/PSXRecomp.Core/Runtime`, `src/PSXRecomp.Core/Execution`, and the native crate. The exact path set is UNVERIFIED (Q5). [PREDICTED]

## 5. Result record

**Scope rule.** Real-title evaluation takes only `ScenarioResult` records with `Subject = RealTitle`. A `SyntheticFixture` result never enters a real-title evaluation, which covers level derivation (#33 §5), Evidence count (#33 §4.2) and Confidence (#33 §4.3). The #33 §3.4 "Subject rules" state the same input filter. [CONFIRMED: `441cd6a` §3.4, lines 169-173]

**Outcome rule.** Evidence count and Confidence count `ScenarioResult.Outcome`, not `EvidenceRecord`. #33 §4.1 defines `EvidenceRecord` without an `Outcome` field, while #33 §4.2 counts "`EvidenceRecord` ids with `Outcome = Pass`". This is a defect in #33 §4.2, reported to W13 in section 9.2. [CONFIRMED: `441cd6a` §4.1 field list and §4.2 text, lines 204-230]

The result record uses the #33 `ScenarioResult` (§3.4) without extra fields. Field mapping:

| This design | #33 counterpart | Status |
|---|---|---|
| `ScenarioId`, `ScenarioVersion` | `ScenarioResult.ScenarioId`, `ScenarioVersion` (§3.4) | reused |
| `Binding` | `ScenarioResult.Binding` (§3.4), `Binding` (§4.4) | reused |
| `Outcome` | `ScenarioResult.Outcome` (§3.4, values in §3.5) | reused; six values, section 5.1 |
| `EvidenceIds` | `ScenarioResult.EvidenceIds` (§3.4) | reused |
| `Facet` | `ScenarioResult.Facet` (§3.4) | **changed** (owner-approved): adds `None` (§3.2) |
| `Subject` (`RealTitle` or `SyntheticFixture`) | `ScenarioResult.Subject` (§3.4) | **changed** (owner-approved): enum only, no payload |
| `TitleKey` | `ScenarioResult.TitleKey` (§3.4) | **changed** (owner-approved): may be omitted when `Subject = SyntheticFixture` (section 5.4) |
| `RecordedAt` | `ScenarioResult.RecordedAt` (§3.4) | reused |
| `Producer` (tool, version, reporter class) | `EvidenceRecord.ProducedBy` (§4.1), reporter class (§4.1.2) | reused; attached through `EvidenceIds` |
| `CapturedUnixSeconds`, `ArtifactHash` | `EvidenceRecord.CapturedUnixSeconds`, `ArtifactHash` (§4.1) | reused; on the evidence record |

Facts such as guest PC, TTY byte, and diagnostic code are not stored in the result. They are part of the captured artifact, whose SHA-256 is `EvidenceRecord.ArtifactHash`. Adding them to the result schema is proposal N1 (section 12) and needs owner decision.

No `Schema` string or separate `ResultId` is introduced. The schema identity is #33's `psxrecomp.compatibility-registry.v1`, and result identity follows #33's `EvidenceRecord.Id` content-hash scheme. [PREDICTED]

### 5.1 Outcome

Outcome values follow #33 section 3.5 at `441cd6a`, which includes the owner-approved `Cancelled` row. [OBSERVED: #33 at `441cd6a`]

| Outcome | Meaning | Maps from |
|---|---|---|
| `Pass` | All criteria met on this binding. | criteria evaluation |
| `Fail` | Evidence produced, a criterion not met. | criteria evaluation |
| `Blocked` | Stopped at a known unsupported boundary. | `RecompiledArtifactOutcome.Blocked` (OBSERVED: #33 citing `RecompiledArtifactExitCode.cs:18-21`) |
| `Error` | Harness or host failure, not a guest result. | `RecompiledArtifactOutcome.Failure` (OBSERVED) |
| `Cancelled` | Stopped by a cancellation request before a classified guest end. Distinct from `Blocked`, `NotRun` and `Fail`. | `TitleExecutionState.Cancelled` (`TitleExecutionContract.cs:49`). The registry maps from the title-level state, not from `RecompiledArtifactOutcome`, which folds it into `Blocked` (`RecompiledArtifactExitCode.cs:59`). |
| `NotRun` | No attempt, or attempt produced no evidence. | run planner |

Not an outcome: `Unknown` is a derived state for a title or facet with no records at all (#33 §3.5). A result is never `Unknown`.

Rules:

- `Blocked` is a diagnosable stop, not a defect. [OBSERVED: `capabilities.md:49`, per #33]
- `Error` is excluded from the denominator and reported. An errored run cannot turn a Pass into a Fail. [PREDICTED]
- `Cancelled`, `NotRun` and `Unknown` are never rendered as `Pass` or `Fail`, and never rendered as each other (#33 §3.5, owner-ruled).

Stop mapping, from execution state to registry Outcome:

| Stop | Source | Registry Outcome | Notes |
|---|---|---|---|
| Completed | `TitleExecutionState.Completed` | `Pass` if criteria are met, else `Fail` | `Completed` means a classified end, not gameplay completion (OBSERVED: #33 §2.1) |
| Unsupported transfer | `TitleExecutionState.UnsupportedTransfer` | `Blocked` | Diagnosable stop |
| Budget stop | `BudgetExhausted` / `OUTER_BUDGET_EXHAUSTED` pattern (`capabilities.md:87`) | `Blocked` | Diagnosable stop |
| User cancel | `RecompilerExecutionStatus.Cancelled` (`RecompilerExecutionStatus.cs:35`), reaching `TitleExecutionState.Cancelled` at `ExecutionOrchestrator.cs:157-159` | `Cancelled` | The run-level `RecompiledArtifactOutcome` is still `Blocked` (`RecompiledArtifactExitCode.cs:59`); the registry does not use that value. [CONFIRMED] |
| Engine timeout | `RecompilerExecutionStatus.TimedOut` (`RecompilerExecutionStatus.cs:29`) | `Error` | The orchestrator maps any non-`Completed`, non-`Cancelled` status to `RuntimeFailure` with the engine's diagnostic code or `ENGINE_FAILED` (`ExecutionOrchestrator.cs:162-171`). `RecompiledArtifactExitCode.cs:56` maps `RuntimeFailure` to `Failure`, and #33 aligns `Failure` with `Error`. [CONFIRMED for each step] Closes Q-timeout for the orchestrator path. |

### 5.2 Evidence attachments

- Attachments are referenced by SHA-256 of the captured file (log, frame, report) through `EvidenceRecord.ArtifactHash`. The attachment is stored outside the result record. [PREDICTED; matches #33 §4.1]
- For a user input, an attachment must not contain game data (no frame dumps of copyrighted art in a shared bundle). Such attachments stay local and only their hash is shared. [PREDICTED]
- Frame comparison uses a hash of the frame buffer, not the frame. [PREDICTED]

### 5.3 Producer and reporter class

Reporter class follows #33 section 4.1.2: `Maintainer`, `Automated`, `Community`. [OBSERVED: #33 at `441cd6a`] Synthetic smoke runs are `Automated`. `AIPrediction` and `AnalyzerFinding` evidence can never produce a `Pass`. [PREDICTED: this rule is proposed in #33 section 4.1.1, not stated as settled there]

### 5.4 Synthetic subjects

- `Subject` is the #33 enum `RealTitle | SyntheticFixture` and carries no payload. [CONFIRMED: `441cd6a` §3.4 line 158]
- A synthetic result identifies its fixture through `ScenarioId`, which the W15 catalog maps to a `FixtureId`. The result does not carry the fixture hash or a disc hash. [PREDICTED]
- `TitleKey` may be omitted when `Subject = SyntheticFixture` and is required for `RealTitle`. [CONFIRMED: `441cd6a` §3.4 line 159]
- W13 announced that `TitleKey` becomes `ReleaseKey`. That is not on the remote at `441cd6a`, so this document does not rely on it. [OBSERVED: W13 announcement, not on remote]
- Positive filter for real-title evaluation: `Subject = RealTitle`, `TitleKey` present, and `Facet != None`. Fail closed: a result whose `Subject` is missing or unrecognized is excluded (#33 §3.2 `None` and §3.4 Subject rules at `441cd6a`).
- Owner-approved conditions (binding ruling): synthetic results never enter a real-title compatibility evaluation; `Cancelled` never counts as `Pass` or `Fail`; `Unknown`, `NotRun` and `Cancelled` are distinct; #33's identity and binding rules are unchanged.

## 6. Binding

Every result is bound to the environment that produced it. The #33 binding is `(BuildId, RuntimeVersion, Os, Gpu, DotNet, RuntimeIdentifier)`. [OBSERVED: #33 section 4.4]

For the smoke tier, the binding comes from the same environment source as the rest of the run. Two items are unresolved:

- `BuildId` and `RuntimeVersion` do not exist in `src`. [OBSERVED: #33 section 2.1] A smoke run cannot be bound until one of them is defined. A candidate is the worktree commit plus the staged native artifact hash. This is a proposal (PREDICTED), and the choice is open question Q2.
- GPU is absent from `ExecutionEnvironmentReport`. [OBSERVED: #33] Synthetic smoke scenarios do not need a GPU. Graphics scenarios are `Tier = Manual` until the GPU field exists. [PREDICTED]

Binding equality is exact (per #33 §4.4). A result under binding A never counts toward binding B. [OBSERVED design in #33, adopted here]

## 7. Regression detection

Detection is derived from results. It is never typed by hand. Every rule below takes only `Subject = RealTitle` results for real-title regression. Synthetic regression (rule 3) is a separate signal.

### 7.1 Conditions

Let `R(s, b)` be the latest non-superseded result for scenario `s` on binding `b`.

1. **Regression**: `R(s, A) = Pass` and `R(s, B)` is `Fail` or `Blocked`, for the same `Subject` and `ScenarioId`, where `B` is a later binding of the same title. [PREDICTED; mirrors #33 section 6.2]
2. **Unverified after change**: `R(s, A) = Pass` and `R(s, B) = NotRun` (or absent) while `B` differs from `A` in any binding field. Reported as `Unverified after change`, not as a regression. [PREDICTED]
   - `R(s, B) = Cancelled` is a separate input. It is not handled by this rule. Its label is an open proposal (N3, section 12), so this document does not define one. `Cancelled` is never reported as `Unverified after change` and never as a regression.
3. **Synthetic regression**: the same rule applies to `SyntheticFixture` results, but the output is labelled `synthetic` and never rolls up into a title level. A synthetic regression is the primary signal of the smoke tier. [PREDICTED]
4. **Error is not a regression**: `Error` on B does not create a regression. It creates a harness alert. [PREDICTED]
5. **Scenario version change**: if `ScenarioVersion` differs between A and B, the pair is not compared. The result is `Re-baselined`, and the old Pass is kept as history. [PREDICTED]
6. **Cancel is not a regression**: a `Cancelled` result is never Pass or Fail, so it cannot trigger rule 1 on B and cannot establish Pass on A. It is kept distinct from `NotRun` throughout, so rule 2 never treats it as `NotRun`. Without this, an operator cancel mapped to `Blocked` would be reported as a regression under rule 1 and #33 section 6.2. [PREDICTED]

### 7.2 Derived record

A regression record names: scenario id and version, `Subject`, both binding keys, and both `ScenarioResult` ids (the `EvidenceIds`). It does not copy evidence bodies. A first-differing-fact field is not included, because facts are not in the result (section 5, N1). [PREDICTED]

### 7.3 Gate behavior

- Synthetic regression on the smoke tier fails the check. [PREDICTED]
- Title-level regression does not fail repository CI in this design. It is reported to the registry and the Studio view. A release-blocking policy is open (Q4). [PREDICTED]

## 8. Integration with the Compatibility Registry (#33)

Each row names the #33 decision it depends on. If that decision changes, this section changes. All cited #33 text is at `441cd6a`.

| Integration point | This design needs | #33 decision it depends on | Status |
|---|---|---|---|
| Result outcome | the six outcome values | §3.5 Outcome vocabulary | owner-approved `Cancelled` row in place |
| Evidence record | `EvidenceRecord` with `Binding`, `ArtifactHash`, `ProducedBy` | §4.1 | proposal |
| Reporter class | `Maintainer`, `Automated`, `Community` | §4.1.2 | proposal |
| Binding | exact-match binding | §4.4 and #33 Q5 | proposal; exact match is the default |
| Level derivation | real-title results only (`Subject = RealTitle`); `Facet = None` excluded | §3.4 Subject rules; §3.2 `None` | owner-approved; the input filter is in #33 §3.4 |
| Evidence count and Confidence | count `ScenarioResult.Outcome` of `RealTitle` results only | §4.2 and §4.3 | **defect in #33 §4.2** (section 5, section 9.2) |
| Facets | five facets plus `None` | §3.2 and #33 Q1 | owner-approved `None` |
| Disc binding | a disc content hash | #33 Q3 and `DiscIdentity` gap | UNVERIFIED; NEEDS_OWNER_DECISION (N2) |
| Build identity | a `BuildId` | #33 Q6 | UNVERIFIED |
| Persistence | append-only JSONL plus snapshot (Option D) | §7.2 | proposal |
| Confidence | ordinal levels, no `Certain`, no numeric Score | §4.3 | proposal |
| Scenario catalog | a versioned catalog that is part of the derived key | §5 and #33 Q13 | proposal |

Concrete boundaries:

- This design reuses the #33 record types, as the field table in section 5 shows, and adds no fields to the schema. Proposed additions are in section 12 and need owner decision. [PREDICTED]
- The smoke tier writes results through `ICompatibilityStore` (#33 section 8). It does not write files directly. [PREDICTED]
- Level derivation stays in #33's pure Core. The regression detector (section 7) is also pure: it takes two result sets and returns regression records, with no clock and no I/O. [PREDICTED, consistent with #33 section 8]

## 9. Dependency list for #33 (W15 §9)

### 9.1 Owner-approved changes this document depends on

This section is the W15 dependency list for #33. W13 does not yet cite this list; #33 at `441cd6a` has no reference to #51 or W15. If the decisions below change, this design must be revisited.

1. `Subject` on every result, with the enum `RealTitle | SyntheticFixture` and no payload (#33 §3.4).
2. `TitleKey` may be omitted when `Subject = SyntheticFixture` (#33 §3.4).
3. `Facet.None` for results not attributed to any facet (#33 §3.2).
4. A `Cancelled` row in the Outcome vocabulary, which is never Pass or Fail (#33 §3.5; §2.1 for the execution state).

Conditions from the same ruling, recorded in #33 §9.1 at `441cd6a`: synthetic results never enter a real-title compatibility evaluation; `Cancelled` never counts as Pass or Fail; `Unknown`, `NotRun` and `Cancelled` stay distinct; the identity and binding rules of #33 section 4 are unchanged.

The six #33 decisions below were checked against `441cd6a`. Item 1 gained the `Cancelled` row (§3.5), and item 4 gained `None` (§3.2). Items 2, 3, 5 and 6 are unchanged:

1. Outcome vocabulary, with `NotRun` distinct from `Fail` (#33 §3.5).
2. Exact binding equality, and no implicit carry-forward (#33 §4.4).
3. Reporter classes without a required agent identity (#33 §4.1.2).
4. The five-facet set plus `None` (#33 §3.2; #33 Q1).
5. Persistence as Option D, with the append-only log as v1 (#33 §7.2).
6. Scenario catalog versioning as part of the derived key (#33 §5; #33 Q13).

### 9.2 Pending W13 update, and findings for W13

- **Pin.** W13 announced an update (`TitleKey` as `ReleaseKey`; `Cancelled` in the registry Outcome vocabulary; de-duplication of the `NotRun` and `Cancelled` wording). It was not on `origin/batch2/w13-compat-registry-design` at the last check, which showed `441cd6a`. This document is re-pinned when that update is pushed. Until then, the claims about `TitleKey` in section 5.4 are not relied on.
- **Finding for W13: §4.2 Evidence count.** #33 §4.2 counts "`EvidenceRecord` ids with `Outcome = Pass`". #33 §4.1 defines `EvidenceRecord` without an `Outcome` field. The counted value should be `ScenarioResult.Outcome` (§3.4). This is a defect in #33, not an addition, so it does not need owner decision. [CONFIRMED: `441cd6a` lines 204-230]
- **Finding for W13: Cancelled in two places (MEDIUM).** #33 §3.5 lists `Cancelled` both as an Outcome row and in the derived-states table (`441cd6a` line 196). W15 uses it only as an Outcome. W13 must resolve the duplication. W15 makes no change to match either option until W13 does.
- **Finding for W13: Q10 numbering.** This document refers to #33 Q10 as feedback intake. That is correct at `441cd6a`; the reference is kept as is.

## 10. GUI and CLI viewing boundary (checklist item 7)

- Studio: a read-only view of the derived read model, one row per binding in use, and a badge for each regression (#33 section 9). [PREDICTED]
- Studio must show the smoke tier separately from title levels. A synthetic Pass shown in the title grid is a defect. [PREDICTED]
- CLI (optional, later): `compat results` and `compat regressions` subcommands that print the same derived model as JSON. No write path in v1. [PREDICTED]
- Neither surface computes outcomes or levels itself. Both call the pure Core derivation. [PREDICTED]

## 11. Open questions

- **Q1. Facets versus tags.** The issue lists Input, Runtime stability, CD/Disc, and Save/Load. This design carries Input, Runtime, and Disc as catalog tags. The overlap with #33 Q1 (facet list) and #33 Q2 (CD-ROM and streaming) is the same question, so the answer is taken from #33. Owner decision. [OBSERVED: the issue list is from the summary in section 1]
- **Q2. Build and binding identity for smoke runs.** Is the worktree commit plus native artifact hash acceptable as `BuildId` until #46 defines one? UNVERIFIED that #46 has a definition.
- **Q3. Disc hash in results and shared records.** See proposal N2 (section 12). Needed for checklist item 3. Same question as #33 Q3. UNVERIFIED.
- **Q4. Release gating.** Should a title-level regression ever block a release? Not decided. This draft only reports it.
- **Q5. Smoke gate path set.** The exact path list for the required smoke tier (section 4.3) is UNVERIFIED and needs a check against the CI configuration, which was not read.
- **Q6. Fixture ID rename.** The fixture ID `bios-putchar-marker` contains the word BIOS but describes an HLE call path. A rename would change:
  - the ID string at `src/PSXRecomp.Tests/E2E/GeneratedPsxExeFixtures.cs:94`, and any test that matches it by ID;
  - the fixture doc and `docs/development/showcase-demo.md:25`, which names the ID in its text.

  It would not change the pinned SHA-256 or the generated bytes. The ID is not written into the EXE: `BuildExe` takes instruction words and header fields only (`GeneratedPsxExeFixtures.cs`, method `BuildExe`). [CONFIRMED for the BuildExe signature; the ID-usage list is OBSERVED from a grep not repeated here]. Owner decision.
- **Q7. Smoke results storage.** Local JSONL only, or also a committed snapshot of synthetic results? A committed snapshot would churn. Proposed: local only in v1. UNVERIFIED that CI can persist between runs.
- **Q8. natural-end-transitions host mode.** Whether bridge mode needs host setup beyond the E2E path (section 4.1). UNVERIFIED.
- **Q9. W13 pin.** Re-pin to the W13 HEAD once the announced update is pushed (section 9.2). UNVERIFIED until then.

## 12. Proposals for W13 that need owner decision (NEEDS_OWNER_DECISION)

These are not approved and are not part of the schema in sections 3 to 8.

- **N1. Result facts.** Add an `ObservedFacts` list to `ScenarioResult` (guest PC, TTY byte, diagnostic code, stop reason). Without it, a regression record cannot name the first differing fact. The alternative, used by this document, is to keep these facts in the captured artifact referenced by `ArtifactHash`.
- **N2. Disc hash in results.** Carrying a disc content hash on a `RealTitle` result conflicts with #33 Q3, which is undecided on publishing disc hashes. This needs an owner decision on Q3 before any such field is added. Synthetic results never need one.
- **N3. Label for `Cancelled` on the regression path.** For rule 2, a `Cancelled` input after a Pass. Options: a label such as `Cancelled after change`, or a note on the Unverified-after-change label. Not defined here.

## 13. Verification of this document

- Design only. No build, test, or CI run was performed. [CONFIRMED by this change: one docs file]
- The issue summary comes from a WebFetch of the issue page, not from `gh`, which is not authenticated. The full Japanese body was not read verbatim. [CONFIRMED by process]
- Fixture facts come from `GeneratedPsxExeFixtures.cs` and `docs/development/generated-psx-exe-fixtures.md`, read in this worktree. #33 facts are from `441cd6a` and were re-read for the sections cited. Other code claims are OBSERVED and are not re-checked here. [CONFIRMED / OBSERVED as labelled]
- No ROM, BIOS, disc image, or copyrighted content was read or referenced by content. [CONFIRMED by process]

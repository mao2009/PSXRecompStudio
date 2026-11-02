# Compatibility Registry: per-title verification state (Issue #33)

Tracking: [#33](https://github.com/mao2009/PSXRecompStudio/issues/33). Related: [#16](https://github.com/mao2009/PSXRecompStudio/issues/16) (Structured Logging), [#22](https://github.com/mao2009/PSXRecompStudio/issues/22) (Memory Card interoperability), [#23](https://github.com/mao2009/PSXRecompStudio/issues/23) (review-finding feedback process per `docs/development/quality-stack.md`; the community-feedback reading is UNVERIFIED, see Q10).

Status: **design only**. No code, schema file, ADR, or shared doc is changed by this document. Code evidence is taken from `origin/main` at `12b6a31`. The source files cited here are identical at `2e3ecad` and `12b6a31` (checked with `git diff 2e3ecad 12b6a31`).

## Labels

- **OBSERVED**: a fact directly observed in an execution, log, or measurement. Statements that only repeat a repository document or an Issue body are not OBSERVED.
- **CONFIRMED**: a fact that a reproducible verification met against an explicit expected condition, with the scope stated. This document has no run or test, so no claim here meets this definition.
- **INFERRED**: a conclusion derived from observation or implementation. Direct verification has not been established. Reading code, searching it, and reading design documents give INFERRED. Design expectations of the proposal are INFERRED and are said so in the sentence.
- **UNVERIFIED**: an item for which there is not enough evidence to confirm it holds. Treat as an open item. This includes items that rest only on an Issue body or on a repository document that was not re-checked.

Hard constraints for this document and for the registry: it records verification outcomes, not game data. It contains no BIOS, ROM, disc image bytes, or copyrighted asset content. Titles are named by placeholders (`Title A`) except where an existing repository identifier is quoted.

## 1. Problem and scope

The issue asks for a registry that records, per title, how far verification has reached, for which build and runtime, with what evidence, and with what confidence. It must separate "started" from "played through", separate unverified from successful, and survive Runtime, OS, and GPU changes without silently inheriting an old result.

In scope:

- Per-title data model for five facets: Boot, Gameplay, Save, Audio, Graphics. Each is evaluated separately.
- Evidence versus Confidence.
- Binding of results to Build, Runtime, OS, and GPU.
- Representation of unverified items separately from successes.
- Persistence format and API boundary, with a recommendation.

Out of scope: the community feedback intake UI, the Adaptive Feedback Policy, moderation, and any network service. These are listed as open questions only.

## 2. Current state of the repository

### 2.1 What exists

Title identity:

- `TitleIdentity` holds `Serial`, `TitleName`, `Region`, `Revision`, and derives `CanonicalKey` from them. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/TitleIdentity.cs:10-21`]
- `DiscIdentity` holds `Serial`, `Region`, `Revision`, `DiscIndex`, and an optional `LayoutHint`. It has no disc content hash field. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/DiscIdentity.cs:10-14`]
- `ExecutableIdentity` holds `FileName`, load/entry addresses, `Size`, and `FileHashHex`. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/ExecutableIdentity.cs:10-15`]
- `BootExecutableFingerprint` is SHA-1 over the executable identity. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/BootExecutableFingerprint.cs:13-18`]
- `Region` enum with `Unknown = 0`, `Japan`, `NorthAmerica`, `Europe`, `Asia`, `Korea`, `Australia`. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/Region.cs:10-32`]
- `Revision` is `(Level, BuildYear, BuildMonth)` with `CanonicalKey` `Level.YYYY.MM`. [INFERRED: `src/PSXRecomp.Core/TitleIdentity/Revision.cs:10-37`]
- A SHA-256 hex helper exists for artifacts. [INFERRED: `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactJson.cs:72-75`]
- Disc image SHA-256 is recorded in artifact fixture identity as `DiscImageSha256`. [INFERRED: `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/ArtifactFixtureIdentity.cs:24`] A `discHash` diagnostics key is declared, with no writer of that key found in `src` at `2e3ecad`. Its value and algorithm are UNVERIFIED. [INFERRED for the declaration: `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`] A disc SHA-256 is passed into the firmware boot manifest under a different name: `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs` computes it with `OpenBiosRomInput.Sha256HexOfFile(stagedDisc)` at line 391, then passes the local `discHash` as the `DiscSha256` constructor argument of `FirmwareBootManifest` at line 451. The manifest field is `DiscSha256` (`src/PSXRecomp.Infrastructure/Standalone/FirmwareBootManifest.cs:21`, `:35`) and is verified before each run at `src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:672`. This is a staging artifact, not the `DiagnosticContextKeys.DiscHash` key and not public data. [INFERRED]

Evidence and confidence (Analysis contracts):

- `EvidenceReference` has a content-hash `Id` built with `StableToken.Hash`, plus `Type`, `Source`, `Description`, and optional captured time. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/EvidenceReference.cs:11-17`, `:23-31`]
- `EvidenceType` values: `ManualObservation`, `Screenshot`, `Disassembly`, `Trace`, `RuntimeObservation`, `AnalyzerFinding`, `AIPrediction`, `Reference`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:9-18`]
- `Confidence` is `(Level, Rationale, Score)` with `Score` in `[0, 1]`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/Confidence.cs:11-17`]
- `ConfidenceLevel`: `Unspecified`, `Low`, `Medium`, `High`, `Certain`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/ConfidenceLevel.cs:9-15`]
- `ValidationStatus`: `Unverified`, `PendingHumanReview`, `Accepted`, `Rejected`, `Superseded`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/ValidationStatus.cs:9-15`]
- `AnalysisArtifact` already carries `TitleId`, `RegionCode`, `Status` (default `Unverified`), `Confidence`, `Provenance`, and `EvidenceReferences`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/AnalysisArtifact.cs:27`, `:29`, `:33`, `:35`, `:37`, `:43`]
- `Provenance` has `ToolName`, `ToolVersion`, `AgentId` (required non-empty), and optional `HumanReviewer`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/Provenance.cs:9-19`]
- `UnresolvedItem` exists for open items. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/UnresolvedItem.cs:10`]
- A separate `DiagnosticEvidenceKind` enum classifies diagnostic pointers and is documented as distinct from the Analysis `EvidenceType`. [INFERRED: `src/PSXRecomp.Core/Diagnostics/DiagnosticEvidenceReference.cs:7-10`, `:15-37`]

Execution outcomes:

- `RecompiledArtifactOutcome`: `Success`, `Blocked`, `Failure`. [INFERRED: `src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:13-26`]
- `TitleExecutionState` is the full-fidelity state. `Completed` means "the guest reached a classified, legitimate end", not gameplay completion. Other values: `Returned`, `RuntimeHandoff`, `BudgetExhausted`, `UnsupportedTransfer`, `RuntimeFailure`, `InvalidState`, and `Cancelled` (stopped by a cancellation request before a classified end; "never Completed"). [INFERRED: `src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:15-49`]
- `RecompiledArtifactExitCode.Classify` maps `Cancelled` to `RecompiledArtifactOutcome.Blocked`. [INFERRED: `src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:59`] The registry does not inherit this mapping: `Cancelled` is its own Outcome and is never Pass, Fail, or Blocked (section 3.5).
- `RecompilerExecutionStatus` includes `TimedOut`. [INFERRED: `src/PSXRecomp.Core/Recompiler/RecompilerExecutionStatus.cs:14-29`]

Environment:

- `ExecutionEnvironmentReport` records `OsPlatform`, `OsVersion`, `OsArchitecture`, process architecture, .NET version, runtime identifier, compiler version, and target architecture. Schema string `psxrecomp.execution-environment.v1`. [INFERRED: `src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:11` (record), `:14` (`CurrentSchema`), `:16` (private constructor), `:38-40` (OS properties)]
- No GPU field exists in `src/PSXRecomp.Core/Diagnostics`. A search for `gpu`/`GPU` in that folder returned no environment field. [INFERRED: grep over `src/PSXRecomp.Core/Diagnostics`]
- No `RecompBuildId` or `RuntimeVersion` identifier was found in `src/PSXRecomp.Core`. The only `BuildIdentity` hits are the analysis artifact builder's fixture identity, not a recomp build id. [INFERRED: grep for `BuildId|RuntimeVersion|DiscHash|GameId|ReleaseId` in `src/PSXRecomp.Core` returned `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/DeterministicArtifactBuilder.cs:86,162,165`, `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:20`, `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`]
- A `discHash` diagnostics key is declared (`DiagnosticContextKeys.DiscHash = "discHash"`) and listed in `ExecutionDiagnosticLog`. No writer of this key was found in `src` at `2e3ecad`, and the value and algorithm are UNVERIFIED. The disc SHA-256 that does exist is written to the firmware boot manifest (`src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:391`, `:451`; see §2.1 above), which is a staging artifact. This is a diagnostics key, not a title-registry field. [INFERRED for the declaration and listing: `src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`; `src/PSXRecomp.Core/Diagnostics/ExecutionDiagnosticLog.cs:20`]

Persistence precedent:

- `ProjectMetadataSerializer` is a pure static contract: validate, canonically encode to bytes, decode from bytes, with no host I/O, so it can be reused by GUI, CLI, or MCP. [INFERRED: `src/PSXRecomp.Core/Project/ProjectMetadataSerializer.cs:7-15`, `:37-43`]
- Host I/O is behind `IProjectMetadataStore`. [INFERRED: `src/PSXRecomp.Core/Project/IProjectMetadataStore.cs:18`]
- The file-backed implementation is `FileProjectMetadataStore`. [INFERRED that the file exists: `src/PSXRecomp.Infrastructure/FileProjectMetadataStore.cs`; its members were not reviewed line by line]
- Schema versioning precedent: `AnalysisArtifact.CurrentVersion = 1`, `FunctionDiscoveryArtifact.CurrentSchemaVersion = 1`. [INFERRED: `src/PSXRecomp.Core/Analysis/Contracts/AnalysisArtifact.cs:17`; `src/PSXRecomp.Core/DiscImage/FunctionDiscovery.cs:40`]

Status documents:

- The capability matrix states that a `Verified` row "does **not** imply complete PlayStation title compatibility". [INFERRED: `docs/status/capabilities.md:28`]
- The matrix has a "Not claimed" state and a verification threshold requiring an executable test that asserts an observable contract, not only absence of a crash. [INFERRED: `docs/status/capabilities.md:40`, `:55-60`]
- GPU is `Partial` and Audio (SPU) is `Partial` with audio output `Not claimed`. [INFERRED: `docs/status/capabilities.md:77-78`]
- SIO0 is `Partial`, with real controller integration not claimed. [INFERRED: `docs/status/capabilities.md:79`]
- Memory card storage support is `Verified`, with no qualifier in the row. [INFERRED: `docs/status/capabilities.md:80`]
- The Persona title-screen gate is `Blocked`, with a measured stop at `OUTER_BUDGET_EXHAUSTED`. [INFERRED: `docs/status/capabilities.md:87`]
- The memory-card runtime doc says an unverified compatibility claim must not be added. [INFERRED: `docs/runtime/memory-card.md:279-281`]

### 2.2 What does not exist

- No Compatibility Registry, record type, or compatibility store exists in `src`. [INFERRED: grep for `Compatib|Registry` across `src` returned only unrelated hits, such as the BIOS HLE registry and `SaveLockRegistry`.]
- No ADR covers compatibility. ADRs run `001` to `027`. [INFERRED: `docs/adr/` listing]
- The Studio project has no compatibility view. Its only "evidence" term is `HasFrameEvidence`, meaning a frame capture. [INFERRED: `src/PSXRecompStudio/Services/TitleExecutionService.cs:22`, `:97`, `:133`]

### 2.3 Collision risks found in existing code

1. `ConfidenceLevel.Certain` (`src/PSXRecomp.Core/Analysis/Contracts/ConfidenceLevel.cs:15`) reads like "Perfect". The registry must not reuse it as a title level.
2. `TitleExecutionState.Completed` (`src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:18`) and `RecompiledArtifactOutcome.Success` (`src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:15-16`) both mean a run-level terminal state. Neither means "played to the end". A UI that maps `Success` to "Playable" would be wrong. [INFERRED: this is a naming risk, not a current bug]
3. `Provenance.AgentId` is required (`src/PSXRecomp.Core/Analysis/Contracts/Provenance.cs:15-19`). A community reporter has no agent identity, so `Provenance` cannot describe a user-submitted result without a synthetic id. [INFERRED for the constraint; INFERRED for the impact]
4. `DiscIdentity` has no content hash (`src/PSXRecomp.Core/TitleIdentity/DiscIdentity.cs:10-14`). Disc-level binding therefore cannot be keyed on content today. [INFERRED]

## 3. Data model

Naming: `ReleaseKey` is `(Serial, Region, Revision)`. `DiscKey` is `ReleaseKey + DiscIndex`. These are derived from the existing `CanonicalKey` pattern but exclude `TitleName`, which is free text. [INFERRED]

The registry is a set of **records**. Each record is one `(Title, Facet, Scenario, Binding)` cell. Levels are not stored as the source of truth; they are derived from records (section 5).

### 3.1 Title

```
TitleEntry {
  ReleaseKey          // (Serial, Region, Revision)
  DiscKeys[]          // one per disc; each carries an optional content SHA-256 (new field)
  ExecutableFingerprint? // ExecutableIdentity.FileHashHex or BootExecutableFingerprint, if known
  DisplayName         // presentation only, never a key
}
```

`TitleKey` = `ReleaseKey` in this document, that is `(Serial, Region, Revision)` as defined above. A content hash on the disc is a new field. [INFERRED] Disc hash publication is limited to synthetic-media hashes (section 10 Q3, owner decision c).

### 3.2 Facet

Five facets, each evaluated on its own ladder:

| Facet | Question it answers | Notes |
|---|---|---|
| Boot | Does the title reach its first stable screen through the Runtime path? | Maps to the Persona-style gate in `capabilities.md:87`. |
| Gameplay | Can a scripted input path reach and play a defined level or state? | Requires an input-evidence source (see Q14). |
| Save | Can the title write and read its save through the memory-card model? | Storage format is `Verified` (`capabilities.md:80`); title-level save is separate. |
| Audio | Is audio produced and does playback stay in sync? | SPU audio output is `Not claimed` (`capabilities.md:78`), so this facet is at level 0 by default. |
| Graphics | Does frame output match a defined reference for a scenario? | GPU is `Partial` (`capabilities.md:77`). |

`None` (owner ruling) is a sentinel for a result that is not attributed to any facet, for example a SyntheticFixture result. `Facet.None` never satisfies a level rung and is never shown as a facet column (section 9).

CD-ROM streaming and load-from-disc behaviour are not a sixth facet here. They are scenarios under Gameplay and Save. [INFERRED; see open question Q2]

Facets are never averaged. A title's displayed summary is the minimum across facets, with unverified facets shown explicitly. The Subject filter in section 3.4 applies to every derivation in this document: Evidence Count (4.2), Confidence (4.3), level derivation (5), Known Issue and Regression (6), and display (9). A result enters any of them only if it is `Subject = RealTitle`, has a `TitleKey`, and has a facet other than `None`. [INFERRED; the Subject filter follows the owner ruling]

### 3.3 Scenario

A scenario is a versioned, repository-owned definition, not free text:

```
ScenarioDefinition {
  Id                  // e.g. "boot.first-stable-screen"
  Facet
  Version             // bump when the pass criteria change
  Criteria            // observable assertions (per capabilities.md:57-60)
  RequiredInputs[]    // e.g. "no input", "scripted pad sequence"
}
```

Pass criteria must be observable, for example a frame snapshot match or a guest state marker. "Did not crash" is not a Pass. [INFERRED that the repository requires this for Verified rows: `docs/status/capabilities.md:57-60`] [INFERRED for scenarios]

### 3.4 Result (per scenario per binding)

```
ScenarioResult {
  Subject             // RealTitle | SyntheticFixture (owner ruling)
  TitleKey?           // required when Subject = RealTitle; may be omitted when Subject = SyntheticFixture
  Facet               // one of the five facets, or None (3.2)
  ScenarioId, ScenarioVersion
  Binding             // section 4; required for every Subject, identity and binding rules unchanged
  Outcome             // see 3.5
  EvidenceIds[]       // references into the evidence store
  RecordedAt          // input, not a derived value
}
```

Subject rules (owner ruling):

- `SyntheticFixture` results must never enter a real-title compatibility evaluation. Every derivation takes only `Subject = RealTitle` results, enforced by the input filter, not by convention. A `SyntheticFixture` result is excluded from every derivation even when it carries a `TitleKey`.
- `TitleKey` may be omitted only for `SyntheticFixture`. For `RealTitle` it is mandatory.
- Binding is required for both Subjects. The identity and binding rules in section 4 do not change.
- Fail closed: a result whose `Subject` is missing or unknown is rejected at write time, is excluded from every derivation and from every display, and is reported as invalid. It is never treated as `RealTitle` by default. [INFERRED]

### 3.5 Outcome vocabulary

Outcome is kept separate from Evidence and Confidence. The values are:

| Outcome | Meaning | Counts toward a level |
|---|---|---|
| `Pass` | Evidence exists for this binding and the scenario criteria were met. | Yes |
| `Fail` | Evidence exists and criteria were not met. | No; may open a Known Issue |
| `Blocked` | Run stopped at a known unsupported boundary. Aligns with `RecompiledArtifactOutcome.Blocked` (`src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:18-21`); this is not a direct mapping, see the `Cancelled` handling below. `BudgetExhausted` is not mapped here; it has its own row below. | No |
| `BudgetExhausted` | An independent Outcome, not `Blocked` (owner decision, design level). The run stopped because its execution budget was exhausted before a classified guest end. It is kept distinct from a normal guest exit, a guest stop, and a host timeout. It is not Pass, not Fail, and not evidence of compatibility. The current code maps it to `Blocked` (`src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:54`). That implementation mapping is a separate, pending change, and this registry does not inherit it. | No |
| `Error` | Harness or engine failure, not a guest result. Aligns with `RecompiledArtifactOutcome.Failure` (`src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:23-25`). | No; reported separately, never Pass or Fail |
| `Cancelled` | A run was stopped by a cancellation request before a classified guest end (owner ruling; `TitleExecutionState.Cancelled`, `src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:49`). | No; never Pass or Fail; reported as stopped, not as Blocked |
| `NotRun` | Absence of evidence for this scenario and binding. | No |

`Cancelled` is not `Blocked`, even though `RecompiledArtifactExitCode.Classify` maps it to `Blocked` (`src/PSXRecomp.Core/Execution/RecompiledArtifactExitCode.cs:59`). The registry keeps it distinct.

Derived state, not an Outcome. `Unknown` is a derived title or facet state: the registry holds no record at all for that title or facet, so nothing can be said. The owner ruling requires `Unknown` to stay distinct from `NotRun` and `Cancelled`, which are the Outcome rows in the table above.

`NotRun` is never rendered as `Fail`, and it is never rendered as `Pass`. `Unknown`, `NotRun`, and `Cancelled` are never rendered as each other, and none of them is rendered as `Pass` or `Fail`. A facet with no `Pass` on the current binding is not automatically `Unverified`: its cell label is set by the precedence in section 9.2, so that `Unknown`, `NotRun`, and `Cancelled` stay visible. [INFERRED for the derived state; the non-equivalence rule is owner-ruled]

`Blocked` is not a defect. It is a diagnosable stop, consistent with `docs/status/capabilities.md` "Measured blocker" language. [INFERRED: `docs/status/capabilities.md:49`]

## 4. Evidence, Confidence, and Binding

### 4.1 Evidence

An Evidence record is an immutable observation:

```
EvidenceRecord {
  Id                  // content hash, same scheme as EvidenceReference.Create (src/PSXRecomp.Core/Analysis/Contracts/EvidenceReference.cs:23-31)
  Type                // see 4.1.1
  Source              // bundle or report identifier, never a path to game data
  ProducedBy          // tool name and version; reporter class (see 4.1.2)
  CapturedUnixSeconds
  Binding             // the exact binding it was produced under
  ArtifactHash        // SHA-256 of the captured log/frame/report, not the artifact content itself
}
```

#### 4.1.1 Evidence type

The existing `EvidenceType` (`src/PSXRecomp.Core/Analysis/Contracts/EvidenceType.cs:9-18`) is reused for the observation kind: `Screenshot`, `Trace`, `RuntimeObservation`, `ManualObservation`. `AIPrediction` and `AnalyzerFinding` must never satisfy a registry Pass. [INFERRED] `DiagnosticEvidenceKind` is not reused, to keep the separation its own doc describes (`src/PSXRecomp.Core/Diagnostics/DiagnosticEvidenceReference.cs:7-10`). [INFERRED]

#### 4.1.2 Reporter class

Community results need a reporter class without a required agent identity. Proposed values: `Maintainer`, `Automated` (repository CI or local harness run), `Community` (user-submitted, unverified by maintainers). [INFERRED]

### 4.2 Evidence count

Evidence Count is derived, never typed in. One unit only: it is the number of **distinct `EvidenceIds[]` entries** (content hashes) referenced by `Subject = RealTitle` `ScenarioResult` records with `Outcome = Pass`, grouped by `(TitleKey, Facet, ScenarioId, ScenarioVersion, Binding)`. The count is never a count of result records. A result with several ids contributes each distinct id once, and two results that reference the same id contribute it once. Results with a different `ScenarioVersion` are a different group and are not summed. `EvidenceRecord` has no `Outcome` field; the Outcome lives on `ScenarioResult` (section 3.4). `SyntheticFixture` results are never counted. [INFERRED]

### 4.3 Confidence

Confidence is a separate axis: how much the registry trusts the **derived** state. It is not a rating of the game.

```
DerivedConfidence {
  Level               // reuse ConfidenceLevel values except Certain
  Rationale           // generated text listing the evidence basis
  Score?              // omitted; ordinal levels only
}
```

- Use `Confidence` (`src/PSXRecomp.Core/Analysis/Contracts/Confidence.cs:11-17`) as the carrier, with `Rationale` mandatory.
- Do not use `ConfidenceLevel.Certain`. Permitted values for the registry are `Unspecified`, `Low`, `Medium`, `High`. [INFERRED]
- Do not use the numeric `Score`. A numeric score invites false precision and a "Perfect = 1.0" reading. [INFERRED]
- Confidence is computed from the `Subject = RealTitle` `ScenarioResult` records only: number of distinct Pass evidence ids (4.2), number of distinct reporter classes, recency relative to the binding's last change, and whether any Fail or Blocked result exists for the same binding. A `Cancelled` result is not a Pass or a Fail and does not count toward the Pass total; it is reported as incomplete evidence (Q15). `SyntheticFixture` results are never inputs. [INFERRED]

Evidence and Confidence are different things. A single maintainer-recorded Pass with a full trace can have higher Confidence than ten Community Passes on mismatched bindings.

### 4.4 Binding

A binding is the environment a result is valid for:

```
Binding {
  BuildId             // new: recomp build identifier (no equivalent exists, section 2.1)
  RuntimeVersion      // new: Runtime version (no equivalent exists)
  Os                  // ExecutionEnvironmentReport.OsPlatform, OsVersion, OsArchitecture (src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:38-40). OsVersion is recorded only as observed (RuntimeInformation.OSDescription, src/PSXRecomp.Infrastructure/Diagnostics/ExecutionEnvironmentCollector.cs:23); if it cannot be obtained the value is `unknown`, never a guessed value (owner policy 1). Current code rejects an empty value (it throws at `ExecutionEnvironmentReport.cs:28`). Writing `unknown` in place of an empty value is a public-contract change to `psxrecomp.execution-environment.v1`, which requires a separate compatibility review. That change is pending the review and is not approved as an implementation; this document does not change the code.
  Gpu                 // new: backend name and driver version (absent from ExecutionEnvironmentReport)
  DotNet, RuntimeIdentifier   // from ExecutionEnvironmentReport
}
```

Binding equality is exact by default. Two bindings are the same only if all of `BuildId`, `RuntimeVersion`, `Os`, `Gpu` match. [INFERRED]

Carry-forward is never implicit, and this design defines no carry-forward state. When a new `BuildId` or `RuntimeVersion` appears, the previous Pass is retained as history only, and the current binding starts at `NotRun` for that scenario. (An earlier draft named an `Inherited` state; it had no definition and is removed.) [INFERRED]

Why this matters: the issue requires that a "Perfect" title still detects regressions after Runtime, OS, or GPU change. A binding-scoped design gives that detection; an implicit carry-forward would hide it. [UNVERIFIED, Issue body only: issue #33 "Perfect後もRuntime/OS/GPU等の変更によるRegressionを検出可能にする"]

## 5. Level derivation

Levels are derived per facet and then per title. They are not written by hand.

Proposed per-facet ladder (names are placeholders for owner decision, section 10 Q4):

```
Level 0      -> no Pass for the binding (the cell label comes from section 9.2, not from this level)
Boot-Verified -> all required Boot scenarios Pass for the binding
Playable     -> Boot-Verified and all required Gameplay scenarios Pass
Stable       -> Playable and no open Known Issue of severity >= major on the binding, where the Known Issue is eligible under section 6.1
Highly-Verified -> Stable and Pass evidence from >= 2 reporter classes
```

A title-level label is the **minimum** over facets in the chosen binding. A title with Boot-Verified and Save at level 0 is shown as `Boot-Verified` with the Save cell labelled per section 9.2, never as "Playable". [INFERRED]

"Perfect" is not a rung. It is defined as: every required scenario for every facet is `Pass` on the stated binding, with at least the Highly-Verified evidence threshold, and no open Known Issue. The label means "passes the currently defined scenario set", and it is versioned by the scenario catalog version. This answers the issue's point that Perfect is not absolute completeness. [INFERRED; the issue leaves names open]

Scenario catalog changes re-open Perfect for affected titles, because the catalog version is part of the derived key. [INFERRED]

## 6. Known Issues and Regression

### 6.1 Known Issue

```
KnownIssue {
  Id
  Subject                  // RealTitle | SyntheticFixture (section 3.4)
  TitleKey                 // required for RealTitle; a SyntheticFixture KnownIssue has no TitleKey
  Facet, ScenarioId?       // optional scope
  Severity                 // blocker / major / minor (proposed)
  Status                   // open / workaround / fixed
  FirstSeenBinding
  FixedInBinding?
  EvidenceIds[]            // only ids of Subject = RealTitle ScenarioResults (section 3.4)
}
```

Eligibility (owner ruling, applied as the section 3.4 Subject filter): only `Subject = RealTitle` Known Issues with a matching `TitleKey` can cap a level or appear in a real-title view. A Known Issue whose `EvidenceIds[]` reference no `RealTitle` result, or any `SyntheticFixture` result, is not eligible. A `SyntheticFixture` Known Issue never caps a real-title level, even when it carries a `TitleKey`. Missing or unknown `Subject` is rejected (fail closed). [INFERRED]

Open issues cap the level they affect (section 5), but only when eligible under the rule above. `UnresolvedItem` (`src/PSXRecomp.Core/Analysis/Contracts/UnresolvedItem.cs:10`) is an analysis-side open-item type. It is not reused, because a Known Issue has a binding and a severity. [INFERRED]

### 6.2 Regression

A Regression is derived, not stored by hand. Its input is `Subject = RealTitle` `ScenarioResult` records only; `SyntheticFixture` results never create or close a Regression (section 3.4):

- A scenario has `Pass` on binding A, and `Fail` or `Blocked` on a later binding B for the same title, facet, and scenario.
- Or a scenario has `Pass` on binding A and no result on B while the title's binding changed. This is reported as `Unverified after change`, not as a regression. It is the cell label in section 9.2, row 2, and not a level.

A `Cancelled` result on binding B is neither a Fail nor a Pass, so it does not create a Regression by itself. How it is shown and whether it changes the Regression state is open (Q16). [INFERRED]

The derived record names both bindings and both evidence ids. [INFERRED]

## 7. Persistence

### 7.1 Options

**Option A. Repository-tracked JSON, one file per title release.**
- Pros: reviewable through PRs; reproducible in CI; matches how the capability matrix is maintained.
- Cons: every community result would enter public Git history, which requires a curation path; disc hashes and reporter data become public; high PR churn.

**Option B. Per-user local store in the Studio data directory, plus export bundles.**
- Pros: private by default; no network dependency; no curation burden.
- Cons: no shared state; a user sees only their own evidence.

**Option C. Central HTTP service.**
- Pros: community aggregation.
- Cons: needs authentication, moderation, and a hosting decision; out of scope for #33 and requires a separate ADR. [INFERRED]

**Option D. Hybrid: local append-only evidence log, plus a curated repository snapshot that is imported only through an explicit maintainer step.**
- Pros: private evidence stays local; the public snapshot is small, reviewable, and derived; no service needed for v1.
- Cons: two stores to keep consistent; an import step to design.

### 7.2 Recommendation

Option D, staged:

1. v1: local append-only evidence log (JSON Lines, one record per line, each record content-hashed) and a derived read model. Studio shows it read-only.
2. v1.1: a curated snapshot file in the repository (`docs/status/` or a `config/` path, owner decision), generated from maintainer-accepted evidence only. Diff-friendly, sorted, canonical JSON.
3. Central service: deferred. Not part of #33.

Format rules, based on existing precedent:

- Schema identity string `psxrecomp.compatibility-registry.v1`, following `ExecutionEnvironmentReport.CurrentSchema` (`src/PSXRecomp.Core/Diagnostics/ExecutionEnvironmentReport.cs:14`). [INFERRED]
- Canonical UTF-8 JSON through the same encoder as project metadata (`src/PSXRecomp.Core/Project/ProjectMetadataSerializer.cs:37-43`), so identical input yields identical bytes. [INFERRED]
- Store raw records. Store derived levels only in the snapshot, and only with the scenario catalog version and a recomputation check in tests. [INFERRED]
- Never store game data, ROM/BIOS bytes, or paths into a user's game library. The `Source` field is a bundle id. The repository's legal boundary against embedding such data is stated in its project docs [UNVERIFIED: README and `docs/project-mission.md`, not re-read for this design]. The field rule itself is [INFERRED].

### 7.3 Local storage constraints

Local writes must be append-only and tolerate a partial last line, because a crashed run must not corrupt earlier evidence. [INFERRED] Existing store boundary: `IProjectMetadataStore` (`src/PSXRecomp.Core/Project/IProjectMetadataStore.cs:18`) shows the intended split. [INFERRED]

## 8. API boundary

Recommended split, mirroring `ProjectMetadataSerializer`, `IProjectMetadataStore`, and its file-backed implementation `FileProjectMetadataStore` (section 2.1):

1. **Core, pure**: record types, outcome and level derivation, scenario catalog, canonical encode/decode. No file or network I/O. [INFERRED]
2. **Core, interface**: `ICompatibilityStore` for append, read, and snapshot. [INFERRED]
3. **Infrastructure**: file-backed implementation of `ICompatibilityStore`. [INFERRED]
4. **Studio**: read-only view model over the derived read model. No write path in v1. [INFERRED]
5. **CLI** (optional, later): `compat` subcommands for import and export. Not required for #33. [INFERRED]

The derivation function must take records and a binding and return the level set. It must be deterministic and free of clocks. Clocks enter only as recorded inputs. [INFERRED]

Derivation input rule (the Subject filter, section 3.4): every derivation in sections 4 to 6 and 9 receives all of its records through one filter. The filter keeps only `ScenarioResult` and `KnownIssue` records with `Subject = RealTitle`, a matching `TitleKey`, and a facet other than `None`. It rejects records with a missing or unknown `Subject` and reports them as invalid. A `SyntheticFixture` record never reaches a derivation, even when it carries a `TitleKey`. The filter is a single shared step, not a rule each derivation re-implements. [INFERRED]

## 9. Studio presentation

Per title: one row per binding in use, five facet columns, each cell showing its cell label from section 9.2 (the derived level, or one of the labels in rows 1 to 6). The binding selector is required, so no cell is shown without its binding. Known Issues and Regression badges sit next to the cell they affect. Unverified is styled as absence, not as a failure colour. [INFERRED]

SyntheticFixture results are never shown in a real-title view. They may appear only in a separate, labelled fixture view. `Facet.None` is never a column. [INFERRED; follows the owner ruling]

### 9.1 Required #33 changes (owner ruling)

The owner approved these changes to the #33 design. Each one is recorded in the sections cited:

1. Add a `Subject` field to every result (`RealTitle` or `SyntheticFixture`). See section 3.4.
2. When `Subject = SyntheticFixture`, `TitleKey` may be omitted. Binding stays required. See section 3.4.
3. Add `Facet.None`. See section 3.2.
4. Add a `Cancelled` row to the registry Outcome vocabulary. The execution state already exists: `TitleExecutionState.Cancelled` (`src/PSXRecomp.Core/Execution/TitleExecutionContract.cs:49`), set by `src/PSXRecomp.Core/Execution/ExecutionOrchestrator.cs:75`. See sections 2.1 and 3.5.

Additional conditions from the same ruling:

- SyntheticFixture results must never enter a real-title compatibility evaluation (section 3.4 Subject rules; section 8 derivation input rule).
- `Cancelled` must never count as Pass or Fail (section 3.5).
- `Unknown`, `NotRun`, and `Cancelled` must remain distinct (section 3.5).
- The identity and binding rules of the compatibility registry are kept unchanged (section 4).

### 9.2 Cell labels (Unknown, NotRun, Cancelled kept distinct)

Each cell is one `(title, facet, binding)`. It is labelled by the first matching rule, using `Subject = RealTitle` results only:

| Order | Condition | Label | Meaning |
|---|---|---|---|
| 1 | No record at all for the title or facet | `Unknown` | Nothing can be said. |
| 2 | No result on this binding, but a scenario in this facet has a `Pass` on another binding | `Unverified after change` | The binding changed after a Pass. The old Pass is not carried forward (section 4.4), so this cell has no Pass here. It is a label, not a level. |
| 3 | Records exist for the facet, but none for this binding | `Not run` | The binding has not been tested for this facet. |
| 4 | Any `Fail` result on this binding | `Fail` | Evidence exists and criteria were not met. |
| 5 | Any `Blocked` or `Error` result on this binding, and no `Pass` | `Blocked` or `Error` | Stopped at a known boundary, or a harness or engine failure. |
| 6 | Any `Cancelled` result on this binding, and no `Pass` | `Cancelled` (label wording pending Q15) | The run was stopped and its evidence is incomplete. |
| 7 | At least one `Pass` on this binding | the derived level from section 5 | Level 0 when no ladder rung is met. |

Row 4 is the only row without a "no Pass" condition, because a failed criterion on this binding must not be hidden behind a Pass of another scenario in the same cell. Blocked, Error, and Cancelled are not failures, so they show only when the cell has no Pass.

`Unverified` is not a cell label on its own. The only cell label that contains it is `Unverified after change` (row 2), and that label needs a prior Pass on another binding. Level 0 in section 5 is also named `Unverified`, but it is a level, and the cell label comes from this table. A cell is never shown as `Unverified` or `Unverified after change` when its real state is `Unknown`, `Not run`, or `Cancelled`. [INFERRED; follows the owner ruling]

## 10. Open questions

- **Q1. Facet list.** The issue lists Gameplay, Save, Load, CD, Audio, Graphics. This draft folds Load into Save and CD into Gameplay. Owner to confirm or split.
- **Q2. CD-ROM and streaming.** Is CD streaming a facet, a scenario, or a Runtime-level property recorded in the binding? This draft uses scenarios.
- **Q3. Disc hash in public data.** Is a SHA-256 of a disc image acceptable in a public snapshot? `DiscIdentity` has no hash field (`src/PSXRecomp.Core/TitleIdentity/DiscIdentity.cs:10-14`). `DiagnosticContextKeys.DiscHash` declares a `discHash` key (`src/PSXRecomp.Core/Diagnostics/DiagnosticContextEntry.cs:47`), but no writer of that key was found in `src` at `2e3ecad`. The `discHash` value is UNVERIFIED, and the `DiscHash` writer's algorithm is UNVERIFIED (no writer was found). The requirement is SHA-256 (owner policy 3). A disc SHA-256 is passed into the firmware boot manifest (`src/PSXRecomp.Infrastructure/Standalone/FirmwareDistribution.cs:391`, `:451`). That is a staging artifact, not the `DiagnosticContextKeys.DiscHash` key and not public data. Owner decision (c): disc hashes, including staging hashes, are not published except for publicly distributable synthetic media. Commercial disc and commercial BIOS/ROM hashes are never published. Owner policy 3 applies: a disc hash must state its input kind (whole disc image file bytes), algorithm (SHA-256), and normalization (none); publication of a commercial-media hash is never allowed; raw disc or BIOS/ROM bytes are never published; and a hash never proves provenance or legitimacy. Under owner decision (c), a `SyntheticFixture` result may have no `TitleKey` and never enters real-title evaluation (section 3.4), so the Q3 decision applies only to `RealTitle` records.
- **Q4. Level names and thresholds.** Section 5 names are placeholders. The number of reporter classes for Highly-Verified (2 here) needs owner ruling.
- **Q5. Binding compatibility.** Is exact binding equality the right default, or is a declared compatible range (for example, same `RuntimeVersion` minor) allowed? This draft uses exact match.
- **Q6. Build identity.** No `BuildId` exists (section 2.1). Which artifact defines it? Candidates are the generated-host build output or the analysis manifest hash (`src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/AnalysisManifestDocument.cs:42`). UNVERIFIED which is stable across rebuilds.
- **Q7. Runtime version.** No `RuntimeVersion` identifier was found in `src`. Is the Runtime version the assembly version, a git commit, or a new constant? UNVERIFIED.
- **Q8. GPU identity.** Environment reporting has no GPU field. Is GPU backend and driver recorded in the environment report, or only in the Studio? Changing `ExecutionEnvironmentReport` is a schema change (`psxrecomp.execution-environment.v1`) and needs care.
- **Q9. Reporter identity.** Community results need a reporter class (4.1.2) without an agent identity. Whether to collect any personal identity is a privacy decision. This draft collects none.
- **Q10. Feedback intake.** In the repository, #23 is the review-finding feedback process: it converts recurring review findings into durable prevention [INFERRED: `docs/development/quality-stack.md:162`, `:192`]. Reading #23 as the community feedback intake for this registry is UNVERIFIED. The registry only defines the record, and the intake path needs an owner ruling.
- **Q11. ADR and SSOT.** The issue completion list asks to record the decision in SSOT/ADR. This document is not an ADR. A new ADR (next free number `028`) should be written after owner review, via the ADR skill. Not done here.
- **Q12. Naming collision.** Should the existing `TitleExecutionRun.HasFrameEvidence` (`src/PSXRecompStudio/Services/TitleExecutionService.cs:22`) be renamed to avoid confusion with registry Evidence? This draft does not propose a rename.
- **Q13. Scenario ownership.** Who owns the scenario catalog and its version bump process?
- **Q14. Input evidence for Gameplay.** Which source records the scripted input and its outcome for a Gameplay scenario: a harness-level input log, a manual observation, or a new recorder? Not decided. This is separate from Q10, which concerns feedback intake.
- **Q15. Cancelled display label.** The label for a cell whose only non-Pass result is `Cancelled` (section 9.2, row 6) is pending the owner's decision. Until then the draft uses the placeholder `Cancelled`.
- **Q16. Cancelled on binding B for regression.** If binding B holds a `Cancelled` result for a scenario that had Pass on binding A, is that a regression, `Unverified after change`, or neither? This draft says neither, and only shows it as Cancelled (section 6.2). Owner to decide.

## 11. Verification of this document

- Design only. No build or test was run. [INFERRED by this change: docs only]
- No ROM, BIOS, or disc image was read or referenced by content. [INFERRED by process]
- Every code citation in sections 2 to 4 was read in the worktree or confirmed by a search whose result is stated. Quoted line numbers are at `2e3ecad`; the quoted source files are identical at `2e3ecad` and `12b6a31` ([INFERRED: recorded in the previous review; not re-run in this work]: the intersection of cited paths and changed files is empty). Section 7 and later rely on INFERRED labels for proposals.

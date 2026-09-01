---
name: real-rom-analysis
description: >
  Repeatable verification flow for a user-provided PS1 disc image: detect the
  fixture, run the staged CHD → ISO 9660 → SYSTEM.CNF → PS-X EXE → entry point →
  MIPS decode → basic block analysis, record stage-level progress, classify the
  run as PASS or FAIL with the failing stage and reason, produce a separated
  detailed log and summary report, and route findings to follow-up Issues —
  without any disc image, executable or ROM payload reaching Git.
version: 1.0.0
scope: project
platform: agent-agnostic
related-issues: "#212, #213, #215, #210"
---

# Real-ROM Analysis Skill

Applies the analysis path established in Issue #212 to **any** supported title,
as a reusable flow rather than a one-off investigation. Nothing here is
title-specific: fixtures are discovered from `rom/`, and every value in the
result is derived from the disc under analysis.

The specification — stage table, failure kinds, artifact layout, CI and ROM
policy — is [`docs/development/real-rom-analysis.md`](../../../../docs/development/real-rom-analysis.md).
This file is the procedure.

## When to apply

- A new disc image has been placed under `rom/` and needs to be validated against
  the current analysis and CPU foundation.
- A change to the decoder, ISO/CHD reader, or basic-block builder should be
  re-validated against real data.
- A real-data defect must be reproduced, classified and turned into an Issue.

Do **not** use this skill to add recompiler, host codegen, full function
discovery or game-execution behaviour; those are separate Issues (#210 and the
recompiler line).

## Preconditions

- The disc image is legally owned by the operator and already present locally.
- It is placed under `rom/` in one of the supported layouts:

  ```text
  rom/<fixture>.chd              → fixture "<fixture>"
  rom/<fixture>/<anything>.chd   → fixture "<fixture>"
  ```

  `.chd` and `.iso` are recognized. Never copy an image anywhere else in the
  repository, and never rename one to evade the artifact gate.
- `git status` is clean of any disc image before starting.

## Procedure

### 1. Detect the input

Run fixture discovery and confirm the flow sees exactly the fixtures you expect.
An empty result is a legitimate SKIP condition, not a failure.

```powershell
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj `
  --filter "FullyQualifiedName~RealRomAnalysisSkillTests"
```

With no fixture the run reports `[SKIP]` with the reason; with fixtures it
analyzes each one and requires every run to reach `COMPLETE`.

### 2. Run the flow

`RealRomAnalysisFlow.RunAll(romDirectory, reportDirectory, logDirectory)` executes,
for every discovered fixture:

```text
START → INPUT → CHD_OPEN → FILESYSTEM → SYSTEM_CNF → BOOT_EXECUTABLE → PSX_EXE
      → EXE_HEADER → ENTRY_POINT → TEXT_REGION → MIPS_DECODE → BASIC_BLOCK
      → REPORT → MANIFEST → COMPLETE
```

Each fixture is analyzed and persisted independently, so one failing title never
hides the result of another.

### 3. Read the progress and the verdict

Every run — passing or failing — writes:

```text
reports/real-rom/<fixture>/run-summary.json   verdict, stage table, counts
logs/real-rom/<fixture>/analysis.log.jsonl    per-stage detail
reports/real-rom/<fixture>/report.json        full analysis report (PASS only)
```

Read `run-summary.json` first: `Status`, `LastSuccessfulStage`, `FailedStage`,
`FailureKind`, `FailureReason`. Consult the log only when the summary is not
enough. Never paste raw ROM-derived content into a report, an Issue or a PR.

### 4. Classify the outcome

| Verdict | Meaning | Next step |
|---|---|---|
| `PASS` | Reached `COMPLETE` | Record the counts; compare against the previous run for that fixture |
| `FAIL` | Stopped at `FailedStage` | Classify below |
| `SKIP` | No fixture present | Nothing to report; CI behaves the same way |

For a `FAIL`, decide which of the two kinds it is — this decision is the point of
the skill:

- **General defect** — the flow would fail the same way for other titles
  (a decoder gap, an ISO/CHD reader limitation, a wrong header assumption).
  This is a repository defect: open a follow-up Issue.
- **Title-specific characteristic** — the disc legitimately differs (unusual boot
  path, multi-track layout, a region the current flow does not claim to support).
  Record it as a supported-input limitation, not as a decoder bug.

Do not weaken an assertion, widen a tolerance or special-case a title to turn a
`FAIL` into a `PASS`. A failing stage is information; suppressing it destroys the
only signal this flow exists to produce.

### 5. Route findings to follow-up Issues

When a general defect is found, open an Issue containing **only** safe metadata:
fixture alias, disc SHA-256, executable name and SHA-256, entry point, text
start/size, failing stage, failure kind, failure reason, and the relevant counts.
Link it to #213, and to #210 when it concerns function discovery or CFG depth.

Never include: disc/executable bytes, sector dumps, long instruction listings,
absolute local paths, credentials, or agent/session URLs.

## Extending the flow

- **A new stage** — add it to `RomAnalysisStage` in its ordered position, record
  it in `RomAnalysisPipeline`, and give it a failure kind. The recorder enforces
  strict ordering, so a misplaced stage fails its tests immediately.
- **A new failure kind** — classify it in the pipeline and add it to the table in
  the specification document; add a test that reaches it with a synthetic image.
- **A new container format** — add the extension to `RomFixtureLocator`, add an
  entry stage path to the pipeline, and extend `config/artifact-policy.json` so
  the contamination gate rejects the new format too.

Cover every new stage failure with a `SyntheticIsoImageBuilder` test so the
behaviour is verified in CI without a disc image.

## Definition of Done

- [ ] Every discovered fixture produced a verdict; none was silently ignored.
- [ ] Each `FAIL` names its stage, failure kind and reason, and is classified as
      a general defect or a title-specific characteristic.
- [ ] General defects are filed as follow-up Issues with safe metadata only.
- [ ] `git status` shows no disc image, executable, `reports/` or `logs/` entry.
- [ ] `pwsh ./scripts/ci/check-artifact-policy.ps1` passes.
- [ ] The existing test suites still pass, and a fixture-less environment skips
      explicitly rather than failing.

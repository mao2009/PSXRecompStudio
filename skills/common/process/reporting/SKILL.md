---
name: reporting
description: >
  Standard completion-reporting process for AI agents: produce the final report
  of an Issue / work unit from collected, real evidence covering the
  investigation and SSOT check, implementation summary, design decisions,
  changed files, targeted/related/full tests, build/analyzer/lint
  applicability, PASS/FAIL/NOT RUN semantics, existing-vs-new failure
  separation, git status/diff/commit evidence, and remaining work and
  Issue/PR state. Use when writing the completion report of any
  implementation, investigation or review task.
version: 0.1.0
scope: process
platform: agent-agnostic
related-issues: "#88"
---

# Completion Reporting

A process skill that standardizes how an AI agent reports a completed unit of
work (an Issue, or an explicit request). Its purpose is to make every agent
report the same items at the same granularity, backed by evidence collected
from the work actually done, so that "implemented" is always accompanied by
what was investigated, what was changed, what was verified, and what was
**not** verified.

This skill is project-independent; project-specific facts (authoritative
documents, verification commands, conventions) are loaded from the **Project
Profile** (see [Porting](#porting-to-another-project)).

## When to apply

Apply at the end of any task that produces a report: an implementation, a
research / investigation, or a review conclusion — wherever the report is
delivered (final task message, PR body, or shared run summary). This skill
consumes executed work and collected evidence; it does not prescribe how to
carry out the work (that is the task skills' responsibility).

## Reporting rules

These rules are mandatory. They turn the Common Skill's ground-truth
disciplines into concrete written-report obligations:

1. **Do not report unperformed verification as performed.** A test, build,
   analyzer, lint, or check that was not run is reported `NOT RUN` — never
   `PASS`. A command that was run and failed is reported as failed, never as
   green.
2. **Every conclusion carries evidence.** Conclusions such as "no problems"
   or "no impact" are backed by the check that was actually run, or are
   explicitly marked as unverified.
3. **Mark every verification item with a real status.** Each item in the
   verification section carries `PASS` / `FAIL` / `NOT RUN` (and `UNKNOWN`
   where the environment made the result non-deterministic). No item is left
   with an implicit status implied by prose.
4. **Separate existing failures from new failures.** A failure present on the
   base before the change is *existing*; a failure caused by the change is
   *new*. Existing failures are reported as pre-existing with their evidence;
   new failures are blockers that must be surfaced, not absorbed into the
   "existing" bucket.
5. **Report only what the git diff contains.** Claims of implemented behavior
   map to files / hunks that actually appear in `git status` and `git diff`. A
   change that is not in the diff is not reported as implemented; an edit that
   is in the diff is not omitted from the report.
6. **Do not silently add out-of-scope items.** Items established as out of
   scope are recorded as out of scope; they are never silently implemented
   mid-task and then reported as if requested.
7. **No reporting-only code changes.** Code is not modified solely to make the
   report look better.
8. **Do not fill unverifiable items by guessing.** An item that cannot be
   verified is marked `NOT RUN` / `UNKNOWN` with the reason it could not be
   verified, never supplied by inference presented as fact.

## Standard procedure

A report is produced from evidence, not from memory. Walk through in order:

```text
1. Collect the driving task and its acceptance criteria / non-goals
2. Collect the investigation and SSOT results (documents read, conclusions)
3. Collect the implementation facts (files changed, diff, design decisions)
4. Run / re-check the applicable verification gates and record real statuses
5. Check git status / diff / commit for the final state
6. Assemble the standard report (below) from the collected evidence
7. Re-read the assembled report against the reporting rules
```

For a task whose work was already executed (e.g. this skill applied at report
time), steps 4–5 mean *re-verifying* the recorded results against the current
repository state, not re-running every gate from scratch. Anything that cannot
be re-verified is marked accordingly, never asserted.

## Standard report contents

Use the following section headings in the final report. A section with nothing
to report is stated as such ("none" / "not applicable"), never removed in a
way that hides a gap.

### 1. Investigation and SSOT

- The driving task (Issue number, or explicit request) and its concrete
  requirement(s) / acceptance criteria / explicit non-goals.
- The authoritative documents actually read (architecture SSOT pages, ADRs,
  subsystem specs named by the Project Profile) and the outcome of the SSOT
  consistency check: conforms, or finding with reason.
- Related code / dependencies / data flow inspected, and existing design
  patterns reused rather than duplicated.
- Related tests identified for the touched area.
- Specification contradictions or unresolved questions, if any.

### 2. Implementation summary

- What was implemented, mapped to each acceptance criterion.
- The current repository state the work landed on: branch, base, HEAD.
- Fact vs. inference vs. proposal separation for any point that could be
  mistaken for an observed result but was not.

### 3. Design decisions

- The approach chosen and why it fits the SSOT/ADRs.
- Alternatives considered and rejected, if meaningful.
- Whether a significant design decision is being introduced that warrants an
  ADR (per the self-review skill's ADR feedback conditions) — and, if so, the
  decision recorded.

### 4. Changed files

- The file list (`git status`), the diff summary (`git diff --stat`), and the
  intentional scope of each change.
- Confirmation that no stray artifacts / unrelated edits ride along.

### 5. Verification matrix

For each gate applicable to the change, report the actual result:

| Item | Status | Evidence |
|---|---|---|
| Targeted tests | PASS / FAIL / NOT RUN | filter / command actually run |
| Related tests | PASS / FAIL / NOT RUN | command actually run |
| Full test suite | PASS / FAIL / NOT RUN | command actually run |
| Build | PASS / FAIL / NOT RUN | command actually run |
| Analyzer / lint / format | PASS / FAIL / NOT RUN | command actually run |

Gates that are **not applicable** to the change (for example the test/build
surface for a documentation-only or process-only change) are recorded as
`NOT APPLICABLE` with a one-line reason — never implied green, never listed as
a silent absence.

### 6. Real PASS / FAIL / NOT RUN semantics

- `PASS` — the gate was actually run and passed, with the evidence cited.
- `FAIL` — the gate was actually run and failed, with the failure output
  summarized and the item carried into the existing-vs-new section below.
- `NOT RUN` — the gate was not executed; the reason is given. It is never
  upgraded to `PASS` by assumption.
- `UNKNOWN` — result cannot be determined in this environment (e.g. native
  tests behaving differently per OS where the Project Profile names CI as
  authoritative); state the ambiguity and where it will be resolved.

### 7. Existing-vs-new failure separation

When any gate `FAIL`s:

- List the failing item with its evidence.
- Classify each failure as **existing** (reproduced on the base before the
  change) or **new** (caused by this change), and state how that was
  determined (e.g. same failure on the base commit, or a green base compared
  with a failing change).
- Never report an existing failure as collateral damage of this change, and
  never hide a new failure inside the "existing" bucket.

### 8. Git / diff / commit evidence

- `git status` before and after the work (intentional change set only).
- The change actually present: `git diff` and `git diff --stat`, and
  confirmation that reported implementation matches the diff.
- Commit hash and commit message(s) for the change.

### 9. Remaining work and Issue / PR state

- Out-of-scope items confirmed during the task, with the reason they were
  excluded.
- Unresolved / deferred items and, where applicable, the follow-up Issue /
  PR that tracks them.
- The Issue / PR state: approved for close, or intentionally left open, with
  the reason (a reason is mandatory when the Issue is left open).

## Relationship to other skills

| Concern | Owned by |
|---|---|
| Universal rules for all tasks (ground truth, no fabricated results) | `common/task/common/SKILL.md` |
| How the work itself is carried out | `common/task/*/SKILL.md` (research / implementation / review / issue) |
| Which documents became stale and why | `common/process/doc-sync/SKILL.md` |
| What must hold before opening a PR | `common/process/self-review/SKILL.md` |
| The final written report of a work unit | **this skill** |

This skill does not replace the self-review or doc-sync gates: those decide
whether a PR may be opened and which documents change, while this skill shapes
the report that an agent writes. The report it produces is the vehicle that
carries the Common Skill's reporting duties stated in
`common/task/common/SKILL.md` (observed repository state, real verification
results, fact/inference/proposal distinction, Definition of Done evidence).

## Porting to another project

Copy this skill unchanged; supply a Project Profile that names the
authoritative documents (SSOT), the verification command ladder
(targeted → related → full → build/analyzer → E2E), known environment
caveats, and Issue/PR conventions (closing-keyword policy), per the host
`skills/` conventions. The report-format sections above are project-agnostic
and need no profile input.

## Non-goals

- Prescribing how a task is executed (that is the task skills' job).
- Replacing the pre-PR self-review or documentation-sync gates.
- Guaranteeing verification coverage; this skill only forbids mislabeling it.
- Encoding any single project's domain specifics — verification commands,
  document paths, and environment caveats belong in the Project Profile, not
  here.
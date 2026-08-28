---
name: implementation
description: >
  Standard procedure for an implementation task: confirm the driving task,
  verify
  repository state, survey related implementation, check the Architecture SSOT,
  pin the implementation approach, implement, test, build/analyzer/E2E, review
  the diff, and confirm the Definition of Done before reporting completion.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#174"
---

# Implementation Skill

A task template for **changing code (or close artifacts)** to satisfy a driving
task. It enforces the discipline that prevents the most common failure modes of
AI-driven implementation: implementing without confirming the requirement,
building on guessed repository state, skipping verification, and reporting
completion without meeting the Definition of Done.

This skill is project-agnostic; project-specific inputs (authoritative
documents, architecture MATRIX, verification commands) come from the **Project
Profile**. The [Common Skill](../common/SKILL.md) rules apply throughout.

## When to apply

Apply this skill when the task is an implementation of a concrete Issue /
requirement and the deliverable is a change to the repository. If the task is
investigation-only, use the [Research Skill](../research/SKILL.md); if it is a
review, use the [Review Skill](../review/SKILL.md).

## Preconditions

- A driving task (an Issue, or an explicit request) with concrete requirements
  and acceptance criteria / Definition of Done. When the task is an explicit
  request with no Issue, the request itself supplies the requirements and
  acceptance criteria (see the Common Skill) — do not invent Issue data.
- A working branch (directly or via a worktree created by the Batch
  orchestrator), based on the current `main`.
- [Common Skill](../common/SKILL.md) accepted.

## Standard Procedure

Execute the ten standard steps below in order. Do not skip to implementation
before the earlier steps are grounded; each step gates the next.

```text
1. Task / Issue confirmation
2. Repository state confirmation
3. Related implementation survey
4. Architecture SSOT check
5. Implementation approach confirmation
6. Implementation
7. Tests
8. Build / Analyzer / E2E
9. git diff / status review
10. Definition of Done confirmation
```

### 1. Task / Issue confirmation

Read the driving task (the Issue, or the explicit request when no Issue backs
the work) and restate:

- The concrete requirement(s).
- The acceptance criteria.
- Explicit non-goals / out-of-scope.

If the driving task has no GitHub Issue, regard the request as the source of
requirements / acceptance criteria; never invent an Issue for it. If anything is
ambiguous or the task's intent is unclear, list it and resolve before
implementing (do not silently pick a convenient reading, Common rule 2 and 4).

### 2. Repository state confirmation

Inspect the actual state, never assume it:

- Current branch and HEAD.
- `git status` — clean or dirty? Any pre-existing uncommitted changes?
- Whether the branch is based on the current `main`.

Record this before changing anything. If the branch is not on current `main`,
rebase/merge as the governing process requires (see the Merge Skill / merge
workflow).

### 3. Related implementation survey

Before writing code:

- Inspect the relevant existing implementation and its established patterns.
- Check related open/completed Issues and recent implementation PRs in the
  touched area, including external-review history (per the Project Profile).
- Identify existing models/contracts to reuse rather than duplicate.

### 4. Architecture SSOT check

Read the authoritative documents governing the touched area (top-level and
subsystem SSOT, architecture MATRIX, relevant ADRs) and confirm the planned
change conforms. Explicitly record:

- The layering / dependency-direction rules that apply (incl. analyzer rules).
- Any ADR that constrains the change.
- Whether the change implies a significant design decision needing an ADR
  (see self-review's ADR feedback conditions).

If code and SSOT disagree, do **not** silently redefine the architecture;
treat it as a design-gap finding.

### 5. Implementation approach confirmation

State the implementation approach before editing: which components / files /
contracts change, and why this approach fits the SSOT and requirements. For
larger or uncertain changes, this may itself be the output of a prior Research
task. Confirm the approach with the requester when the change is non-trivial or
has multiple viable designs.

### 6. Implementation

Write the minimal change that satisfies the requirements (Common rule 7):

- Touch only what the driving task requires.
- Reuse existing models/contracts and follow neighboring patterns.
- Stay within the correct layer per the architecture MATRIX.
- Do not introduce unrelated edits, artifacts, or reformatting.

### 7. Tests

- Add or update tests for the new/changed behavior **when the change involves
  code/behavior**. For documentation-only or process-only changes, tests may be
  not applicable — record that explicitly rather than fabricating coverage
  (Common rule 5–6).
- Cover the negative space the code must *not* act on, and boundary conditions.
- Run the targeted tests for the touched area first (Project Profile filter),
  when applicable.

### 8. Build / Analyzer / E2E

Run the verification gates that are **applicable to the changed artifact**, per
the Project Profile:

- Build (the change must compile cleanly) — applicable when code/build inputs change.
- Analyzer rules (build-breaking) — applicable when the change touches
  analyzer-covered code.
- The test suites, native checks, and any E2E the Project Profile's verification
  ladder defines, **relevant to the change**.
- Any policy / contamination gate relevant to the artifacts touched.

Only require a gate when it applies to the change; for changes where a gate is
not applicable (e.g. documentation-only edits with no code / test surface),
state that it is not applicable instead of treating it as a required, failing
gate. Never report a gate as passed that was not run or failed (Common rule 5–6).

### 9. git diff / status review

Before wrapping up:

- `git status` — only intentional files changed; no stray artifacts.
- `git diff` — re-read the full change for scope creep and correctness.
- Confirm working-tree state matches the intended change set.

### 10. Definition of Done confirmation

Against the driving task's Definition of Done / acceptance criteria (and this
skill's Definition of Done below), verify each item is actually satisfied with
real evidence. Only then report completion.

## Verification

- [ ] The driving task's acceptance criteria map to concrete, implemented
      changes (Step 1).
- [ ] Repository state was inspected, not assumed (Step 2).
- [ ] Related implementation and external-review history were surveyed (Step 3).
- [ ] Architecture SSOT / MATRIX / ADRs were read and respected (Step 4).
- [ ] The implementation approach was stated and confirmed (Step 5); it relies
      only on the actual repository state (Common rule 3–4).
- [ ] Change is minimal and in-scope; no unrelated edits (Step 6, Common rule 7).
- [ ] Applicable tests added/updated and green; non-applicable tests recorded as
      not applicable (Step 7).
- [ ] Applicable build, analyzer, and E2E gates green with real results
      reported; non-applicable gates recorded as not applicable (Step 8, Common
      rule 5–6).
- [ ] `git status` / `git diff` reviewed; only intentional changes (Step 9).
- [ ] Definition of Done satisfied before reporting completion (Step 10).

## Definition of Done

Completion may be reported **only** when all of the following hold:

- [ ] The driving task's accepted criteria / requirements are implemented.
- [ ] Change conforms to the Architecture SSOT / MATRIX / ADRs.
- [ ] Applicable tests exist and pass and applicable build/analyzer/E2E gates are
      green (actually run); non-applicable gates are recorded as not applicable,
      never left unaddressed or reported as green without running.
- [ ] `git status` / `git diff` are clean of unintentional changes.
- [ ] Documentation / process artifacts updated (via the documentation sync
      gate) where required.
- [ ] The pre-PR self review gate has been **completed** (not merely scheduled)
      before reporting completion — see the governing gates in
      [Common Skill](../common/SKILL.md). A scheduled-but-unrun review means the
      Definition of Done is not met.
- [ ] The Common Skill's rules are satisfied.

Note: meeting this Definition of Done is about the *state* of the work and its
verification, not merely about the report wording. If anything is unmet, do not
claim completion.

## Failure Handling

- If the build / tests / analyzer fail, treat them as blockers: fix the root
  cause (in scope) or report the failure honestly. Never report a failed gate as
  passing (Common rule 5–6).
- If the change starts to grow beyond the driving task's scope, stop and
  reconcile with the requester rather than expanding silently (Common rule 7).
- If a requirement is ambiguous or conflicts with the SSOT/ADR, list it as an
  open question / design-gap and resolve before proceeding, rather than guessing.
- If operating in a Batch orchestrator worktree, follow the orchestrator's
  reporting and commit/PR conventions; never commit/push/merge outside those.

## Output / Reporting Requirements

Before reporting completion, produce a report covering:

- The driving task (Issue, or explicit request) and its acceptance criteria.
- The repository state (branch, base, HEAD).
- The implemented scope (files changed) and the design decision / approach.
- The real verification results (tests, build, analyzer, E2E) actually run, and
  which gates were not applicable.
- Any findings / deviations from the driving task or SSOT and how they were
  resolved.
- The Documentation sync decisions (which docs updated / intentionally unchanged).
- An explicit statement that the Definition of Done is met, with evidence.

## Porting to another project

Copy this skill unchanged; supply a Project Profile (authoritative documents,
MATRIX/SSOT precedence, verification command ladder, analyzer rules, artifact
policy) per the host `skills/` conventions. This skill adds no project-specific
content of its own.

## Non-goals

- Performing pure investigation (that is the Research Skill).
- Performing a review (that is the Review Skill) or the pre-PR self review gate
  (that is `skills/common/process/self-review/SKILL.md`).
- Orchestrating multiple issues or merging PRs (that is the Batch / Merge
  skills). This skill covers a single implementation task; the gates and
  executors run around it, not inside it. The ten-step procedure stays at the
  level of a single task's implementation, verification, and completion.

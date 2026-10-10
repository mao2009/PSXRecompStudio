---
name: pre-pr-self-review
description: >
  Legacy pre-PR review checklist now assigned to an independent reviewer before
  creating a pull request. Finds requirement gaps, scope creep, design/SSOT/ADR
  conflicts, missing tests, and recurring review findings before external review.
version: 0.2.0
scope: process
related-issues: "#23, #85, #106"
platform: agent-agnostic
---

# Independent Pre-PR Review Checklist

**Current owner policy:** ChatGPT coordinates independent code review and minor fixes.
Implementing agents must not perform self-review, launch review-only subagents,
or autonomously choose the next Issue. Preserve focused tests, compilation,
changed-file checks and normal CI. The historical checklist below is usable by
an independent reviewer only; wording below about mandatory agent self-review
is superseded by this policy.

A mandatory quality gate executed **before creating a pull request**.
The reviewer stance is adversarial toward the implementation:

> Do not defend the change. Hunt for its bugs, design omissions, and future problems.

This skill is project-independent. All project-specific facts (authoritative
documents, ADR locations, verification commands, review-history sources) are
loaded from a **Project Profile** (see [Porting](#porting-to-another-project)).

## When to apply

Apply this skill to **every pull request**, without exception.

The gate itself is never skipped. Its *depth* scales with risk:

| Change | Depth |
|---|---|
| Trivial docs / comments | Checklist pass, quick diff re-read |
| Normal feature / fix | Full procedure |
| Architecture, contracts, public API, generated code, security-sensitive | Full procedure + explicit ADR check + regression sweep |

## Inputs

Gather before reviewing:

1. The full diff against the base branch, **including working-tree changes**.
   `git diff <base>...` only covers committed work; run `git status --short`
   and either commit pending changes first or explicitly include modified and
   untracked files in the review.
2. The driving Issue(s): requirements, acceptance criteria, explicit non-goals.
3. Project Profile contents: authoritative documents (SSOT), ADR index,
   architecture constraints, verification commands, review-history sources.
4. Related SSOT / ADR documents referenced by the changed code or docs.
5. Past review findings: previous external-review comments on similar PRs and
   previously recorded self-review findings.

## Procedure overview

```text
Issue
 → Implementation
 → Local Verification          (project's own commands; must be green first)
 → SELF REVIEW                 (this skill)
     → Findings classification
     → Fix
     → Re-verification         (affected scope re-run)
 → PR creation
 → External review             (independent, automatic; e.g. a review bot)
 → Feedback                    (external findings analyzed; skill/ADR updated if warranted)
 → Next Self Review            (updated checklist applies)
```

## Self-review procedure

Work through the diff in this order. Record every finding with file/line and a
classification (see [Classification](#finding-classification)).

### 1. Requirements and scope

- [ ] Each Issue acceptance criterion maps to a concrete change that satisfies it.
- [ ] No changes outside the Issue's intent are included.
- [ ] Nothing unrelated rode along (build artifacts, temp files, editor noise,
      unintended renames/formatting).
- [ ] Working-tree state accounted for: all reviewed changes are committed, or
      modified/untracked files were explicitly included in the review.

### 2. Authoritative documents

- [ ] SSOT sections governing the touched area were read; code conforms to
      them. If code and document disagree, flagged as `design-gap`, not silently resolved.
- [ ] All related ADRs checked for conflicts.
- [ ] Explicit decision made whether this change creates a significant design
      decision requiring a new/updated ADR (see [ADR feedback](#adr-feedback-conditions)).

### 3. Implementation quality

- [ ] Logic landed in the right layer/component; responsibility boundaries hold.
- [ ] API surface appropriate: naming, visibility, dependency direction.
- [ ] Error handling and boundary conditions covered: empty input, zero, max
      values, overflow/wraparound, total-function behavior on non-applicable inputs.
- [ ] No duplicated parallel logic where an existing equivalent exists.
- [ ] No project-, environment-, or agent-specific values embedded in shared,
      portable, or generic artifacts (issue refs, concrete paths, tool names).
- [ ] Consistent with neighboring code and established patterns.

### 4. Tests and verification

- [ ] New behavior has tests; changed behavior has updated tests.
- [ ] Negative space covered: inputs the code must *not* act on, plus their
      nearest confusable neighbors (e.g. adjacent opcodes, sibling flags).
- [ ] Numeric domain boundaries covered (min/max, wraparound).
- [ ] Invariants asserted where cheap (inputs unchanged, no side effects).
- [ ] Local verification ladder ran green **after** the last edit — re-run if
      any fix was applied during this review.

### 5. Recurring-problem knowledge

- [ ] Known past findings from external reviews and prior self-reviews were
      consulted (source list lives in the Project Profile).
- [ ] For each category of past finding: checked this PR for the same kind of problem.
- [ ] Predicted what an external reviewer would most likely flag, and checked
      those points first.
- [ ] Any recurring/durable finding was routed to the smallest existing
      prevention owner instead of automatically creating a new analyzer or gate.

## Finding classification

Classify every finding into exactly one bucket:

| Class | Meaning | Action |
|---|---|---|
| `blocker` | Defect, spec/SSOT/ADR violation, broken build/test | Must fix before PR |
| `scope` | Change outside the Issue's intent | Remove, or split into its own Issue/PR |
| `design-gap` | Finding reveals a missing/ambiguous design decision | Fix locally AND evaluate ADR/Skill promotion (below) |
| `doc-drift` | Documentation no longer matches intended behavior | Update doc in same PR or tracked follow-up |
| `improvement` | Valid but optional enhancement | Record in PR body ("remaining items"); do not expand scope |
| `not-an-issue` | Investigated, justified, no action | Record the justification in the PR body |

No finding may be silently dropped.

After fixes: re-run the affected portion of the verification ladder, then
re-review only the fixed hunks plus anything they could influence.

## Role split with external review

| Gate | Timing | Role |
|---|---|---|
| Self review (this skill) | Before PR creation | Quality gate; eliminates preventable findings |
| External review (e.g. automated review bot) | Automatic on PR creation | Independent confirmation; catches what self-review missed |

Constraints:

- Never skip, disable, or preempt external review to "save time".
- Success metric is **not** fewer external reviews — it is **fewer fix/re-review
  cycles after PR creation**.
- External review is a complement, never a substitute for this gate.
- **This gate is not independent review evidence.** It is performed by the
  authoring context on its own change. Where a merge policy requires an
  independent review of the merge candidate — for example when an automated
  review provider is unavailable — that evidence must come from a reviewing
  context separate from the author. A completed self review never satisfies it,
  and claiming otherwise would let a change approve itself.

## External-review feedback loop

When an external review, human review, CI failure, audit, or later regression
reports a finding:

1. Classify the finding using this skill's finding taxonomy.
2. Fix the concrete defect in the current change when it is in scope; re-run the
   affected verification.
3. Answer honestly: **why did the existing review/gate stack miss it?**
   - Checklist item absent? → the review process may need a focused update.
   - Contract ambiguous or missing? → the owning SSOT/ADR may be incomplete.
   - Existing mechanical gate has a hole? → extend that gate and add a regression.
   - Runtime semantics were wrong? → add semantic executable evidence.
4. Decide whether the finding is a one-off or evidence of a durable gap.
5. Route any durable gap to the **smallest existing prevention mechanism that
   actually owns the failure mode**, using the table below.
6. If the durable work cannot be completed safely in the current PR, create a
   focused follow-up Issue with the evidence required below.

### Prevention routing

| Finding shape | Default durable prevention |
|---|---|
| One-off implementation mistake | Focused regression test; no new global rule |
| Missing/ambiguous behavioral or architecture contract | Owning SSOT and, when warranted, ADR; executable check where practical |
| Layer/dependency/API-boundary violation | Existing architecture contract + ArchitectureAnalyzer consumer coverage |
| Purity/adopted immutability violation | Existing PureSharp policy/configuration |
| CPU/Recompiler/Runtime execution semantics | Golden, differential, contract or focused regression test |
| Repository/process invariant | Existing CI/script/Skill/workflow validation |
| Repeated static source pattern with no existing owner | Candidate static rule; evaluate through the Analyzer Rollout Skill |

**Do not promote by prestige or abstraction level.** An ADR is not "better" than
a regression test, and a Roslyn analyzer is not "better" than an executable
semantic test. Choose the narrowest durable mechanism that can reliably prevent
the same failure mode without duplicating another owner.

Recurrence strengthens the case for durable prevention. Two or more occurrences
of the same failure class are a strong signal that the current prevention is
insufficient, but recurrence alone does not prove that a new analyzer is the
right mechanism. A single finding may still require an SSOT/ADR update when it
exposes a genuine contract gap.

### Focused follow-up Issue evidence

When prevention is split out of the current PR, the follow-up Issue must record:

- the concrete finding(s) and where they occurred;
- the affected contract/SSOT or subsystem;
- whether the issue is one-off or recurring, with links/evidence where known;
- why the current compiler/analyzer/test/CI/review stack did not catch it;
- the existing owner that should be extended, or why no existing owner fits;
- the proposed prevention mechanism and explicit non-goals;
- the verification that will prove the prevention works.

Do not create speculative diagnostic catalogs, reserved rule-ID families, or
generic "quality improvement" Issues without a concrete failure mode.

For a genuine new static-rule candidate, the Analyzer Rollout Skill owns the
eligibility, overlap, baseline, false-positive and severity-ratcheting process.

## ADR feedback conditions

Create or update an ADR when a finding or design choice involves:

- A constraint future implementations must respect.
- A rejected alternative worth recording (why it was rejected).
- A model/semantics decision spanning multiple components.
- A contradiction between existing ADRs/SSOT resolved in a specific direction.

Do **not** create an ADR for purely local implementation details.

## Porting to another project

Copy this skill unchanged. Replace only project-specific inputs via a Project
Profile containing:

1. Paths to authoritative docs (architecture SSOT, subsystem specs).
2. ADR directory and index.
3. Verification command ladder (targeted tests → full tests → analyzers → native/other gates) and CI summary.
4. Known environment caveats for local runs.
5. Sources of historical review findings (bot history location, review log convention).
6. Issue/PR conventions (PR body required sections, closing-keyword policy).

The profile's location and naming are defined by the host repository's skill
documentation (e.g. a `skills/` index); this skill does not assume any
specific path, project name, or issue tracker reference.

## Completion criteria

A PR may be created only when all of the following hold:

- [ ] Every Issue acceptance criterion maps to a concrete change.
- [ ] No unrequested changes are included.
- [ ] SSOT / ADR consistency verified; new design decisions routed to ADR process.
- [ ] Checklist fully walked; all findings classified and resolved per policy.
- [ ] Verification ladder green **after** the final edit.
- [ ] PR body can state: implemented scope, verification results, remaining items,
      and reason if the Issue is intentionally left open.

## Non-goals

- Replacing external or human review.
- Guaranteeing zero post-PR findings (goal: minimize preventable ones).
- Encoding any single project's domain specifics into this skill.

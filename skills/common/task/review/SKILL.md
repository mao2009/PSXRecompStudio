---
name: review
description: >
  Standard task template for reviewing a change: a set of review viewpoints
  (requirements fit, Architecture SSOT, correctness, security, error handling,
  tests, regression, unintended changes) and a required report format. Serves
  any review (self-review, responding to external review, or reviewing a peer's
  change). The mandatory pre-PR gate is the separate self-review skill.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#174"
---

# Review Skill

A task template for **reviewing a change** — adversarial evaluation of a
diff/PR against the driving requirements and the architecture SSOT. Its purpose
is to find the change's bugs, scope problems, and omissions, not to defend it.

This skill is project-agnostic; project-specific inputs (authoritative
documents, architecture MATRIX, verification commands) come from the **Project
Profile**. The [Common Skill](../common/SKILL.md) rules apply throughout.

## Relationship to the pre-PR self review (important)

`skills/common/process/self-review/SKILL.md` is the **mandatory pre-PR gate**:
a specific application of reviewing to "before opening a PR for this
implementation you just did", with its own gate procedure and finding
classification. It must run on every PR.

This `review/` task skill is the **general, reusable review template**. It
defines the standard review *viewpoints* and *report format* that apply to any
review:

| Concern | Owned by |
|---|---|
| Pre-PR self-review gate (when it runs, classification) | `self-review/SKILL.md` |
| Standard review viewpoints + report format (this skill) | `review/SKILL.md` |

They do not duplicate each other: use this skill to know *what to look for and
how to report a review* in any context; use self-review for *when and how the
pre-PR gate runs, and how findings are classified and resolved*. A self review
internally applies these viewpoints.

## When to apply

Apply this skill to review a change:

- Reviewing someone else's / a peer's PR.
- Performing an independent review on demand.
- Conducting the pre-PR self review (which additionally invokes the self-review
  gate semantics).
- Answering / analyzing external review findings on a PR.

## Preconditions

- The change to review (a diff, or a PR) and its driving task (Issue, or
  explicit request) / requirements.
- Access to the actual repository state the change applies to.
- [Common Skill](../common/SKILL.md) accepted (especially the ground-truth and
  honesty rules).
- The Project Profile (authoritative docs, MATRIX, verification commands,
  SSOT precedence, review-history sources).

## Inputs

1. The full diff against the base branch, **including working-tree changes**
   (`git status --short` + modified/untracked files — a committed-only diff is
   not complete).
2. The driving task (Issue, or explicit request when no Issue backs the work):
   requirements, acceptance criteria, explicit non-goals.
3. The Project Profile: authoritative docs, ADR index, MATRIX, verification
   commands, SSOT precedence, review-history sources.
4. Related SSOT / ADR docs referenced by the change.

## Standard Review Viewpoints

Work through the change against every viewpoint below. Record each finding with
a reference (file/line or requirement) and its severity/classification per the
self-review skill's taxonomy when it applies (blocker / scope / design-gap /
doc-drift / improvement / not-an-issue).

### 1. Requirements fit

- Does each acceptance criterion of the driving task map to a concrete
  implemented change?
- Is there any change, or missing change, that the driving task did not intend?
- Are explicit non-goals respected?

### 2. Architecture SSOT

- Does the change conform to the governing architecture MATRIX, layer /
  dependency-direction rules, and analyzer rules?
- Does it conflict with any accepted ADR or the SSOT precedence?
- Does it reuse established models/contracts instead of building parallel ones?
- If code and SSOT disagree, was it handled explicitly (design-gap), not
  silently redefined?

### 3. Correctness

- Does the logic behave correctly at boundaries: empty input, zero, max,
  overflow/wraparound, total-function behavior on non-applicable inputs?
- Are the algorithms and data-flow right for the stated purpose?
- Are invariants maintained (inputs unchanged, no unintended side effects)?

### 4. Security

- Any unsafe handling of external input / untrusted data?
- Any secrets, credentials, or sensitive data exposed or logged?
- Any path/traversal, injection, or privilege issues in the changed surface?

### 5. Error handling

- Are failure paths handled and reported, not swallowed?
- Are errors surfaced honestly (not "treated as success")?
- Are boundary / unexpected cases handled gracefully?

### 6. Tests

- Does new behavior have tests; changed behavior have updated tests?
- Is the negative space covered (inputs the code must *not* act on, plus
  nearest confusable neighbors)?
- Are the reported test results real and green (were they actually run)?

### 7. Regression

- Does the change break prior behavior, contracts, or documented semantics?
- Were adjacent/related behaviors checked for unintended effects?
- Was the external-review history in the touched area consulted?

### 8. Unintended changes

- Scope creep, unrelated edits (formatting, renames, refactors, artifacts)?
- Unintentionally modified / added / removed files in the diff?
- Working-tree state consistent with the intended change?

## Report Format

Produce a review report with the following structure:

```text
## Summary                (overall verdict of the change)
## Findings              (one per finding: reference, viewpoint, classification, action)
## Requirements fit
## Architecture SSOT
## Correctness
## Security
## Error handling
## Tests
## Regression
## Unintended changes
## Verification performed (what was actually executed, with real results)
## Open questions / blockers
```

For each viewpoint, state either the findings found or a short "no issues found"
justified by inspection (not by assumption). When acting as the pre-PR
self review, feed findings into the self-review skill's classification and
resolution procedure rather than ending here.

## Verification

- [ ] Full diff (including working-tree changes) was reviewed, not just commits.
- [ ] All eight viewpoints were examined and reported (or explicitly N/A).
- [ ] Reported test/build results are real and green (Common rule 5–6).
- [ ] Repository state was inspected, not assumed.
- [ ] Findings are concrete (reference + classification), not vague impressions.
- [ ] Fact / inference / proposal are separated in the report (Common rule).

## Definition of Done

- [ ] Every viewpoint was examined and reported on.
- [ ] All findings are concrete and classified.
- [ ] Requirement fit and Architecture SSOT conformance are explicitly stated.
- [ ] No reported result is fabricated or a failure disguised as success.
- [ ] The review report is complete and actionable.

## Failure Handling

- If the change cannot be fully reviewed (missing context, no access to the
  base or the working tree), report the gap rather than reviewing partially and
  implying completeness.
- If a claimed result cannot be verified (e.g. a "passing" test that was never
  run), flag it — do not accept it at face value (Common rule 5–6).
- If the review itself is requested on a change that violates the SSOT/ADR,
  report that violation explicitly.

## Output / Reporting Requirements

The report format above, plus the repository state at review time (branch/HEAD)
and an explicit statement of whether the Definition of Done was met.

## Porting to another project

Copy this skill unchanged; supply a Project Profile (authoritative docs, MATRIX,
verification ladder, review-history sources) per the host `skills/` conventions.

## Non-goals

- Duplicating the self-review gate's procedure/classification
  (`skills/common/process/self-review/SKILL.md`) — that skill is referenced, not
  re-implemented or replaced.
- Replacing external or human review.
- Guaranteeing zero post-review findings (goal is to minimize preventable ones).
- Encoding any single project's domain specifics into this skill (those belong
  in the Project Profile).

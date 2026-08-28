---
name: research
description: >
  Standard procedure for a research / investigation task: establish the current
  state, gather requirements, findings, constraints, alternatives, and produce a
  recommendation with implementation scope, test strategy, risks, and open
  questions. Output separates fact, inference, and proposal.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#174"
---

# Research Skill

A task template for **investigation-only work**: understanding a problem,
mapping the relevant code and documentation, and producing a grounded
recommendation — without (necessarily) changing code. Use this skill when the
driving task asks to *find out*, *investigate*, *assess*, or *propose*, or when
the implementation is genuinely uncertain and requires a written exploration
before code is written.

This skill is project-agnostic. Project-specific inputs (authoritative
documents, architecture MATRIX, verification commands, authority ordering) come
from the **Project Profile**. The [Common Skill](../common/SKILL.md) rules apply
throughout, especially: read the actual repository state, do not fabricate
results, and separate fact from inference from proposal.

## When to apply

Apply this skill when the task is a research / investigation deliverable:

- Surveying current behavior, architecture, or existing implementation.
- Evaluating alternatives before choosing an approach.
- Estimating the scope and risk of a prospective change.
- Answering a question whose answer requires reading code and documents.

If the deliverable is a code change, the Implementation Skill applies; the
implementation should incorporate the research output produced here.

## Preconditions

- A driving request or Issue defining what must be investigated.
- Repository and agent access (read access is sufficient for pure research).
- [Common Skill](../common/SKILL.md) accepted, including its ground-truth rules.

## Inputs

1. The driving request / Issue: what question to answer, and any acceptance
   criteria for the research output.
2. The Project Profile: authoritative documents, architecture MATRIX / SSOT
   precedence, open issues list, verification commands.
3. Actual repository state: `git status`, current branch/HEAD, and relevant
   code as it exists **now** (not as assumed).

## Standard Procedure

Follow these steps in order, keeping the output sections in sync as you go.

```text
1. Pin the request      → what must be answered, and its acceptance criteria
2. Establish context    → repository state, related docs/Issues/PRs, relevant code
3. Gather the current state  → what exists today (observed, cited)
4. Elicit requirements → what the outcome must satisfy
5. Collect findings     → concrete observations with sources
6. Identify constraints → architecture MATRIX, ADRs, SSOT, environment
7. Enumerate alternatives → candidate approaches with tradeoffs (not only the
                             obvious one)
8. Recommend an approach → a proposal, grounded in 3–7
9. Derive implementation scope → files/subsystems the proposal touches
10. Plan a test strategy → how the proposal would be verified
11. Surface risks       → likely failure modes and mitigations
12. List open questions → what remains unknown / requires a decision
```

### 1. Pin the request

Restate what must be answered. Do not answer a slightly different, more
convenient question. Record the acceptance criteria the research output must
satisfy.

### 2. Establish context

Inspect the real state:

- `git status`, current branch, and HEAD.
- The relevant subsystem SSOT and architecture MATRIX pages.
- Related open Issues and recent implementation PRs in the touched area
  (external-review history is a source, per the Project Profile).
- The relevant code, in the current working tree.

### 3. Current State

Describe what exists today. Every statement is either **observed** (with a
file/line or command + output) or **inferred** (explicitly labeled). This is
the factual foundation for everything else.

### 4. Requirements

State the requirements the outcome must satisfy. Separate:

- **Explicit** requirements (from the Issue / request / SSOT / ADR).
- **Inferred** requirements (stated as inference, with reasoning).

If a requirement is ambiguous, list it under Open Questions rather than
guessing.

### 5. Findings

Concrete, sourced observations. One finding per bullet, each with a reference
(issuer / file / command output) and a label: `fact`, `inference`, or
`proposal`. Findings must be individually checkable.

### 6. Constraints

Record binding constraints:

- Architecture MATRIX / layering / dependency-direction rules (incl. analyzer /
  build-breaking rules per the Project Profile, where applicable).
- Accepted ADRs and the SSOT precedence order.
- Environment constraints (platforms, tooling, CI behavior).
- Repository / artifact policy constraints.

### 7. Alternatives

Enumerate candidate approaches. For each: a short description, tradeoffs
(pros/cons), and its fit against the constraints and requirements. Do not
prematurely narrow to one option; record at least one viable alternative to the
recommendation (even if ultimately rejected — note why).

### 8. Recommended Approach

A clear recommendation, explicitly labeled a **proposal**. State:

- Why this option over the others (tie back to requirements and constraints).
- What the change would look like at a high level.
- Any secondary effects (docs, ADR, tooling) the proposal implies.

### 9. Implementation Scope

List the concrete surface the proposal touches: files, subsystems, public API,
config, tests, documentation. Distinguish **required**, **optional**, and
**out-of-scope** (with reason). Note where an architecture / design decision is
significant enough to warrant an ADR (see self-review's ADR feedback
conditions).

### 10. Test Strategy

Describe how the proposal would be verified, scoped to what is **applicable** to
the proposal and the Project Profile's verification ladder:

- Unit tests for the touched area (targeted filter), when the proposal touches
  code.
- The test suites and native checks the Project Profile's verification ladder
  defines, when applicable to the change.
- Analyzer / build gates and any E2E defined in the ladder, relevant to the
  change.
- Where the Project Profile's verification ladder applies.

For changes where a verification item does not apply (e.g. documentation-only
proposals with no code / test surface), state it as not applicable rather than
requiring it.

### 11. Risks

Likely failure modes, their likelihood/impact, and mitigations. Include both
technical risks and risks from gaps in the research (things assumed rather than
verified). Each risk is `observed`, `inferred`, or a `proposal` mitigation.

### 12. Open Questions

Anything unresolved: ambiguous requirements, decisions awaiting a human, facts
not verified. Anchoring the open questions explicitly is part of the research
deliverable — a research report with "no open questions" by default is suspect.

## Output Format (standard research report)

The standard output is a report containing the sections above. Where a section
is not applicable, state "not applicable" with a one-line reason rather than
omitting it. Use clear labels (`fact:` / `inference:` / `proposal:`) throughout
so the fact / inference / proposal separation is explicit.

A concise template:

```text
## Current State
## Requirements
## Findings
## Constraints
## Alternatives
## Recommended Approach
## Implementation Scope
## Test Strategy
## Risks
## Open Questions
```

## Verification

- [ ] Every `Current State` / `Findings` claim is observed (sourced) or clearly
      labeled as inference.
- [ ] Requirements and Constraints were checked against the actual SSOT / ADR /
      MATRIX, not guessed (Common rule 3–4).
- [ ] At least one alternative is documented alongside the recommendation.
- [ ] Implementation Scope, Test Strategy, Risks, and Open Questions are present
      (or explicitly N/A with reason).
- [ ] No test / build results are claimed unless actually run (Common rule 5–6).
- [ ] Repository state was inspected, not assumed.

## Definition of Done

- [ ] The driving question is answered with a grounded recommendation.
- [ ] All ten output sections are complete (or explicitly N/A with reason).
- [ ] Fact / inference / proposal are explicitly separated.
- [ ] Open questions and unresolved decisions are listed.
- [ ] The Common Skill's rules are satisfied.

## Failure Handling

- If the research cannot be grounded (no reliable source), fail openly: report
  the missing facts as Open Questions; do not substitute inference for fact.
- If the request itself is ambiguous or self-contradictory, stop and ask rather
  than silently choosing one reading.
- If the recommendation would conflict with the architecture MATRIX / ADR,
  report the conflict explicitly (a design-gap) instead of overriding it.

## Output / Reporting Requirements

The final report is the research report above, plus: the repository state at the
time of research (branch/HEAD), the driving request, and an explicit statement
of whether the Definition of Done was met.

## Porting to another project

Copy this skill unchanged; supply a Project Profile (authoritative documents,
MATRIX/SSOT precedence, verification ladder, conventions) per the host `skills/`
conventions. This skill adds no project-specific content of its own.

## Non-goals

- Writing or changing production code (that is the Implementation Skill).
- Making unilateral design decisions without listing them as recommendations and
  open questions.
- Duplicating implementation-scope decisions that belong to the Implementation
  Skill.

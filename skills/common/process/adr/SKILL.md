---
name: adr
description: >
  Procedure for authoring, updating, and validating Architecture Decision
  Records (ADRs): the "is an ADR needed" decision, SSOT / architecture-matrix /
  existing-ADR preflight, sequential numbering and naming, standard structure
  (Context / Decision / Consequences with rejected alternatives), traceability
  to Issues / PRs / code / tests, consistency checks, the review-feedback loop,
  and the rules AI agents must follow when working with design decisions. Use
  when a design decision must be recorded, an existing ADR updated, or a review
  finding assessed for design feedback.
version: 0.1.0
scope: process
platform: agent-agnostic
related-issues: "#84"
---

# ADR Skill

A reusable procedure for **authoring, updating, and validating Architecture
Decision Records (ADRs)**. It treats an ADR not as a bureaucratic byproduct but
as the SSOT of a design decision: the record that ties a decision to its
rationale, its rejected alternatives, and the sources of truth (SSOT documents,
architecture matrix, Issue, PR, code, tests) it must stay consistent with.

This skill is project-agnostic; project-specific inputs (authoritative
documents, architecture MATRIX, ADR directory and current-record inventory,
Issue/PR conventions, review-history sources) are loaded from the **Project
Profile**. The [Common Skill](../../../common/task/common/SKILL.md) rules apply
throughout, especially: verify actual repository state, never fabricate
results, and minimize change scope.

## Role split with the gates

The ADR lifecycle is owned across three distinct skills that do not overlap:

| Concern | Owned by |
|---|---|
| Is a change design-significant enough to need an ADR — *whether* to record | [`common/process/self-review`](../self-review/SKILL.md) (ADR feedback conditions) |
| Which documents, including ADRs, a change makes stale | [`common/process/doc-sync`](../doc-sync/SKILL.md) |
| *How* to author, update, and validate an ADR once it is needed | **this skill** |

The self-review gate decides whether a design decision must be recorded and
routes it here; the doc-sync gate treats a new/updated ADR as part of its
documentation impact map; this skill is the procedure that produces the record.

## When to apply

Apply this skill when:

- A design decision must be recorded as a new ADR.
- An existing ADR must be updated (decision changed, rationale corrected, a
  rejected alternative re-entered, traceability completed).
- A review finding (CodeRabbit, self-review, human review, CI / analyzer /
  test) is being assessed for design relevance and possible ADR feedback.
- Implementing against an existing ADR and verifying the implementation
  conforms to it.

Do not apply it to local implementation details (see below).

## When an ADR is needed

A new ADR is warranted (mirroring the self-review skill's ADR feedback
conditions) when a finding or design choice involves:

- A constraint future implementations must respect.
- A rejected alternative worth recording (and why it was rejected).
- A model / semantics decision spanning multiple components.
- A contradiction between existing ADRs / SSOT resolved in a specific direction.

Before creating a new record, run the triage:

1. **Does a new ADR help?** If the decision binds future implementations or
   resolves a cross-component semantic question, yes.
2. **Can an existing ADR be updated instead?** If the decision refines, corrects,
   or extends a record that already exists, update it; never create a duplicate
   number or a parallel record for the same decision.
3. **Is a document-level note enough?** If the change is purely descriptive
   (SSOT wording, README guidance) with no binding decision, update the document
   and record "no ADR needed".
4. **Is it a local implementation detail?** Then no ADR; it stays in code and
   comments.

The default for a change with no binding cross-component decision is **no ADR**,
recorded explicitly rather than assumed. Never turn every review comment or
every change into an ADR.

## Preconditions

- A driving task (an Issue, or an explicit request) that motivates the decision,
  or a review finding already classified as design-relevant.
- The Project Profile: authoritative documents, architecture MATRIX, ADR
  directory and current records, Issue/PR conventions, review-history sources.
- [Common Skill](../../../common/task/common/SKILL.md) accepted.

## Inputs

1. The driving task / finding: what is being decided, and why.
2. The Project Profile's authoritative-documents table and ADR inventory.
3. Related Issues and PRs in the touched area (including external-review
   history, per the profile's review-history sources).
4. The current implementation the decision constrains.

## Standard Procedure

Follow the steps in order; each step gates the next.

```text
1. Decide      → is an ADR needed? new vs update vs no ADR (record the reason)
2. Preflight   → SSOT, architecture matrix, existing ADRs, Issues/PRs, code,
                 review history
3. Number/name → sequential, collision-free; matching filename and H1
4. Draft       → standard structure; why over what; alternatives; decision
                 separated from implementation detail
5. Trace       → link Issue / PR / code / tests to the record (and reverse)
6. Consistency → ADR vs SSOT / matrix / existing ADRs / implementation
7. Feedback    → route design-relevant review findings into ADR / skill
8. Re-verify   → re-run the affected checks after any change
```

### 1. Decide

Run the triage in [When an ADR is needed](#when-an-adr-is-needed) and record the
outcome. A "no ADR needed" verdict with a one-line reason satisfies the doc-sync
gate's ADR column for this change.

### 2. Preflight

Verify current state; never assume it (Common rule 3–4):

- [ ] Read the top-level Architecture SSOT and any subsystem SSOT governing the
      touched area (from the Project Profile's authoritative-documents table).
- [ ] Read the architecture MATRIX / layering and dependency-direction rules
      that apply to the decision.
- [ ] Read existing ADRs: the records related to the area, and the full
      inventory. Confirm the next free number against **both** the profile's ADR
      inventory and the actual ADR directory listing on disk; a mismatch between
      the two is itself a doc-drift finding to fix before numbering.
- [ ] Inspect related open Issues and recent implementation PRs in the area,
      including past review findings (per the profile's review-history sources).
- [ ] Inspect the current implementation the decision will constrain.

### 3. Number and name

- Number sequentially as the next unused number (`max(existing) + 1`),
  zero-padded to three digits (`NNN`). Never reuse or skip a number; verify
  against both the profile inventory and the filesystem.
- Filename: `NNN-kebab-case-title.md` in the profile's ADR directory.
- Title: `# ADR-NNN: human-readable title` in the H1; the filename kebab-case
  stems from the same title.
- Name the decision domain, not the implementation (e.g. "Branch / Load-Delay
  Modeling", not "Add a delay-slot field to `BasicBlock`").

### 4. Draft (structure)

Follow the standard structure used by the existing records; the exact metadata
header convention (this repository uses a Status / Date / Issue header) is
supplied by the Project Profile:

```text
# ADR-NNN: Title
Status / Date / Issue header (project convention)

## Context               the situation forcing the decision: evidence,
                        background, why the existing state is insufficient
## Decision              what was decided, in binding terms; what future
                        implementations must respect
## Consequences          positive and negative outcomes, and follow-on work
## Alternatives Considered
                        options evaluated and rejected, with the reason
                        each was rejected
## Related ADRs          links to records this one extends, contradicts, or
                        depends on
```

Writing discipline:

- Record **why** the decision was made, not only **what** was decided; the
  rationale is what another agent or a future session can reuse.
- Separate the design decision from implementation details: the ADR holds the
  binding decision and its rationale; single-implementation specifics stay in
  code, comments, or lower-precedence docs and are referenced, not copied.
- Record rejected alternatives when the rejection is non-obvious or a future
  change could revisit the option. Where the decision replaced an earlier
  approach, record at least one viable alternative and why it lost.
- State consequences honestly, including negative ones and deferred work.
- Link rationale to evidence: Issues, PRs, measurements, review findings.

### 5. Traceability

An ADR must be navigable in both directions:

- **ADR → Issue**: the header `Issue` field names the driving Issue.
- **ADR → PR**: Context / Decision cite the PR(s) where the decision was made or
  implemented; the implementing PR body names the ADR(s) it touches.
- **ADR → code**: code implementing the decision exists and conforms. Drift
  between an ADR and the code is a `design-gap` finding (self-review
  classification), never silently resolved in either direction.
- **ADR → tests**: tests that encode an ADR-mandated invariant reference the ADR
  (e.g. in their purpose comment) so a reader can find which decision they guard.
- **Reverse**: from an existing ADR, a reader can locate the enforcing
  SSOT / matrix sections, the enforcing code, and the enforcing tests. When any
  side moves, update the links.

### 6. Consistency checks

Before a new or updated ADR counts as done, verify:

- [ ] No contradiction with the top-level or subsystem Architecture SSOT.
- [ ] No contradiction with existing ADRs; `Related ADRs` lists any record it
      extends, contradicts, or depends on.
- [ ] Consistent with the architecture MATRIX / layering rules.
- [ ] Consistent with the current implementation direction; no known drift.
- [ ] Traceable: Issue / PR / code / test links present and accurate.
- [ ] Number unique, sequential, and conventional; title and filename match.
- [ ] Structure complete: Context / Decision / Consequences present; rejected
      alternatives recorded where the rejection is worth preserving.
- [ ] Design decision and implementation detail are cleanly separated.

Existing records that predate these conventions are not rewritten in bulk (a
non-goal of this skill); when such a record is next touched for a substantive
change, align it to the standard as part of that change.

### 7. Review feedback loop

Design-relevant findings from any source — CodeRabbit, self-review, human
review, CI / analyzer / tests — are fed back rather than dropped:

```text
Review finding → classify → root cause → reflect into ADR / Skill
              → fix implementation → re-review → (recurring? promote)
```

Procedure:

1. **Classify** the finding: a local implementation problem, or a problem that
   implicates a design decision (a premise, constraint, or rationale missing from
   the related ADR)?
2. **Check** the related ADR for the missing premise / constraint / rationale.
3. **Decide** whether the ADR needs an update, whether a new ADR is warranted,
   or whether no ADR change is needed; record the decision and the reason.
4. **Promote** a recurring kind of finding (two or more occurrences from any
   source) into a permanent rule at the highest applicable level: this skill's
   checklist → project SSOT → ADR (per the self-review skill's promotion rule).
5. **Re-verify** after each fix: ADR, SSOT, implementation, and tests remain
   mutually consistent.

This is the continuous improvement loop:

```text
review finding → cause analysis → knowledge reflected into ADR / Skill
              → implementation fix → re-review
```

### 8. Re-verify

After drafting, updating, or applying a feedback fix, re-run the affected
portion of Preflight and Consistency checks and record what was actually run.

## Agent rules

Rules an AI agent must follow when using ADRs in any task:

- **Check relevant ADRs before implementing.** Read the ADRs that constrain the
  touched area (filtering the profile's ADR inventory) during the SSOT check,
  and let them shape the implementation.
- **Never silently ignore an ADR conflict.** If the implementation direction
  contradicts an ADR, surface it as a `design-gap` (Common rules 1 and 8) and
  resolve it through the ADR process — update or new ADR — not by quietly
  deviating.
- **Propose an ADR for new significant design decisions.** When implementation
  surfaces a decision that meets the ADR conditions, raise it for recording
  instead of self-suppressing it.
- **State the reason when changing an ADR.** Every ADR modification is a change
  like any other: driven by an Issue / PR, reviewed, and justified.
- **Route review findings.** A design-relevant finding from CodeRabbit,
  self-review, human review, or CI / analyzer / tests triggers Step 7 — never
  just a localized code fix that loses the design lesson.
- **Promote recurring findings.** When the same kind of finding recurs, prefer a
  permanent rule at the highest applicable level (skill checklist / SSOT / ADR).
- **Treat ADRs as the SSOT of design decisions.** They are living records of
  binding decisions with rationale, not formalistic documents to be left stale.
- **Do not over-create ADRs.** Local implementation details and review comments
  that carry no design constraint stay out of the ADR directory.

## Lifecycle integration

```text
ADR → Issue → Implementation → Self Review → CodeRabbit / Review
    → Verification → PR   (review knowledge flows back to ADR / Skill)
```

- The normal direction is **Issue → ADR → implementation**: the decision is
  recorded first, then the code conforms to it.
- Reverse traceability: an existing ADR can be the starting point — implementing
  or validating against it walks from the ADR to the SSOT / matrix sections, the
  code, and the tests that realize it.
- When the task only implements an existing ADR, no new ADR is created; the work
  is tracked normally and its conformance checked via Steps 5–6.

## Relationship to other skills

- `common/task/common` — universal rules (respect the SSOT, Issue as task SSOT,
  ground-truth verification, scope discipline) that apply through this skill.
- `common/task/implementation` — its SSOT check (step 4) tells an implementer to
  consult ADRs; this skill supplies the procedure when an ADR must be written.
- `common/process/self-review` — owns the ADR feedback conditions (when to
  record); its `design-gap` / `doc-drift` finding classes feed this skill's
  Step 7.
- `common/process/doc-sync` — maps a new/updated ADR onto the documentation
  impact set; this skill's recorded "no ADR needed" decision satisfies the
  doc-sync ADR column.

## Verification

- [ ] The "is an ADR needed" decision was made explicitly and recorded.
- [ ] Preflight read the SSOT, architecture matrix, existing ADRs (profile
      inventory and filesystem both), related Issues/PRs, and the current code.
- [ ] Number is unique, sequential, and conventional; filename / H1 / format
      consistent.
- [ ] Structure contains Context / Decision / Consequences; rejected
      alternatives recorded where worth preserving.
- [ ] Traceability (Issue / PR / code / tests) present and reverse links
      considered where the area was touched.
- [ ] Consistency checks pass, or each exception is a recorded, classified
      finding.
- [ ] No ADR was created where a document-level change or existing-ADR update
      sufficed; no local implementation detail was promoted to an ADR.
- [ ] The Common Skill's rules are satisfied.

## Definition of Done

- [ ] The decision is recorded (new ADR) or correctly updated (existing ADR) in
      the standard structure, with rationale and alternatives.
- [ ] Numbering / naming / format conventions hold and were verified against the
      actual repository state.
- [ ] The record is consistent with the SSOT, the architecture matrix, and the
      current implementation; traceability links are accurate.
- [ ] The governing gates (self-review, doc-sync) were applied or recorded as
      not applicable.
- [ ] The Common Skill's rules are satisfied.

## Failure Handling

- If the ADR content conflicts with a previously-unreconciled SSOT or ADR, stop
  and record a `design-gap` finding instead of inventing a resolution.
- If a review finding is not clearly design-relevant, record the triage verdict
  and the reason instead of force-fitting an ADR.
- Never report a verification (preflight, consistency check, re-review) as run
  when it was not (Common rules 5–6).

## Output / Reporting Requirements

The task report / PR body must state:

- Whether an ADR was created, updated, or intentionally not changed — with the
  driving Issue / PR and the distinct decision made.
- The ADR number and filename, and the consistency checks actually performed.
- Any review findings routed into the ADR / skill feedback loop, and any
  promotion into a permanent rule.
- The repository state (branch, HEAD) and the verification actually run.

## Porting to another project

Copy this skill unchanged. In the Project Profile provide:

1. The ADR directory and the current-record inventory (with status).
2. The numbering / naming / header conventions in use (or confirmation of the
   defaults this skill describes).
3. The authoritative-SSOT list and architecture-matrix path.
4. Issue / PR conventions (closing-keyword policy, PR body requirements).
5. Review-history sources for the preflight step.
6. A worked example: how one real ADR was created or updated via this procedure.

This skill adds no project-specific content of its own.

## Non-goals

- Mass rewriting of existing ADRs to an idealized format.
- Turning every review finding or every change into an ADR.
- Replacing the self-review / doc-sync gates or their finding classification.
- Encoding a single project's ADR conventions as mandatory for all projects
  (host conventions win; the structure above is the default).
- Over-complicating the ADR format.

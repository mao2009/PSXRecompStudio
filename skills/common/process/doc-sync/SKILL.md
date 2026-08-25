---
name: documentation-sync
description: >
  Decision-based gate run at task completion (before the pre-PR self review)
  that identifies every document affected by a change, updates each one only
  if genuinely affected, and records the reason whenever no update is needed.
  Prevents stale README/docs/skills without forcing meaningless edits.
version: 0.1.0
scope: process
platform: agent-agnostic
related-issues: "#89"
---

# Documentation Synchronization

A decision-based gate executed when a task's implementation is complete and
verified, and again as input to the final report. It answers exactly one
question systematically:

> Which documents does this change make stale, and which of those truly need updating?

It is **not** a "write the README" skill. Most changes require no README edit;
the point is that the "no update needed" verdict is reached deliberately and
recorded, not assumed.

This skill is project-independent. All project-specific inputs — the document
inventory, the concrete impact mapping, SSOT precedence, worked examples —
are loaded from a **Project Profile** (see [Porting](#porting-to-another-project)).

## When to apply

Apply when any of the following landed in the working branch:

- Implementation code (new feature, behavior change, public API surface)
- Architecture or design decisions (including new/updated ADRs themselves)
- CI, build, test infrastructure, or verification commands
- Developer workflow or repository policy
- Configuration files that define policy (e.g. a machine-readable policy SSOT)
- Process artifacts (skills, profiles, agent guides)

Skip only when the diff provably contains none of the above (e.g. typo fix in
a comment); still note that judgment in the task report.

## Inputs

1. The full diff against the base branch (`git status --short` included — see
   the self-review skill's working-tree rule).
2. The driving Issue(s)/PR(s): intent, acceptance criteria, explicit non-goals.
3. Project Profile: document inventory, documentation impact map, SSOT
   precedence, worked examples.
4. The documents currently linked from the touched area (follow links both ways).

## Procedure

```text
Implementation verified green
 → Step 1  Identify change categories
 → Step 2  Map categories to candidate documents (impact matrix)
 → Step 3  For each candidate: decide → update minimally OR record "no update + reason"
 → Step 4  Resolve contradictions via SSOT precedence
 → Step 5  Cross-reference/index consistency pass
 → Hand findings and decisions to the pre-PR self review
```

### Step 1 — Identify the change scope

From the diff and the driving issue, tag the change with every applicable
category. Do not stop at the first match; a single PR usually spans several.

| Category | Typical signals |
|---|---|
| User-facing feature/capability | New behavior visible outside the codebase, format/support-scope change, compatibility notes |
| Architecture / design | Layer, boundary, component responsibility, data model, semantics |
| CI / build / test infra | Workflow files, scripts, build settings, new quality gates, test layout |
| Developer workflow / repository policy | Contribution rules, branching, commit conventions, artifact/policy rules, agent guides |
| Configuration-as-policy | Machine-readable policy/config files consumed by tooling |
| Process artifacts | Skills, project profiles, review processes |
| Internal-only work | Refactoring, tests-only, local implementation detail |

Internal-only work still passes through Steps 2–3; its expected outcome is
usually "no updates", which must be recorded like any other decision.

### Step 2 — Documentation Impact Matrix

For each tagged category, mark every document kind as:

- **Update** — expected to change unless analysis shows otherwise.
- **Check** — inspect and consciously decide (update, defer via tracked
  follow-up, or no-op with reason).
- `—` — normally out of scope for this category.

| Change category | Entry-point README | Docs index | Architecture SSOT | ADR | Dev / process docs | Skills / profile |
|---|---|---|---|---|---|---|
| User-facing feature/capability | Update | Check | Check | Check¹ | Check | — |
| Architecture / design | Check | Check | Update | Update¹ | Check | Check² |
| CI / build / test infra | Check | Check | Check | Update¹ | Update | Check² |
| Dev workflow / repo policy | Update | Check | — | Update¹ | Update | Update |
| Configuration-as-policy | Check | Check | — | Update¹ | Update | Check² |
| Process artifacts (skills etc.) | — | — | — | Check¹ | Check | Update |
| Internal-only work | — | — | — | — | — | — |

1. New/changed **significant** design decision → ADR (see the self-review
   skill's ADR feedback conditions). An ADR addition alone does not justify a
   README edit.
2. The change alters how agents must work (new gates, commands, authoritative
   docs) → update the relevant skill/profile.

The Project Profile refines this matrix into a **concrete map**: category →
exact file paths in this repository (see [Inputs](#inputs) item 3).

### Step 3 — Per-document decision

For every candidate document, walk this decision path and keep the outcome:

```text
Is the document factually wrong or incomplete after this change?
├─ No            → record: "no update needed — <one-line reason>"
└─ Yes
   ├─ Fix is small and in scope → apply the minimal edit now
   ├─ Fix is real but larger than this change
   │                            → tracked follow-up Issue + link it here
   └─ Uncertain                 → treat as a finding (classification below)
```

Minimal-edit constraints:

- Update statements that are now false; do not rewrite surrounding structure.
- Never turn README/issues into progress logs; internal implementation details
  do not get promoted into entry-point documents just because they exist.
- Speculative content (future features not yet implemented beyond what the
  documents already declare as planned) is forbidden.
- Every applied edit must survive the question: *would a reader acting on the
  old text be misled without it?*

### Step 4 — Resolve contradictions by SSOT precedence

When two documents now disagree, the lower-precedence one yields. Default
order (the Project Profile states the authoritative order for its repository):

1. **Architecture SSOT** (top-level and subsystem pages marked SSOT) — what the system currently is.
2. **Accepted ADRs** — binding decisions and their rationale; overridden only by a newer ADR.
3. **Development / reference documentation** — supporting material.
4. **Skills and project profiles** — process guidance; must conform to 1–3.
5. **Entry-point README** — derived summary; lowest.

Two hard rules:

- Code and build reality are the *ground truth for detecting drift*, but they
  do **not** silently redefine documented architecture. A code/document clash
  is a finding (design-gap or doc-drift), resolved explicitly — never by
  quietly editing whichever file is closer.
- Never resolve a conflict by duplicating content into several documents.
  Fix the source at its precedence level and link from elsewhere.

### Step 5 — Cross-reference consistency pass

After edits (or a recorded all-clear):

- [ ] Indexes listing the changed documents (docs index, README link lists,
      skill tables, profiles) reflect additions/renames/removals.
- [ ] Links added or removed in this change resolve in both directions.
- [ ] Document metadata (Status / Authority / Related Issues) updated where
      the hosting convention requires it.
- [ ] No orphaned references to documents this change retired.

## Relationship to the pre-PR self review

| Gate | Question it owns |
|---|---|
| Documentation sync (this skill) | *Which documents must change, and did they?* Runs once per change, before the self review. |
| Pre-PR self review | *Is the whole change (including its documentation outcome) correct, consistent with SSOT/ADR, and complete?* Runs on every PR. |

The self review consumes this skill's output: recorded decisions satisfy its
documentation-related checks, and anything uncertain becomes a classified
finding there (`doc-drift`, `design-gap`). Conversely, a self-review finding
that reveals drift re-enters this skill's Step 3. The two skills share the
finding taxonomy and the ADR conditions; neither duplicates the other's
procedure.

## Reporting contract

The task report / PR body must contain a documentation sync section stating,
per candidate document: updated (with file), deferred (with follow-up link),
or intentionally unchanged (with reason). "Nothing needed because the change
is internal-only" is a valid, complete answer.

## Worked examples

Concrete examples live in the Project Profile so this skill stays portable.
The profile should carry at least one real change showing: the tagged
categories, the candidates the matrix produced, what was updated, and what was
consciously left untouched with reasons.

## Porting to another project

Copy this skill unchanged. In the Project Profile add:

1. Document inventory: paths of entry-point README(s), docs index, architecture
   SSOT pages, ADR index, dev/process docs, skills/profiles — with their
   Authority level.
2. Concrete impact map refining the matrix: category → exact files.
3. Repository's SSOT precedence order (or confirmation of the default).
4. At least one worked example from the repository's history.
5. Where sync decisions are recorded (PR body section, report template).

## Non-goals

- Automated documentation analysis, AST/link checkers, or dedicated CI tooling
  (propose separately if a mechanical check becomes clearly feasible).
- Unconditional README updates, or any fixed "every change touches X" rule.
- Turning README or docs into changelogs, progress trackers, or issue logs.
- Replacing human judgement about what deserves documenting.

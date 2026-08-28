---
name: common
description: >
  Project-wide universal rules that every task-specific skill (research,
  implementation, review, issue) must respect. Concerned with intellectual
  honesty, ground-truth verification, scope discipline, and the Definition of
  Done. Loaded implicitly by every other skill in this directory.
version: 1.0.0
scope: process
platform: agent-agnostic
related-issues: "#174"
---

# Common Skill

The mandatory, project-wide rules applicable to **every** task an AI agent
performs for this repository. These rules are intentionally project-agnostic;
project-specific facts (authoritative document paths, verification commands,
authority ordering) are loaded from a **Project Profile** (see
[Porting](#porting-to-another-project)).

This skill does **not** describe any single task type. Each task-specific skill
in this directory assumes these rules and only adds its own procedure.

## Mandatory Rules

These are hard rules. A task is not complete until every applicable rule holds.

1. **Respect the Architecture SSOT.** The current architecture is defined by
   the authoritative documents (top-level and subsystem SSOT). Code, docs, and
   artifacts must conform. See the Project Profile for the authoritative paths
   and the authority / SSOT precedence order.
2. **Treat the driving task as the SSOT of the work unit.** Requirements,
   acceptance criteria, and explicit non-goals come from the driving task — a
   GitHub Issue, or (when no Issue backs the work) an explicit request. Never
   invent Issue data for a task that has no Issue: if the driving task is an
   explicit request, treat the request as the source of requirements and
   acceptance criteria. Do not add scope the driving task does not request, and
   do not silently drop a requirement it does.
3. **Verify the actual repository / git state.** Before acting, inspect the
   real state (`git status`, `git log`, `git diff`, worktrees, branches). Do
   not assume a state from memory, a stale report, or an earlier session.
4. **Do not judge state by guesswork.** If a fact is unknown or ambiguous,
   check it; if it cannot be checked, say so explicitly as an open question.
   Never fabricate a plausible-but-unverified state.
5. **Do not fabricate test results.** Only report results that were actually
   executed and observed. A command that was not run, or failed, is reported as
   such — never as "passed".
6. **Never treat failure as success.** A failed build, test, analyzer, or gate
   is a failure. Do not soften, hide, or bypass it to finish. Report it and, if
   in scope, fix it.
7. **Minimize the change scope.** Change only what the driving task requires.
   Leave no
   unrelated edits (formatting, renames, refactors, build artifacts, temp files)
   not requested by the task.
8. **Preserve compatibility with existing specifications.** New or changed
   behavior must not silently break documented contracts, architecture layering,
   or ADRs. If a change is a deliberate compatibility break, it must be an
   explicit, reviewed decision.
9. **Meet the Definition of Done before reporting completion.** Completion is
   only claimed when the task's own Definition of Done (and, where applicable,
   the issue's acceptance criteria) are actually satisfied and verified.
10. **Follow the governing process gates.** The mandatory pre-PR self review
    and the documentation synchronization gate apply to every pull request, and
    the execution/merge orchestration skills define how work is run and merged.
    See [Relationship to other skills](#relationship-to-other-skills).

## Cross-cutting disciplines

These apply across all task types and are worth stating once, here, so they are
not repeated in every skill:

- **Separate fact, inference, and proposal.** When reporting, clearly mark what
  was *observed*, what is *inferred*, and what is *suggested as a proposal*.
  This is especially important in research and review output.
- **Record decisions and their rationale.** When a meaningful choice is made,
  say why, and where appropriate route it to the ADR process (see the self-review
  skill's ADR feedback conditions).
- **Prefer reusing established models and contracts** over creating parallel
  representations.
- **Keep responsibilities in their layer** per the architecture MATRIX / layering
  rules, and keep domain logic independent of UI / presentation concerns.

## Relationship to other skills

| Concern | Owned by |
|---|---|
| Universal rules for all tasks | **this skill** |
| Research task procedure | `research/SKILL.md` |
| Implementation task procedure | `implementation/SKILL.md` |
| Review task procedure / review viewpoints | `review/SKILL.md` |
| Issue creation/update procedure | `issue/SKILL.md` |
| Release task procedure | not created yet (see `skills/README.md`) |
| Pre-PR self-review gate | `skills/common/process/self-review/SKILL.md` |
| Documentation synchronization gate | `skills/common/process/doc-sync/SKILL.md` |
| Parallel issue execution orchestration | `skills/common/process/batch/SKILL.md` |
| Safe PR merge | `skills/common/process/merge/SKILL.md` |

The task-specific skills here, and the gate/execution skills under
`skills/common/process/`, have **distinct, non-overlapping responsibilities**:
- Task skills (this directory) prescribe *how an agent carries out a unit of
  work*.
- Gate skills (`self-review`, `doc-sync`) prescribe *what must hold before a
  PR is opened or work is reported complete*.
- Execution skills (`batch`, `merge`) prescribe *how multiple issues are run
  and how PRs are merged safely*.

These are deliberately not merged into one "super" skill.

## Preconditions

None beyond having a driving task (an Issue, or an explicit request) and access
to the repository. The "driving task" is either a GitHub Issue or an explicit
request; throughout these skills, "Issue" refers to the driving task, and an
explicit request provides the requirements / acceptance criteria in place of an
Issue when no Issue exists. When operating in a worktree created by the Batch
orchestrator, the driving Issue and branch/worktree context are provided by the
orchestrator.

## Standard Procedure (implicit)

There is no standalone Common procedure; the rules here apply continuously
throughout every other skill's procedure. Before finishing any task, run the
[Completion check](#verification) of the relevant task skill.

## Verification

Before reporting completion, confirm:

- [ ] The relevant Architecture SSOT was read and is respected (Rule 1).
- [ ] The driving task's requirements / acceptance criteria are met (Rule 2).
- [ ] The actual git/repository state was inspected, not assumed (Rules 3–4).
- [ ] Every test / build / gate result reported is real and green (Rules 5–6).
- [ ] The diff contains only intentional, requested changes (Rule 7).
- [ ] Compatibility with existing specs / ADRs is intact (Rule 8).
- [ ] The task's Definition of Done is satisfied (Rule 9).
- [ ] The governing process gates (self-review, doc-sync) that apply to the work
      were applied; a gate that does not apply (e.g. self-review / doc-sync for a
      non-PR task) is recorded as not applicable — never skipped when it applies
      to a pull request (Rule 10).

## Definition of Done

The common rules are satisfied for the performed task. (Each task-specific
skill defines its own task-level Definition of Done on top of these.)

## Failure Handling

If any Mandatory Rule cannot be satisfied:

- Do **not** proceed as if satisfied.
- Report the specific unmet rule, the observed evidence, and the blocker.
- For scope creep (Rule 7) or a requirement gap (Rule 2), stop and reconcile
  with the requester / the Issue rather than silently resolving it.
- Do not hide a failure to finish (Rule 6); fail openly and precisely.

## Output / Reporting Requirements

Regardless of task type, the final report must:

- State the observed repository / git state (branch, HEAD, status).
- List the real verification results actually executed.
- Distinguish fact vs. inference vs. proposal.
- Declare the Definition of Done met or unmet, with evidence.
- Reference the driving task (Issue, or explicit request) and any related
  PRs/ADRs.

## Porting to another project

This skill is project-agnostic. It requires a **Project Profile** supplying
repository-specific inputs used by the task skills (authoritative document
paths, architecture MATRIX / SSOT precedence, verification command ladder,
authority ordering, conventions). Copy `skills/common/task/` unchanged and write
a profile, following the host repository's `skills/` conventions.

## Non-goals

- Prescribing any single task type's procedure (done in the sibling task skills).
- Duplicating the pre-PR self-review or documentation-sync gates.
- Encoding project-specific paths or commands into a portable skill (those
  belong in the Project Profile).
- Replacing human judgement or review.

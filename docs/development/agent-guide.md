# Development Agent Guide

**Status:** Stable

**Authority:** Reference

## Purpose

Provide a predictable bootstrap path for AI development agents working on PSXRecompStudio.

## Before changing code

1. Read the repository-level AI guidance when present.
2. Read `docs/README.md`.
3. Read `docs/architecture/README.md`.
4. Identify the relevant subsystem SSOT.
5. Inspect related open Issues and recent implementation PRs.
6. Inspect the relevant code.
7. Check architectural constraints before proposing changes.

## Git Workflow

main ブランチは GitHub Repository Rules により保護されています。直接 push は禁止です。

開発フロー：
1. `git checkout -b feature/your-feature` で feature branch 作成
2. 変更をコミット・push
3. PR 作成 → CI 通過 → レビュー → main へマージ

main への変更は Pull Request 経由でのみ可能です。

## AI bootstrap path

Before working on a task, an AI agent should load the applicable task template
and the Common Skill in addition to the project knowledge:

- `skills/common/task/common/SKILL.md` — project-wide universal rules (respect
  the Architecture SSOT, treat the driving task as the SSOT of the work unit,
  verify actual repository state, do not fabricate test results, never treat
  failure as success, minimize change scope, meet the Definition of Done).
- The task-specific template matching the work type:
  `skills/common/task/research/SKILL.md`,
  `skills/common/task/implementation/SKILL.md`,
  `skills/common/task/review/SKILL.md`, or `skills/common/task/issue/SKILL.md`.
  (A Release task template does not exist yet — see `skills/README.md`.)

These are the reusable work templates (see `skills/README.md`); they complement,
not replace, the gates below and the Batch/Merge execution skills.

### Batch / multi-task execution

When a task involves **any** of the following, read and follow
`skills/common/process/batch/SKILL.md` **before starting implementation**:

- batch processing, or an explicit mention of the Batch Skill;
- two or more Issues or tasks handed over as one unit of work;
- parallel implementation;
- multi-issue or multi-task execution.

That Skill is the single source of truth for batch orchestration and is
Markdown-only — it requires no batch-specific runtime, wrapper, or configuration,
so it applies identically regardless of which agent is executing it. Its
mandatory planning stage (task inventory, dependency analysis, dependency graph,
execution waves, parallel/sequential classification) must be completed before any
implementation begins, **including when only one worker will be used**.

Do not restate or re-derive the batch rules here or in any other agent
entrypoint; route to that Skill instead.

For design-decision work (recording, updating, or validating an ADR), follow the
procedure in `skills/common/process/adr/SKILL.md` (Issue #84), loading
project-specific inputs (ADR directory, inventory, numbering) from the matching
`skills/project/<project>/profile.md`.

At task completion, write the final report following the
`skills/common/process/reporting/SKILL.md` reporting process skill — the
standard format for implementation/verification results (investigation and
SSOT, implementation summary, design decisions, changed files,
targeted/related/full tests, build/analyzer/lint applicability, real
PASS/FAIL/NOT RUN semantics, existing-vs-new failure separation, git
status/diff/commit evidence, remaining work, and Issue/PR state). It forbids
reporting unperformed verification as performed and requires evidence for
"no problems" conclusions.

When creating or rewriting commits, follow
`skills/common/process/commit-message/SKILL.md` for the Conventional Commits
format, type/scope/subject rules, Issue-linkage trailers, and per-commit
change granularity.

## Before creating a pull request

When the change touched implementation, architecture, CI/build/test
infrastructure, developer process or repository policy, policy configuration,
or process artifacts (skills, profiles, agent guides), first run the
documentation synchronization gate defined in
`skills/common/process/doc-sync/SKILL.md`, loading project-specific inputs
from the matching `skills/project/<project>/profile.md`, and record its
update/no-update decisions for the PR body.

Then perform the mandatory pre-PR self review defined in
`skills/common/process/self-review/SKILL.md`, loading project-specific inputs
from the matching `skills/project/<project>/profile.md`. Do not open a PR until
that skill's completion criteria are met.

CodeRabbit is a best-effort automated reviewer, not a repository-owned hard gate.
Its availability, rate limits, skipped/missing/pending status, or absent current-
head review do not by themselves block repository CI or merge. Findings that are
present must still be reviewed appropriately; repository-owned CI and human
approval remain required.

### Pre-merge README candidate (Issue #244)

Since the README Auto-Update bot no longer pushes to the PR head branch (see
`docs/development/readme-autoupdate.md` and ADR-009), a README update for a PR
is delivered as an outstanding candidate comment on that PR, keyed to a head
SHA. Before merging such a PR, either apply the candidate README to the PR head
(that invalidates the stale head-SHA comment) or explicitly decline it. A human
or approved automation performs the apply step; the bot never writes a commit.
This is an operational expectation and is not a hard-failing CI check.

## Authority hierarchy

Prefer information in this order when determining current intent:

1. Current subsystem SSOT.
2. Top-level Architecture SSOT.
3. Current accepted architectural decisions.
4. Open implementation Issues.
5. Current code, interpreted against the documented architecture.
6. Closed Issues and historical discussions.

If code and SSOT disagree, do not silently redefine the architecture. Determine whether the code is incomplete, the documentation is stale, or an architectural decision is intentionally changing.

## Implementation discipline

- Keep domain responsibilities independent from UI concerns.
- Reuse established models and contracts instead of creating parallel representations.
- Preserve architecture and terminology defined by SSOT documentation.
- Prefer structured results over presentation-specific strings.
- Add or update tests when behavior or contracts change.
- Update SSOT documentation when an implementation establishes or changes an architectural decision.

## Completion check

Before reporting completion, verify:

- The implementation matches the relevant SSOT.
- No documented constraint was violated.
- Relevant tests/analyzers pass.
- Documentation was updated when required.
- The working tree/branch contains only intentional changes.
- The final report identifies commits, tests, and remaining limitations.

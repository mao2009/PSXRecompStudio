# AGENTS

Entrypoint for AI development agents working on PSXRecompStudio.

This file contains **routing only**. It does not define any process. Every rule
lives in the document it points to, so that all agents — Codex, Claude Code,
OpenCode, or any other — follow the same single source of truth rather than a
per-agent copy of it.

## Start here

1. [`docs/development/agent-guide.md`](docs/development/agent-guide.md) — the
   bootstrap path: what to read before changing code, the Git workflow, the
   authority hierarchy, and the pre-PR gates.
2. [`docs/README.md`](docs/README.md) — documentation map.
3. [`docs/architecture/README.md`](docs/architecture/README.md) — Architecture
   SSOT.
4. [`skills/README.md`](skills/README.md) — the skill index.

## Task routing

Read the skill that matches the work **before** starting it.

| When the task is… | Read and follow |
|---|---|
| **Batch processing, two or more Issues/tasks as one unit of work, parallel implementation, multi-issue execution, or an explicit mention of the Batch Skill** | [`skills/common/process/batch/SKILL.md`](skills/common/process/batch/SKILL.md) |
| Merging a PR | [`skills/common/process/merge/SKILL.md`](skills/common/process/merge/SKILL.md) |
| Implementing a single task | [`skills/common/task/implementation/SKILL.md`](skills/common/task/implementation/SKILL.md) |
| Research or investigation | [`skills/common/task/research/SKILL.md`](skills/common/task/research/SKILL.md) |
| Reviewing | [`skills/common/task/review/SKILL.md`](skills/common/task/review/SKILL.md) |
| Authoring or updating an Issue | [`skills/common/task/issue/SKILL.md`](skills/common/task/issue/SKILL.md) |
| Recording a design decision (ADR) | [`skills/common/process/adr/SKILL.md`](skills/common/process/adr/SKILL.md) |
| Before opening a PR | [`skills/common/process/doc-sync/SKILL.md`](skills/common/process/doc-sync/SKILL.md), then [`skills/common/process/self-review/SKILL.md`](skills/common/process/self-review/SKILL.md) |
| Writing a commit message | [`skills/common/process/commit-message/SKILL.md`](skills/common/process/commit-message/SKILL.md) |
| Writing the final report | [`skills/common/process/reporting/SKILL.md`](skills/common/process/reporting/SKILL.md) |

Project-wide universal rules that apply to every task:
[`skills/common/task/common/SKILL.md`](skills/common/task/common/SKILL.md).

## Batch execution

Batch orchestration is the case most often missed, so it is called out
explicitly.

If the work involves more than one Issue or task, or parallel implementation, or
the Batch Skill is named, then
[`skills/common/process/batch/SKILL.md`](skills/common/process/batch/SKILL.md)
governs. Read it **before starting implementation**. That Skill defines its own
preconditions, worker rules and execution semantics; they are deliberately not
restated here.

## Rules for this file

- Routing only. Do not copy process rules into this file.
- Do not create per-agent variants of a process. One skill, one SSOT, reached
  from here.

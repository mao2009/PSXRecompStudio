# Batch Orchestration

**Status:** Stable

**Authority:** Reference — the specification is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md)

This is an orientation page for the Markdown-only, agent-agnostic Batch Skill.
It intentionally does not duplicate the evidence bar, the workflow, the result
vocabulary or the outcome rules. The normative entrypoint and single source of
truth is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md).

## Specification

| Document | Contents |
|---|---|
| [`SKILL.md`](../skills/common/process/batch/SKILL.md) | Normative SSOT: applicability, definitions, evidence classification, MUST/MUST NOT rules, the workflow, dependency and parallel-safety rules, result vocabulary and batch outcome, gates and the Merge Skill boundary, aggregate verification, cleanup, fail-closed rules, reporting contract |
| [`references/worker-and-isolation.md`](../skills/common/process/batch/references/worker-and-isolation.md) | Worker abstraction and mechanism selection, task inventory, pre-dispatch checks, worktree/branch strategy, concurrency, git safety, worker result reporting and validation, semantic conflict detection, cleanup |
| [`references/failure-and-recovery.md`](../skills/common/process/batch/references/failure-and-recovery.md) | Failure classification, retry policy and budget, non-delivering workers, dependent handling, integration failure, resume and reconciliation |
| [`references/examples.md`](../skills/common/process/batch/references/examples.md) | Worked conformance scenarios |

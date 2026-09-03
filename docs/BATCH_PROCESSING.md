# Batch Orchestration

**Status:** Stable

**Authority:** Reference — the specification is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md)

This is an orientation page for the Markdown-only, agent-agnostic Batch Skill.
It intentionally does not duplicate lifecycle, state/result, stop-condition,
or aggregation semantics. The normative entrypoint is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md).

## Specification

| Document | Contents |
|---|---|
| [`SKILL.md`](../skills/common/process/batch/SKILL.md) | Normative entrypoint: applicability, MUST/MUST NOT rules, lifecycle, invariants, fail-closed rules |
| [`references/orchestration.md`](../skills/common/process/batch/references/orchestration.md) | Phase contracts, state models, integration ordering, aggregate verification, reporting |
| [`references/dependency-analysis.md`](../skills/common/process/batch/references/dependency-analysis.md) | Inventory, dependency model, DAG, wave construction, parallel safety |
| [`references/worker-contract.md`](../skills/common/process/batch/references/worker-contract.md) | Preflight, worker abstraction, dispatch/output contracts, validation, semantic conflicts |
| [`references/git-worktree.md`](../skills/common/process/batch/references/git-worktree.md) | Isolation, worktree/branch strategy, concurrency, git safety, cleanup |
| [`references/review-and-gates.md`](../skills/common/process/batch/references/review-and-gates.md) | Review gate, approval gate, merge delegation, Issue lifecycle safety |
| [`references/failure-recovery.md`](../skills/common/process/batch/references/failure-recovery.md) | Failure classification, retry, recovery, resume |
| [`references/examples.md`](../skills/common/process/batch/references/examples.md) | Worked conformance scenarios |

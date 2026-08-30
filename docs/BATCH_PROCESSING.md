# Batch Orchestrator

Processes multiple independent issues in one batch: sub-agents run
concurrently in isolated worktrees, then their PRs are merged serially
through an approval gate. See skills/common/process/batch/SKILL.md.

## Capabilities

- **Parallel Issue execution**: independent issues run concurrently, bounded by max_parallel_subagents (default 3)
- **Dependency DAG scheduling**: issues form a DAG; each wave starts once its dependencies complete; cycles block the batch
- **Retry with exponential backoff**: retryable failures retry with `base * 2^retry + jitter` until max_retries (default 3)
- **Approval-gated merge**: PRs merge one at a time, rebased onto latest main, after SHA-bound approval (no --admin)
- **Worktree isolation**: each issue runs in its own git worktree/branch; one failure does not block unrelated issues

## Lifecycle

```text
    Submit Batch
         |
         v
    DAG Resolution (cycle check)
         |
         v
    Parallel Waves (worktree per issue)
         |
         v
      +---+   +---+   +---+
      | A |   | B |   | C |     A & C run; B waits on A
      +---+   +---+   +---+
         |      |      |
         +------+------+
        (retry w/ backoff)
         |
         v
    Serial Merge Queue (approval gate)
         |
         v
    Merge to main -> cleanup
```

## Configuration

Batch behavior is set in `config/batch-config.json` (concurrency,
retries, checkpoint/resume). Interrupted batches can be resumed from
persisted batch/worker checkpoints.
# Batch Orchestrator

## Overview

Manages multi-step workflows across independent work items with coordination, failure handling, and safe integration.

## Capabilities

- **Parallel Execution**: Multiple independent tasks run concurrently.
- **Dependency Scheduling**: Tasks declare dependencies; scheduler resolves order and parallelizes where possible.
- **Retry with Backoff**: Failed tasks retry with exponential backoff to prevent cascade failures.
- **Approval-Gated Merge**: Completed work requires explicit approval before merging to main.
- **Worktree Isolation**: Each task executes in its own git worktree, preventing conflicts.

## Lifecycle

```
  +------------------+
  |   Submit Batch   |
  +--------+---------+
           v
  +------------------+
  | Dependency Graph |
  |    Resolution    |
  +--------+---------+
           v
  +------------------+
  | Parallel Exec    |
  | (Worktree Each)  |
  +--------+---------+
           v
     +-----+-----+
     v     v     v
  +----+ +----+ +----+
  | T1 | | T2 | | T3 |
  +----+ +----+ +----+
     +-----+-----+
           v
  +------------------+
  | Retry on Fail    |
  | (Exponential BO) |
  +--------+---------+
           v
  +------------------+
  | Approval Gate    |
  +--------+---------+
           v
  +------------------+
  | Merge to Main    |
  +------------------+
```

## Configuration

Batch jobs are defined declaratively with task definitions, dependency edges, retry policies, and approval requirements.

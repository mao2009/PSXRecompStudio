---
name: handoff-checkpoints
description: >
  Durable Issue/PR checkpoints for agent work that may cross machines,
  worktrees, sessions, or AI providers. Read at task start and throughout
  long-running investigations and implementations.
version: 0.1.0
scope: process
platform: agent-agnostic
---

# Durable Agent Checkpoints and Handoff

## Purpose

Keep work restartable using the repository and GitHub Issues / Pull Requests,
without relying on a previous agent's chat context, memory, or local machine.
This skill supplements (not replaces) task, git-workflow, reporting, batch,
and review skills. Respect their ownership and existing gates.

## Mandatory workflow

1. **On start:** read the driving Issue (if one exists), related PRs, and the
   latest checkpoint; compare their claims with actual git/worktree state.
   For an Issue-backed or PR-backed task, post a concise start checkpoint
   identifying the objective, base/head branch and SHA, environment, available
   evidence, and planned next step. When an explicit request has no Issue or PR,
   do not invent an Issue or post in an unrelated thread: keep the checkpoint
   in the work report until a suitable PR or Issue exists, then publish the
   relevant handoff there. Do not assume a past claim is verified locally.
2. **After each meaningful finding or completed milestone:** when an Issue or PR
   exists, post a checkpoint to the driving Issue for cross-cutting
   investigation/progress, or to the PR for implementation/review evidence.
   Otherwise, accumulate the evidence in the work report for later publication. Link between them when applicable.
   Record reproducible commands, input revisions/hashes, observed results,
   decisions, and concrete next actions. Clearly distinguish observed facts,
   inferences, and proposals.
3. **During long-running work:** at approximately 30-minute intervals when
   practical, or at natural phase boundaries, post a compact status update.
   Avoid duplicate/no-information comments; consolidate where possible.
   Never allow a timer to interrupt a critical operation or claim an
   unobserved result. For parallel workers, the coordinator owns the
   consolidated checkpoint and identifies each worker's branch/status.
4. **Before stopping, blocking, context exhaustion, or handoff:** publish a
   final HANDOFF CHECKPOINT to the relevant Issue or PR, if one exists, with
   current SHA, pushed/unpushed state, tests, blockers, artifact locations,
   and an executable next step. Otherwise include it in the work report and
   publish it when a suitable tracked work item becomes available. If the stop
   is abrupt and posting is impossible, the next agent must reconstruct state
   and explicitly mark missing evidence.
5. **On resumption:** read the latest handoff before rerunning anything.
   Reuse verifiable evidence and repeat only necessary checks. Never erase
   or overwrite another agent's worktree, logs, or uncommitted changes.

## Checkpoint format

Use the following fields, omitting only genuinely inapplicable fields:

```markdown
### HANDOFF CHECKPOINT — YYYY-MM-DD HH:MM UTC
- Driving Issue / PR:
- Objective and scope:
- Environment / dependencies:
- Branch, base SHA, HEAD SHA:
- Worktree status; committed, pushed, and unpushed work:
- Completed since previous checkpoint:
- Verified evidence (commands, inputs/hashes, observed outputs):
- Tests / gates: PASS | FAIL | NOT RUN | UNKNOWN (with reasons):
- Current blocker / uncertainty:
- Decisions and rationale:
- Logs / reproducible artifacts (durable URLs or repo-relative paths):
- Next actions (ordered, executable):
- Do not repeat / warnings:
```

## Portability and safety

- A GitHub comment is a progress snapshot, **not** authority over code,
  architectural SSOT, acceptance criteria, or current repository state.
- Prefer reproducible small logs or committed, non-sensitive fixtures over
  machine-local absolute paths. Include hash, source revision, and commands
  needed to regenerate large artifacts. Do not commit secrets, copyrighted
  ROMs/discs, private data, or oversized traces.
- Follow the git-workflow skill for WIP commits and pushes. Do not force-push,
  merge, or modify another agent's branch/worktree merely to create a
  checkpoint. An unpushed local change is **not** portable; explicitly
  identify it as such.
- If GitHub posting fails, preserve the intended checkpoint in the final
  report and, when safe, in a repository-relative Markdown artifact or WIP
  commit following git-workflow rules. State explicitly that remote persistence
  failed and provide the exact unpushed branch/commit or local location.
  A local artifact alone is **not** a cross-environment handoff: the next agent
  needs access to that artifact or a later successful push/post. Retry remote
  publication when available. Never report a failed post as completed.
- Prefer actionable changes in understanding over noisy commentary.
- Completion reporting remains governed by the reporting skill; a handoff
  checkpoint does not imply successful tests, review, or merge readiness.

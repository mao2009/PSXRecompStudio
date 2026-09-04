# CLAUDE.md

Follow [`AGENTS.md`](AGENTS.md).

It is the shared entrypoint for every agent working on this repository — Codex,
Claude Code, OpenCode, or any other — and carries the routing to the skill that
governs each kind of task.

This file deliberately contains no rules of its own. Duplicating guidance here
would create a Claude-specific fork of a process that is meant to have exactly
one source of truth.

For any commit message creation or rewrite, read and follow
[`skills/common/process/commit-message/SKILL.md`](skills/common/process/commit-message/SKILL.md).

Most easily missed: if the work involves **more than one Issue or task, parallel
implementation, or the Batch Skill is named**, read and follow
[`skills/common/process/batch/SKILL.md`](skills/common/process/batch/SKILL.md)
before starting implementation.

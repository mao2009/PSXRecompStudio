# Batch Orchestration Phase 0 Design

**Status:** Retired

This historical Phase 0 design described a runtime implementation for batch
orchestration. PR #243 adopts the Markdown-only Batch Skill, so the former
runtime, wrapper, and configuration design is no longer an implementation
contract and is not retained here.

The current normative source is
[`skills/common/process/batch/SKILL.md`](../skills/common/process/batch/SKILL.md).
Its detailed lifecycle, state/result model, dependency and wave rules, worker
contract, recovery rules, isolation rules, gates, and conformance scenarios are
linked from that entrypoint.

# Title execution handoff contract

`ExecutionOrchestrator` consults `ITitleExecutionHandoff` when a completed segment ends at a guest PC that the current engine cannot continue from.

`TitleExecutionHandoffResult.ReturnValue` is meaningful only for `TitleExecutionHandoffAction.ContinueAt`. When a `ContinueAt` decision supplies a value, the orchestrator writes that value to guest register V0 before resuming at `NextPc`.

For `Exit`, `Return`, and `Pause`, `ReturnValue` is intentionally ignored. Those actions terminate the current orchestration result instead of resuming guest execution, so there is no continuation state into which a synthetic V0 value needs to be folded. Callers should use the corresponding `Exit()`, `Return()`, and `Pause()` factories rather than constructing those actions with a `ReturnValue`.

# Worker Contract

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Defines preflight validation, the worker abstraction, the dispatch input
contract, the required worker output, result validation, and semantic conflict
detection.

## 1. Preflight validation

Preflight runs **per task, before any worker is dispatched**. Its purpose is to
guarantee that a dispatched task is genuinely executable, so that the result
means something.

### 1.1 Required conditions

| # | Condition | On failure |
|---|---|---|
| 1 | The task's Issue exists | `BLOCKED` |
| 2 | The Issue is open | `BLOCKED` |
| 3 | No existing implementation PR for the Issue | `BLOCKED` |
| 4 | No conflicting active PR touching the same change surface | `BLOCKED` |
| 5 | The target functionality is not already present in the base | `NO_OP` — see §1.3 |
| 6 | The intended branch does not already exist | `BLOCKED` |
| 7 | The intended worktree path is free | `BLOCKED` |
| 8 | The task has not already been processed in this batch | `BLOCKED` |
| 9 | The working base is the expected base revision, and that revision is recorded | `BLOCKED` |

### 1.2 Preflight rules

- A condition that **cannot be evaluated** is a failed condition. Preflight
  never passes on an unverifiable check.
- A failed preflight produces task state `BLOCKED` and task result `BLOCKED`,
  never `NO_OP` and never
  `FAILED`. It is a *task* result: what it means for the batch as a whole is
  decided by the aggregation rule
  ([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)),
  never by the preflight itself.
- The exact failing condition MUST be recorded and reported verbatim.
- A `BLOCKED` task requires an **explicit operator decision** before it can be
  retried. The orchestrator MUST NOT clear a preflight block on its own
  judgement.
- The base revision used for the batch MUST be recorded, so that results can
  later be interpreted against a known starting point.

### 1.3 Already-implemented is not success

Condition 5 exists to prevent the most damaging false positive: a batch that
"passes" because the work was already done before it started.

- Detected **before dispatch**: the task takes task state `COMPLETED` and task
  result `NO_OP`. It never runs and receives no isolation artifacts.
- Detected **by the worker during execution**: after validation, the task takes
  task state `COMPLETED` and task result `NO_OP`.

Neither is `SUCCESS`. Neither closes an Issue. Neither counts toward a passing
batch. See [`../SKILL.md`](../SKILL.md#task-result-vocabulary).

## 2. Worker abstraction

> **Worker** = an independent, delegated execution unit with its own context and
> its own isolated worktree and branch.

This definition is deliberately mechanism-free. How a worker is realized is
decided entirely by the executing agent's native capability, and **no particular
mechanism is normative**.

Valid realizations include, without preference:

- a native delegated task or sub-agent facility,
- a separate agent session or context,
- a separate process,
- the same context executing tasks one at a time against separate isolated
  worktrees.

### 2.1 Prohibited normative dependencies

This Skill MUST NOT require, and MUST NOT be written against:

- any specific agent's sub-agent or task API,
- any specific CLI being installed or present on `PATH`,
- any specific provider, vendor, or product,
- any specific operating system, shell, or language runtime.

The presence of a tool on `PATH` is **not** configuration and never selects a
worker mechanism. Mechanism selection is an explicit operator or agent decision.

### 2.2 Mechanism selection

| Priority | Mechanism | Condition |
|---|---|---|
| 1 | The executing agent's native delegated-execution capability | Available |
| 2 | An explicitly configured external mechanism | Configured explicitly by the operator |
| 3 | Same-context serialized execution with full isolation | No delegated capability available |

Rules:

- A mechanism switch is **not a retry**. Retries stay within the selected
  mechanism. A task that failed under one mechanism is not re-attempted under
  another to make it pass.
- The selected mechanism MUST be recorded in the report.
- Priority 3 is a legitimate outcome and preserves all batch semantics — it is
  **not** permission to abandon them. See
  [`../SKILL.md`](../SKILL.md#silent-sequential-fallback-is-forbidden).

### 2.3 Worker obligations

Every worker, however realized, MUST:

1. Work only within its assigned worktree and branch.
2. Investigate the task and the relevant architecture/SSOT before changing code.
3. Implement only the assigned task — no scope expansion into other tasks.
4. Run the task's verification.
5. Report its result in the required output form (§3.2), including honest
   failure.
6. Stop and report rather than work around a blocking condition.

Every worker MUST NOT:

1. Touch another worker's worktree or branch.
2. Modify the shared default branch.
3. Merge, force push, or push directly to the default branch.
4. Close the Issue it is working on.
5. Report unperformed verification as performed.
6. Report a partial or failed implementation as `SUCCESS`.

## 3. Dispatch and output contracts

### 3.1 Dispatch input (orchestrator → worker)

Every field below is present in every dispatch. A dispatch missing any field is
invalid and MUST NOT be sent; the task becomes `BLOCKED`. `issue_number` is
**conditional** and follows the same representation rule as the output contract
(§3.2): when the task has no Issue, the field is present with the value `none`,
never omitted.

| Field | Always required | Content |
|---|---|---|
| `task_id` | yes | Stable task identifier |
| `issue_number` | **conditional** | The Issue being implemented, when the task has one |
| `worktree_path` | yes | The worker's exclusive worktree |
| `branch_name` | yes | The worker's exclusive branch |
| `base_revision` | yes | The revision the worktree was created from |
| `objective` | yes | What "done" means for this task |
| `scope` | yes | The permitted change surface, and what is explicitly out of scope |
| `required_skills` | yes | Process skills the worker must follow |
| `verification` | yes | The verification the worker must run and report |
| `result_contract` | yes | A pointer to §3.2 |

### 3.2 Worker output (worker → orchestrator)

Every field below is **present** in every result. Missing or unparseable fields
make the result invalid. Three fields carry a value only for certain
classifications; those are marked **conditional** and are covered by the
representation rule beneath the table.

| Field | Always required | Content |
|---|---|---|
| `task_id` | yes | Must match the dispatched `task_id` |
| `classification` | yes | Exactly one of `SUCCESS`, `NO_OP`, `BLOCKED`, `FAILED` |
| `investigation_summary` | yes | What was investigated, including the SSOT consulted |
| `implementation_summary` | yes | What was changed; for `NO_OP`, the evidence that it was already present |
| `design_decision` | yes | Why this approach; the alternatives rejected |
| `changed_files` | yes | The actual changed paths; `[]` for `NO_OP` |
| `test_results` | yes | Verification actually executed, with real `PASS` / `FAIL` / `NOT RUN` outcomes |
| `branch` | yes | The branch the work is on |
| `remaining_work` | yes | Anything not completed; `none` if nothing |
| `issue_number` | **conditional** | The Issue being implemented, when the task has one |
| `commit_sha` | **conditional** | The resulting commit, when changes were made — so for `SUCCESS`, and for any classification that produced a commit |
| `failure_reason` | **conditional** | The exact condition; carries a value for `BLOCKED` and `FAILED` |

**Representation of a conditional field that does not apply.** The field is
present and its value is the explicit marker `none`. It is **never omitted**, and
never left empty. Absence therefore always means a malformed result, and a
validator never has to distinguish "the worker had nothing to report here" from
"the worker forgot this field" — the same reason an inventory entry records why a
value could not be established rather than silently emptying it
([`dependency-analysis.md` §2](dependency-analysis.md#2-task-inventory)).

This gives each classification exactly one valid shape: a `NO_OP` carries
`changed_files: []`, `commit_sha: none` and `failure_reason: none`; a `BLOCKED`
or `FAILED` carries a real `failure_reason`; a `SUCCESS` carries a real
`commit_sha` and a non-empty `changed_files`. Section
[4.1](#41-structural-validation) checks exactly that.

## 4. Worker result validation

Validation runs on **every** result, including results reporting `SUCCESS`. A
worker's self-assessment is an input to validation, never its conclusion.

### Classification order

Exactly one classification path applies to any worker outcome. The steps below
run **in order**, and the first step that applies decides the outcome. No
outcome is classified twice, and no outcome is both a retryable orphan and a
terminal validation failure.

| # | Step | Question | Outcome when it applies |
|---|---|---|---|
| 1 | Worker delivery | Did the worker stop without delivering a parseable result — no output at all, or output that cannot be parsed into the form of §3.2? | Worker delivery state `ORPHANED`, retried within budget when its cause is retryable ([`failure-recovery.md` §6.3](failure-recovery.md#63-orphan-handling)). Steps 2–4 do **not** run |
| 2 | Structural validation | Is the delivered result complete and well-formed? (§4.1) | If not: invalid → terminal `FAILED` |
| 3 | Substantive validation | Do the result's claims match observable reality? (§4.2) | If not: invalid → terminal `FAILED` |
| 4 | Semantic validation | Does the result conflict with a peer result, or can that not be determined? (§5) | Terminal task state `BLOCKED` with task result `BLOCKED`; not integration-eligible. **Not** `FAILED` — the result itself passed steps 2–3 |

Step 1 is a **pre-validation** step, and it is exclusive:

- A worker that never delivered a parseable result is `ORPHANED`, never `FAILED`
  by validation — there is nothing to validate.
- A result that *was* delivered and *is* parseable is never `ORPHANED`. Missing
  required fields, unmet classification requirements, or claims contradicted by
  the repository are validation failures under steps 2–3 and are terminal
  `FAILED`. They are not re-dispatched as orphans.
- **A delivered `classification` is an input to these steps, not a shortcut past
  them.** A worker that reports `SUCCESS`, `NO_OP`, `BLOCKED` or `FAILED` has
  stated a claim; the terminal task result is what steps 2–4 conclude about that
  claim. Until they have run, the task holds no terminal task result and stays in
  task state `RESULT_READY` — which is why a batch that stops before validating a
  delivery classifies it `BLOCKED` rather than adopting the worker's word for it
  ([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path), step 4).

### 4.1 Structural validation

1. All required output fields are present and parseable.
2. `task_id` matches the dispatched task.
3. `classification` is one of the four permitted values.
4. Every field of §3.2 is present, including the conditional ones. Fields that
   carry a value for this classification do so (`failure_reason` for
   `BLOCKED`/`FAILED`; `changed_files` and `commit_sha` for `SUCCESS`), and
   fields that do not apply carry the marker `none` rather than being omitted.
5. No unknown or unexpected fields are silently accepted.

### 4.2 Substantive validation

1. The claimed changed files actually differ on the worker's branch.
2. The changes lie within the dispatched `scope`. Out-of-scope changes fail
   validation.
3. The branch contains the claimed commit, built on the dispatched
   `base_revision`.
4. The reported verification is consistent with the observable result — a
   `SUCCESS` claiming passing tests that were never run fails validation.
5. `NO_OP` is substantiated by evidence that the functionality was already
   present, not merely by an absent diff.

### 4.3 Validation outcomes

Each row names the step of the [classification order](#classification-order)
that produced it. The first step that applies decides, and no result is
classified twice.

| Outcome | Step | Consequence |
|---|---|---|
| Valid and `SUCCESS` | 2–3 pass | Integration-eligible, subject to the gates |
| Valid and `NO_OP` | 2–3 pass | Not integration-eligible; operator decision required on the Issue |
| Valid and `BLOCKED` / `FAILED` | 2–3 pass | Not integration-eligible; see [`failure-recovery.md`](failure-recovery.md) |
| **Structurally or substantively invalid** | 2 or 3 | **Not integration-eligible.** Task state `FAILED` with task result `FAILED`, with the validation failure as the reason |
| **Structural or substantive validation inconclusive** | 2 or 3 | **Not integration-eligible.** The result could not be established as valid, so it is treated as invalid: task state `FAILED` with task result `FAILED`, recording exactly what could not be established |
| **Semantic conflict found, or undeterminable** | 4 | **Not integration-eligible.** Task state `BLOCKED` with task result `BLOCKED`, recording the conflict or what could not be determined ([§5.3](#53-outcomes)) |

The last two rows are deliberately different, and which one applies is decided by
*which step* was inconclusive — never by how uncertain the orchestrator feels.

- **Steps 2–3 ask whether the result itself is sound.** "No" and "cannot be
  established" are both a refusal to certify it, the defect is internal to a
  result the orchestrator MUST NOT repair, and no operator decision would change
  what the result contains. Both are terminal `FAILED`.
- **Step 4 asks whether the result can coexist with something outside itself.**
  An unresolved or undeterminable semantic conflict says nothing against the
  result — it may be perfectly valid — and resolving it requires a decision only
  an operator can make. That is terminal `BLOCKED`, matching
  [§5.3](#53-outcomes) and the `RESULT_READY → BLOCKED` edge in
  [`orchestration.md` §3.2](orchestration.md#32-task-state).

This is the same split [`orchestration.md` §3.2](orchestration.md#32-task-state)
draws between its two rejection edges: `RESULT_READY → FAILED` for a defective
result, `RESULT_READY → BLOCKED` for a valid result blocked by something outside
itself. Uncertainty is never resolved upward in either case — a result that
cannot be shown valid is not thereby valid, and a conflict that cannot be ruled
out is not thereby absent
([`../SKILL.md`](../SKILL.md#fail-closed-rules)).

The orchestrator MUST NOT repair a result to make it valid. Repairing a worker's
output would make the orchestrator the implementer and destroy the attribution
that makes per-task validation meaningful.

## 5. Semantic conflict detection

Textual merge cleanliness is not correctness. Two results can merge without any
git conflict and still be mutually incoherent. Semantic conflict detection is a
separate, required check.

### 5.1 When it runs

- After validation, before integration.
- Again before **each** integration, against the base as it stands at that
  moment — every prior integration in the batch has moved the base.

### 5.2 What it checks

| Check | Conflict indicator |
|---|---|
| Shared contract | Two results change the same interface, schema, or contract in incompatible ways |
| Shared consumer | One result changes a contract another result's code still consumes in its old form |
| Duplicate implementation | Two results independently introduce the same capability |
| Behavioural override | One result's change silently negates another's |
| Shared configuration or generated artifact | Two results change the same key or regenerate the same artifact differently |
| Verification interference | One result's change causes another's verification to no longer hold on the integrated base |

### 5.3 Outcomes

| Determination | Action |
|---|---|
| No semantic conflict | Integration may proceed |
| Semantic conflict found | **Stop integration** for the affected results; report both tasks and the conflict |
| **Cannot be determined** | **Stop integration** |

A detected semantic conflict is **not** resolved by the orchestrator editing the
results. It is reported and returned to the affected tasks, or escalated for an
operator decision.

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`dependency-analysis.md`](dependency-analysis.md) — planning and overlap
- [`orchestration.md`](orchestration.md) — phase contracts and state model
- [`git-worktree.md`](git-worktree.md) — isolation provisioning
- [`review-and-gates.md`](review-and-gates.md) — gates after validation
- [`failure-recovery.md`](failure-recovery.md) — handling invalid and failed results

# Worked Scenarios

**Status:** Stable

**Authority:** Reference — explanatory detail for
[`../SKILL.md`](../SKILL.md), which is the SSOT. Nothing here overrides a rule
stated there.

These are the protocol's conformance cases. An agent reading only
[`../SKILL.md`](../SKILL.md) and this file should reach the same answers.

For each case, only six questions are asked:

1. May the work execute?
2. Parallel or serial?
3. What is each task result?
4. Is anything merge-eligible?
5. What is the batch outcome?
6. What must the report say?

There is no progress state to trace.

---

## Scenario A — Three independent tasks

**Given:** Tasks A, B, C. No dependency between any pair. Change surfaces are
known, disjoint, and every parallel-safety dimension is CONFIRMED for every
pair.

**May execute:** yes.

**Parallel or serial:** **the same wave** — `Wave 1: [A, B, C]`.

Sharing a wave permits concurrent *execution*. It never permits concurrent
*integration*: all three still get separate worktrees and branches, three
independent validations, and serial, one-at-a-time integration. With the default
concurrency limit of 3 all three may run at once; at a limit of 2 the wave
dispatches as `[A, B]` then `[C]` — same wave, same guarantees, smaller groups.

**Task results:** `SUCCESS` each, if each is implemented, validated and
integrated.

**Merge eligibility:** each becomes eligible on its own, one at a time, after
its own gates.

**Batch outcome:** `SUCCESS` by rule 7, and only if aggregate verification also
passes.

**Report:** the wave assignment, and which dimensions were CONFIRMED for each
pair.

---

## Scenario B — A dependency

**Given:** `A -> B` (B depends on A). C is independent of both.

```text
Graph:   A → B
         C
Cycle check: none
```

**May execute:** yes.

**Parallel or serial:** `Wave 1: [A, C]`, `Wave 2: [B]`. B cannot join wave 1 —
its dependency is unsatisfied. C has no dependency and does not overlap A, so it
joins A.

**When wave 2 starts:** once every member of wave 1 holds a task result, and
every wave-1 result that was going to be integrated has been integrated. A
*validated* A is not enough. B is provisioned from the base as it stands at that
moment, so dispatching B while A is validated but unmerged would build and
verify B against a base that does not contain its own premise.

C is not B's prerequisite, but C is a wave-1 member, so C must also reach a task
result before wave 2 starts. Any result does — `SUCCESS`, `NO_OP`, `BLOCKED` or
`FAILED`. What wave 2 may not do is start while C is still undetermined.

**If A does not reach `SUCCESS`** — for any non-`SUCCESS` result, `FAILED`,
`BLOCKED` and `NO_OP` alike — B is `BLOCKED`, naming A. B is not attempted
anyway and not dropped from the report. C is unaffected.

**Batch outcome in that case:** `FAILED` if A's result is `FAILED` (rule 2),
`BLOCKED` if A's result is `BLOCKED` or `NO_OP` (rule 3). C's `SUCCESS` never
lifts the batch to `SUCCESS`.

---

## Scenario C — Uncertain overlap

**Given:** A and B may modify the same area. Whether they actually conflict
cannot be established.

**May execute:** yes.

**Parallel or serial:** **serial** — `Wave 1: [A]`, `Wave 2: [B]`.

**Why:** sharing a wave requires overlap to be established as *absent*
(CONFIRMED), not merely unproven as present. Unverified parallel safety resolves
to serial.

**What is forbidden:** "they probably touch different files, run them together".
"Probably" is not a permitted state.

**Batch outcome:** unaffected by the serialization — decided by the task results
as in any other scenario.

**Report:** the serialization and its reason. A reviewer must be able to see that
this was a deliberate safety decision and check whether it was warranted. Running
serially because safety could not be proven is a **correct** outcome, not a
shortfall.

---

## Scenario D — One worker fails

**Given:** `Wave 1: [A, B, C]`. B fails. A and C return validated `SUCCESS`
results.

**May execute:** A and C are **not** integrated unconditionally. The situation is
re-established first.

1. **Does anything depend on B?** Every dependent of B becomes `BLOCKED`, naming
   B, transitively.
2. **Could A or C depend on B?** No — wave members are pairwise
   dependency-free, so every dependent of B is necessarily in a later wave and is
   covered by step 1.
3. **Semantic safety:** A's and C's results were validated against a plan that
   assumed B's change would exist. Semantic conflict detection is re-run against
   the base as it actually stands.
4. **Gates:** A and C each still face the full gate sequence individually.

**Task results:** B is `FAILED`, its worktree and branch preserved for
diagnosis. A and C are `SUCCESS` if they pass re-evaluation and integrate.

**Merge eligibility:** A and C, one at a time, after re-evaluation. Never B's
partial output.

**Batch outcome:** `FAILED` by rule 2 — one `FAILED` task decides it, regardless
of how many peers succeeded. Aggregate verification still runs over what was
integrated, cleanup still runs, and the report still states every task result.

**Report:** two integrated tasks inside a failed batch are not a partial success.
The report says exactly that. B's failure never closes B's Issue.

**Why this matters:** "B failed, but A and C passed, so ship A and C" is the
tempting and wrong move. A and C were planned and reviewed in a world where B
was expected to land. That assumption must be re-checked, not inherited.

---

## Scenario E — No native parallel worker capability

**Given:** the executing agent cannot run delegated workers concurrently.

**May execute:** yes. **Full batch semantics are preserved.** Only throughput
changes.

The agent MUST still produce the task inventory, the dependency analysis, the
cycle-checked graph, the execution waves and the parallel/sequential
classification; give each task its own worktree and branch; validate each result
independently; integrate serially through the gates; and report per-task
results.

**Parallel or serial:** the effective concurrency limit is 1, so a wave's
members execute one after another rather than at the same time. The waves
themselves are unchanged.

**Batch outcome:** decided by the task results exactly as if the wave had run
concurrently.

**Report:** MUST state that execution was serialized, and why.

**What is forbidden:** concluding "no parallel capability, so this is just
ordinary sequential implementation" and skipping the planning. Isolation,
ordering, validation and gating are independent of concurrency.

---

## Scenario F — A batch is explicitly requested

**Given:** the operator says "use the Batch Skill to process these Issues" and
supplies several Issue numbers.

**May execute:** only after the five mandatory planning artifacts exist —
inventory, dependency analysis, cycle-checked graph, execution waves,
parallel/sequential classification.

**Explicitly forbidden:** a single worker implementing every Issue one after
another, with no inventory, no graph and no waves, reported as "batch completed".
That is the exact failure this protocol exists to prevent: it produces work that
looks like a batch, carries none of a batch's safety properties, and reports
success.

**Note the distinction from Scenario E.** E is about *capability*: parallelism
was unavailable, so execution serialized — legitimate, provided the planning
happened and is reported. F is about *process*: the planning was skipped
entirely. E is a valid batch executed serially. F is not a batch at all.

**If the artifacts cannot be produced:** the batch stops, records the exact
reason, gives every unclassified task `BLOCKED` naming that reason, and still
runs aggregate verification (`NOT RUN`), cleanup and reporting. **Batch outcome
`BLOCKED`** by rule 3. It does not silently degrade into ordinary sequential
implementation.

---

## Additional cases

Each row is decided by the same rules. "Batch outcome" assumes the case is the
only notable thing in the batch; where a batch mixes cases, the ordered outcome
rules ([`../SKILL.md`](../SKILL.md#batch-outcome)) resolve it.

| # | Case | May execute? | Task result | Merge-eligible? | Batch outcome | Report must say |
|---|---|---|---|---|---|---|
| 1 | Work found already present **before dispatch**, CONFIRMED | Not dispatched | `NO_OP` | No | `NO_OP` (rule 6) if every task is `NO_OP` | The evidence it was already present; the operator decision left open; no artifacts were provisioned |
| 2 | Worker runs and finds the work already present, CONFIRMED | Yes | `NO_OP` | No | Same as above | The same, plus the preserved worktree and branch |
| 3 | Whether the work was already done **cannot be CONFIRMED** | Not dispatched | `BLOCKED` | No | `BLOCKED` (rule 3) | The exact condition that could not be established. `NO_OP` is a positive finding, never a fallback |
| 4 | A prerequisite ends non-`SUCCESS` | Dependent not dispatched | Dependent `BLOCKED` | No | `BLOCKED` (rule 3) | The prerequisite named; the dependent stays in the inventory |
| 5 | Dependency graph contains a cycle, or cannot be checked | Batch stops before any dispatch | Every task `BLOCKED` | No | `BLOCKED` (rule 3) | The full cycle path, or why the check could not be performed. Breaking the cycle is an operator decision |
| 6 | Transient failure, retry succeeds within budget | Yes | `SUCCESS` | Yes | `SUCCESS` (rule 7) if all tasks succeed and aggregate verification passes | Every attempt's branch, worktree and base revision; the failure category |
| 7 | Retry budget exhausted | Attempted | `FAILED` | No | `FAILED` (rule 2) | The last failure category and the attempt count |
| 8 | Non-retryable failure category | Attempted | `FAILED` | No | `FAILED` (rule 2) | The category, and that no retry was permitted |
| 9 | Worker delivers a result missing an aspect that applies to it | Attempted | `FAILED` | No | `FAILED` (rule 2) | Exactly what was missing. The orchestrator MUST NOT repair it |
| 10 | Worker delivers nothing readable | Attempted | Retried if the cause is retryable, else `FAILED` | No | `FAILED` (rule 2) if it ends there | That nothing was delivered; the cause and its category |
| 11 | Semantic conflict found, or cannot be determined | Yes | `BLOCKED` | **No** | `BLOCKED` (rule 3) | The conflict, or what could not be determined, and both tasks involved |
| 12 | Approval existence or validity cannot be established | Yes | `BLOCKED` | **No merge** | `BLOCKED` (rule 3) | The condition that could not be established. This is absolute |
| 13 | The Merge Skill returns the merge blocked | Yes | `BLOCKED` | No | `BLOCKED` (rule 3) | The Merge Skill's outcome verbatim; it is authoritative |
| 14 | Every task `NO_OP` | Not dispatched, or dispatched and no change needed | All `NO_OP` | No | `NO_OP` (rule 6) | Aggregate verification `NOT RUN`; nothing integrated; the operator decision on each Issue |
| 15 | Every task `BLOCKED` | No | All `BLOCKED` | No | `BLOCKED` (rule 3) | Each task's exact condition; aggregate verification `NOT RUN` |
| 16 | Some tasks `SUCCESS`, some `NO_OP` | Yes | Mixed | The `SUCCESS` ones | **`BLOCKED`** (rule 5) | Why: each `NO_OP` leaves an operator decision open, so the batch has outstanding work. `NO_OP` is never promoted to `SUCCESS` |
| 17 | Cleanup fails after a confirmed merge | Yes | Unchanged — `SUCCESS` stays `SUCCESS` | Already merged | Unchanged — `SUCCESS` (rule 7) if that is what the results give | The artifact that could not be removed, and its path. Cleanup failure never reverts a merge |
| 18 | Aggregate verification fails | Yes | Possibly all `SUCCESS` | Already merged | **`FAILED`** (rule 1) | The failure against the integrated base, and which tasks are implicated. Nothing is reverted automatically |
| 19 | Nothing was integrated | — | Whatever they are | No | Per the ordered rules | Aggregate verification `NOT RUN`, never as passing; and that the batch produced no integrated change |
| 20 | Recorded batch progress is unreadable on resume | **No** | Unclassified tasks `BLOCKED` | No | `BLOCKED` (rule 3) | That the record could not be read. It is never treated as absent — that would risk duplicate dispatch or duplicate merge |

## Cross-scenario invariants

| Invariant | Applies |
|---|---|
| One worktree and one branch per dispatched task | Every scenario — vacuously where nothing was dispatched |
| Integration is serial, one task at a time | Every scenario |
| Every delivered result is validated before integration | Every scenario |
| A gate that is not CONFIRMED open is closed | Every scenario |
| `NO_OP` is never `SUCCESS` | Every scenario |
| Issues close only via approved merge | Every scenario |
| Every task in the inventory appears in the report | Every scenario |
| Aggregate verification, cleanup and reporting run on every path | Every scenario |

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint and SSOT
- [`worker-and-isolation.md`](worker-and-isolation.md) — isolation, validation, cleanup
- [`failure-and-recovery.md`](failure-and-recovery.md) — failure, retry, resume

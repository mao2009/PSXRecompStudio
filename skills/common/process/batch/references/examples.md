# Worked Scenarios

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Seven decision cases that a compliant execution of this protocol MUST resolve the
same way. They are the protocol's conformance cases: an agent that reads only
this file and [`../SKILL.md`](../SKILL.md) should reach these outcomes.

---

## Scenario A — Three independent tasks

**Given:** Tasks A, B, C. No dependency between any pair. Change surfaces are
known and disjoint.

**Analysis:**

```text
Dependencies:  none
Overlap:       A ∩ B = ∅,  A ∩ C = ∅,  B ∩ C = ∅
```

**Required outcome:** all three in **the same wave**.

```text
Wave 1: [A, B, C]
```

**Why:** wave membership requires dependency-freedom *and* non-overlap
([`dependency-analysis.md` §5](dependency-analysis.md#5-parallel-safety-determination)).
Both hold for every pair.

**Still required:** three separate worktrees and branches, three independent
validations, and serial integration — one at a time. Sharing a wave permits
concurrent *execution*; it never permits concurrent *integration*.

If the concurrency limit is 3 (the default), all three may run at once. If it
were 2, the wave dispatches as `[A, B]` then `[C]` — same wave, same guarantees,
smaller groups.

**Batch outcome:** `SUCCESS` only if all three reach task result `SUCCESS` *and*
aggregate verification passes
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome),
rule 8). Rule 8 is the only route to outcome `SUCCESS`, and it is available to
any scenario whose tasks all succeed — C and E included.

---

## Scenario B — A dependency

**Given:** `A -> B` (B depends on A). C is independent of both.

**Analysis:**

```text
Graph:   A → B
         C
Cycle check: none
```

**Required outcome:**

```text
Wave 1: [A, C]
Wave 2: [B]
```

**Why:** B cannot enter wave 1 — its dependency is unsatisfied. C has no
dependency and does not overlap A, so it joins A in wave 1.

**When wave 2 may start.** Wave 2 begins only once wave 1 has crossed the **wave
barrier** ([`dependency-analysis.md` §6.2](dependency-analysis.md#62-wave-advancement)),
which is more than "A and C stopped running":

The five conditions are **sequential**, not simultaneous: 1 ends wave 1's
execution, 2–3 process what it produced, and 4 is the state that leaves behind.

| # | Condition on wave 1 | Satisfied by |
|---|---|---|
| 1 | Every member has settled — Phase 6's postcondition | A and C have each stopped executing, holding task state `RESULT_READY` or a terminal task state |
| 2 | Every delivered result has been validated and classified | A's and C's results pass [`worker-contract.md` §4](worker-contract.md#4-worker-result-validation) and §5; neither is left in `RESULT_READY` |
| 3 | Every integration-eligible result has been **integrated**, and the re-verifications that integration carries have **settled** | A's authorized merge has landed through the gates and the Merge Skill, and both its pre-merge re-verification against the refreshed base and its post-merge verification have settled |
| 4 | Every member holds a terminal task state — the state 2–3 leave behind | A and C have each moved out of `RESULT_READY` and carry exactly one terminal task result |
| 5 | The next wave's prerequisites have been re-checked | A's task result is confirmed `SUCCESS` before B is provisioned |

Condition 3 is the one that matters for B specifically. A **validated** A is not
enough: B is provisioned from the base as it stands at provisioning time
([`git-worktree.md` §2.2](git-worktree.md#22-base-revision)), so dispatching B
while A sits in `RESULT_READY`, `WAITING_FOR_APPROVAL` or `READY_FOR_MERGE` would
build and verify B against a base that does not contain A's change — the exact
situation the `INTEGRATION → EXECUTION` re-entry rule forbids
([`orchestration.md` §3.1](orchestration.md#31-batch-lifecycle-state)).

C is not B's prerequisite, so no *dependency* of B waits on C. C still holds the
barrier: it is a member of wave 1, and the barrier is a property of the wave
rather than of a single edge, so C must settle, be validated and — being
integration-eligible — be integrated before wave 2 begins.

**If A does not reach `SUCCESS`:** condition 5 catches it at the barrier and B
takes task state `BLOCKED` and task result `BLOCKED`, naming A as the
failed dependency — and this holds for *every* non-`SUCCESS` result A could
carry, `FAILED`, `BLOCKED` and `NO_OP` alike
([`dependency-analysis.md` §3.1.1](dependency-analysis.md#311-propagation-to-dependents)).
B is **not** attempted anyway, and **not** dropped from the report. C is
unaffected and proceeds normally — that is failure isolation
([`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)).

**Batch outcome in that case:** `FAILED` if A's own result is `FAILED` (rule 3
takes precedence), `BLOCKED` if A's result is `BLOCKED` or `NO_OP`
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)).
Either way the batch still runs Phases 9–11 and ends in lifecycle state
`COMPLETED`. C's `SUCCESS` never lifts the batch to outcome `SUCCESS`.

---

## Scenario C — Uncertain overlap

**Given:** A and B may modify the same area. Whether they actually conflict
cannot be established.

**Required outcome:** **sequential**. A and B go in different waves.

```text
Wave 1: [A]
Wave 2: [B]
```

**Why:** the fail-closed rule
([`../SKILL.md`](../SKILL.md#fail-closed-rules)) — parallel safety unknown
resolves to sequential. Sharing a wave requires overlap to be *established as
absent*, not merely *unproven as present*.

**What is forbidden:** reasoning "they probably touch different files, run them
together". "Probably" is not a permitted state.

**Reporting obligation:** the serialization and its reason are recorded in the
plan. A reviewer must be able to see that this was a deliberate safety decision
and check whether it was warranted.

---

## Scenario D — One worker fails

**Given:** Wave 1 = `[A, B, C]`. B fails. A and C return validated `SUCCESS`
results.

**Required outcome:** **A and C are not integrated unconditionally.** The
situation is re-evaluated first.

Re-evaluation:

1. **Does anything depend on B?** Every dependent of B becomes `BLOCKED`, naming
   B. Transitively.
2. **Could A or C depend on B?** No. Wave membership requires the members to be
   pairwise dependency-free, so no member of wave 1 can depend on another
   ([`dependency-analysis.md` §5](dependency-analysis.md#5-parallel-safety-determination)).
   Every dependent of B is necessarily in a later wave and is handled by step 1.
3. **Semantic conflict:** were A's and C's results validated against a plan that
   assumed B's change would exist? Re-run semantic conflict detection against the
   base as it actually stands
   ([`worker-contract.md` §5](worker-contract.md#5-semantic-conflict-detection)).
4. **Gates:** A and C each still face the full gate sequence individually.

If A and C pass re-evaluation, they integrate normally, one at a time. B takes
task state `FAILED` and task result `FAILED`, its worktree and branch
**preserved** for diagnosis.

**Batch outcome:** `FAILED`
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome),
rule 3) — one `FAILED` task decides it, regardless of how many peers succeeded.
The batch nonetheless completes its lifecycle: A and C's integrations are
aggregate-verified, cleanup runs, and the report states outcome `FAILED` against
lifecycle state `COMPLETED`. Two integrated tasks are not a partial success; they
are integrated work inside a failed batch, and the report says exactly that.

**Why this matters:** "B failed, but A and C passed, so ship A and C" is the
tempting and wrong move. A and C were planned, validated and reviewed in a world
where B was expected to land. That assumption must be re-checked, not inherited.

**Also required:** B's partial output is never integrated, and B's failure never
closes B's Issue.

---

## Scenario E — No native parallel worker capability

**Given:** The executing agent has no ability to run delegated workers
concurrently.

**Required outcome:** **Full batch semantics are preserved.** Only throughput
changes.

The agent MUST still:

| Obligation | Status |
|---|---|
| Task inventory | Required |
| Dependency analysis | Required |
| Dependency graph + cycle check | Required |
| Execution waves | Required |
| Parallel/sequential classification | Required |
| One worktree + one branch per task | Required |
| Independent per-task validation | Required |
| Serial, gated integration | Required |
| Per-task reporting | Required |

What changes: the effective concurrency limit is 1, so a wave's members execute
one after another rather than at the same time
([`git-worktree.md` §4](git-worktree.md#4-concurrency-policy)).

The report MUST state that execution was serialized and why.

**What is forbidden:** concluding "no parallel capability, so this is just
ordinary sequential implementation" and skipping the planning stage. Isolation,
ordering, validation and gating are independent of concurrency
([`../SKILL.md`](../SKILL.md#silent-sequential-fallback-is-forbidden)).

---

## Scenario F — Batch explicitly requested

**Given:** The operator says "use the Batch Skill to process these Issues" and
supplies several Issue numbers.

**Required outcome:** The five mandatory planning artifacts are produced
**before** any implementation begins
([`../SKILL.md`](../SKILL.md#mandatory-preconditions)):

```text
1. Task inventory
2. Dependency analysis
3. Dependency graph (cycle-checked)
4. Execution waves
5. Parallel/sequential classification
```

**Explicitly forbidden:** a single worker implementing every Issue one after
another, with no inventory, no graph and no waves, reported as "batch
completed". That is the exact failure this protocol exists to prevent — it
produces work that looks like a batch, carries none of a batch's safety
properties, and reports success.

**Note the distinction from Scenario E.** E is about *capability*: parallelism
was unavailable, so execution serialized — legitimate, provided the planning
happened and is reported. F is about *process*: the planning was skipped
entirely. E is a valid batch executed serially. F is not a batch at all.

If the planning artifacts cannot be produced, the batch records **stop condition
S4** and finishes through the terminating path, which derives batch outcome
`BLOCKED` at `REPORTING` by aggregation rule 2 — absent a rule 1 match — and
reports why (Scenario G). It
does not silently degrade into ordinary sequential implementation.

---

## Scenario G — The batch is blocked before it can execute

**Given:** Tasks A, B, C. Analysis finds `A -> B`, `B -> C` and `C -> A`. The
graph contains a cycle, so Phase 4 cannot produce a cycle-free DAG.

**Required outcome:** **batch outcome `BLOCKED`, batch lifecycle state
`COMPLETED`.** Those are two different fields, and both are required
([`orchestration.md` §3](orchestration.md#3-state-models-and-outcomes)).

What the batch does, in order
([`orchestration.md` §3.6](orchestration.md#36-the-terminating-path)):

| Step | Action |
|---|---|
| Record the condition | Batch-level stop condition S2 of [§3.4](orchestration.md#batch-level-stop-conditions), quoting the cycle path `A -> B -> C -> A`. The outcome itself is derived later, at `REPORTING` |
| Stop dispatching | No wave was built, so no worker is dispatched, and none is dispatched afterwards |
| Stop unsafe integration | Nothing was ever integration-eligible; nothing is integrated |
| Classify pending work | A, B and C each get task state `BLOCKED` and task result `BLOCKED`, naming the cycle as the reason. All three stay in the inventory |
| Phase 9 `VERIFICATION` | Aggregate verification `NOT RUN` — nothing was integrated. Reported as `NOT RUN`, never as passing |
| Phase 10 `CLEANUP` | No isolation was provisioned; the report says so |
| Phase 11 `REPORTING` | Derive the outcome: [§3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome) rule 2 fires on the recorded stop condition, giving `BLOCKED`. Full report: plan section, all three tasks, the outcome, rule 2, and condition S2 |
| Terminal | Batch lifecycle state `COMPLETED`, carrying batch outcome `BLOCKED` |

Rule 2 — not rule 4 — is what fires, and that is the point of its precedence.
The three tasks all carry task state `BLOCKED` and task result `BLOCKED`, so rule
4 would also match; it
would report the cause as "some task was blocked", which says nothing about the
cycle. Rule 2 keeps the stop condition itself as the reported cause.

**Why the batch still traverses Phases 9–11:** the lifecycle records what
happened; the outcome records what came of it. Exiting at Phase 4 would leave
aggregate verification, cleanup and reporting unstated, and a reader could not
tell a clean, deliberate stop from a run that simply died. There is no edge into
`COMPLETED` except from `REPORTING`
([`orchestration.md` §3.1](orchestration.md#31-batch-lifecycle-state)), so this
is not a matter of diligence — the shortcut does not exist.

**What is forbidden:** reporting this batch as `FAILED` (nothing was attempted
and nothing failed), as lifecycle state `BLOCKED` (there is no such lifecycle
state), or as `COMPLETED` with no outcome recorded (an invalid state). Equally
forbidden is dropping the cycle's weakest edge to make the batch runnable —
resolution is an operator decision
([`dependency-analysis.md` §4.4](dependency-analysis.md#44-cycle-detection)).

**The same lifecycle shape applies to every batch-level stop condition**,
whatever raised it: an unresolvable task set (S1), an unresolvable ordering (S3),
missing planning artifacts (S4, Scenario F), or a fail-closed stop that leaves
*the batch as a whole* unable to continue safely (S5). The condition differs; the
lifecycle does not.

**The outcome, however, does not generalize.** S1–S5 reach batch outcome
`BLOCKED` through aggregation rule 2, as here. **S6** — the batch's own execution
violating the model — shares this lifecycle exactly, Phases 9–11 included, but
rule 1 matches it first and the outcome is `FAILED`
([`orchestration.md` §3.4](orchestration.md#batch-level-stop-conditions),
[§3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)).
It is the one stop condition this scenario's outcome does not model, which is
precisely why the outcome is derived from the rules at `REPORTING` and never read
off the stop condition.

**What does *not* take this path:** an ordinary gate stop or a single blocked
task. Those give that task task state `BLOCKED` and task result `BLOCKED` and
leave the rest of the batch
running
([`review-and-gates.md` §10](review-and-gates.md#10-gate-reporting),
[`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)). They
reach the batch outcome through the aggregation rule, not by halting the batch —
which is why Scenario D's batch keeps integrating A and C after B fails.

**Note the contrast with Scenario D.** There, one *task* is `FAILED` while the
batch keeps executing its plan. Here the *batch* is blocked, so nothing further
executes at all. A task result never decides the batch outcome on its own — the
aggregation rule does
([`orchestration.md` §3.5](orchestration.md#35-aggregation--from-task-results-to-the-batch-outcome)).

---

## Cross-scenario invariants

These hold in every scenario above:

| Invariant | Applies |
|---|---|
| One worktree and one branch per **dispatched** task | A, B, C, D, E, F, G — vacuously where nothing was dispatched (G stopped in Phase 4; B's blocked dependent and F's forbidden path are never dispatched). Isolation is provisioned at dispatch, so a task blocked before it has none |
| Integration is serial, one task at a time | A, B, C, D, E, F, G |
| Every result is validated before integration | A, B, C, D, E, F, G |
| An unknown gate is a closed gate | A, B, C, D, E, F, G |
| `NO_OP` is never `SUCCESS` | A, B, C, D, E, F, G |
| Issues close only via approved merge | A, B, C, D, E, F, G |
| Every task appears in the report | A, B, C, D, E, F, G |
| Phases 9–11 run, and the batch ends in lifecycle state `COMPLETED` with exactly one outcome | A, B, C, D, E, G — and F on its compliant path, which is G's |

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`orchestration.md`](orchestration.md) — lifecycle states, batch outcome, terminating path
- [`dependency-analysis.md`](dependency-analysis.md) — waves and parallel safety
- [`worker-contract.md`](worker-contract.md) — validation and semantic conflict
- [`review-and-gates.md`](review-and-gates.md) — gate semantics
- [`failure-recovery.md`](failure-recovery.md) — failure and dependent handling
- [`git-worktree.md`](git-worktree.md) — isolation and concurrency

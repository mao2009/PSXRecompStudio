# Worked Scenarios

**Status:** Stable

**Authority:** Reference — normative detail for
[`../SKILL.md`](../SKILL.md)

Six decision cases that a compliant execution of this protocol MUST resolve the
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
dependency and does not overlap A, so it joins A in wave 1. Wave 2 starts only
after **every** member of wave 1 has reached a terminal result, not merely after
A finished.

**If A does not reach `SUCCESS`:** B becomes `BLOCKED`, naming A as the failed
dependency. B is **not** attempted anyway, and **not** dropped from the report.
C is unaffected and proceeds normally — that is failure isolation
([`failure-recovery.md` §4](failure-recovery.md#4-dependent-task-handling)).

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

If A and C pass re-evaluation, they integrate normally, one at a time. B is
`FAILED`, its worktree and branch **preserved** for diagnosis.

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

If the planning artifacts cannot be produced, the batch is `BLOCKED` and reports
why. It does not silently degrade into ordinary sequential implementation.

---

## Cross-scenario invariants

These hold in every scenario above:

| Invariant | Applies |
|---|---|
| One worktree and one branch per task | A, B, C, D, E, F |
| Integration is serial, one task at a time | A, B, C, D, E, F |
| Every result is validated before integration | A, B, C, D, E, F |
| An unknown gate is a closed gate | A, B, C, D, E, F |
| `NO_OP` is never `SUCCESS` | A, B, C, D, E, F |
| Issues close only via approved merge | A, B, C, D, E, F |
| Every task appears in the report | A, B, C, D, E, F |

## Related

- [`../SKILL.md`](../SKILL.md) — normative entrypoint
- [`dependency-analysis.md`](dependency-analysis.md) — waves and parallel safety
- [`worker-contract.md`](worker-contract.md) — validation and semantic conflict
- [`review-and-gates.md`](review-and-gates.md) — gate semantics
- [`failure-recovery.md`](failure-recovery.md) — failure and dependent handling
- [`git-worktree.md`](git-worktree.md) — isolation and concurrency

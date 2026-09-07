# Review Provider Policy

This document is a normative part of the Merge Skill. It defines how automated
review-provider availability affects merge eligibility without turning a
third-party service into a single point of failure.

## Ownership and adapters

This document is the **single source of truth** for provider states, fallback
eligibility, and the review evidence the merge gate requires. Other skills are
adapters: they reference this policy and must not restate, relax, or re-derive
provider semantics of their own.

| Skill | Relationship |
|---|---|
| [Merge Skill](SKILL.md) | Owns this policy; consumes it during `VALIDATING` |
| [Git Workflow Skill](../git-workflow/SKILL.md) | References it for provider-side unavailability |
| [Batch Skill](../batch/SKILL.md) | Delegates all merge/review-gate conditions to the Merge Skill |
| [Review Skill](../../task/review/SKILL.md) | Supplies the viewpoints an independent review applies |
| [Self-Review Skill](../self-review/SKILL.md) | The authoring-side gate; explicitly **not** independent review evidence |
| [Reporting Skill](../reporting/SKILL.md) | Reports provider states without converting them into a pass |

If any other document disagrees with this one about provider states or fallback
eligibility, this document governs.

## Default path

CodeRabbit is the preferred automated reviewer when available. A completed
current-HEAD review is authoritative evidence for automated-review findings.
A skipped, failed, pending, unavailable, or rate-limited run is never treated as
a successful review.

CodeRabbit is **preferred, not mandatory**: it is not a single-provider merge
gate. When it cannot review the current HEAD, the fallback below supplies the
required review evidence from another source, rather than the merge waiting on
provider capacity.

## Provider state vocabulary

Exactly one provider state applies to a given PR HEAD SHA. The state is
`CODERABBIT_UNKNOWN` unless another state is **positively established** from
provider evidence; it is never inferred from the mere absence of a review.

| State | Meaning | Evidence required to establish it |
|---|---|---|
| `CODERABBIT_REVIEWED` | A review completed for this exact PR HEAD | The completed review posted against this HEAD SHA |
| `CODERABBIT_RATE_LIMITED` | The provider declined for quota / rate-limit reasons | A provider message stating that the review is rate-limited or the review limit is reached |
| `CODERABBIT_UNAVAILABLE` | The provider recognized the PR but could not review it — service unavailable, or another provider-side failure | A provider message reporting the service unavailable, errored, or otherwise unable to execute the review |
| `CODERABBIT_SKIPPED` | The provider or the repository's provider configuration deliberately excluded this PR/HEAD from automatic review | Either the provider reported the skip **and its reason**, or the PR demonstrably matches a configured exclusion (draft, ignored label / title / author, manual pause, automatic review disabled) in the repository's provider configuration |
| `CODERABBIT_PENDING` | The provider accepted the PR and the review has not finished | Provider evidence that a review is in progress for this HEAD |
| `CODERABBIT_UNKNOWN` | No review, and no established reason for its absence | None — this is the fail-closed default |

`CODERABBIT_RATE_LIMITED`, `CODERABBIT_UNAVAILABLE`, `CODERABBIT_SKIPPED`,
`CODERABBIT_PENDING`, and `CODERABBIT_UNKNOWN` are **provider failure or
non-completion states**. A provider failure state is never a review pass: none
of them may be reported as one, and none of them satisfies the merge gate on
its own.

A `CODERABBIT_SKIPPED` claim is only valid with the positive evidence named
above. "No review appeared, so it must have been skipped" is
`CODERABBIT_UNKNOWN`, not `CODERABBIT_SKIPPED`.

### Disambiguating a pending or unknown state

`CODERABBIT_PENDING` and `CODERABBIT_UNKNOWN` are not terminal. To resolve one,
re-request a review from the provider and record its response: the request
either produces a completed review (`CODERABBIT_REVIEWED`) or a provider-side
failure message that establishes `CODERABBIT_RATE_LIMITED` /
`CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED`. Until a terminal state is
established from that evidence, the merge stays blocked. Waiting longer is never
converted into a state by assumption.

## Fallback review evidence vocabulary

| State | Meaning |
|---|---|
| `FALLBACK_REVIEW_PASS` | An independent current-HEAD review completed and left no unresolved `blocker` finding |
| `FALLBACK_REVIEW_FAIL` | An independent current-HEAD review completed and left at least one unresolved `blocker` finding |
| `FALLBACK_REVIEW_ABSENT` | No independent current-HEAD review evidence exists — the fail-closed default |

## Independent current-HEAD review

The fallback substitutes an **independent review**, not an assumption of
quality. It is valid evidence only when all of the following hold:

1. **Separate reviewing context.** The review is produced by a reviewing context
   separate from the one that authored the change — a different agent session,
   a different tool, or a human other than the author. The author's own pre-PR
   self review runs on every PR and is **never** independent review evidence.
2. **Documented checklist.** The review applies the viewpoints of
   [`skills/common/task/review/SKILL.md`](../../task/review/SKILL.md) and
   reports in that skill's format.
3. **Bound to the current HEAD.** The review is performed against the
   exact PR HEAD SHA that is the merge candidate, and records that SHA.
4. **Recorded on the PR.** The reviewer / tool identity, the reviewed HEAD SHA,
   the timestamp, the resulting state token, and the findings are recorded on
   the PR.
5. **Fail-closed verdict.** Any unresolved `blocker` finding yields
   `FALLBACK_REVIEW_FAIL` and blocks the merge. Absence of a recorded review is
   `FALLBACK_REVIEW_ABSENT`, which also blocks the fallback path.

A change to the PR HEAD invalidates the evidence; a new independent review is
required for the new candidate.

## Provider-unavailable fallback

A provider-unavailable fallback is permitted only when the provider state is one
of `CODERABBIT_RATE_LIMITED`, `CODERABBIT_UNAVAILABLE`, or
`CODERABBIT_SKIPPED`, established by the evidence named in the state vocabulary,
for example:

- CodeRabbit reports that the review is `rate-limited` or that its review limit
  is reached;
- CodeRabbit reports the service as unavailable or otherwise cannot execute the
  requested review for a provider-side reason;
- CodeRabbit reports that it skipped the PR, or the PR demonstrably matches a
  configured repository-side exclusion, so the absent review is explained by
  configuration rather than by a silently missed review;
- equivalent evidence shows that the provider accepted or recognized the PR but
  did not perform the review because the provider itself was unavailable.

The fallback is not a general bypass. It is an alternate fail-closed evidence
path and all of the following are mandatory:

1. repository-owned current-HEAD CI is green;
2. the final PR HEAD is known and stable;
3. the current main HEAD is known and stable;
4. there are no unresolved actionable findings from any completed CodeRabbit
   review on the PR;
5. an [independent current-HEAD review](#independent-current-head-review) is
   recorded with the state `FALLBACK_REVIEW_PASS`;
6. the provider failure evidence is recorded in the PR, including the reason;
7. the human approval is explicit and SHA-bound to the final PR HEAD and the
   current main HEAD;
8. final HEAD revalidation still passes immediately before merge.

A fallback must not be used to ignore, waive, hide, or downgrade an actual
CodeRabbit finding. Any unresolved actionable finding blocks the merge regardless
of provider availability.

The fallback changes **nothing else** about the merge flow: the mandatory rebase
onto the latest main HEAD, required CI, the prohibition on admin bypass, direct
`main` pushes and unsafe force pushes, the merge method, and the final SHA-bound
human approval are all unchanged by it.

## Audit record

When the fallback is used, the PR must contain a concise audit note with:

- `Provider state`: the established state token
  (`CODERABBIT_RATE_LIMITED` / `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED`);
- `Fallback reason`: the provider-side evidence that established that state;
- the final PR HEAD SHA;
- the current main HEAD SHA;
- current-HEAD CI result;
- unresolved actionable CodeRabbit finding count;
- `Independent review`: the state token (`FALLBACK_REVIEW_PASS`), the reviewing
  context / tool identity, the reviewed HEAD SHA, and the timestamp;
- the identity and timestamp of the final human approval.

The note must not contain private session URLs, credentials, tokens, or other
sensitive data.

## Recovery

The fallback applies only to the affected merge attempt. Once the provider is
available again, normal CodeRabbit review requirements resume automatically for
subsequent PRs or any new PR HEAD that has not already completed the fallback
path.

If the PR HEAD changes after fallback evidence was collected, all current-HEAD
CI, review/fallback evidence, and SHA-bound approval must be revalidated for the
new candidate.

## Decision table

"Fallback allowed" always means: allowed **with** the audit note and the final
SHA-bound human approval. Every other row is fail-closed.

| Provider state | Unresolved actionable findings | Current-HEAD CI | Independent current-HEAD review | Result |
|---|---:|---|---|---|
| `CODERABBIT_REVIEWED` | 0 | green | not required | normal review path |
| `CODERABBIT_REVIEWED` | >0 | any | any | blocked |
| `CODERABBIT_REVIEWED` | 0 | not green | any | blocked |
| `CODERABBIT_RATE_LIMITED` | 0 | green | `FALLBACK_REVIEW_PASS` | fallback allowed |
| `CODERABBIT_UNAVAILABLE` | 0 | green | `FALLBACK_REVIEW_PASS` | fallback allowed |
| `CODERABBIT_SKIPPED` | 0 | green | `FALLBACK_REVIEW_PASS` | fallback allowed |
| `CODERABBIT_RATE_LIMITED` / `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED` | 0 | green | `FALLBACK_REVIEW_FAIL` | blocked |
| `CODERABBIT_RATE_LIMITED` / `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED` | 0 | green | `FALLBACK_REVIEW_ABSENT` | blocked |
| `CODERABBIT_RATE_LIMITED` / `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED` | >0 | any | any | blocked |
| `CODERABBIT_RATE_LIMITED` / `CODERABBIT_UNAVAILABLE` / `CODERABBIT_SKIPPED` | any | not green | any | blocked |
| `CODERABBIT_PENDING` | any | any | any | blocked until a terminal provider state is established |
| `CODERABBIT_UNKNOWN` | any | any | any | blocked until provider-side unavailability is established |

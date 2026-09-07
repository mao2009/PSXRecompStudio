# ADR-010: CodeRabbit Best-Effort Automated Review

- Status: Accepted (amended 2026-09-07 by Issue #270)
- Date: 2026-09-03
- Issue: amendment #270

## Decision

CodeRabbit is a best-effort automated reviewer, not a repository-owned hard gate. Automatic review is enabled without a manual mention, and incremental review is enabled for PR updates. The repository does not add description markers, post review commands, poll for evidence, or require a CodeRabbit check to succeed.

CodeRabbit availability, rate limits, skipped reviews, and missing current-head review do not by themselves block repository CI, and no repository-owned check depends on them. Any actual findings that CodeRabbit posts remain review input and must be assessed by a human or the Merge Skill. Existing repository-owned CI and human approval requirements remain mandatory.

What such a state means for **merge eligibility** is decided by the Merge Skill's [Review Provider Policy](../../skills/common/process/merge/REVIEW_PROVIDER_POLICY.md), which is the single source of truth for that question — see the amendment below.

## Configuration

The root `.coderabbit.yaml` uses the supported settings:

- `reviews.auto_review.enabled: true`
- `reviews.auto_review.auto_incremental_review: true`
- `reviews.auto_review.auto_pause_after_reviewed_commits: 0`
- no `description_keyword`

This means a newly opened eligible PR is reviewed automatically, and pushes can receive incremental reviews. CodeRabbit may still skip or defer reviews for provider/service conditions or configured exclusions such as drafts, ignored labels/titles/users, manual pause, or rate limits; those states are informational and are not consumed by repository CI.

## Consequences

README Auto-Update has only two responsibilities: validate a candidate README and present it safely (an advisory PR comment; it never pushes a commit to the PR head — see ADR-009, amended by Issue #244). It does not coordinate with CodeRabbit. CodeRabbit runs outside GitHub Actions as the installed GitHub App, so README notification and repository CI remain independent of CodeRabbit outages or timing.

A CodeRabbit finding is not automatically a merge blocker. A confirmed unresolved major finding may be a blocker under normal human review policy. The absence of a CodeRabbit review does not block repository CI, and is not by itself a finding against the change; what it means for merge eligibility — and what evidence replaces the missing review — is decided by the Review Provider Policy, as amended below.

## Supersedes

This ADR supersedes the former design that ordered CodeRabbit after README publication and implemented a repository-owned `CodeRabbit Review Gate` with current-HEAD evidence, marker mutation, polling, and fail-closed timeout behavior.

## Amendment (Issue #270): fail-closed fallback review evidence

### Context

The original decision above answered one question — whether the repository owns a CodeRabbit check — and answered it correctly: it does not. But it was also read as answering a second question it never analyzed: what review evidence a *merge* requires when CodeRabbit did not review the current HEAD.

That reading is too permissive on one side and, in practice, too restrictive on the other. Too permissive, because "absence of a review is not a blocker" taken literally would let a change merge with no independent review evidence at all. Too restrictive, because treating a CodeRabbit review as the only acceptable evidence makes a free-tier quota the project's throughput ceiling: PR #269 fixed every reported finding and then could not obtain a current-HEAD re-review, because the review quota was rate-limited.

Issue #260 introduced `REVIEW_PROVIDER_POLICY.md` with a provider-unavailable fallback, but that fallback substituted CI-green plus zero-unresolved-findings for the review — it required no review of the candidate at all — and it left an unexplained absent review indistinguishable from a confirmed provider outage.

### Decision

CodeRabbit is **preferred but never a single-provider mandatory merge gate**, and merge requires review evidence bound to the current PR HEAD, from one of exactly two paths:

1. a completed CodeRabbit review of that HEAD; or
2. the fail-closed fallback, when a provider failure state is positively established from provider-side evidence.

The binding constraints future implementations must respect:

- Provider states are a defined, parseable vocabulary, and `CODERABBIT_UNKNOWN` is the fail-closed default — a state is never inferred from the mere absence of a review. A provider failure or non-completion state is never a review pass and is never reported as one.
- The fallback requires an **independent** current-HEAD review recorded as `FALLBACK_REVIEW_PASS`: a reviewing context separate from the one that authored the change, applying the Review Skill's viewpoints against the exact candidate SHA. The author's own pre-PR self review runs on every PR and never satisfies this — otherwise a change could approve itself.
- Actual CodeRabbit findings are never waived by provider availability. Any unresolved actionable finding blocks the merge.
- The fallback changes nothing else: required CI, the mandatory rebase, the prohibitions on admin bypass / direct `main` push / unsafe force push, the merge method, and the final SHA-bound human approval all still apply.
- Provider semantics live in exactly one document. `REVIEW_PROVIDER_POLICY.md` is the SSOT; the Merge, Git Workflow, Batch, Review, Self-Review and Reporting skills reference it as adapters and must not restate or relax it.

### Consequences

Positive: a provider outage or exhausted quota no longer blocks an otherwise validated change, and the path that replaces it is deterministic, auditable, and SHA-bound. The contradiction between this ADR's original wording, the Merge Skill's own looser paragraph, and `REVIEW_PROVIDER_POLICY.md` is resolved in one direction, with the policy as the single definition.

Negative: the fallback is more expensive than before — it now costs a full independent review pass, where Issue #260's version cost none. That is deliberate; the cheaper version bought availability by lowering the evidence bar. A `CODERABBIT_PENDING` or `CODERABBIT_UNKNOWN` state still blocks, and is resolved by re-requesting a review to obtain terminal evidence, not by waiting it out.

Not addressed here at amendment time: the Merge Skill's `Merge Strategy` section
named `gh pr merge --merge` as the standard method and listed `--squash` as not
allowed, while Issue #270 assumes squash is the default merge method. That
inconsistency predated this amendment and was orthogonal to review-provider
semantics. It was subsequently resolved by the Merge Skill 1.5.0 Squash and
merge standardization (Issue #265, PR #273), which made `gh pr merge --squash`
the standard method; this amendment does not depend on either side of that
change.

### Alternatives Considered

- **Keep the Issue #260 fallback unchanged (CI-green + zero findings, no review).** Rejected: it makes "no reviewer looked at this HEAD" an acceptable merge state whenever the provider is down, which is precisely the evidence gap Issue #270 names in its title.
- **Accept the author's pre-PR self review as the fallback evidence.** Rejected: the self review is produced by the authoring context, so accepting it would let a change supply its own independent-review evidence. The Self-Review Skill now states this exclusion explicitly.
- **Add a second automated review provider as the fallback.** Rejected for now: it moves the dependency rather than removing it, and adds a provider to configure and pay for. The policy is written in terms of *evidence*, so a second provider can later satisfy the independent-review requirement without changing this decision.
- **Make an unexplained absent review fallback-eligible.** Rejected: it is indistinguishable from a silently missed review, so it would convert the fallback into a general bypass. Only a positively evidenced skip qualifies.

### Related ADRs

- ADR-009: PR-Triggered README Auto-Update via OpenCode — the README flow this ADR decoupled CodeRabbit from; unaffected by this amendment.

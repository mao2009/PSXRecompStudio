# ADR-010: CodeRabbit Best-Effort Automated Review

- Status: Accepted
- Date: 2026-09-03

## Decision

CodeRabbit is a best-effort automated reviewer, not a repository-owned hard gate. Automatic review is enabled without a manual mention, and incremental review is enabled for PR updates. The repository does not add description markers, post review commands, poll for evidence, or require a CodeRabbit check to succeed.

CodeRabbit availability, rate limits, skipped reviews, and missing current-head review do not by themselves block repository CI or merge. Any actual findings that CodeRabbit posts remain review input and must be assessed by a human or the Merge Skill. Existing repository-owned CI and human approval requirements remain mandatory.

## Configuration

The root `.coderabbit.yaml` uses the supported settings:

- `reviews.auto_review.enabled: true`
- `reviews.auto_review.auto_incremental_review: true`
- `reviews.auto_review.auto_pause_after_reviewed_commits: 0`
- no `description_keyword`

This means a newly opened eligible PR is reviewed automatically, and pushes can receive incremental reviews. CodeRabbit may still skip or defer reviews for provider/service conditions or configured exclusions such as drafts, ignored labels/titles/users, manual pause, or rate limits; those states are informational and are not consumed by repository CI.

## Consequences

README Auto-Update has only two responsibilities: validate a candidate README and publish it safely. It does not coordinate with CodeRabbit. CodeRabbit runs outside GitHub Actions as the installed GitHub App, so README publication and repository CI remain independent of CodeRabbit outages or timing.

A CodeRabbit finding is not automatically a merge blocker. A confirmed unresolved major finding may be a blocker under normal human review policy; absence of a review is not.

## Supersedes

This ADR supersedes the former design that ordered CodeRabbit after README publication and implemented a repository-owned `CodeRabbit Review Gate` with current-HEAD evidence, marker mutation, polling, and fail-closed timeout behavior.

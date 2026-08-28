# ADR-010: CodeRabbit Review Runs After README Auto-Update

- **Status**: Accepted
- **Date**: 2026-08-28
- **Issue**: #185

## Context

CodeRabbit (a GitHub App) reviews every eligible pull request automatically.
The README Auto-Update workflow (#180) analyzes a PR and, when the README
changed, commits the updated README to the PR's head branch. Because both run
immediately on PR creation / `synchronize`, CodeRabbit could start its review
**before** the README Auto-Update finishes. The first review would then cover a
PR state that does not include the README changes, wasting a review and
duplicating comments once the README lands.

The goal is a strict execution order:

```
PR
 ↓
README Auto-Update            (publish README commit if any)
 ↓
CodeRabbit review             (reviews the FINAL PR state)
 ↓
Human Review / Approval
 ↓
Merge
```

Constraints carried over from the existing design:

- No secrets besides the built-in `GITHUB_TOKEN`.
- PR/repository content is untrusted input; the boundary must be mechanical.
- The security boundary established in #184 (OpenCode model job has
  `contents: read` and NO repository credentials) must not be weakened.
- No automatic merge; final merge always goes through the Human Approval Gate.
- Fork PRs must keep their existing safe-skip policy.

## Official CodeRabbit Specification (verified)

CodeRabbit's configuration reference and "Automatic review controls" docs
(https://docs.coderabbit.ai/configuration/auto-review) specify:

- `reviews.auto_review.enabled: false` disables automatic reviews.
- Manual review commands always work regardless of configuration:
  `@coderabbitai review` (incremental) and `@coderabbitai full review` (from
  scratch) posted as a PR comment.
- `reviews.auto_review.ignore_usernames` skips automatic reviews for listed
  authors; **manual** commands are still honored for those authors.
- Positive `reviews.auto_review.labels` opt-in and a
  `description_keyword` opt-in still trigger reviews even when `enabled` is
  `false`; we configure neither so the only review trigger is our explicit
  comment.
- There is no public API/GitHub Action to start a review programmatically;
  the documented trigger is the PR comment command.

## Decision

### 1. Automatic reviews disabled

Add a root-level `.coderabbit.yaml`:

```yaml
reviews:
  auto_review:
    enabled: false
    ignore_usernames:
      - "github-actions[bot]"
```

`enabled: false` stops CodeRabbit from racing the README Auto-Update on
`opened`/`synchronize`. `github-actions[bot]` is added to `ignore_usernames`
as defense in depth: the README publish and review-trigger actions run under
that identity, so even if auto-review were later re-enabled, bot-authored
commits/comments would not start a review by themselves. Manual commands
(`@coderabbitai review`) are unaffected.

### 2. Explicit review trigger in the README Auto-Update workflow

Add a third job `review-trigger` to `.github/workflows/readme-autoupdate.yml`:

- `needs: [update-readme, publish-readme]`, `if: always()` and then **gated
  inside the job** on the upstream results:
  - `needs.update-readme.result == 'success'`
  - `needs.update-readme.outputs.action == 'proceed'`
  - `needs.publish-readme.result == 'success'`
- Only runs for same-repo PRs (fork PRs are skipped by the upstream jobs'
  `if: <head.repo == repo>`, so their results are `skipped` and the gate fails).
- Before triggering, it mechanically verifies that the current PR head SHA
  still equals the SHA the README Auto-Update analyzed
  (`gh api repos/{owner}/{repo}/pulls/{n} --jq '.head.sha'`). If the PR head
  moved, it does **not** trigger CodeRabbit from the stale run; the newer
  `synchronize` run (which analyzes the new head) triggers the review instead.
- Triggers the review by posting the fixed comment body
  `## CodeRabbit Review Request` + `@coderabbitai review` via
  `actions/github-script`.

The comment is a **fixed literal**. No PR- or README-controlled content is
interpolated into it, so prompt injection in the PR cannot alter the trigger
or the workflow.

### 3. Permission separation

| | Model job (`update-readme`) | Publish job (`publish-readme`) | Review job (`review-trigger`) |
|---|---|---|---|
| Permission | `contents: read` | `contents: write` | `pull-requests: write` |
| `GITHUB_TOKEN` | never | publish step only | comment step only |
| Purpose | analyze PR, emit README-only artifact | commit/push README | post `@coderabbitai review` comment |

The model job still has no repository credentials and no pull-request
permissions; the review-trigger job is its own concern and owns the only
`pull-requests` permission. `pull-requests: write` grants the ability to post
comments/reviews; it is not `contents` and cannot push code, and no repository
secret is involved.

### 4. Loop prevention

- CodeRabbit auto-review is disabled, so the trigger comment is the only review
  start point and it does not re-open the workflow state machine.
- The `@coderabbitai review` comment is posted with `GITHUB_TOKEN`; comments
  created by the GitHub Actions token do **not** re-trigger `pull_request`
  workflows, so the comment cannot restart README Auto-Update.
- The review-trigger job serializes per PR via its own concurrency group
  (`coderabbit-trigger-<PR number>`, `cancel-in-progress: false`), so
  overlapping `synchronize` runs cannot post duplicate trigger comments.
- The README publish job's SHA gate and the trigger comment's
  `github-actions[bot]` identity keep the publish → synchronize → publish loop
  closed, unchanged from #180.

### 5. Fork PRs / bootstrap runs

Fork PRs skip the model and publish jobs at the job level and in preflight
(#180). The review-trigger job's dependence on `update-readme.result` /
`publish-readme.result` makes it skip for forks as well: on forks the jobs are
`skipped`, not `success`, so no comment is ever posted with a fork-controlled
context. The first (bootstrap) run that introduces these files analyzes only
and never publishes; with `publish-readme` skipped, no CodeRabbit review is
triggered until the trusted assets are on `origin/main` and a real publish
runs.

### 6. Human Approval Gate preserved

Nothing here merges. CodeRabbit reports on the PR; the final merge still goes
through the existing Human Approval Gate and `main` ruleset, unchanged.

## Consequences

### Positive

- CodeRabbit always reviews the final PR state, including any README commit
  made by the Auto-Update workflow.
- The ordering is mechanical (job dependency + result gates + SHA check), not
  prompt-dependent.
- Least privilege is preserved: the model job stays credential-free; the
  review trigger only gets `pull-requests: write`.
- No double reviews and no automatic-review race with the README update.

### Negative

- A PR whose head moves while README Auto-Update is running does not get a
  review from that stale run; the review is deferred to the next `synchronize`
  run that has the stable head. This is intentional (stale-review avoidance).
- If the publish step is skipped (fork or bootstrap), no CodeRabbit review is
  triggered by CI until a real publish occurs; a reviewer can always request a
  manual `@coderabbitai review`.
- CodeRabbit review allowances are consumed by manual (`@coderabbitai review`)
  reviews, which count against the per-developer review rate limit just like
  automatic ones.

## Alternatives Considered

### Keep automatic reviews enabled and rely on sequencing

Rejected: GitHub Actions cannot guarantee that CodeRabbit waits for the README
Auto-Update job; automatic reviews would still race the update.

### Separate `workflow_run` workflow for the trigger

Rejected: a third GitHub workflow file adds `workflow_run` semantics and
another concurrency surface without improving the guarantee. The trigger is
part of the README Auto-Update run's completion; keeping it as a job in the
same workflow reuses the existing dependency graph and result gating.

### `pull_request_target` for the trigger

Rejected: runs workflow code defined by the PR ref with the base-branch
context; unnecessary because the trigger job posts a fixed comment and needs
no PR-controlled code.

## Related ADRs

- ADR-009: PR-Triggered README Auto-Update via OpenCode (the workflow this
  issue extends)
- ADR-007: Repository Artifact Policy (unchanged)
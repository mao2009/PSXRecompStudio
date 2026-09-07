# ADR-009: PR-Triggered README Auto-Update via OpenCode

- **Status**: Accepted (amended 2026-09-03 by Issue #244, 2026-09-07 by Issue #268)
- **Date**: 2026-08-28
- **Issue**: #180 (amendments: #244, #268)

## Context

`README.md` is the primary user-facing document and drifts out of date as
features change. Related issues (#121, #122 and successors) ask for README
updates and multi-language support. Manual maintenance is unreliable, so the
project wants an automated assistant to review pull requests and keep README.md
accurate.

Automation constraints:

- No API-key Secrets. Only the built-in `GITHUB_TOKEN` may be used; the solution
  must run on the GitHub-hosted runner at zero cost.
- PR and repository content is **untrusted input**. A PR can contain prompt
  injection text; the enforcement boundary must be mechanical (fail-closed),
  not dependent on the model behaving correctly.
- The repository is protected by a `main` ruleset (no direct pushes); the
  automation must comply with that.
- The model is fixed to **Big Pickle** (`opencode/big-pickle`) for this
  proof-of-minimum-implementation. If it is unavailable the run must fail
  explicitly, never silently fall back to another model.

## Decision

### 1. PR-Triggered Analysis

Run on `pull_request` to `main` (`opened`, `synchronize`, `reopened`). Preflight
checks the PR head and repository reality; only when safe, OpenCode reviews the
PR in the model job.

### 2. Model: OpenCode Zen Big Pickle (Free, No Key)

Use the built-in `opencode` provider (OpenCode Zen), model `opencode/big-pickle`,
endpoint `https://opencode.ai/zen/v1`, package `@ai-sdk/openai-compatible`.
Big Pickle is a free (US$0) model during its limited-time availability and the
free tier is usable **without** an API key, so no Secret is needed.

The model is intentionally pinned and cannot be overridden by repository
variables or PR-controlled configuration. The workflow passes
`--model opencode/big-pickle` as a literal constant, and the `verify-config`
gate fails the job unless the trusted SSOT config (`opencode.model`) and the
opencode config (`model`/`small_model`) pin the same value. If the model or
provider is unavailable, the step fails and the job reports failure; there is
no fallback model.

### 3. Trusted-Ref Enforcement

The security-critical assets — `readme-sync.sh`, the SSOT config, the opencode
config, and `prompt.md` — are extracted from `origin/main` (`git archive
origin/main`) at runtime, never from the PR head. A PR therefore cannot relax
its own enforcement.

Bootstrap exception (analyze-only): the first PR that introduces these files
(none exist on `origin/main` yet) must source them from the PR head so the model
job can run at all. This is gated by normal PR review and emits an explicit
`::warning::BOOTSTRAP`. The notify job never falls back to PR-head assets: in
bootstrap mode the token-backed notify path is skipped entirely, so the
bootstrap path never produces a `GITHUB_TOKEN`-backed change. After the PR
merges, every subsequent PR extracts the trusted copy from `origin/main` and the
fallback no longer triggers.

### 4. Model/Notify Isolation (the trust boundary)

The OpenCode execution environment and the token-enabled notify environment
are **separate jobs on separate runners**:

| | Model job (`update-readme`) | Notify job (`notify-readme`) |
|---|---|---|
| Permission | `contents: read` | `contents: read` + `pull-requests: write` |
| `GITHUB_TOKEN` | never referenced | notify step only |
| Input | PR head + trusted assets | PR head + README-only artifact |
| Output | candidate `README.md` artifact | advisory comment (never a commit/push) |
| Trusted assets | from `origin/main` (PR head only in gated bootstrap) | **always** re-extracted from `origin/main` |

Only a single candidate `README.md` crosses the job boundary, as an artifact.
The notify job downloads it into a clean workspace, mechanically verifies the
artifact contains exactly one root-level `README.md` (no symlinks, no extra
paths), and runs the trusted notify script. Even if OpenCode modified the model
job's trusted directory, those bytes are never reused: the notify job
re-extracts the trusted assets from `origin/main` in its own clean workspace.

**Issue #244 amendment:** the publish design below (a bot commit pushed to the
PR head branch) is **replaced** with a comment-only notify design. A
`github-actions[bot]`-authored PR head left required CI in `action_required`
with 0 jobs because GitHub skips `pull_request`-triggered workflows when the
head is owned by `github-actions[bot]`, permanently blocking the merge gate. To
make that impossible, the workflow holds no `contents: write` and makes no git
commit/push of any kind. README candidates are surfaced as an idempotent,
advisory PR comment referencing the trusted-run artifact; applying a README
change is a human (or approved automation) action, never an automated bot push.
SHA-bound approvals remain intact because the bot never introduces a new head
SHA that could be approved around.

### 5. Fail-Closed Mechanical Boundary

`readme-sync.sh notify` is the enforcement point. In the notify job's clean
workspace, it:

- validates the artifact contains exactly one root-level `README.md`;
- compares the candidate against the PR head's `README.md` with `cmp -s` and
  posts **no** comment when they match (no noise);
- posts **one** idempotent comment per `(marker, PR head SHA)` (never one per
  run);
- refuses to run in bootstrap mode (no token-backed comment is ever produced by
  the bootstrap path);
- only reads repository state; it never writes files, commits, or pushes.

Any violation aborts with no comment and no repository mutation.

**Transitional fail-safe:** the notify step runs the notify command only when the
re-extracted `origin/main` script actually implements it (a capability guard).
On the PR that first ships the notify design, origin/main still holds the older
script, so the step emits an explicit `::warning::` and skips the comment
(analyze-only) rather than failing or posting a mismatched comment. This keeps
the transitional PR's CI green and never runs a token-backed action against a
trusted asset that is not yet ready — the same philosophy as the bootstrap
boundary.

### 6. Loop Prevention

A PR whose head commit is authored by the bot email is skipped in preflight, so
a stale candidate comment never re-triggers infinite analysis cycles. Because
the notify design never pushes, it also introduces no new `synchronize` event.

### 7. Fork PRs Skipped

Fork PRs are skipped at the job level for **both** jobs (the `pull_request`
payload for forks is empty and their `GITHUB_TOKEN` is read-only) and again in
preflight (`PR_HEAD_REPO != GITHUB_REPOSITORY`). Untrusted fork code never runs
with write permissions.

### 8. Prompt Injection Hardening

The prompt treats all repository/PR content as untrusted data and forbids
modifying anything but `README.md`, running state-changing git/network
commands, reading secrets, or relaxing constraints. As defense in depth, the
opencode config mechanically denies edits to anything but `README.md`, denies
state-changing bash commands and network tools, and there is no trust placed in
the model's self-report — the real guarantee is Decisions 4 and 5.

### 9. Configuration as SSOT

`config/readme-autoupdate.json` is the single source of truth for the managed
file list, the notification marker/title/instructions, the pinned opencode
version/model, and bot name/email (still used for preflight loop-skip and
comment attribution). The model is fixed to `opencode/big-pickle` in the
workflow, the SSOT config, and the opencode config; `verify-config` enforces the
pin mechanically and fails closed. Repository variables are ignored entirely,
so nothing can select a different model or version outside the trusted review
path. The former publish keys (`pushRefPrefix`, `forbiddenPushBranches`,
`commitMessage`) are removed: nothing is pushed, so nothing to refuse to push.

### 10. Advisory Checks: README Maintenance Never Gates a Pull Request

**Issue #268 amendment.** README Auto-Update is maintenance automation, not a
merge gate, and it must be impossible for it to affect whether a pull request
can be merged.

Observed on PR #267: all four required checks (`Artifact Contamination Gate`,
`CI Gate`, `.NET Build and Test`, `Native Core Build and Test`) succeeded and
the PR was conflict-free, yet GitHub reported `mergeable_state: unstable`
because the `notify-readme` job published a `failure` check run on the head SHA.
GitHub's `UNSTABLE` merge state means "Mergeable with non-passing commit
status": it is produced by *any* non-passing check run on the head commit,
including check runs that are not in the `main-protection` ruleset's required
list — as this workflow's never were. The merge itself stays permitted, but the
pull request is presented as broken and the normal merge flow is disrupted.

Decision: **every step in this workflow carries step-level
`continue-on-error: true`**, so each job's check run always concludes `success`
and can never contribute to `unstable`.

- Step-level `continue-on-error` is documented to "allow a job to pass when this
  step fails": the step's `outcome` stays `failure` while its `conclusion` — and
  therefore the job's conclusion and the job's check run — becomes `success`.
- **Job-level `continue-on-error` is rejected.** It only prevents the *workflow
  run* from failing; the job's own check run is still reported as `failure`,
  which is exactly the condition that produces `unstable`. It also reports
  `needs.<job>.result == 'success'` for a job that actually failed, which would
  silently defeat the `notify-readme` job's dependency gating.
- Moving the mutation to `push` on `main` or to `workflow_run` was rejected as
  disproportionate: it would discard the pre-merge candidate handoff that
  Decision 4 and the #244 amendment exist to provide, and it would rebuild the
  trust boundary for a problem that is purely about check-run reporting.

Fail-closed behavior (Decisions 3, 4, 5, 7) is **preserved, not weakened**. Each
step is gated on the previous step's `outcome == 'success'`, so the first
failure still stops the chain and no later step runs against a half-built state;
the token-backed notify steps additionally keep their explicit
`bootstrap == '0'` guard. A failure is not swallowed either: a final
`if: always()` step in each job emits a `::warning::` annotation and a job
summary naming the failing step.

Residual, accepted: a job that is cancelled or killed at the infrastructure
level (job-level timeout, runner loss, `concurrency` cancellation) still
concludes `cancelled`/`failure`, because no workflow-level setting can override
a conclusion the runner sets outside step execution. Concurrency cancellation
targets superseded head SHAs, and the model step carries its own
`timeout-minutes`, so this path is not the failure class Issue #268 describes.

### 11. README Maintenance Must Not Be Silently Green

Because Decision 10 makes the check runs unconditionally green, a genuine
failure must never be left in place unfixed and unnoticed: the warning
annotation and job summary are the observability contract, and a recurring
warning is treated as a defect to fix, not as accepted noise.

The failure that produced PR #267's `unstable` state was itself a real defect in
`readme-sync.sh`: `notify_has_marker` armed a `RETURN` trap for its temporary
directory, and a `RETURN` trap armed inside a function stays armed after that
function returns. It fired again when `cmd_notify` returned, when the trap's
`tmpdir` was out of scope, so `set -u` aborted `readme-sync.sh notify` with
`tmpdir: unbound variable` **after** the candidate comment had already been
posted successfully. The cleanup is now explicit rather than trap-driven, and
the scenario suite covers the real REST path (previously bypassed entirely by
the `GITHUB_API_ROOT` test seam).

## Consequences

### Positive

- README.md stays accurate with minimal human effort, per PR.
- No API-key Secret; zero runtime cost for the free Big Pickle tier.
- Prompt injection cannot relax the enforcement boundary.
- Fully auditable: candidate README candidates are posted as idempotent
  comments linking a head SHA and the producing run.
- Compromise of the model job cannot push anything: the token lives only in an
  isolated notify job fed by a single-file artifact and trusted origin/main
  assets, and the notify job only posts a comment.
- The permanent fix for Issue #244 (no `github-actions[bot]` commit to the PR
  head) keeps all `pull_request`-triggered CI runnable on every human/authored
  head, so required CI can never be left in `action_required` (0 jobs) by the
  README bot.
- README maintenance is fully decoupled from mergeability (Issue #268): a
  failure in this workflow can no longer publish a failing check run, so it
  cannot put an otherwise merge-ready pull request into `unstable`, and the four
  required checks are untouched and still enforced.

### Negative

- Free-tier/anonymous Zen availability for Big Pickle is not guaranteed by an
  SLA; a 429/unavailable response fails the job (fail-closed by design).
- README updates now require a human (or approved automation) apply step on the
  PR rather than being auto-applied, adding a small manual step for reviewers
  of PRs that change README-relevant content. This is the accepted cost of the
  permanent #244 fix (documented in `docs/development/agent-guide.md`).
- Big Pickle is a stealth model with no long-term availability commitment; its
  free period collects data to improve the model.
- CI depends on OpenCode install location/behavior; version is pinned in the
  trusted SSOT config.
- The workflow's PR checks are green even when README maintenance failed, so
  the green tick no longer certifies that the automation worked. Diagnosis moves
  to the warning annotation and job summary (Decisions 10 and 11), which is a
  weaker signal than a red check and requires the maintenance warnings to be
  actually read.

## Alternatives Considered

### Official `anomalyco/opencode/github@latest` Action

Rejected: requires an API key (`OPENCODE_API_KEY`) in the environment.

### GitHub Models with `GITHUB_TOKEN`

- Zero cost, built-in auth, no Secret.
- Superseded by the explicit requirement to use Big Pickle for this
  proof; retained as a possible fallback design if Big Pickle is withdrawn.

### `pull_request_target`

Rejected: runs untrusted PR code with base-branch definitions and secrets;
not needed because the trusted assets live in `origin/main` already.

### Relying on the Model to Self-Report Compliance

Rejected: AI self-report is not a security boundary. The notify job validates
the actual README-only artifact after the model runs and only ever surfaces the
single-file candidate; the model can neither enforce nor weaken this check.

### Bot Commit to the PR Head (the pre-#244 publish design)

Rejected as the permanent mechanism (Issue #244): a `github-actions[bot]` commit
to the PR head left required CI in `action_required` with 0 jobs, permanently
blocking the merge gate. The comment-only notify design replaces it so no
bot-authored head SHA is ever produced.

## Related ADRs

- ADR-007: Repository Artifact Policy and CI Contamination Gate (file/path/
  size policy that this workflow's deliverable files also must satisfy)
- ADR-008: Batch Orchestrator Checkpoint and Resume Design (states that docs
  are kept current per PR)
- Operation guide: `docs/development/readme-autoupdate.md`
# ADR-009: PR-Triggered README Auto-Update via OpenCode

- **Status**: Accepted
- **Date**: 2026-08-28
- **Issue**: #180

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
PR and may update `README.md`.

### 2. Model: OpenCode Zen Big Pickle (Free, No Key)
Use the built-in `opencode` provider (OpenCode Zen), model `opencode/big-pickle`,
endpoint `https://opencode.ai/zen/v1`, package `@ai-sdk/openai-compatible`.
Big Pickle is a free (US$0) model during its limited-time availability and the
free tier is usable **without** an API key, so no Secret is needed. `model` and
`small_model` are both pinned to `opencode/big-pickle`, and the OpenCode step
receives **no** `GITHUB_TOKEN` (least privilege). If the model or provider is
unavailable, the step fails and the job reports failure; there is no fallback
model.

### 3. Trusted-Ref Enforcement
The security-critical assets — `readme-sync.sh`, the SSOT config, the opencode
config, and `prompt.md` — are extracted from `origin/main` (`git archive
origin/main`) at runtime, never from the PR head. A PR therefore cannot relax
its own enforcement.

### 4. Fail-Closed Mechanical Boundary
`readme-sync.sh publish` is the enforcement point. It inspects the actual
working tree after OpenCode runs and:
- rejects any changed path outside `workflow.managedFiles` (Phase 1:
  `README.md` only — no `.github/`, scripts, config, or docs);
- rejects deletions;
- commits only managed files as `github-actions[bot]`;
- refuses to push to `workflow.forbiddenPushBranches` (`main`);
- pushes only `HEAD:refs/heads/<PR head branch>`.

Any violation aborts with no commit and no push.

### 5. Loop Prevention
A PR whose head commit is authored by the bot email is skipped in preflight, so
a bot push never re-triggers infinite analysis/production cycles.

### 6. Fork PRs Skipped
Fork PRs are skipped at the job level (the `pull_request` payload for forks is
empty and their `GITHUB_TOKEN` is read-only) and again in preflight
(`PR_HEAD_REPO != GITHUB_REPOSITORY`). Untrusted fork code never runs with
write permissions.

### 7. Prompt Injection Hardening
The prompt treats all repository/PR content as untrusted data and forbids
modifying anything but `README.md`, running state-changing git/network
commands, reading secrets, or relaxing constraints. This is defense-in-depth;
the real guarantee is Decision 4.

### 8. Configuration as SSOT
`config/readme-autoupdate.json` is the single source of truth for the managed
file list, bot identity, commit message, and opencode version/model. Overrides
for version/model are optionally available via repository variables
(`README_AUTOUPDATE_OPENCODE_VERSION`, `README_AUTOUPDATE_MODEL`) but must
never fall back to a non-Big-Pickle model.

## Consequences

### Positive
- README.md stays accurate with minimal human effort, per PR.
- No API-key Secret; zero runtime cost for the free Big Pickle tier.
- Prompt injection cannot relax the enforcement boundary.
- Fully auditable: bot commits use the `github-actions[bot]` identity and
  restricted push target.

### Negative
- Free-tier/anonymous Zen availability for Big Pickle is not guaranteed by an
  SLA; a 429/unavailable response fails the job (fail-closed by design).
- The bot's own push triggers a new `synchronize` event whose workflow run
  requires approval (a useful extra human gate, but adds a step for reviewers).
- Big Pickle is a stealth model with no long-term availability commitment; its
  free period collects data to improve the model.
- CI depends on OpenCode install location/behavior; version is pinned.

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
Rejected: AI self-report is not a security boundary. The workflow inspects the
actual git diff after the model runs (`publish`), which is the only enforceable
check.

## Related ADRs
- ADR-007: Repository Artifact Policy and CI Contamination Gate (file/path/
  size policy that this workflow's deliverable files also must satisfy)
- ADR-008: Batch Orchestrator Checkpoint and Resume Design (states that docs
  are kept current per PR)
- Operation guide: `docs/development/readme-autoupdate.md`
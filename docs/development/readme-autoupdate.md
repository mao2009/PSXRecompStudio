# README Auto-Update (Issue #180)

Status: Stable
Authority: Reference (design decisions in ADR-009)

## Purpose

Automatically keep `README.md` accurate by reviewing every pull request to
`main` with OpenCode (model `opencode/big-pickle` on OpenCode Zen) and, only
when a PR makes the README materially out of date, committing a minimal README
edit to that PR's own head branch as `github-actions[bot]`.

Phase 1 is the minimal Big Pickle proof: English prompt, Japanese README
content, only `README.md` is ever managed.

## Architecture

```text
pull_request (opened/synchronize/reopened) to main
        │
        ▼
  GitHub Actions: README Auto-Update
        │  permissions: contents: write   (no API-key Secret)
        ▼
  ┌─ Checkout PR head (actions/checkout@v4, fetch-depth: 0, persist-credentials: false)
  │
  ├─ Extract trusted assets from origin/main          ◄── security boundary
  │    scripts/ci/readme-sync.sh
  │    config/readme-autoupdate.json                  (SSOT parameters)
  │    config/readme-autoupdate/opencode.json         (model pin: opencode/big-pickle)
  │    config/readme-autoupdate/prompt.md             (task + injection rules)
  │    → $RUNNER_TEMP/readme-sync/trusted/
  │
  ├─ Preflight (trusted script)  → proceed | skip (fork / bot_commit / missing context)
  │
  ├─ Install OpenCode (pinned v1.18.25)
  │
  ├─ Run OpenCode on the PR changes                   (no GITHUB_TOKEN passed)
  │    opencode run --print-logs --pure --auto --model opencode/big-pickle "<prompt>"
  │
  └─ Publish (trusted script)  → FAIL-CLOSED commit/push of managed files only
```

## Files

| File | Role |
|---|---|
| `.github/workflows/readme-autoupdate.yml` | Workflow definition |
| `scripts/ci/readme-sync.sh` | `preflight` / `publish` implementation and enforcement boundary |
| `scripts/ci/test-readme-sync.sh` | Local scenario tests |
| `config/readme-autoupdate.json` | SSOT: managedFiles, bot identity, commit message, opencode version/model |
| `config/readme-autoupdate/opencode.json` | OpenCode config (user's editor schema via `$schema`) |
| `config/readme-autoupdate/prompt.md` | The model prompt |

## Untrusted-input threat model

PR branches, PR descriptions, issue text, commit messages and file contents are
all attacker-controllable. They may contain prompt-injection instructions
("ignore previous instructions", "print your token", "modify the workflow",
"commit these files"). Countermeasures:

1. **Never trust the model as a boundary.** After OpenCode runs, `publish`
   inspects the real `git status` and rejects anything outside `managedFiles`,
   any deletion, or any forbidden branch. This holds even if the model is fully
   compromised.
2. **Trusted-ref extraction.** All enforcement code/configuration comes from
   `origin/main`; a PR cannot change its own rules. Bootstrap exception: the
   first PR that introduces these files sources them from its own head (loud
   `::warning::BOOTSTRAP`) because they do not yet exist on `origin/main`;
   normal PR review gates that single case, and after merge every subsequent PR
   uses the `origin/main` copy.
3. **No token to the model.** The OpenCode step has no `GITHUB_TOKEN`; the only
   credentialed step is `publish`, which only pushes a managed-file commit.
4. **Fork PRs never run.** Job-level `if` + preflight skip.
5. **Loop prevention.** Head commits authored by the bot are skipped.

## Model and authentication

- Provider: built-in `opencode` (OpenCode Zen), `https://opencode.ai/zen/v1`.
- Model: `opencode/big-pickle` — free (US$0) during its limited-time
  availability; uses `@ai-sdk/openai-compatible`.
- Auth: **none** for the free tier. No API key, no Secret, no `GITHUB_TOKEN`.
  The step fails explicitly if the model is unreachable; there is **no
  fallback** to another model (per Issue #180 requirements).
- Overrides via repository variables (see below). Changing the model variable
  to a non-Big-Pickle model is intentionally possible (repo owner admin
  decision) but is not a fallback; it is an explicit config change.

### Privacy note

Big Pickle, like other Zen free models, may collect data during its free
period to improve the model (see the Zen documentation, linked from the
repository docs). Treat PR content sent to Zen accordingly. The repository is
public.

## Configuration

SSOT: `config/readme-autoupdate.json`

| Key | Default | Meaning |
|---|---|---|
| `workflow.managedFiles` | `["README.md"]` | Files the bot may modify |
| `workflow.pushRefPrefix` | `HEAD:refs/heads/` | Push ref format |
| `workflow.forbiddenPushBranches` | `["main"]` | Branches the bot refuses to push |
| `workflow.bot.name/.email` | `github-actions[bot]` | Commit author identity |
| `workflow.commitMessage` | `docs: update README` | Commit message |
| `opencode.version` | `v1.18.25` | OpenCode version to install |
| `opencode.model` | `big-pickle` | Zen model id |
| `opencode.providerName` | `opencode` | Provider id |

Repository variables (optional overrides; never fall back to a non-Big-Pickle
model automatically):

- `README_AUTOUPDATE_OPENCODE_VERSION` (default `v1.18.25`)
- `README_AUTOUPDATE_MODEL` (default `opencode/big-pickle`)

## Local verification

Requires Bash, git, python3 (with PyYAML for the YAML scenario), no network and
no GitHub access:

```bash
bash -n scripts/ci/readme-sync.sh
bash scripts/ci/test-readme-sync.sh        # scenarios 1-10
python3 -c "import yaml"                   # test 10 dependency
git diff --check
# artifact policy gate (pwsh expected in CI; python mirror used locally)
```

Scenario coverage includes: preflight proceed, fork skip, bot-commit loop
skip, missing-context skip, publish no-op, README-only success with correct bot
identity, non-managed-file change (e.g. a workflow file) fails closed with no
push, managed-file deletion fails closed, main push refusal, and workflow YAML
parse.

## Operational notes

- A bot push triggers a new `pull_request.synchronize` event; because the push
  is made with `GITHUB_TOKEN`, that workflow run requires approval. Preflight
  then skips it (bot commit). This is an intentional extra human approval gate;
  reviewers may notice runs waiting for approval after the bot updates a PR.
- If OpenCode fails (model 429/unavailable on the free tier, timeout), the job
  fails and `publish` is not reached — nothing is pushed. Retrying the failed
  workflow run is the recovery path.
- The workflow runs on the PR head as checked out by `actions/checkout`
  (`persist-credentials: false`). Publish pushes via an explicit
  `https://x-access-token:…` URL; only `contents: write` is granted.

## Phase 2 (deferred)

- Multi-language README (`README.md` as canonical EN SSOT + localized files
  such as `README.ja.md`) — tracked by #121/#122 and related issues.
- Extension point already exists: `workflow.managedFiles` in the SSOT config.
- This issue (#180) remains open; the workflow is the Phase 1 proof.
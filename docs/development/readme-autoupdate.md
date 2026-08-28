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

Three isolated jobs share exactly one input, a candidate `README.md` artifact
(job 3 additionally consumes the publish job's result and posts the CodeRabbit
trigger):

```text
pull_request (opened/synchronize/reopened) to main
        │
        ▼
  Job 1: update-readme (model)        permissions: contents: read   (NO GITHUB_TOKEN)
        │
        ├─ Checkout PR head (actions/checkout@v4, persist-credentials: false)
        ├─ Extract trusted assets from origin/main       ◄── security boundary
        │    scripts/ci/readme-sync.sh
        │    config/readme-autoupdate.json               (SSOT parameters, model/version pin)
        │    config/readme-autoupdate/opencode.json      (model pin + permission denies)
        │    config/readme-autoupdate/prompt.md          (task + injection rules)
        │    → $RUNNER_TEMP/readme-sync/trusted/
        ├─ verify-config  → fails unless model == opencode/big-pickle (no fallback)
        ├─ Preflight      → proceed | skip (fork / bot_commit / missing context)
        ├─ Install OpenCode (version from the trusted SSOT config)
        ├─ Run OpenCode on the PR changes
        │    opencode run --pure --auto --model opencode/big-pickle "<prompt>"
        └─ Export candidate README (single file)
             └── artifact: readme-candidate/README.md   ◄── the ONLY cross-job input
        │
        ▼
  Job 2: publish-readme (clean workspace)   permissions: contents: write
        │
        ├─ Checkout PR head (fresh, never shared with the model job)
        ├─ Extract trusted assets from origin/main (fresh extraction, NEVER PR head)
        ├─ Download artifact + validate: exactly one root-level README.md,
        │    no symlinks, no extra paths
        ├─ Apply candidate README to the working tree
        └─ Publish (trusted script, GITHUB_TOKEN only here)
             → FAIL-CLOSED commit/push of managed files only
        │
        ▼
  Job 3: review-trigger (CodeRabbit ordering, Issue #185)
         permissions: pull-requests: write
        │
        ├─ (only if update-readme succeeded + action=proceed + publish succeeded)
        ├─ Verify current PR head SHA == the analyzed SHA (stale-run guard)
        └─ Post "@coderabbitai review" comment        ◄── the ONLY review trigger
             → CodeRabbit reviews the FINAL PR state (auto_review disabled)
```

## Files

| File | Role |
|---|---|
| `.github/workflows/readme-autoupdate.yml` | Workflow definition (three isolated jobs) |
| `scripts/ci/readme-sync.sh` | `preflight` / `verify-config` / `publish` implementation and enforcement boundary |
| `scripts/ci/test-readme-sync.sh` | Local scenario tests |
| `config/readme-autoupdate.json` | SSOT: managedFiles, bot identity, commit message, pinned opencode version/model |
| `config/readme-autoupdate/opencode.json` | OpenCode config (model pin + permission denies) |
| `config/readme-autoupdate/prompt.md` | The model prompt |
| `.coderabbit.yaml` | CodeRabbit config: automatic reviews disabled so CI controls review timing |

## Untrusted-input threat model

PR branches, PR descriptions, issue text, commit messages and file contents are
all attacker-controllable. They may contain prompt-injection instructions
("ignore previous instructions", "print your token", "modify the workflow",
"commit these files"). Countermeasures:

1. **Never trust the model as a boundary.** The model job mechanically rejects
   any non-`README.md` working-tree change, and the publish job validates the
   artifact and inspects the real `git status` after applying the candidate,
   rejecting anything outside `managedFiles`, any deletion, or any forbidden
   branch. This holds even if the model is fully compromised.
2. **Job isolation.** The model job has `contents: read` and never receives
   `GITHUB_TOKEN`. The publish job runs on a clean runner, downloads only the
   README artifact, and re-extracts the trusted script/config from
   `origin/main`. A compromised model job cannot publish, cannot push a
   modified enforcement script, and cannot smuggle files past the artifact
   validation. The review-trigger job (`pull-requests: write` only) is a
   separate concern; it cannot push code and posts a **fixed literal** comment
   that no PR-controlled content is ever interpolated into.
3. **Trusted-ref extraction.** All enforcement code/configuration comes from
   `origin/main`. Bootstrap exception (analyze-only): the first PR that
   introduces these files sources them from its own head (loud
   `::warning::BOOTSTRAP`) because they do not yet exist on `origin/main`;
   normal PR review gates that single case, and the publish job refuses to run
   in bootstrap mode — no GITHUB_TOKEN-backed change is ever produced by the
   bootstrap path. After merge every subsequent PR uses the `origin/main` copy.
4. **No token to the model.** The only credentialed steps are `publish` in the
   publish job (managed-file commit) and the review-trigger comment step
   (`pull-requests: write`, CodeRabbit trigger). The model job receives none.
5. **Fork PRs never run.** Job-level `if` on all jobs + preflight skip.
6. **Loop prevention.** Head commits authored by the bot are skipped; the
   review-trigger comment is posted with `GITHUB_TOKEN` (comments do not
   re-trigger `pull_request` workflows) and `.coderabbit.yaml` ignores
   `github-actions[bot]`, so the trigger never restarts the pipeline.

## Model and authentication

- Provider: built-in `opencode` (OpenCode Zen), `https://opencode.ai/zen/v1`.
- Model: `opencode/big-pickle` — free (US$0) during its limited-time
  availability; uses `@ai-sdk/openai-compatible`.
- Auth: **none** for the free tier. No API key, no Secret, no `GITHUB_TOKEN`
  in the model job. The step fails explicitly if the model is unreachable;
  there is **no fallback** to another model (per Issue #180 requirements).

**Big Pickle is intentionally pinned and cannot be overridden by repository
variables or PR-controlled configuration.** The workflow passes
`--model opencode/big-pickle` as a literal constant; `readme-sync.sh
verify-config` fails the job unless the trusted SSOT config and the opencode
config pin the same model. Repository variables are ignored entirely — no
`README_AUTOUPDATE_MODEL` / `README_AUTOUPDATE_OPENCODE_VERSION` override
exists.

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
| `opencode.version` | `v1.18.25` | OpenCode version to install (validated, no override) |
| `opencode.model` | `opencode/big-pickle` | Pinned model, enforced by `verify-config` |
| `opencode.providerName` | `opencode` | Provider id |

There are no override repository variables. The model is pinned to
`opencode/big-pickle` and changing it requires a reviewed change to the trusted
configuration on `origin/main`.

## Local verification

Requires Bash, git, python3 (with PyYAML for the YAML scenarios), no network
and no GitHub access:

```bash
bash -n scripts/ci/readme-sync.sh
bash scripts/ci/test-readme-sync.sh        # all scenarios
python3 -c "import yaml"
git diff --check
# artifact policy gate (pwsh expected in CI; python mirror used locally)
```

Scenario coverage includes: preflight proceed, fork skip, bot-commit loop
skip, missing-context skip, publish no-op, README-only success with correct bot
identity, non-managed-file change fails closed with no push, managed-file
deletion fails closed, main push refusal, Big Pickle pin enforcement for the
SSOT config and the opencode config, invalid/forged version rejection,
bootstrap-mode publish refusal, bootstrap extraction fallback, artifact
validation (extra file / symlink rejection), workflow YAML structure (model job
has no token, no `vars.` override, publish job is feed-isolated), the
review-trigger job structure and gates, and the `.coderabbit.yaml` auto-review
disable + bot ignore.

## Operational notes

- **CodeRabbit ordering (Issue #185).** CodeRabbit automatic reviews are
  disabled in `.coderabbit.yaml`. After the publish job completes, the
  `review-trigger` job verifies the PR head SHA is still the analyzed SHA and
  posts an `@coderabbitai review` comment, so CodeRabbit always reviews the
  final PR state (README commit included). A stale run whose head moved skips
  the trigger; the newer synchronize run reviews instead. Fork and bootstrap
  runs never trigger a review.
- A bot push triggers a new `pull_request.synchronize` event; because the push
  is made with `GITHUB_TOKEN`, that workflow run requires approval. Preflight
  then skips it (bot commit). This is an intentional extra human approval gate;
  reviewers may notice runs waiting for approval after the bot updates a PR.
- If OpenCode fails (model 429/unavailable on the free tier, timeout), the
  model job fails and the publish job is not reached — nothing is pushed and no
  CodeRabbit review is triggered. Retrying the failed workflow run is the
  recovery path.
- The workflow runs on the PR head as checked out by `actions/checkout`
  (`persist-credentials: false`). Publish pushes via an explicit
  `https://x-access-token:…` URL; `contents: write` is granted only to the
  publish job, `pull-requests: write` only to the review-trigger job.
- Bootstrap runs (the first PR introducing these files) are analyze-only: the
  model job runs, the publish job skips the token path, the README must be
  updated manually by the PR author if needed, and no CodeRabbit review is
  triggered until a real publish succeeds.

## Phase 2 (deferred)

- Multi-language README (`README.md` as canonical EN SSOT + localized files
  such as `README.ja.md`) — tracked by #121/#122 and related issues.
- Extension point already exists: `workflow.managedFiles` in the SSOT config.
- This issue (#180) remains open; the workflow is the Phase 1 proof.
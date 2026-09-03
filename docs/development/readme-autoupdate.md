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

The workflow contains two isolated jobs: `update-readme` creates a README-only
candidate from trusted assets, and `publish-readme` validates and safely
publishes only that candidate. HEAD verification, trusted `origin/main` assets,
artifact validation, and concurrent-write safety remain in the workflow.

CodeRabbit runs outside this workflow as a GitHub App. It is enabled in
`.coderabbit.yaml` and may automatically review the PR on open and on pushes.
There is no workflow trigger, marker, evidence polling, timeout, or completion
dependency for CodeRabbit.
## Files

| File | Role |
|---|---|
| `.github/workflows/readme-autoupdate.yml` | Workflow definition (three isolated jobs) |
| `scripts/ci/readme-sync.sh` | `preflight` / `verify-config` / `publish` implementation and enforcement boundary |
| `scripts/ci/test-readme-sync.sh` | Local scenario tests |
| `config/readme-autoupdate.json` | SSOT: managedFiles, bot identity, commit message, pinned opencode version/model |
| `config/readme-autoupdate/opencode.json` | OpenCode config (model pin + permission denies) |
| `config/readme-autoupdate/prompt.md` | The model prompt |
| `.coderabbit.yaml` | CodeRabbit best-effort automatic and incremental review configuration |

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
   `origin/main`. A compromised model job cannot publish, cannot push a modified enforcement
   script, and cannot smuggle files past artifact validation.
3. **Trusted-ref extraction.** All enforcement code/configuration comes from
   `origin/main`. Bootstrap exception (analyze-only): the first PR that
   introduces these files sources them from its own head (loud
   `::warning::BOOTSTRAP`) because they do not yet exist on `origin/main`;
   normal PR review gates that single case, and the publish job refuses to run
   in bootstrap mode — no GITHUB_TOKEN-backed change is ever produced by the
   bootstrap path. After merge every subsequent PR uses the `origin/main` copy.
4. **No token to the model.** The model job receives no repository credentials.
5. **Fork PRs never publish.** Job-level `if` and preflight skip protect the
   token-backed publish path.
6. **Loop prevention.** Head commits authored by the bot are skipped.

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
has no token, no `vars.` override, publish job is feed-isolated), the two-job workflow structure and token boundary, and the `.coderabbit.yaml`
automatic/incremental review configuration.

## Operational notes

### CodeRabbit

CodeRabbit is a best-effort automated reviewer, not a repository-owned hard gate.
Automatic review and incremental review are enabled without a manual mention or
PR description marker. CodeRabbit availability, rate limits, skipped reviews,
or a missing current-head review do not by themselves fail repository CI or
block merge. Findings that are present must still be reviewed appropriately;
repository-owned CI and human approval remain required.
## Phase 2 (deferred)

- Multi-language README (`README.md` as canonical EN SSOT + localized files
  such as `README.ja.md`) — tracked by #121/#122 and related issues.
- Extension point already exists: `workflow.managedFiles` in the SSOT config.
- This issue (#180) remains open; the workflow is the Phase 1 proof.

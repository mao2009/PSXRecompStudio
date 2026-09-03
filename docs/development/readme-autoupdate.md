# README Auto-Update (Issue #180)

Status: Stable
Authority: Reference (design decisions in ADR-009)

## Purpose

Automatically keep `README.md` accurate by reviewing every pull request to
`main` with OpenCode (model `opencode/big-pickle` on OpenCode Zen) and, only
when a PR makes the README materially out of date, surfacing a mechanical,
advisory comment on the PR with the candidate `README.md` content and
apply/decline instructions.

The bot **never** commits to the PR head branch and **never** pushes. README
updates are always applied by a human (or an approved automation), never by an
automated bot commit. This is a permanent, structural fix for Issue #244: a
`github-actions[bot]`-authored PR head left required CI in `action_required`
with 0 jobs (all `pull_request`-triggered workflows are skipped on bot-authored
heads), permanently blocking the merge gate.

Phase 1 is the minimal Big Pickle proof: English prompt, Japanese README
content, only `README.md` is ever managed.

## Architecture

The workflow contains two isolated jobs: `update-readme` creates a README-only
candidate from trusted assets, and `notify-readme` validates the artifact and
posts an advisory comment only when the candidate differs from the PR head's
README. HEAD verification, trusted `origin/main` assets, and artifact
validation remain in the workflow. The notify job holds `pull-requests: write`
(comment-only) and `contents: read`; it never holds `contents: write` and never
pushes.

CodeRabbit runs outside this workflow as a GitHub App. It is enabled in
`.coderabbit.yaml` and may automatically review the PR on open and on pushes.
There is no workflow trigger, marker, evidence polling, timeout, or completion
dependency for CodeRabbit.
## Files

| File | Role |
|---|---|
| `.github/workflows/readme-autoupdate.yml` | Workflow definition (two isolated jobs) |
| `scripts/ci/readme-sync.sh` | `preflight` / `verify-config` / `notify` implementation and enforcement boundary |
| `scripts/ci/test-readme-sync.sh` | Local scenario tests |
| `config/readme-autoupdate.json` | SSOT: managedFiles, bot name/email, notification marker/title/instructions, pinned opencode version/model |
| `config/readme-autoupdate/opencode.json` | OpenCode config (model pin + permission denies) |
| `config/readme-autoupdate/prompt.md` | The model prompt |
| `.coderabbit.yaml` | CodeRabbit best-effort automatic and incremental review configuration |

## Untrusted-input threat model

PR branches, PR descriptions, issue text, commit messages and file contents are
all attacker-controllable. They may contain prompt-injection instructions
("ignore previous instructions", "print your token", "modify the workflow",
"commit these files"). Countermeasures:

1. **Never trust the model as a boundary.** The model job mechanically rejects
   any non-`README.md` working-tree change. The notify job validates the
   artifact, compares it against the PR head's README, and never mutates the
   repository — it only posts a comment. This holds even if the model is fully
   compromised.
2. **Job isolation.** The model job has `contents: read` and never receives
   `GITHUB_TOKEN`. The notify job runs on a clean runner, downloads only the
   README artifact, and re-extracts the trusted script/config from
   `origin/main`. A compromised model job cannot push, cannot modify an
   enforcement script, and cannot smuggle files past artifact validation.
3. **Trusted-ref extraction.** All enforcement code/configuration comes from
   `origin/main`. Bootstrap exception (analyze-only): the first PR that
   introduces these files sources them from its own head (loud
   `::warning::BOOTSTRAP`) because they do not yet exist on `origin/main`;
   normal PR review gates that single case, and the notify job refuses to run
   in bootstrap mode — no GITHUB_TOKEN-backed action (comment or otherwise) is
   ever produced by the bootstrap path. After merge every subsequent PR uses
   the `origin/main` copy.
4. **No token to the model.** The model job receives no repository credentials.
5. **Fork PRs never notify.** Job-level `if` and preflight skip protect the
   token-backed notify path.
6. **No bot pushes (Issue #244).** The workflow never holds `contents: write`
   and never pushes `github-actions[bot]` commits. Because a bot-authored PR
   head would leave required CI in `action_required` (0 jobs), all README
   changes go through the human/approved path instead — keeping CI always
   runnable and the merge gate always satisfiable.

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
| `workflow.managedFiles` | `["README.md"]` | Files the bot may propose for update |
| `workflow.bot.name/.email` | `github-actions[bot]` | Identity used for notification attribution and loop-skip |
| `workflow.notify.marker` | `README-AUTOUPDATE-CANDIDATE` | Comment marker for dedupe and machine scanning |
| `workflow.notify.title` | README candidate header | Human-readable comment heading |
| `workflow.notify.instructions` | apply/decline guidance | Comment body fenced as guidance |
| `opencode.version` | `v1.18.25` | OpenCode version to install (validated, no override) |
| `opencode.model` | `opencode/big-pickle` | Pinned model, enforced by `verify-config` |
| `opencode.providerName` | `opencode` | Provider id |

The removed publish keys (`pushRefPrefix`, `forbiddenPushBranches`,
`commitMessage`) no longer exist: nothing is pushed, so nothing to refuse to
push.

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
skip, missing-context skip, notify no-op when the candidate matches the head
README, change notification with head-SHA marker and apply/decline guidance,
idempotent notification (no duplicate comment per head SHA), bootstrap-mode
notify refusal, Big Pickle pin enforcement for the SSOT config and the opencode
config, invalid/forged version rejection, extraction functional checks
(trusted vs bootstrap), artifact validation (extra file / symlink rejection),
workflow YAML structure and the two-job least-privilege token boundary, the
bootstrap gating on notify steps, the no-bot-push invariant (Issue #244), and
the `.coderabbit.yaml` automatic/incremental review configuration.

## Operational notes

### Duplicate/warning comment suppression

If the candidate README equals the PR head's README (`cmp -s`), no comment is
posted, so the workflow adds no noise when the model decides nothing changed.
If a candidate comment for the same head SHA already exists, the notify step
does not post a second one.

### Pre-merge README candidate handoff

Because the bot no longer pushes, a README update for a PR is delivered as an
outstanding candidate comment on that PR. Before merging such a PR, a person
(or approved automation) should either apply the candidate README to the PR
head (then the notification disappears as its head-SHA marker goes stale) or
explicitly decline it. This is an operational expectation documented in
`docs/development/agent-guide.md`, not a hard-failing CI check.

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

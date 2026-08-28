#!/usr/bin/env bash
#
# readme-sync.sh - preflight + publish helper for the README auto-update
# workflow (.github/workflows/readme-autoupdate.yml, Issue #180).
#
# This script is the security enforcement boundary for the workflow. It is
# executed from the trusted origin/main ref (extracted by the workflow from the
# base branch), never from the PR head, so its behavior cannot be changed by a
# pull request.
#
# Usage:
#   readme-sync.sh preflight [<output-file>]
#   readme-sync.sh publish
#
# Environment:
#   README_SYNC_CONFIG     path to the SSOT config (default: config/readme-autoupdate.json)
#   PR_HEAD_SHA            pull request head commit SHA (preflight/publish)
#   PR_HEAD_BRANCH         pull request head branch name (preflight/publish)
#   PR_HEAD_REPO           pull request head repo "owner/name" (preflight)
#   GITHUB_REPOSITORY      current repository "owner/name" (preflight/publish)
#   GITHUB_TOKEN           required for publish when PUSH_URL is not set
#   PUSH_URL               explicit git push URL (CI sets this; tests use a file URL)
#
# Guarantees (fail-closed):
#   * No change outside the configured managedFiles is ever committed or pushed.
#   * Deletions are never committed.
#   * main (and other configured branches) are never pushed to.
#   * Fork PRs and PRs whose head commit is authored by the bot are skipped.
set -euo pipefail

CONFIG="${README_SYNC_CONFIG:-config/readme-autoupdate.json}"

die() {
  echo "ERROR: $*" >&2
  exit 1
}

# json_get <dot-separated-key> - read a value from the SSOT config.
# Lists are printed one entry per line.
json_get() {
  local key="$1"
  python3 - "$CONFIG" "$key" <<'PY'
import json, sys
cfg, key = sys.argv[1], sys.argv[2]
d = json.load(open(cfg))
for k in key.split("."):
    d = d[k]
if isinstance(d, list):
    print("\n".join(str(x) for x in d))
elif isinstance(d, dict):
    print(json.dumps(d))
else:
    print(str(d))
PY
}

require_config() {
  [[ -f "$CONFIG" ]] || die "config not found: $CONFIG (expected at $(realpath "$CONFIG" 2>/dev/null || echo unknown))"
}

cmd_preflight() {
  require_config
  local output="${1:-${GITHUB_OUTPUT:-/dev/stdout}}"
  local action="proceed"
  local reason=""
  local head_repo="${PR_HEAD_REPO:-}"
  local repo="${GITHUB_REPOSITORY:-}"
  local head_sha="${PR_HEAD_SHA:-}"
  local head_branch="${PR_HEAD_BRANCH:-}"
  local bot_email
  bot_email="$(json_get workflow.bot.email)"

  # Fork PRs: never run the model or push from untrusted fork code.
  if [[ -n "$head_repo" && -n "$repo" && "$head_repo" != "$repo" ]]; then
    action="skip"; reason="fork_pr"
  elif [[ -z "$head_repo" || -z "$repo" ]]; then
    action="skip"; reason="missing_pr_repo_context"
  elif [[ -z "$head_sha" ]]; then
    action="skip"; reason="missing_head_sha"
  elif [[ -z "$head_branch" ]]; then
    action="skip"; reason="missing_head_branch"
  else
    # Loop prevention: never process a commit authored by the bot itself, so a
    # bot push does not re-trigger analysis forever.
    local author_email
    author_email="$(git log -1 --format='%ae' "$head_sha" 2>/dev/null || true)"
    if [[ "$author_email" == "$bot_email" ]]; then
      action="skip"; reason="bot_commit"
    fi
  fi

  printf 'action=%s\naction_reason=%s\n' "$action" "$reason" > "$output"
  echo "preflight: action=$action reason=${reason:-(none)}"
}

cmd_publish() {
  require_config
  local -a managed=()
  local -a forbidden=()
  mapfile -t managed < <(json_get workflow.managedFiles)
  mapfile -t forbidden < <(json_get workflow.forbiddenPushBranches)
  local bot_name bot_email commit_message push_prefix head_branch push_ref
  bot_name="$(json_get workflow.bot.name)"
  bot_email="$(json_get workflow.bot.email)"
  commit_message="$(json_get workflow.commitMessage)"
  push_prefix="$(json_get workflow.pushRefPrefix)"
  head_branch="${PR_HEAD_BRANCH:-}"

  [[ -n "$head_branch" ]] || die "PR_HEAD_BRANCH is required"
  [[ "$push_prefix" == "HEAD:refs/heads/" ]] || die "unexpected pushRefPrefix: $push_prefix"

  local b
  for b in "${forbidden[@]}"; do
    [[ "$head_branch" == "$b" ]] && die "refusing to push to forbidden branch '$head_branch'"
  done

  # Collect changed paths (excludes ignored files, includes untracked).
  local -a lines=()
  local line status first second path delete
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    status="${line:0:2}"
    first="${status:0:1}"
    second="${status:1:1}"
    path="${line:3}"
    # porcelain rename entry: "R  old -> new"; track the destination.
    if [[ "$path" == *" -> "* ]]; then
      path="${path##* -> }"
    fi
    lines+=("|$first|$second|$path")
  done < <(git status --porcelain=v1 --untracked-files=all)

  if [[ ${#lines[@]} -eq 0 ]]; then
    echo "publish: no working-tree changes; nothing to commit or push"
    return 0
  fi

  # Fail-closed boundary: every changed path must be a managed file and must
  # not be a deletion. Gather all violations first, then abort without commit.
  local -a stage=()
  local -a violations=()
  local m
  local in_managed=0
  for line in "${lines[@]}"; do
    IFS='|' read -r _ first second path <<< "$line"
    in_managed=0
    for m in "${managed[@]}"; do
      [[ "$path" == "$m" ]] && in_managed=1
    done
    if [[ "$first" == "D" || "$second" == "D" ]]; then
      violations+=("$path (deletion is not allowed)")
    elif [[ $in_managed -eq 0 ]]; then
      violations+=("$path (not in workflow.managedFiles)")
    else
      stage+=("$path")
    fi
  done

  if [[ ${#violations[@]} -gt 0 ]]; then
    echo "publish: FAILED - unacceptable working-tree changes:" >&2
    local v
    for v in "${violations[@]}"; do echo "  - $v" >&2; done
    echo "publish: no commit was created and no push was performed" >&2
    return 1
  fi

  local m m2
  for m in "${stage[@]}"; do
    git diff HEAD --check -- "$m" || die "whitespace errors in '$m'"
  done

  echo "staging: ${stage[*]}"
  git add -- "${stage[@]}"
  git -c user.name="$bot_name" -c user.email="$bot_email" \
    commit -m "$commit_message" -- "${stage[@]}"

  push_ref="${push_prefix}${head_branch}"
  local push_url="${PUSH_URL:-}"
  local new_sha
  new_sha="$(git rev-parse HEAD)"
  if [[ -z "$push_url" ]]; then
    push_url="https://x-access-token:${GITHUB_TOKEN:?GITHUB_TOKEN is required for publish without PUSH_URL}@github.com/${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}.git"
  fi
  echo "pushing $new_sha to $push_ref"
  git push "$push_url" "$push_ref"
  echo "publish: updated $head_branch -> $new_sha"
}

main() {
  local command="${1:-}"
  shift || true
  case "$command" in
    preflight) cmd_preflight "$@" ;;
    publish) cmd_publish ;;
    "") die "usage: readme-sync.sh <preflight [<output-file>]|publish>" ;;
    *) die "unknown command: $command" ;;
  esac
}

main "$@"
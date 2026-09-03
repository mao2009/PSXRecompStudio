#!/usr/bin/env bash
#
# readme-sync.sh - preflight / verify-config / notify helper for the README
# auto-update workflow (.github/workflows/readme-autoupdate.yml, Issue #180).
#
# This script is the security enforcement boundary for the workflow. It is
# executed from the trusted origin/main ref (extracted by the workflow from the
# base branch), never from the PR head, so its behavior cannot be changed by a
# pull request.
#
# Usage:
#   readme-sync.sh preflight [<output-file>]
#   readme-sync.sh verify-config [<output-file>]
#   readme-sync.sh notify
#
# Environment:
#   README_SYNC_CONFIG     path to the SSOT config (default: config/readme-autoupdate.json)
#   OPENCODE_CONFIG        path to the opencode config (verify-config; default: <config dir>/opencode.json)
#   PR_HEAD_SHA            pull request head commit SHA (preflight/notify)
#   PR_HEAD_BRANCH         pull request head branch name (preflight)
#   PR_HEAD_REPO           pull request head repo "owner/name" (preflight)
#   GITHUB_REPOSITORY      current repository "owner/name" (preflight/notify)
#   GITHUB_RUN_ID          current workflow run id (notify; artifact reference)
#   PR_NUMBER              pull request number (notify)
#   CANDIDATE_README       path to the validated candidate README artifact (notify)
#   GITHUB_TOKEN           required for notify to post a PR comment
#   GITHUB_API_ROOT         optional local directory used INSTEAD of the GitHub
#                          REST API (tests only). When set, "comments.json" is
#                          read for existing comments and appended to when
#                          posting, simulating GET/POST /issues/{n}/comments.
#   README_SYNC_BOOTSTRAP  when "1", notify is refused: the trusted origin/main
#                          assets do not exist yet, so the token-backed notify
#                          path must never run PR-controlled code.
#
# Guarantees (fail-closed):
#   * Big Pickle (opencode/big-pickle) is intentionally pinned; verify-config
#     rejects any other model in the trusted configuration and there is no
#     fallback model or repository-variable override.
#   * No comment is ever posted unless every required context is present and the
#     candidate README actually differs from the PR head README.
#   * The bot never commits or pushes to the PR head branch (Issue #244): a
#     github-actions[bot]-authored HEAD leaves required CI in action_required
#     with 0 jobs, permanently blocking the merge gate. README updates are
#     surfaced as an advisory PR comment + artifact reference only.
#   * Fork PRs and PRs whose head commit is authored by the bot are skipped.
#   * Bootstrap mode never posts the token-backed comment.
#   * Comment posting is idempotent per (marker, head SHA): repeat runs do not
#     create duplicate comments, and no notification is emitted when the README
#     is already consistent (no noise).
set -euo pipefail

CONFIG="${README_SYNC_CONFIG:-config/readme-autoupdate.json}"
PINNED_MODEL="opencode/big-pickle"

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

# cmd_verify_config - mechanical Big Pickle pin + pinned version gate.
# Reads the trusted SSOT config and the opencode config and fails (exit 1)
# unless the model is exactly opencode/big-pickle everywhere and the OpenCode
# version is a well-formed pinned semver. No repository variable is read and no
# fallback default is applied. Writes `model=` and `version=` to the output.
cmd_verify_config() {
  require_config
  local output="${1:-${GITHUB_OUTPUT:-/dev/stdout}}"
  local model version opencode_cfg opencode_model opencode_small
  model="$(json_get opencode.model)"
  version="$(json_get opencode.version)"

  [[ "$model" == "$PINNED_MODEL" ]] || die "model pin violation: SSOT config opencode.model is '$model', but only '$PINNED_MODEL' is allowed. Big Pickle is intentionally pinned and cannot be overridden by repository variables or PR-controlled configuration."

  [[ "$version" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "invalid pinned OpenCode version '$version' in SSOT config (expected semver like v1.18.25). No fallback version is applied."

  # The OpenCode runtime config must pin the same model for both the primary
  # and small-model slots; otherwise a PR could route calls elsewhere.
  opencode_cfg="${OPENCODE_CONFIG:-$(dirname "$CONFIG")/opencode.json}"
  [[ -f "$opencode_cfg" ]] || die "opencode config not found: $opencode_cfg"
  opencode_model="$(OPENCODE_CONFIG="$opencode_cfg" python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("model",""))' "$opencode_cfg")"
  opencode_small="$(OPENCODE_CONFIG="$opencode_cfg" python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("small_model",""))' "$opencode_cfg")"
  [[ "$opencode_model" == "$PINNED_MODEL" ]] || die "model pin violation: opencode config model is '$opencode_model'; only '$PINNED_MODEL' is allowed."
  [[ "$opencode_small" == "$PINNED_MODEL" ]] || die "model pin violation: opencode config small_model is '$opencode_small'; only '$PINNED_MODEL' is allowed."

  printf 'model=%s\nversion=%s\n' "$PINNED_MODEL" "$version" > "$output"
  echo "verify-config: OK model=$PINNED_MODEL version=$version"
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

  # Fork PRs: never run the model or post comments from untrusted fork code.
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
    # (legacy) bot-authored push does not re-trigger analysis forever.
    local author_email
    author_email="$(git log -1 --format='%ae' "$head_sha" 2>/dev/null || true)"
    if [[ "$author_email" == "$bot_email" ]]; then
      action="skip"; reason="bot_commit"
    fi
  fi

  printf 'action=%s\naction_reason=%s\n' "$action" "$reason" > "$output"
  echo "preflight: action=$action reason=${reason:-(none)}"
}

# notify_has_marker <marker_line> - returns 0 if a comment containing the exact
# marker line (marker + head SHA) already exists on the PR. Uses a local file
# simulation when GITHUB_API_ROOT is set (tests) or the real GitHub REST API
# otherwise. Fails closed (non-zero) if the marker cannot be confirmed absent.
notify_has_marker() {
  local marker_line="$1"
  if [[ "${GITHUB_API_ROOT:-}" != "" ]]; then
    local f="$GITHUB_API_ROOT/comments.json"
    [[ -f "$f" ]] && grep -qsF "$marker_line" "$f"
    return
  fi
  [[ -n "${GITHUB_TOKEN:-}" ]] || die "GITHUB_TOKEN is required to check existing PR comments"
  local api headers body next
  local tmpdir
  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' RETURN
  api="https://api.github.com/repos/${GITHUB_REPOSITORY}/issues/${PR_NUMBER}/comments?per_page=100"
  while [[ -n "$api" ]]; do
    headers="$tmpdir/headers"
    body="$tmpdir/body"
    if ! curl -fsS -D "$headers" -o "$body" \
         -H "Authorization: Bearer $GITHUB_TOKEN" \
         -H "Accept: application/vnd.github+json" "$api"; then
      die "failed to list PR comments while checking candidate marker"
    fi
    if grep -qsF "$marker_line" "$body"; then
      return 0
    fi
    next="$(awk -F'[<>]' '/^[Ll]ink:/ && /rel="next"/ {print $2; exit}' "$headers")"
    api="$next"
  done
  return 1
}

# notify_post <body> - posts an issue comment to the PR. Uses a local file log
# when GITHUB_API_ROOT is set (tests) or the real GitHub REST API otherwise.
# Fails closed (non-zero) on any non-2xx response so a lost comment is never
# treated as success.
notify_post() {
  local body="$1"
  if [[ "${GITHUB_API_ROOT:-}" != "" ]]; then
    mkdir -p "$GITHUB_API_ROOT"
    printf '%s\n' "$body" >> "$GITHUB_API_ROOT/comments.json"
    return 0
  fi
  [[ -n "${GITHUB_TOKEN:-}" ]] || die "GITHUB_TOKEN is required to post a PR comment"
  local payload api status
  payload="$(python3 -c 'import json,sys; print(json.dumps({"body": sys.argv[1]}))' "$body")"
  api="https://api.github.com/repos/${GITHUB_REPOSITORY}/issues/${PR_NUMBER}/comments"
  status="$(curl -sS -X POST -o /tmp/readme-notify-post.json -w '%{http_code}' \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Accept: application/vnd.github+json" \
    -H "Content-Type: application/json" \
    -d "$payload" "$api")"
  if [[ "$status" != "2"?? ]]; then
    echo "ERROR: comment POST failed (HTTP $status)" >&2
    return 1
  fi
  return 0
}

cmd_notify() {
  require_config
  # Bootstrap mode: trusted origin/main assets are absent, so posting the
  # token-backed comment that references enforcement decisions is never
  # acceptable.
  [[ "${README_SYNC_BOOTSTRAP:-0}" == "0" ]] || die "notify refused: bootstrap mode (README_SYNC_BOOTSTRAP=1) never uses the token-backed comment path. Trusted assets are missing on origin/main."

  local head_sha="${PR_HEAD_SHA:-}"
  local repo="${GITHUB_REPOSITORY:-}"
  local run_id="${GITHUB_RUN_ID:-}"
  local pr_number="${PR_NUMBER:-}"
  local candidate="${CANDIDATE_README:-}"
  local marker title instructions
  marker="$(json_get workflow.notify.marker)"
  title="$(json_get workflow.notify.title)"
  instructions="$(json_get workflow.notify.instructions)"

  [[ -n "$head_sha" ]] || die "PR_HEAD_SHA is required"
  [[ -n "$repo" ]] || die "GITHUB_REPOSITORY is required"
  [[ -n "$pr_number" ]] || die "PR_NUMBER is required"
  [[ -n "$run_id" ]] || die "GITHUB_RUN_ID is required"
  [[ -n "$candidate" && -f "$candidate" ]] || die "CANDIDATE_README is required and must exist: ${candidate:-none}"
  [[ -n "$marker" ]] || die "workflow.notify.marker is not configured"
  [[ -f README.md ]] || die "README.md is required in the working tree (checked out PR head)"

  # No candidate: the candidate README equals the PR head README, so nothing to
  # say. Emitting nothing here is the "no noise when unchanged" guarantee.
  if cmp -s "$candidate" README.md; then
    echo "notify: candidate README matches the PR head README; no notification needed"
    return 0
  fi

  local added removed
  added="$(diff -u README.md "$candidate" | sed '1,2d' | grep -c '^+' || true)"
  removed="$(diff -u README.md "$candidate" | sed '1,2d' | grep -c '^-' || true)"

  local marker_line body
  marker_line="<!-- $marker: $head_sha -->"
  body="$marker_line
**$title**

A validated README update candidate was generated for this PR but was **not applied** to the branch.

- Generated for HEAD SHA: \`$head_sha\`
- Change vs current HEAD README: +$added / -$removed lines
- Candidate artifact: Actions run \`$run_id\` in \`$repo\` (artifact \`README.md\`; short retention)

$instructions"

  # Idempotency: do not post a second comment if one with this marker + HEAD SHA
  # already exists (e.g. a re-run of this workflow for the same HEAD).
  if notify_has_marker "$marker_line"; then
    echo "notify: a candidate notification already exists for HEAD $head_sha; skipping (idempotent)"
    return 0
  fi

  if notify_post "$body"; then
    echo "notify: posted README candidate notification for HEAD $head_sha (marker $marker)"
  else
    echo "ERROR: failed to post README candidate notification for HEAD $head_sha" >&2
    return 1
  fi
}

main() {
  local command="${1:-}"
  shift || true
  case "$command" in
    preflight) cmd_preflight "$@" ;;
    verify-config) cmd_verify_config "$@" ;;
    notify) cmd_notify ;;
    "") die "usage: readme-sync.sh <preflight [<output-file>]|verify-config [<output-file>]|notify>" ;;
    *) die "unknown command: $command" ;;
  esac
}

main "$@"

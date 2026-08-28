#!/usr/bin/env bash
#
# Local scenario tests for scripts/ci/readme-sync.sh
# (Issue #180 README auto-update). No network or GitHub access is required;
# pushes go to a local bare repository via PUSH_URL.
#
# Scenarios:
#   1. preflight: proceed on a normal PR head
#   2. preflight: skip fork PRs
#   3. preflight: skip bot-authored head commit (loop prevention)
#   4. preflight: skip when PR repo context is missing
#   5. publish: no working-tree changes -> clean no-op, no push
#   6. publish: README-only change is committed as github-actions[bot] and pushed
#   7. publish: FAILS (no commit/push) when a NON-managed file changed
#      (e.g. a prompt-injection attempt modifies .github/workflows/...)
#   8. publish: FAILS when a managed file is deleted
#   9. publish: refuses to push to main
#  10. workflow YAML is well-formed
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SYNC="$SCRIPT_DIR/readme-sync.sh"
WORKFLOW="$(cd "$SCRIPT_DIR/../.." && pwd)/.github/workflows/readme-autoupdate.yml"
CONFIG="$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate.json"

PASS=0
FAIL=0
TESTS=()
ROOT_DIR="$(mktemp -d)"
trap 'rm -rf "$ROOT_DIR"' EXIT

fail() {
  echo "  FAIL: $1" >&2
  FAIL=$((FAIL + 1))
}

assert_eq() {
  if [[ "$1" != "$2" ]]; then
    fail "expected [$1] == [$2]"
  fi
}

assert_ne() {
  if [[ "$1" == "$2" ]]; then
    fail "expected [$1] != [$2]"
  fi
}

setup_repo() {
  ROOT="$ROOT_DIR/$1"
  mkdir -p "$ROOT"
  REMOTE="$ROOT/remote.git"
  git init --bare --initial-branch=main "$REMOTE" >/dev/null 2>&1
  WORK="$ROOT/work"
  git clone -q "$REMOTE" "$WORK"
  git -C "$WORK" config user.name tester
  git -C "$WORK" config user.email tester@example.com
  cat > "$WORK/README.md" <<'EOF'
# Demo
日本語のREADME。
EOF
  mkdir -p "$WORK/.github/workflows"
  cat > "$WORK/.github/workflows/test.yml" <<'EOF'
name: CI
on: [push]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
EOF
  git -C "$WORK" add -A
  git -C "$WORK" commit -q -m "seed"
  git -C "$WORK" push -q origin main
  git -C "$WORK" checkout -q -b pr-branch
  git -C "$WORK" commit -q --allow-empty -m "pr head commit"
}

preflight_env() {
  export README_SYNC_CONFIG="$CONFIG"
  export GITHUB_REPOSITORY="${GITHUB_REPOSITORY:-owner/repo}"
}

# --- 1. preflight proceed -----------------------------------------------
test_preflight_proceed() {
  setup_repo t1
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=owner/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=proceed$' "$out" || fail "expected action=proceed"
}

# --- 2. preflight fork skip ----------------------------------------------
test_preflight_fork() {
  setup_repo t2
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=attacker/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
  grep -q '^action_reason=fork_pr$' "$out" || fail "expected reason=fork_pr"
}

# --- 3. preflight bot-commit loop prevention -----------------------------
test_preflight_bot_loop() {
  setup_repo t3
  local out
  out="$(mktemp)"
  git -C "$WORK" config user.name "github-actions[bot]"
  git -C "$WORK" config user.email "41898282+github-actions[bot]@users.noreply.github.com"
  git -C "$WORK" commit -q --allow-empty -m "bot update"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=owner/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
  grep -q '^action_reason=bot_commit$' "$out" || fail "expected reason=bot_commit"
}

# --- 4. preflight missing context -------------------------------
test_preflight_missing_context() {
  setup_repo t4
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO="" \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
}

# --- 5. publish no changes -> no-op ---------------------------------------
test_publish_noop() {
  setup_repo t5
  local before after
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null
  after="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  assert_eq "$before" "$after" "no-op: remote branch must be unchanged"
}

# --- 6. publish README-only -> bot commit pushed --------------------------
test_publish_readme_only() {
  setup_repo t6
  printf '\n更新ノート：タイマ実行時間の改善を追加。\n' >> "$WORK/README.md"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null
  local remote_head
  remote_head="$(git ls-remote "$REMOTE" refs/heads/pr-branch | cut -f1)"
  assert_ne "" "$remote_head" "README change must be pushed"
  git -C "$WORK" fetch -q origin
  git -C "$WORK" show "origin/pr-branch:README.md" | grep -q "更新ノート" \
    || fail "remote README must contain the update"
  git -C "$WORK" show -s --format='%ae' "origin/pr-branch" | grep -q "41898282+github-actions" \
    || fail "commit author must be the configured bot"
  git -C "$WORK" show "origin/pr-branch:.github/workflows/test.yml" | grep -q "echo hi" \
    || fail "workflow file on remote must be untouched"
}

# --- 7. publish non-managed change -> fail closed, nothing pushed ---------
test_publish_non_managed_fail_closed() {
  setup_repo t7
  printf '\nname: EVIL\non: [push]\n' >> "$WORK/.github/workflows/test.yml"
  local before
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must fail when a non-managed file is modified"
  assert_eq "$before" "$(git ls-remote "$REMOTE" refs/heads/pr-branch)" \
    "remote branch must be unchanged"
  assert_ne "$(git -C "$WORK" log --oneline -1 --format=%s)" "docs: update README" \
    "no bot commit may be created"
}

# --- 8. publish deletion of managed file -> fail closed --------------------
test_publish_deletion_fail_closed() {
  setup_repo t8
  rm "$WORK/README.md"
  local before
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must fail when a managed file is deleted"
  assert_eq "$before" "$(git ls-remote "$REMOTE" refs/heads/pr-branch)" \
    "remote branch must be unchanged"
}

# --- 9. publish refuses to push to main ----------------------------------
test_publish_no_main() {
  setup_repo t9
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=main bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must refuse to push to main"
}

# --- 10. workflow YAML well-formed ---------------------------------------
test_workflow_yaml() {
  python3 - "$WORKFLOW" <<'PY' || fail "workflow YAML did not parse"
import sys, yaml
with open(sys.argv[1]) as f:
    d = yaml.safe_load(f)
assert "on" in d, "missing 'on'"
assert "pull_request" in d["on"], "missing pull_request trigger"
assert d.get("permissions", {}).get("contents") == "write", "contents: write permission"
assert "models" not in d.get("permissions", {}), "no models permission (no GitHub Models usage)"
PY
}

main() {
  bash -n "$SYNC" || { echo "syntax error in readme-sync.sh"; exit 1; }
  tests=(test_preflight_proceed test_preflight_fork test_preflight_bot_loop \
         test_preflight_missing_context test_publish_noop \
         test_publish_readme_only test_publish_non_managed_fail_closed \
         test_publish_deletion_fail_closed test_publish_no_main \
         test_workflow_yaml)
  for t in "${tests[@]}"; do
    printf '%s ...\n' "$t"
    if "$t"; then
      PASS=$((PASS + 1))
    else
      TESTS+=("$t")
    fi
    echo
  done
  echo "PASS=$PASS FAIL=$FAIL"
  if [[ $FAIL -gt 0 ]]; then
    printf 'failed: %s\n' "${TESTS[*]}"
    exit 1
  fi
}

main